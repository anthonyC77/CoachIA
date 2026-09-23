#!/usr/bin/env python3
"""Vérifie que les schémas d'outils sont générés, à jour, et que l'eval les référence.

Trois contrôles, tous mécaniques :
  1. Chaque eval/schemas/tools/*.schema.json porte x-generated-by / x-source / x-source-sha256,
     et le sha256 du fichier source C# correspond (sinon : le type a changé sans régénération).
  2. Aucun schéma n'a été édité à la main : regénération en mémoire et comparaison octet à octet.
  3. Chaque outil et chaque propriété que tasks_golden.jsonl référence (expected.tools.*,
     expected.tool_io[].input_has / output_has) existe dans le schéma.

Conséquence : supprimer une propriété d'un type d'outil casse ce script (donc la CI)
au même commit — que le schéma ait été régénéré (contrôle 3) ou non (contrôles 1-2).

Exemples :
  check_tool_schemas.py
  check_tool_schemas.py --json
  check_tool_schemas.py --allow-missing-source   # stage 0 : les .cs ne sont pas dans ce dépôt

Codes de sortie : 0 = cohérent, 1 = divergence détectée, 2 = erreur d'exécution.
"""
import argparse
import hashlib
import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
PROJECT = os.path.abspath(os.path.join(HERE, ".."))
sys.path.insert(0, HERE)
from jsonschema_lite import property_exists  # noqa: E402

DEFAULT_SCHEMAS = os.path.join(HERE, "schemas", "tools")
DEFAULT_GOLDEN = os.path.join(HERE, "tasks_golden.jsonl")
DEFAULT_SRC = os.path.join(PROJECT, "src", "Nexus.Tools", "Contracts")


def load_schemas(dir_):
    out = {}
    for fn in sorted(os.listdir(dir_)):
        if fn.endswith(".schema.json"):
            with open(os.path.join(dir_, fn), encoding="utf-8") as f:
                out[fn] = json.load(f)
    return out


def check(schemas_dir, golden_path, src_dir, allow_missing_source):
    problems = []
    schemas = load_schemas(schemas_dir)
    if not schemas:
        return [{"kind": "erreur", "detail": f"aucun schéma dans {schemas_dir}"}], {}
    by_name = {}
    for fn, s in schemas.items():
        for key in ("x-generated-by", "x-source", "x-source-sha256", "name", "input", "output", "side_effect"):
            if key not in s:
                problems.append({"kind": "schema", "file": fn, "detail": f"champ manquant : {key} (schéma écrit à la main ?)"})
        by_name[s.get("name", fn)] = s
        src = os.path.join(PROJECT, s.get("x-source", ""))
        if not os.path.exists(src):
            if not allow_missing_source:
                problems.append({"kind": "source", "file": fn, "detail": f"source introuvable : {s.get('x-source')}"})
            continue
        with open(src, "rb") as f:
            digest = hashlib.sha256(f.read()).hexdigest()
        if digest != s.get("x-source-sha256"):
            problems.append({"kind": "drift", "file": fn,
                             "detail": f"le type {s.get('x-source')} a changé sans régénération du schéma"})

    # Contrôle 2 : régénération en mémoire (seulement si les sources sont là)
    if os.path.isdir(src_dir):
        try:
            import gen_tool_schemas  # noqa: E402
            fresh = gen_tool_schemas.build_schemas(src_dir)
        except (ValueError, OSError) as e:
            problems.append({"kind": "erreur", "detail": f"régénération impossible : {e}"})
            fresh = {}
        for name, schema in fresh.items():
            on_disk = by_name.get(name)
            if on_disk is None:
                problems.append({"kind": "drift", "file": f"{name}.schema.json", "detail": "outil présent dans le code, absent des schémas"})
            elif on_disk != schema:
                problems.append({"kind": "drift", "file": f"{name}.schema.json", "detail": "schéma sur disque différent du schéma régénéré (édité à la main ?)"})
        for name in by_name:
            if name not in fresh:
                problems.append({"kind": "drift", "file": f"{name}.schema.json", "detail": "schéma sans type source (outil supprimé du code ?)"})

    # Contrôle 3 : ce que l'eval référence existe
    if os.path.exists(golden_path):
        with open(golden_path, encoding="utf-8") as f:
            tasks = [json.loads(l) for l in f if l.strip()]
        for t in tasks:
            exp = t.get("expected", {})
            tools = exp.get("tools", {})
            for tool in tools.get("required", []) + tools.get("forbidden", []) + tools.get("any_of", []):
                if tool not in by_name:
                    problems.append({"kind": "golden", "task": t["id"], "detail": f"outil inconnu du registre : {tool}"})
            for io in exp.get("tool_io", []):
                s = by_name.get(io.get("tool"))
                if s is None:
                    problems.append({"kind": "golden", "task": t["id"], "detail": f"outil inconnu du registre : {io.get('tool')}"})
                    continue
                for prop in io.get("input_has", []):
                    if not property_exists(s["input"], prop):
                        problems.append({"kind": "golden", "task": t["id"], "detail": f"{io['tool']}.input.{prop} n'existe plus dans le schéma"})
                for prop in io.get("output_has", []):
                    if not property_exists(s["output"], prop):
                        problems.append({"kind": "golden", "task": t["id"], "detail": f"{io['tool']}.output.{prop} n'existe plus dans le schéma"})
    return problems, by_name


def main(argv=None):
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--schemas", default=DEFAULT_SCHEMAS)
    p.add_argument("--golden", default=DEFAULT_GOLDEN)
    p.add_argument("--src", default=DEFAULT_SRC)
    p.add_argument("--allow-missing-source", action="store_true")
    p.add_argument("--json", action="store_true")
    a = p.parse_args(argv)
    if not os.path.isdir(a.schemas):
        print(f"dossier de schémas introuvable : {a.schemas}", file=sys.stderr)
        return 2
    try:
        problems, by_name = check(a.schemas, a.golden, a.src, a.allow_missing_source)
    except (OSError, ValueError) as e:
        print(f"erreur d'exécution : {e}", file=sys.stderr)
        return 2
    if any(pr["kind"] == "erreur" for pr in problems):
        print(json.dumps(problems, ensure_ascii=False, indent=1), file=sys.stderr)
        return 2
    if a.json:
        print(json.dumps({"tools": sorted(by_name), "problems": problems}, ensure_ascii=False))
    else:
        print(f"{len(by_name)} outil(s) : {', '.join(sorted(by_name))}")
        for pr in problems:
            print(f"  [{pr['kind']}] {pr.get('file') or pr.get('task')} — {pr['detail']}")
        print("OK" if not problems else f"{len(problems)} divergence(s)")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
