#!/usr/bin/env python3
"""Eval retrieval — oracle mécanique, zéro appel LLM.

Évalue le composant de retrieval (BM25 / vectoriel / RRF / reranker) contre
eval/golden_set.jsonl et compare au bloc "retrieval" de eval/baseline.json.

Exemples :
  run_eval.py --from-file eval/fixtures/results_retrieval.jsonl        # hors ligne, sans serveur
  run_eval.py --api http://localhost:5000 --k 5 --label "$(git rev-parse --short HEAD)"
  run_eval.py --api http://localhost:5000 --write-baseline               # décision humaine uniquement
  run_eval.py --from-file r.jsonl --tags multi-doc,regle

Format --from-file : une ligne JSON par requête du golden set
  {"id": "q-0001", "results": [{"docId": "DOC-…", "chunkId": "…", "score": 0.91, "text": "…"}]}

Codes de sortie : 0 = pas de régression, 1 = régression au-delà de la tolérance,
2 = erreur d'exécution (API injoignable, fichier absent, JSON invalide).
Stdlib uniquement.
"""
import argparse
import datetime as dt
import json
import math
import os
import sys
import urllib.error
import urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
PROJECT = os.path.abspath(os.path.join(HERE, ".."))
DEFAULT_GOLDEN = os.path.join(HERE, "golden_set.jsonl")
DEFAULT_BASELINE = os.path.join(HERE, "baseline.json")
DEFAULT_RUNS = os.path.join(HERE, "runs", "retrieval")

# Hypothèse d'API (à confirmer par un curl — voir LIVRABLE.md, test n°3) :
#   POST {api}/api/retrieval/search   body {"query": str, "k": int}
#   réponse {"results": [{"docId": str, "chunkId": str, "score": float, "text": str?}]}
SEARCH_PATH = "/api/retrieval/search"
METRIC_KEYS = ("recall", "mrr", "ndcg", "all_hit", "facts", "negative_ok")


def load_jsonl(path):
    with open(path, encoding="utf-8") as f:
        return [json.loads(line) for line in f if line.strip()]


def search_api(api, query, k, timeout):
    body = json.dumps({"query": query, "k": k}).encode("utf-8")
    req = urllib.request.Request(
        api.rstrip("/") + SEARCH_PATH, data=body,
        headers={"Content-Type": "application/json"}, method="POST")
    with urllib.request.urlopen(req, timeout=timeout) as resp:
        payload = json.loads(resp.read().decode("utf-8"))
    return payload.get("results", [])


def dcg(relevances):
    return sum(rel / math.log2(i + 2) for i, rel in enumerate(relevances))


def score_one(item, results, k, doc_field="docId"):
    """Métriques d'une seule requête. `results` = liste ordonnée de chunks."""
    expected = list(dict.fromkeys(item.get("expected_doc_ids", [])))
    top = results[:k]
    top_docs = []
    for r in top:  # dédoublonnage par document, ordre conservé
        d = r.get(doc_field)
        if d and d not in top_docs:
            top_docs.append(d)

    m = {"id": item["id"], "tags": item.get("tags", []),
         "difficulty": item.get("difficulty", "medium"),
         "answerable": item.get("answerable", True)}

    if not m["answerable"]:
        # Cas négatif : la bonne réponse est « je ne sais pas ». On mesure que rien
        # ne remonte avec un score élevé (seuil dans baseline.retrieval.negative_score_threshold).
        m["top_score"] = float(top[0].get("score", 0.0)) if top else 0.0
        m["recall"] = m["mrr"] = m["ndcg"] = m["all_hit"] = m["facts"] = None
        return m

    hits = [d for d in expected if d in top_docs]
    m["recall"] = len(hits) / len(expected) if expected else None
    m["all_hit"] = 1.0 if expected and len(hits) == len(expected) else 0.0
    ranks = [top_docs.index(d) + 1 for d in hits]
    m["mrr"] = 1.0 / min(ranks) if ranks else 0.0
    rel = [1.0 if d in expected else 0.0 for d in top_docs]
    ideal = sorted(rel, reverse=True)
    m["ndcg"] = dcg(rel) / dcg(ideal) if any(ideal) else 0.0

    must = item.get("must_contain", [])
    texts = [r.get("text") for r in top if r.get("text")]
    if must and texts:
        blob = "\n".join(texts)
        m["facts"] = sum(1 for s in must if s in blob) / len(must)
    else:
        m["facts"] = None
    return m


