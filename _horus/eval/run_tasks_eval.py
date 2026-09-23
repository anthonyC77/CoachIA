#!/usr/bin/env python3
"""Eval de tâches — l'orchestrateur de bout en bout, jugé du dehors.

Entrées : eval/tasks_golden.jsonl (ce qu'on attend) et un fichier de runs au
format du contrat de run public (eval/schemas/run_contract.schema.json). Le
script ne lit RIEN d'autre : pas de base, pas de table de spans, pas de
prompts, pas d'appel réseau. Si l'eval a besoin d'un accès aux internes du
système évalué, elle a cessé d'être un étalon.

Deux oracles par tâche, et leur accord est une métrique :
  - assertions mécaniques (contrat, statut, outils, politique, E/S typées,
    idempotence, contenu, provenance) ;
  - juge : verdicts fournis de l'extérieur (--judge-file) ou produits par une
    commande (--judge-cmd, JSON sur stdin → JSON sur stdout). Le juge ne doit
    jamais être un agent du système évalué.

Exemples :
  run_tasks_eval.py --runs eval/fixtures/runs_tasks.jsonl
  run_tasks_eval.py --runs eval/fixtures/runs_tasks.jsonl --judge-file eval/fixtures/judge.jsonl
  run_tasks_eval.py --runs r.jsonl --judge-cmd "python3 mon_juge.py" --label "$(git rev-parse --short HEAD)"
  run_tasks_eval.py --runs r.jsonl --write-baseline      # décision humaine uniquement

Format --runs : une ligne JSON par run, conforme au contrat (task_id = id du golden).
Format --judge-file : {"task_id": "T-01", "pass": true, "reason": "…"} par ligne.
Contrat --judge-cmd : reçoit {"task": <golden>, "run": <run>, "rubric": str} sur stdin,
renvoie {"pass": bool, "reason": str} sur stdout, code 0.

Codes de sortie : 0 = pas de régression, 1 = régression au-delà de la tolérance
(bloc "tasks" de baseline.json), 2 = erreur d'exécution.
"""
import argparse
import datetime as dt
import json
import os
import re
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
PROJECT = os.path.abspath(os.path.join(HERE, ".."))
sys.path.insert(0, HERE)
from jsonschema_lite import validate, property_exists  # noqa: E402

DEFAULT_GOLDEN = os.path.join(HERE, "tasks_golden.jsonl")
DEFAULT_BASELINE = os.path.join(HERE, "baseline.json")
DEFAULT_SCHEMAS = os.path.join(HERE, "schemas")
DEFAULT_POLICY = os.path.join(HERE, "policy", "agents.json")
DEFAULT_RUNS_DIR = os.path.join(HERE, "runs", "tasks")
METRIC_KEYS = ("success_rate", "assert_rate", "judge_rate", "agreement", "kappa",
               "contract_valid_rate", "policy_ok_rate", "provenance_ok_rate", "negative_ok", "no_human_rate")
EURO_RE = re.compile(r"\d+\s?(?:€|euros?)", re.I)


def load_jsonl(path):
    with open(path, encoding="utf-8") as f:
        return [json.loads(line) for line in f if line.strip()]


def load_json(path):
    with open(path, encoding="utf-8") as f:
        return json.load(f)


def load_schemas(schemas_dir):
    contract = load_json(os.path.join(schemas_dir, "run_contract.schema.json"))
    tools = {}
    tdir = os.path.join(schemas_dir, "tools")
    for fn in sorted(os.listdir(tdir)):
        if fn.endswith(".schema.json"):
            s = load_json(os.path.join(tdir, fn))
            tools[s["name"]] = s
    return contract, tools


def get_path(obj, dotted):
    """Valeur à un chemin 'a.b' ; sur un tableau, renvoie la liste des valeurs."""
    node = obj
    for part in dotted.split("."):
        if isinstance(node, list):
            node = [x.get(part) for x in node if isinstance(x, dict)]
        elif isinstance(node, dict):
            node = node.get(part)
        else:
            return None
    return node


