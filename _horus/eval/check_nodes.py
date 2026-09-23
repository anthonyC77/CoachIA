#!/usr/bin/env python3
"""Les 4 nœuds de progression — un critère binaire par nœud, vérifié par commande.

On avance par nœuds franchis, pas par calendrier. Un nœud est franchi ou non,
contre un oracle mécanique ; on ne commence pas N+1 avant d'avoir franchi N.

  Nœud 0 — l'étalon existe          : les deux evals tournent hors ligne, la baseline porte un run réel,
                                       les golden sets sont en deny, la CI lit les codes de sortie,
                                       la 3e ligne d'au moins 5 tâches est signée par quelqu'un d'autre.
  Nœud 1 — le passage est typé      : toutes les sondes de garde-fou passent (aucune action dangereuse
                                       n'est arrêtée par le seul prompt), les schémas sont générés et à
                                       jour, tout outil accordé par la politique est typé.
  Nœud 2 — le run est lisible du dehors : un run réel valide le contrat public, « d'où vient cette
                                       phrase ? » a une réponse sur un run pris au hasard, l'eval de
                                       tâches n'importe aucun accès réseau/base.
  Nœud 3 — un tiers lit le chiffre  : deux cycles consécutifs de mesures de largeur, relus par un tiers,
                                       démo d'une corvée montrée à la personne dont on veut le mandat.

Exemples :
  check_nodes.py                # tous les nœuds, verdict par nœud
  check_nodes.py --node 1       # un seul nœud ; 0 = franchi, 1 = non franchi
  check_nodes.py --json --seed 7

Codes de sortie : 0 = nœud(s) demandé(s) franchi(s), 1 = au moins un non franchi, 2 = erreur.
"""
import argparse
import glob
import json
import os
import random
import re
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
PROJECT = os.path.abspath(os.path.join(HERE, ".."))
sys.path.insert(0, HERE)
PY = sys.executable
FORBIDDEN_IMPORTS = ("urllib", "socket", "http.client", "http.server", "sqlite3", "requests", "psycopg", "pyodbc", "httpx", "ftplib", "smtplib")


def load_json(path):
    with open(path, encoding="utf-8") as f:
        return json.load(f)


def load_jsonl(path):
    with open(path, encoding="utf-8") as f:
        return [json.loads(l) for l in f if l.strip()]


def run(cmd, stdin=None):
    env = dict(os.environ, CLAUDE_PROJECT_DIR=PROJECT)
    return subprocess.run(cmd, cwd=PROJECT, input=stdin, capture_output=True, text=True, env=env, timeout=300)


def frontmatter(path):
    text = open(path, encoding="utf-8").read()
    m = re.match(r"^---\n(.*?)\n---", text, re.S)
    out = {}
    if m:
        for line in m.group(1).splitlines():
            if ":" in line:
                k, v = line.split(":", 1)
                out[k.strip()] = v.strip().strip('"')
    return out