def aggregate(rows, key):
    vals = [r[key] for r in rows if r.get(key) is not None]
    return round(sum(vals) / len(vals), 4) if vals else None


def summarize(rows, neg_threshold):
    pos = [r for r in rows if r["answerable"]]
    neg = [r for r in rows if not r["answerable"]]
    summary = {
        "n": len(rows), "n_answerable": len(pos), "n_negative": len(neg),
        "recall": aggregate(pos, "recall"),
        "mrr": aggregate(pos, "mrr"),
        "ndcg": aggregate(pos, "ndcg"),
        "all_hit": aggregate(pos, "all_hit"),
        "facts": aggregate(pos, "facts"),
        "negative_ok": (round(sum(1 for r in neg if r["top_score"] < neg_threshold) / len(neg), 4)
                        if neg else None),
        "by_tag": {}, "by_difficulty": {},
    }
    for t in sorted({t for r in pos for t in r["tags"]}):
        sub = [r for r in pos if t in r["tags"]]
        summary["by_tag"][t] = {"n": len(sub), "recall": aggregate(sub, "recall"),
                                "all_hit": aggregate(sub, "all_hit")}
    for d in ("easy", "medium", "hard"):
        sub = [r for r in pos if r["difficulty"] == d]
        if sub:
            summary["by_difficulty"][d] = {"n": len(sub), "recall": aggregate(sub, "recall"),
                                           "mrr": aggregate(sub, "mrr")}
    return summary


def compare(summary, baseline_metrics, tolerance):
    """Métriques en régression au-delà de la tolérance (baisse seulement)."""
    regressions = []
    for key in METRIC_KEYS:
        b = (baseline_metrics or {}).get(key)
        s = summary.get(key)
        if b is None or s is None:
            continue
        if s < b - tolerance:
            regressions.append({"metric": key, "baseline": b, "now": s, "delta": round(s - b, 4)})
    return regressions


def load_baseline(path):
    if not os.path.exists(path):
        return {}
    with open(path, encoding="utf-8") as f:
        return json.load(f)