def check_task(task, run, contract, tools, policies):
    """Assertions mécaniques. Retourne (liste d'assertions, dict de flags)."""
    exp = task.get("expected", {})
    A = []

    def a(name, ok, detail=""):
        A.append({"name": name, "ok": bool(ok), "detail": detail})
        return bool(ok)

    if run is None:
        a("run_present", False, "aucun run pour cette tâche")
        return A, {"contract_valid": False, "policy_ok": False, "provenance_ok": False}

    errs = validate(run, contract)
    contract_valid = a("contract_valid", not errs, "; ".join(errs[:5]))
    if not contract_valid:
        return A, {"contract_valid": False, "policy_ok": False, "provenance_ok": False}

    a("status", run["status"] == exp.get("status", "completed"),
      f"attendu {exp.get('status', 'completed')}, reçu {run['status']}")

    calls = run["tool_calls"]
    called = [c["tool"] for c in calls]
    tool_exp = exp.get("tools", {})
    for t in tool_exp.get("required", []):
        a(f"tool_required:{t}", t in called, f"outils appelés : {called}")
    for t in tool_exp.get("forbidden", []):
        a(f"tool_forbidden:{t}", t not in called)
    if tool_exp.get("any_of"):
        a("tool_any_of", any(t in called for t in tool_exp["any_of"]), f"outils appelés : {called}")
    if "max_calls" in tool_exp:
        a("max_tool_calls", len(calls) <= tool_exp["max_calls"], f"{len(calls)} appel(s)")

    # Politique : allow-list, déclaration, quotas, identité
    pol = run["policy"]
    declared = policies.get("agents", {}).get(pol["agent"])
    policy_ok = True
    policy_ok &= a("policy_allowlist", all(t in pol["allowed_tools"] for t in called),
                   f"hors allow-list : {[t for t in called if t not in pol['allowed_tools']]}")
    if declared is None:
        policy_ok &= a("policy_declared", False, f"agent inconnu de eval/policy/agents.json : {pol['agent']}")
    else:
        policy_ok &= a("policy_declared", set(pol["allowed_tools"]) <= set(declared["allowed_tools"]),
                       "le run expose des outils que la politique déclarée n'accorde pas")
        policy_ok &= a("policy_identity", pol["identity"].get("principal") == declared["identity"]["principal"])
    q, c = pol["quotas"], run["cost"]
    policy_ok &= a("quota_calls", c["calls"] <= q["max_calls"], f"{c['calls']}/{q['max_calls']}")
    policy_ok &= a("quota_tokens", c["tokens_in"] + c["tokens_out"] <= q["max_tokens"])
    policy_ok &= a("quota_wall_clock", c["wall_clock_ms"] <= q["max_wall_clock_ms"])
    policy_ok &= a("quota_eur", c["eur"] <= q["max_eur"])

    # E/S typées : chaque appel validé contre le schéma généré ; idempotence sur les effets de bord
    side_effects = 0
    for i, call in enumerate(calls):
        s = tools.get(call["tool"])
        if s is None:
            a(f"tool_known[{i}]", False, f"outil hors registre : {call['tool']}")
            continue
        ein = validate(call["input"], s["input"])
        eout = validate(call["output"], s["output"]) if call["ok"] else []
        a(f"tool_io_valid[{i}]:{call['tool']}", not ein and not eout, "; ".join((ein + eout)[:5]))
        if s.get("side_effect"):
            side_effects += 1
            a(f"idempotency_key[{i}]:{call['tool']}", bool(call.get("idempotency_key")))
    for io in exp.get("tool_io", []):
        for call in [c for c in calls if c["tool"] == io["tool"]]:
            for prop in io.get("input_has", []):
                a(f"tool_io_prop:{io['tool']}.input.{prop}", get_path(call["input"], prop) is not None)
            for prop in io.get("output_has", []):
                v = get_path(call["output"], prop)
                a(f"tool_io_prop:{io['tool']}.output.{prop}", v is not None and v != [])
    se = exp.get("side_effects", {})
    if "max" in se:
        a("side_effects_max", side_effects <= se["max"], f"{side_effects} effet(s) de bord")
    if "exactly" in se:
        a("side_effects_exactly", side_effects == se["exactly"], f"{side_effects} effet(s) de bord")

    # Contenu
    text = run["output"]["text"]
    oexp = exp.get("output", {})
    for s_ in oexp.get("must_contain", []):
        a(f"must_contain:{s_}", s_ in text)
    for s_ in oexp.get("must_not_contain", []):
        a(f"must_not_contain:{s_}", s_ not in text)
    if oexp.get("must_contain_any"):
        a("must_contain_any", any(s_ in text for s_ in oexp["must_contain_any"]), str(oexp["must_contain_any"]))
    if oexp.get("no_euro_amount"):
        a("no_euro_amount", not EURO_RE.search(text), "montant trouvé dans la réponse")
    for echo in oexp.get("must_echo_tool_output", []):
        vals = [get_path(c["output"], echo["field"]) for c in calls if c["tool"] == echo["tool"] and c["ok"]]
        vals = [v for v in vals if isinstance(v, str) and v]
        a(f"must_echo:{echo['tool']}.{echo['field']}", bool(vals) and all(v in text for v in vals))

    # Provenance : « d'où vient cette phrase ? » — chaque source documentaire doit
    # être traçable à la sortie d'un appel d'outil du même run.
    pexp = exp.get("provenance", {})
    prov = run["provenance"]
    seen_docs = set()
    for c in calls:
        if not c["ok"]:
            continue
        for path_ in ("results.doc_id", "doc_id"):
            v = get_path(c["output"], path_)
            if isinstance(v, list):
                seen_docs.update(x for x in v if isinstance(x, str))
            elif isinstance(v, str):
                seen_docs.add(v)
    doc_sources = [p["source"]["id"] for p in prov if p["source"]["kind"] == "document"]
    provenance_ok = True
    if pexp.get("required", False):
        provenance_ok &= a("provenance_present", len(prov) > 0)
    if "min_entries" in pexp:
        provenance_ok &= a("provenance_min_entries", len(prov) >= pexp["min_entries"], f"{len(prov)} entrée(s)")
    for d in pexp.get("must_cite", []):
        provenance_ok &= a(f"provenance_cites:{d}", d in doc_sources, f"cités : {sorted(set(doc_sources))}")
    untraceable = [d for d in doc_sources if d not in seen_docs]
    provenance_ok &= a("provenance_traceable", not untraceable, f"cités sans être remontés par un outil : {untraceable}")
    fragments_missing = [p["fragment"] for p in prov if p["fragment"] not in text]
    provenance_ok &= a("provenance_fragments_in_output", not fragments_missing, f"{len(fragments_missing)} fragment(s) absents de la sortie")

    return A, {"contract_valid": True, "policy_ok": bool(policy_ok), "provenance_ok": bool(provenance_ok)}


