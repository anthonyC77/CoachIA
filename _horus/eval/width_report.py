#!/usr/bin/env python3
"""Les 4 mesures de largeur, publiées au même endroit à chaque cycle.

Élargir, pas ajouter : chaque mesure est un compte + un invariant.
  1. % du golden set qui passe            — invariant : golden sets en deny (jamais édités pour passer)
  2. outils exposés sans relâcher la politique — ratio outils typés / exceptions à l'allow-list
  3. taille de contexte traitée sans dégradation — plus grand contexte pour lequel le taux de
     succès cumulé reste ≥ taux global − tolérance
  4. tâches franchies sans intervention humaine — runs réussis avec human_interventions = 0

Sources : le dernier run retrieval (eval/runs/retrieval), le dernier run de tâches
(eval/runs/tasks), le registre de schémas, eval/policy/agents.json. Rien d'autre.

Exemples :
  width_report.py --cycle 1 --label "$(git rev-parse --short HEAD)"
  width_report.py --cycle 1 --reviewed-by "M. Dupont (métier)" --demo-chore "ressaisie des demandes de badge" --demo-shown-to "sponsor X"
  width_report.py --cycle 1 --json

Écrit eval/width/cycle-<N>.json et cycle-<N>.md (ré-exécuter avec les mêmes flags met à jour).
Codes de sortie : 0 = rapport écrit, 1 = une mesure est indisponible (source manquante), 2 = erreur.
"""
import argparse
import datetime as dt
import glob
import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
PROJECT = os.path.abspath(os.path.join(HERE, ".."))
RUNS_RETRIEVAL = os.path.join(HERE, "runs", "retrieval")
RUNS_TASKS = os.path.join(HERE, "runs", "tasks")
SCHEMAS_TOOLS = os.path.join(HERE, "schemas", "tools")
POLICY = os.path.join(HERE, "policy", "agents.json")
WIDTH_DIR = os.path.join(HERE, "width")


def latest(dir_):
    files = sorted(glob.glob(os.path.join(dir_, "*.json")))
    if not files:
        return None, None
    with open(files[-1], encoding="utf-8") as f:
        return os.path.relpath(files[-1], PROJECT).replace(os.sep, "/"), json.load(f)


def context_without_degradation(rows, tolerance):
    rows = [r for r in rows if isinstance(r.get("context_tokens"), int)]
    if not rows:
        return None
    overall = sum(1 for r in rows if r["success"]) / len(rows)
    best, ok, seen = None, 0, 0
    for r in sorted(rows, key=lambda r: r["context_tokens"]):
        seen += 1
        ok += 1 if r["success"] else 0
        if ok / seen >= overall - tolerance:
            best = r["context_tokens"]
    return best


def build(cycle, tolerance):
    missing = []
    ret_path, ret = latest(RUNS_RETRIEVAL)
    tasks_path, tasks = latest(RUNS_TASKS)
    if ret is None:
        missing.append("aucun run retrieval dans eval/runs/retrieval")
    if tasks is None:
        missing.append("aucun run de tâches dans eval/runs/tasks")
    tools = [fn for fn in os.listdir(SCHEMAS_TOOLS) if fn.endswith(".schema.json")] if os.path.isdir(SCHEMAS_TOOLS) else []
    policy = json.load(open(POLICY, encoding="utf-8")) if os.path.exists(POLICY) else {"exceptions": []}
    exceptions = policy.get("exceptions", [])
    rows = (tasks or {}).get("rows", [])
    m = {
        "golden_pass": {
            "retrieval_all_hit": (ret or {}).get("metrics", {}).get("all_hit"),
            "retrieval_recall": (ret or {}).get("metrics", {}).get("recall"),
            "tasks_success_rate": (tasks or {}).get("metrics", {}).get("success_rate"),
            "invariant": "golden sets en permissions.deny + hook Bash (P-01, P-02)",
        },
        "tools_without_relaxing": {
            "tools_exposed": len(tools),
            "policy_exceptions": len(exceptions),
            "ratio": round(len(tools) / (1 + len(exceptions)), 2),
            "invariant": "chaque outil exposé a un schéma généré ; chaque exception est consignée dans eval/policy/agents.json",
        },
        "context_without_degradation": {
            "max_context_tokens": context_without_degradation(rows, tolerance) if rows else None,
            "invariant": f"taux de succès cumulé ≥ taux global − {tolerance}",
        },
        "tasks_without_human": {
            "count": sum(1 for r in rows if r.get("no_human")),
            "rate": (tasks or {}).get("metrics", {}).get("no_human_rate"),
            "invariant": "human_interventions = 0 dans le contrat de run, tâche réussie",
        },
    }
    return m, {"retrieval_run": ret_path, "tasks_run": tasks_path}, missing


