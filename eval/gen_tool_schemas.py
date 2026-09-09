#!/usr/bin/env python3
"""Génère les schémas JSON des outils depuis les types C# annotés.

Stage 0 du source generator (Nexus.Tools.SourceGen, harnais d'exécution en
production) : même contrat de sortie, implémenté en Python stdlib pour que le
principe « un outil est un type, pas une description » soit vérifiable dès
aujourd'hui, avant que le générateur C# existe.

Le schéma produit sert trois fois : description d'outil pour le modèle,
validation E/S dans le harnais, assertion dans l'eval. Il n'est JAMAIS édité à
la main (permissions.deny + hook) : on modifie le type, on régénère.

Exemples :
  gen_tool_schemas.py                         # régénère eval/schemas/tools/*.schema.json
  gen_tool_schemas.py --check                 # 0 si à jour, 1 si un schéma diverge du type, 2 si erreur
  gen_tool_schemas.py --src src/Nexus.Tools/Contracts --out eval/schemas/tools

Grammaire C# reconnue (volontairement étroite) :
  [Tool("nom", SideEffect = bool, Description = "…")] public sealed record XInput(params);
  [ToolOutput("nom")] public sealed record XOutput(params);
  public sealed record Nested(params);
  params : [Doc("…")]? Type Nom (= défaut)?   — un paramètre sans défaut est requis.
"""
import argparse
import hashlib
import json
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
PROJECT = os.path.abspath(os.path.join(HERE, ".."))
DEFAULT_SRC = os.path.join(PROJECT, "src", "Nexus.Tools", "Contracts")
DEFAULT_OUT = os.path.join(HERE, "schemas", "tools")
GENERATOR = "eval/gen_tool_schemas.py (stage 0 de Nexus.Tools.SourceGen)"

RECORD_RE = re.compile(
    r"(?P<attrs>(?:\[[^\]]*\]\s*)*)public\s+sealed\s+record\s+(?P<name>\w+)\s*\((?P<params>.*?)\)\s*;",
    re.S)
TOOL_ATTR_RE = re.compile(r'\[Tool\("(?P<name>[^"]+)"(?P<rest>[^\]]*)\]', re.S)
TOOL_OUT_RE = re.compile(r'\[ToolOutput\("(?P<name>[^"]+)"\)\]')
PARAM_RE = re.compile(
    r'^\s*(?:\[Doc\("(?P<doc>[^"]*)"\)\]\s*)?(?P<type>[\w<>\[\]\.?]+)\s+(?P<pname>\w+)\s*(?:=\s*(?P<default>[^,]+))?\s*$')

SCALARS = {"string": "string", "int": "integer", "long": "integer", "short": "integer",
           "double": "number", "float": "number", "decimal": "number", "bool": "boolean"}


def snake(name):
    return re.sub(r"(?<!^)(?=[A-Z])", "_", name).lower()


def split_params(text):
    """Découpe sur les virgules de premier niveau (ignore celles dans [...] ou <...>)."""
    out, depth, cur = [], 0, []
    for ch in text:
        if ch in "[<(":
            depth += 1
        elif ch in "]>)":
            depth -= 1
        if ch == "," and depth == 0:
            out.append("".join(cur)); cur = []
        else:
            cur.append(ch)
    if "".join(cur).strip():
        out.append("".join(cur))
    return [p.strip() for p in out if p.strip()]


def parse_records(source):
    records = {}
    for m in RECORD_RE.finditer(source):
        params = []
        for raw in split_params(m.group("params")):
            pm = PARAM_RE.match(raw.replace("\n", " "))
            if not pm:
                raise ValueError(f"paramètre non reconnu dans {m.group('name')} : {raw!r}")
            params.append(pm.groupdict())
        records[m.group("name")] = {"attrs": m.group("attrs"), "params": params}
    return records


def type_schema(cs_type, records, seen):
    cs_type = cs_type.strip()
    nullable = cs_type.endswith("?")
    if nullable:
        cs_type = cs_type[:-1]
    arr = re.match(r"^(?:IReadOnlyList|List|IEnumerable|IList)<(.+)>$", cs_type)
    if cs_type.endswith("[]"):
        arr_inner = cs_type[:-2]
    elif arr:
        arr_inner = arr.group(1)
    else:
        arr_inner = None
    if arr_inner:
        return {"type": "array", "items": type_schema(arr_inner, records, seen)}
    if cs_type in SCALARS:
        return {"type": SCALARS[cs_type]}
    if cs_type in records:
        if cs_type in seen:
            raise ValueError(f"type récursif non supporté : {cs_type}")
        return object_schema(records[cs_type], records, seen | {cs_type})
    raise ValueError(f"type C# non supporté : {cs_type}")