def run_judge_cmd(cmd, task, run):
    payload = json.dumps({"task": task, "run": run, "rubric": task.get("judge", {}).get("rubric", "")}, ensure_ascii=False)
    proc = subprocess.run(cmd, input=payload, capture_output=True, text=True, shell=True, timeout=120)
    if proc.returncode != 0:
        raise RuntimeError(f"juge en erreur ({proc.returncode}) : {proc.stderr.strip()[:200]}")
    verdict = json.loads(proc.stdout)
    return {"pass": bool(verdict["pass"]), "reason": verdict.get("reason", "")}


def kappa(pairs):
    """Cohen's kappa pour deux juges binaires ; pairs = [(a, b)]."""
    n = len(pairs)
    if n == 0:
        return None
    po = sum(1 for a, b in pairs if a == b) / n
    pa = sum(1 for a, _ in pairs if a) / n
    pb = sum(1 for _, b in pairs if b) / n
    pe = pa * pb + (1 - pa) * (1 - pb)
    if pe == 1.0:
        return 1.0
    return round((po - pe) / (1 - pe), 4)


def rate(rows, key, subset=None):
    sub = [r for r in rows if r.get(key) is not None and (subset is None or subset(r))]
    return round(sum(1 for r in sub if r[key]) / len(sub), 4) if sub else None