def to_markdown(report):
    m = report["measures"]
    lines = [f"# Largeur — cycle {report['cycle']} ({report['written_at'][:10]})", "",
             "| # | Mesure | Compte | Invariant |", "|---|---|---|---|",
             f"| 1 | % du golden set qui passe | retrieval all_hit = {m['golden_pass']['retrieval_all_hit']} ; tâches success = {m['golden_pass']['tasks_success_rate']} | {m['golden_pass']['invariant']} |",
             f"| 2 | Outils exposés sans relâcher la politique | {m['tools_without_relaxing']['tools_exposed']} outils / {m['tools_without_relaxing']['policy_exceptions']} exceptions (ratio {m['tools_without_relaxing']['ratio']}) | {m['tools_without_relaxing']['invariant']} |",
             f"| 3 | Contexte traité sans dégradation | {m['context_without_degradation']['max_context_tokens']} tokens | {m['context_without_degradation']['invariant']} |",
             f"| 4 | Tâches franchies sans intervention humaine | {m['tasks_without_human']['count']} ({m['tasks_without_human']['rate']}) | {m['tasks_without_human']['invariant']} |",
             "", f"Sources : {report['sources']}", "",
             f"Relu par : {report['reviewed_by'] or '— (non relu)'}",
             f"Démo : corvée « {report['demo']['chore'] or '—'} » montrée à {report['demo']['shown_to'] or '—'}", ""]
    return "\n".join(lines)


def main(argv=None):
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--cycle", type=int, required=True)
    p.add_argument("--label", default="")
    p.add_argument("--author", default=os.environ.get("USER", "anthony"))
    p.add_argument("--reviewed-by", default="", help="Tiers qui a relu le chiffre (vide = non relu)")
    p.add_argument("--demo-chore", default="", help="La corvée qui disparaît dans la démo")
    p.add_argument("--demo-shown-to", default="", help="Personne à qui la démo a été montrée")
    p.add_argument("--tolerance", type=float, default=0.1)
    p.add_argument("--out-dir", default=WIDTH_DIR)
    p.add_argument("--json", action="store_true")
    a = p.parse_args(argv)
    try:
        measures, sources, missing = build(a.cycle, a.tolerance)
    except (OSError, ValueError) as e:
        print(f"erreur d'exécution : {e}", file=sys.stderr)
        return 2
    now = dt.datetime.now(dt.timezone.utc).isoformat(timespec="seconds")
    report = {"cycle": a.cycle, "written_at": now, "label": a.label, "author": a.author,
              "reviewed_by": a.reviewed_by, "reviewed_on": now if a.reviewed_by else "",
              "demo": {"chore": a.demo_chore, "shown_to": a.demo_shown_to, "shown_on": now if a.demo_shown_to else ""},
              "measures": measures, "sources": sources, "missing": missing}
    os.makedirs(a.out_dir, exist_ok=True)
    base = os.path.join(a.out_dir, f"cycle-{a.cycle:02d}")
    with open(base + ".json", "w", encoding="utf-8") as f:
        json.dump(report, f, ensure_ascii=False, indent=1)
    with open(base + ".md", "w", encoding="utf-8") as f:
        f.write(to_markdown(report))
    if a.json:
        print(json.dumps(report, ensure_ascii=False))
    else:
        print(to_markdown(report))
        for m in missing:
            print(f"MANQUE : {m}", file=sys.stderr)
    return 1 if missing else 0


if __name__ == "__main__":
    sys.exit(main())