class Checker:
    def __init__(self, seed):
        self.rng = random.Random(seed)
        self.results = {}

    def add(self, node, name, ok, detail=""):
        self.results.setdefault(node, []).append({"check": name, "ok": bool(ok), "detail": detail})

    # ---------------------------------------------------------------- nœud 0
    def node0(self):
        r = run([PY, "eval/run_eval.py", "--from-file", "eval/fixtures/results_retrieval.jsonl", "--runs-dir", "/tmp/_nodes_r", "--quiet"])
        self.add(0, "run_eval.py --from-file tourne sans serveur", r.returncode in (0, 1), f"exit {r.returncode} {r.stderr.strip()[:120]}")
        r = run([PY, "eval/run_tasks_eval.py", "--runs", "eval/fixtures/runs_tasks.jsonl", "--judge-file", "eval/fixtures/judge.jsonl", "--runs-dir", "/tmp/_nodes_t", "--quiet"])
        self.add(0, "run_tasks_eval.py tourne hors ligne (assertions + juge fichier)", r.returncode in (0, 1), f"exit {r.returncode} {r.stderr.strip()[:120]}")
        b = load_json(os.path.join(HERE, "baseline.json"))
        for kind in ("retrieval", "tasks"):
            src = b.get(kind, {}).get("source_run")
            ok = bool(src) and os.path.exists(os.path.join(PROJECT, src)) and "fixtures" not in src
            self.add(0, f"baseline.{kind} porte un run réel (source_run existe, hors fixtures)", ok, f"source_run = {src}")
        deny = set(load_json(os.path.join(PROJECT, ".claude", "settings.json")).get("permissions", {}).get("deny", []))
        need = {"Edit(eval/golden_set.jsonl)", "Write(eval/golden_set.jsonl)", "Edit(eval/tasks_golden.jsonl)", "Write(eval/tasks_golden.jsonl)"}
        self.add(0, "les deux golden sets sont en permissions.deny", need <= deny, f"manque : {sorted(need - deny)}")
        ci = glob.glob(os.path.join(PROJECT, ".github", "workflows", "*.yml")) + glob.glob(os.path.join(PROJECT, ".gitlab-ci.yml"))
        blob = "".join(open(f, encoding="utf-8").read() for f in ci)
        self.add(0, "la CI lance les deux evals (et échoue sur leur code de sortie)", "run_eval.py" in blob and "run_tasks_eval.py" in blob, f"{len(ci)} fichier(s) CI")
        signed = []
        for path in sorted(glob.glob(os.path.join(HERE, "tasks", "T-*.md"))):
            fm = frontmatter(path)
            if fm.get("verified_by") and fm.get("verified_by") != fm.get("author"):
                signed.append(fm.get("id"))
        self.add(0, "≥ 5 tâches dont la 3e ligne est signée par quelqu'un d'autre que l'auteur", len(signed) >= 5, f"signées : {signed}")

    # ---------------------------------------------------------------- nœud 1
    def node1(self):
        probes = load_jsonl(os.path.join(HERE, "guardrail_probes.jsonl"))
        settings = load_json(os.path.join(PROJECT, ".claude", "settings.json"))
        deny = set(settings.get("permissions", {}).get("deny", []))
        from run_tasks_eval import check_task, load_schemas  # noqa: E402
        contract, tools = load_schemas(os.path.join(HERE, "schemas"))
        policies = load_json(os.path.join(HERE, "policy", "agents.json"))
        golden = {t["id"]: t for t in load_jsonl(os.path.join(HERE, "tasks_golden.jsonl"))}
        fixture = {r["task_id"]: r for r in load_jsonl(os.path.join(HERE, "fixtures", "runs_tasks.jsonl"))}
        for pr in probes:
            if pr["kind"] == "hook":
                r = run(["sh", pr["hook"], *pr.get("args", [])], stdin=json.dumps(pr["input"]))
                ok = r.returncode == pr["expect_exit"]
                self.add(1, f"{pr['id']} {pr['danger']}", ok, f"exit {r.returncode}, attendu {pr['expect_exit']}")
            elif pr["kind"] == "deny":
                missing = [x for x in pr["patterns"] if x not in deny]
                self.add(1, f"{pr['id']} {pr['danger']}", not missing, f"manque : {missing}")
            elif pr["kind"] == "eval":
                patch = pr["run_patch"]
                base = dict(fixture[patch["task_id"]])
                base.update({k: v for k, v in patch.items() if k != "task_id"})
                assertions, _ = check_task(golden[patch["task_id"]], base, contract, tools, policies)
                hit = [x for x in assertions if x["name"] == pr["expect_assertion_false"]]
                self.add(1, f"{pr['id']} {pr['danger']}", bool(hit) and not hit[0]["ok"], f"assertion {pr['expect_assertion_false']} : {hit[0]['ok'] if hit else 'absente'}")
        r = run([PY, "eval/check_tool_schemas.py"])
        self.add(1, "schémas générés depuis les types, à jour, référencés par l'eval", r.returncode == 0, r.stdout.strip().splitlines()[-1] if r.stdout.strip() else r.stderr.strip()[:120])
        r = run([PY, "eval/gen_tool_schemas.py", "--check"])
        self.add(1, "gen_tool_schemas.py --check : rien à régénérer", r.returncode == 0, r.stderr.strip()[:120])
        untyped = sorted({t for ag in policies["agents"].values() for t in ag["allowed_tools"] if t not in tools})
        self.add(1, "tout outil accordé par une politique a un type", not untyped, f"sans schéma : {untyped}")

    # ---------------------------------------------------------------- nœud 2
    def node2(self):
        files = sorted(glob.glob(os.path.join(HERE, "runs", "tasks", "*.json")))
        real = [f for f in files if "fixtures" not in (load_json(f).get("source") or "")]
        self.add(2, "au moins un run de tâches réel (source hors fixtures) dans eval/runs/tasks", bool(real), f"{len(files)} run(s), {len(real)} réel(s)")
        if real:
            last = load_json(real[-1])
            self.add(2, "100 % des runs réels valident le contrat public", last["metrics"].get("contract_valid_rate") == 1.0, f"contract_valid_rate = {last['metrics'].get('contract_valid_rate')}")
            rows = [r for r in last["rows"] if r.get("provenance_required") and r.get("contract_valid")]
            if rows:
                pick = self.rng.choice(rows)
                self.add(2, f"« d'où vient cette phrase ? » a une réponse sur un run au hasard ({pick['id']})", pick.get("provenance_ok"), f"provenance_ok = {pick.get('provenance_ok')}")
            else:
                self.add(2, "« d'où vient cette phrase ? » a une réponse sur un run au hasard", False, "aucun run avec provenance requise")
        for fn in ("run_tasks_eval.py", "jsonschema_lite.py"):
            src = open(os.path.join(HERE, fn), encoding="utf-8").read()
            bad = [m for m in FORBIDDEN_IMPORTS if re.search(rf"^\s*(import|from)\s+{re.escape(m)}\b", src, re.M)]
            self.add(2, f"{fn} n'importe aucun accès réseau ni base (l'eval ne voit que les fichiers qu'on lui donne)", not bad, f"imports interdits : {bad}")
        r = run(["sh", ".claude/hooks/guard-paths.sh", "eval-only"], stdin=json.dumps({"tool_name": "Read", "tool_input": {"file_path": "src/Nexus.Orchestrator/Spans.cs"}}))
        self.add(2, "le juge est bloqué s'il lit les internes (on coupe, l'eval passe quand même)", r.returncode == 2, f"exit {r.returncode}")

    # ---------------------------------------------------------------- nœud 3
    def node3(self):
        reports = []
        for f in sorted(glob.glob(os.path.join(HERE, "width", "cycle-*.json"))):
            reports.append(load_json(f))
        self.add(3, "au moins deux cycles de mesures de largeur publiés", len(reports) >= 2, f"{len(reports)} cycle(s)")
        if len(reports) >= 2:
            a, b = reports[-2], reports[-1]
            self.add(3, "les deux derniers cycles sont consécutifs", b["cycle"] == a["cycle"] + 1, f"{a['cycle']} → {b['cycle']}")
            for rep in (a, b):
                m = rep["measures"]
                full = all(v is not None for v in (m["golden_pass"]["tasks_success_rate"], m["tools_without_relaxing"]["ratio"],
                                                    m["context_without_degradation"]["max_context_tokens"], m["tasks_without_human"]["rate"]))
                self.add(3, f"cycle {rep['cycle']} : les 4 mesures sont renseignées", full, str(rep.get("missing")))
                self.add(3, f"cycle {rep['cycle']} : relu par un tiers", bool(rep.get("reviewed_by")) and rep.get("reviewed_by") != rep.get("author"), f"reviewed_by = {rep.get('reviewed_by')!r}")
            d = b.get("demo", {})
            self.add(3, "démo : une corvée nommée, montrée à la personne dont on veut le mandat", bool(d.get("chore")) and bool(d.get("shown_to")), f"corvée = {d.get('chore')!r}, montrée à {d.get('shown_to')!r}")