def summarize(rows):
    judged = [r for r in rows if r["judge_pass"] is not None]
    pairs = [(r["assert_pass"], r["judge_pass"]) for r in judged]
    summary = {
        "n": len(rows), "n_judged": len(judged),
        "success_rate": rate(rows, "success"),
        "assert_rate": rate(rows, "assert_pass"),
        "judge_rate": rate(rows, "judge_pass"),
        "agreement": round(sum(1 for a, b in pairs if a == b) / len(pairs), 4) if pairs else None,
        "kappa": kappa(pairs),
        "contract_valid_rate": rate(rows, "contract_valid"),
        "policy_ok_rate": rate(rows, "policy_ok"),
        "provenance_ok_rate": rate(rows, "provenance_ok", lambda r: r["provenance_required"]),
        "negative_ok": rate(rows, "success", lambda r: not r["answerable"]),
        "no_human_rate": rate(rows, "no_human"),
        "disagreements": [{"id": r["id"], "assert": r["assert_pass"], "judge": r["judge_pass"], "reason": r["judge_reason"]}
                          for r in judged if r["assert_pass"] != r["judge_pass"]],
        "by_tag": {}, "by_difficulty": {},
    }
    for t in sorted({t for r in rows for t in r["tags"]}):
        sub = [r for r in rows if t in r["tags"]]
        summary["by_tag"][t] = {"n": len(sub), "success_rate": rate(sub, "success")}
    for d in ("easy", "medium", "hard"):
        sub = [r for r in rows if r["difficulty"] == d]
        if sub:
            summary["by_difficulty"][d] = {"n": len(sub), "success_rate": rate(sub, "success")}
    return summary


def compare(summary, baseline_metrics, tolerance):
    regressions = []
    for key in ("success_rate", "assert_rate", "agreement", "policy_ok_rate", "provenance_ok_rate", "negative_ok", "contract_valid_rate"):
        b = (baseline_metrics or {}).get(key)
        s = summary.get(key)
        if b is None or s is None:
            continue
        if s < b - tolerance:
            regressions.append({"metric": key, "baseline": b, "now": s, "delta": round(s - b, 4)})
    return regressions


