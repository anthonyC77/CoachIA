---
name: eval-retrieval
description: Évalue le retrieval (recall@k, MRR, nDCG, multi-doc, négatifs) contre eval/golden_set.jsonl et compare à eval/baseline.json ; à utiliser après un changement de BM25, vectoriel, RRF ou reranker, ou quand on demande "lance l'éval", "le retrieval a régressé ?", "score du RAG", "compare à la baseline".
allowed-tools: Bash, Read
---

1. `python3 eval/run_eval.py --api $HORUS_API_URL --k 5 --label "$(git rev-parse --short HEAD)"` (hors ligne : `--from-file <résultats.jsonl>`).
2. Code 1 = régression au-delà de la tolérance de `baseline.json`. Code 2 = erreur technique : rapporte-la, ne relance pas.
3. Lis le run indiqué en première ligne. Rapporte `recall`, `mrr`, `ndcg`, `all_hit`, `negative_ok` avec le delta, puis `by_tag` pour `multi-doc` et `regle`.
4. Liste les `worst` avec leur `id` ; ne lis pas les documents, ne propose pas de correction.
5. Jamais `--write-baseline` : un nouveau point de référence est une décision humaine, prise depuis un terminal humain (le hook bloque de toute façon).