def main(argv=None):
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--node", type=int, choices=[0, 1, 2, 3], help="Un seul nœud (défaut : les quatre)")
    p.add_argument("--seed", type=int, default=None, help="Graine du choix aléatoire du run (nœud 2)")
    p.add_argument("--json", action="store_true")
    a = p.parse_args(argv)
    c = Checker(a.seed)
    nodes = [a.node] if a.node is not None else [0, 1, 2, 3]
    try:
        for n in nodes:
            getattr(c, f"node{n}")()
    except (OSError, ValueError, KeyError, subprocess.TimeoutExpired) as e:
        print(f"erreur d'exécution : {e}", file=sys.stderr)
        return 2
    crossed = {n: all(x["ok"] for x in c.results.get(n, [])) for n in nodes}
    if a.json:
        print(json.dumps({"crossed": crossed, "checks": c.results}, ensure_ascii=False))
    else:
        names = {0: "l'étalon existe", 1: "le passage est typé", 2: "le run est lisible du dehors", 3: "un tiers lit le chiffre"}
        for n in nodes:
            print(f"Nœud {n} — {names[n]} : {'FRANCHI' if crossed[n] else 'NON FRANCHI'}")
            for x in c.results.get(n, []):
                print(f"  [{'OK' if x['ok'] else 'KO'}] {x['check']}" + (f" — {x['detail']}" if not x["ok"] and x["detail"] else ""))
    return 0 if all(crossed.values()) else 1


if __name__ == "__main__":
    sys.exit(main())