def main(argv=None):
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--runs", required=True, help="JSONL de runs au format du contrat public")
    p.add_argument("--golden", default=DEFAULT_GOLDEN)
    p.add_argument("--baseline", default=DEFAULT_BASELINE)
    p.add_argument("--schemas", default=DEFAULT_SCHEMAS)
    p.add_argument("--policy", default=DEFAULT_POLICY)
    p.add_argument("--runs-dir", default=DEFAULT_RUNS_DIR)
    j = p.add_mutually_exclusive_group()
    j.add_argument("--judge-file", help="Verdicts déjà produits (JSONL task_id/pass/reason)")
    j.add_argument("--judge-cmd", help="Commande juge : JSON sur stdin → {pass, reason} sur stdout")
    p.add_argument("--tags")
    p.add_argument("--tolerance", type=float, default=None, help="Défaut : baseline.tasks.tolerance ou 0.05")
    p.add_argument("--write-baseline", action="store_true")
    p.add_argument("--label", default="")
    p.add_argument("--json", action="store_true")
    p.add_argument("--quiet", action="store_true")
    a = p.parse_args(argv)

    try:
        golden = load_jsonl(a.golden)
        runs = {r.get("task_id"): r for r in load_jsonl(a.runs)}
        contract, tools = load_schemas(a.schemas)
        policies = load_json(a.policy) if os.path.exists(a.policy) else {"agents": {}}
        judge_file = {v["task_id"]: v for v in load_jsonl(a.judge_file)} if a.judge_file else {}
    except (OSError, ValueError, KeyError) as e:
        print(f"erreur d'exécution : {e}", file=sys.stderr)
        return 2
    if a.tags:
        wanted = set(a.tags.split(","))
        golden = [g for g in golden if wanted & set(g.get("tags", []))]
    if not golden:
        print("golden set de tâches vide", file=sys.stderr)
        return 2

    baseline_all = load_json(a.baseline) if os.path.exists(a.baseline) else {}
    baseline = baseline_all.get("tasks", {})
    tolerance = a.tolerance if a.tolerance is not None else baseline.get("tolerance", 0.05)

    rows = []
    for task in golden:
        run = runs.get(task["id"])
        assertions, flags = check_task(task, run, contract, tools, policies)
        assert_pass = all(x["ok"] for x in assertions)
        judge_pass, judge_reason = None, ""
        try:
            if a.judge_cmd and run is not None:
                v = run_judge_cmd(a.judge_cmd, task, run)
                judge_pass, judge_reason = v["pass"], v["reason"]
            elif task["id"] in judge_file:
                judge_pass = bool(judge_file[task["id"]]["pass"])
                judge_reason = judge_file[task["id"]].get("reason", "")
        except (RuntimeError, ValueError, KeyError, subprocess.TimeoutExpired) as e:
            print(f"[{task['id']}] {e}", file=sys.stderr)
            return 2
        success = assert_pass and (judge_pass if judge_pass is not None else True)
        rows.append({
            "id": task["id"], "tags": task.get("tags", []), "difficulty": task.get("difficulty", "medium"),
            "answerable": task.get("expected", {}).get("answerable", True),
            "provenance_required": task.get("expected", {}).get("provenance", {}).get("required", False),
            "context_tokens": (run or {}).get("input", {}).get("context_tokens"),
            "assert_pass": assert_pass, "judge_pass": judge_pass, "judge_reason": judge_reason,
            "success": success, "no_human": (run is not None and run.get("human_interventions", 0) == 0 and success),
            "failed": [x for x in assertions if not x["ok"]], **flags,
        })

    summary = summarize(rows)
    regressions = compare(summary, baseline.get("metrics"), tolerance) if baseline else []
    timestamp = dt.datetime.now(dt.timezone.utc).isoformat(timespec="seconds")
    out_run = {"kind": "tasks", "timestamp": timestamp, "label": a.label, "source": a.runs,
               "judge": a.judge_cmd or a.judge_file or None, "tolerance": tolerance,
               "metrics": summary, "regressions": regressions, "rows": rows}
    os.makedirs(a.runs_dir, exist_ok=True)
    out = os.path.join(a.runs_dir, timestamp.replace(":", "-") + ".json")
    with open(out, "w", encoding="utf-8") as f:
        json.dump(out_run, f, ensure_ascii=False, indent=1)

    if a.write_baseline:
        baseline_all["tasks"] = {
            "written_at": timestamp, "label": a.label, "tolerance": tolerance,
            "source_run": os.path.relpath(out, PROJECT).replace(os.sep, "/"),
            "metrics": {k: summary.get(k) for k in METRIC_KEYS} | {"n": summary["n"]},
        }
        with open(a.baseline, "w", encoding="utf-8") as f:
            json.dump(baseline_all, f, ensure_ascii=False, indent=1)

    if a.json:
        print(json.dumps({"run": out, "metrics": summary, "regressions": regressions}, ensure_ascii=False))
    elif not a.quiet:
        print(f"run     : {out}")
        print(f"n={summary['n']}  jugées={summary['n_judged']}  tolérance={tolerance}")
        bm = baseline.get("metrics", {})
        for key in METRIC_KEYS:
            b, s = bm.get(key), summary.get(key)
            arrow = "" if b is None or s is None else f"  (baseline {b:.4f} → Δ {s - b:+.4f})"
            print(f"  {key:<20} {'-' if s is None else f'{s:.4f}'}{arrow}")
        for r in rows:
            if not r["success"]:
                why = "; ".join(f"{x['name']}" for x in r["failed"][:4]) or f"juge : {r['judge_reason']}"
                print(f"  ÉCHEC {r['id']} — {why}")
        if summary["disagreements"]:
            print("  désaccords assertions/juge :", ", ".join(d["id"] for d in summary["disagreements"]))
        if regressions:
            print("RÉGRESSION :", json.dumps(regressions, ensure_ascii=False))
    return 1 if regressions else 0


if __name__ == "__main__":
    sys.exit(main())