def main(argv=None):
    p = argparse.ArgumentParser(description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    src = p.add_mutually_exclusive_group(required=True)
    src.add_argument("--api", help="URL de base de l'API de recherche")
    src.add_argument("--from-file", help="JSONL {id, results:[...]} déjà produit, scoré hors ligne")
    p.add_argument("--golden", default=DEFAULT_GOLDEN)
    p.add_argument("--baseline", default=DEFAULT_BASELINE)
    p.add_argument("--runs-dir", default=DEFAULT_RUNS)
    p.add_argument("--k", type=int, default=5)
    p.add_argument("--doc-id-field", default="docId", help="Nom du champ identifiant le document dans un résultat")
    p.add_argument("--tags", help="Filtre : tags séparés par des virgules")
    p.add_argument("--tolerance", type=float, default=None, help="Baisse tolérée (défaut : baseline.retrieval.tolerance ou 0.02)")
    p.add_argument("--neg-threshold", type=float, default=None,
                   help="Score max toléré pour les requêtes non répondables (défaut : baseline)")
    p.add_argument("--timeout", type=float, default=15.0)
    p.add_argument("--write-baseline", action="store_true", help="Écrit le bloc retrieval de baseline.json avec ce run")
    p.add_argument("--label", default="", help="Étiquette libre (ex : commit)")
    p.add_argument("--json", action="store_true", help="Résumé JSON sur stdout au lieu du texte")
    p.add_argument("--quiet", action="store_true")
    a = p.parse_args(argv)

    if not os.path.exists(a.golden):
        print(f"golden set introuvable : {a.golden}", file=sys.stderr)
        return 2
    try:
        golden = load_jsonl(a.golden)
    except (OSError, ValueError) as e:
        print(f"golden set illisible : {e}", file=sys.stderr)
        return 2
    if a.tags:
        wanted = set(a.tags.split(","))
        golden = [g for g in golden if wanted & set(g.get("tags", []))]
    if not golden:
        print("golden set vide après filtrage", file=sys.stderr)
        return 2

    baseline_all = load_baseline(a.baseline)
    baseline = baseline_all.get("retrieval", {})
    tolerance = a.tolerance if a.tolerance is not None else baseline.get("tolerance", 0.02)
    neg_threshold = a.neg_threshold if a.neg_threshold is not None \
        else baseline.get("negative_score_threshold", 0.5)

    if a.from_file:
        if not os.path.exists(a.from_file):
            print(f"fichier de résultats introuvable : {a.from_file}", file=sys.stderr)
            return 2
        try:
            raw = {r["id"]: r["results"] for r in load_jsonl(a.from_file)}
        except (ValueError, KeyError) as e:
            print(f"fichier de résultats invalide : {e}", file=sys.stderr)
            return 2

        def fetch(item):
            return raw.get(item["id"], [])
    else:
        def fetch(item):
            return search_api(a.api, item["query"], a.k, a.timeout)

    rows = []
    try:
        for item in golden:
            rows.append(score_one(item, fetch(item), a.k, a.doc_id_field))
    except (urllib.error.URLError, TimeoutError, ValueError, OSError, KeyError) as e:
        print(f"erreur d'exécution : {e}", file=sys.stderr)
        return 2

    summary = summarize(rows, neg_threshold)
    regressions = compare(summary, baseline.get("metrics"), tolerance) if baseline else []
    worst = sorted([r for r in rows if r["answerable"]],
                   key=lambda r: (r["recall"] or 0, r["mrr"] or 0))[:10]

    timestamp = dt.datetime.now(dt.timezone.utc).isoformat(timespec="seconds")
    run = {"kind": "retrieval", "timestamp": timestamp, "label": a.label, "k": a.k,
           "source": a.api or a.from_file, "tolerance": tolerance,
           "metrics": summary, "regressions": regressions,
           "worst": [{"id": r["id"], "recall": r["recall"], "mrr": r["mrr"]} for r in worst],
           "rows": rows}
    os.makedirs(a.runs_dir, exist_ok=True)
    out = os.path.join(a.runs_dir, timestamp.replace(":", "-") + ".json")
    with open(out, "w", encoding="utf-8") as f:
        json.dump(run, f, ensure_ascii=False, indent=1)

    if a.write_baseline:
        baseline_all["retrieval"] = {
            "written_at": timestamp, "label": a.label, "k": a.k,
            "source_run": os.path.relpath(out, PROJECT).replace(os.sep, "/"),
            "negative_score_threshold": neg_threshold, "tolerance": tolerance,
            "metrics": {k: summary.get(k) for k in METRIC_KEYS} | {"n": summary["n"]},
        }
        with open(a.baseline, "w", encoding="utf-8") as f:
            json.dump(baseline_all, f, ensure_ascii=False, indent=1)

    if a.json:
        print(json.dumps({"run": out, "metrics": summary, "regressions": regressions}, ensure_ascii=False))
    elif not a.quiet:
        print(f"run     : {out}")
        print(f"n={summary['n']}  k={a.k}  tolérance={tolerance}")
        bm = baseline.get("metrics", {})
        for key in METRIC_KEYS:
            b, s = bm.get(key), summary.get(key)
            arrow = "" if b is None or s is None else f"  (baseline {b:.4f} → Δ {s - b:+.4f})"
            print(f"  {key:<12} {'-' if s is None else f'{s:.4f}'}{arrow}")
        for t, v in summary["by_tag"].items():
            print(f"  tag {t:<14} n={v['n']:<3} recall={v['recall']}  all_hit={v['all_hit']}")
        if worst:
            print("  pires requêtes :", ", ".join(f"{w['id']}(r={w['recall']})" for w in worst[:5]))
        if regressions:
            print("RÉGRESSION :", json.dumps(regressions, ensure_ascii=False))
    return 1 if regressions else 0


if __name__ == "__main__":
    sys.exit(main())