def object_schema(rec, records, seen=frozenset()):
    props, required = {}, []
    for p in rec["params"]:
        s = type_schema(p["type"], records, seen)
        if p["doc"]:
            s["description"] = p["doc"]
        if p["default"] is not None:
            d = p["default"].strip()
            s["default"] = json.loads(d) if re.match(r"^-?\d+(\.\d+)?$|^(true|false)$", d) else d.strip('"')
        elif not p["type"].strip().endswith("?"):
            required.append(snake(p["pname"]))
        props[snake(p["pname"])] = s
    out = {"type": "object", "properties": props, "additionalProperties": False}
    if required:
        out["required"] = required
    return out


def build_schemas(src_dir):
    schemas = {}
    for fn in sorted(os.listdir(src_dir)):
        if not fn.endswith(".cs"):
            continue
        path = os.path.join(src_dir, fn)
        with open(path, "rb") as f:
            raw = f.read()
        records = parse_records(raw.decode("utf-8"))
        tools = {}
        for name, rec in records.items():
            t = TOOL_ATTR_RE.search(rec["attrs"])
            o = TOOL_OUT_RE.search(rec["attrs"])
            if t:
                rest = t.group("rest")
                se = re.search(r"SideEffect\s*=\s*(true|false)", rest)
                desc = re.search(r'Description\s*=\s*"([^"]*)"', rest)
                tools.setdefault(t.group("name"), {})["input"] = rec
                tools[t.group("name")]["side_effect"] = bool(se and se.group(1) == "true")
                tools[t.group("name")]["description"] = desc.group(1) if desc else ""
            if o:
                tools.setdefault(o.group("name"), {})["output"] = rec
        for tname, parts in tools.items():
            if "input" not in parts or "output" not in parts:
                raise ValueError(f"{fn} : l'outil {tname} doit avoir [Tool] et [ToolOutput]")
            schemas[tname] = {
                "$schema": "https://json-schema.org/draft/2020-12/schema",
                "$id": f"nexus/tools/{tname}/1",
                "x-generated-by": GENERATOR,
                "x-source": os.path.relpath(path, PROJECT).replace(os.sep, "/"),
                "x-source-sha256": hashlib.sha256(raw).hexdigest(),
                "name": tname,
                "description": parts["description"],
                "side_effect": parts["side_effect"],
                "input": object_schema(parts["input"], records),
                "output": object_schema(parts["output"], records),
            }
    return schemas


def main(argv=None):
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--src", default=DEFAULT_SRC)
    p.add_argument("--out", default=DEFAULT_OUT)
    p.add_argument("--check", action="store_true", help="Ne rien écrire ; 1 si les schémas sur disque diffèrent")
    a = p.parse_args(argv)
    if not os.path.isdir(a.src):
        print(f"dossier source introuvable : {a.src}", file=sys.stderr)
        return 2
    try:
        schemas = build_schemas(a.src)
    except (ValueError, OSError) as e:
        print(f"génération impossible : {e}", file=sys.stderr)
        return 2
    if not schemas:
        print("aucun outil trouvé", file=sys.stderr)
        return 2
    os.makedirs(a.out, exist_ok=True)
    drift = []
    for name, schema in schemas.items():
        path = os.path.join(a.out, f"{name}.schema.json")
        text = json.dumps(schema, ensure_ascii=False, indent=1) + "\n"
        if a.check:
            try:
                with open(path, encoding="utf-8") as f:
                    if f.read() != text:
                        drift.append(name)
            except OSError:
                drift.append(name)
        else:
            with open(path, "w", encoding="utf-8") as f:
                f.write(text)
            print(f"écrit : {os.path.relpath(path, PROJECT)}")
    if a.check:
        if drift:
            print("SCHÉMAS OBSOLÈTES (le type a changé sans régénération) : " + ", ".join(drift), file=sys.stderr)
            return 1
        print(f"{len(schemas)} schéma(s) à jour")
    return 0


if __name__ == "__main__":
    sys.exit(main())
