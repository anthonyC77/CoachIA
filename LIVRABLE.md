# Harnais de skills et d'agents — livrable

Construit le 6 septembre 2026 à partir de `analyse.md`. Tout ce qui suit tourne : `python3 -m unittest discover -s eval/tests` (17 tests), `python3 eval/check_nodes.py` (état des 4 nœuds).

## 1. Correspondance `analyse.md` → artefacts

| Section source | Idée d'ingénierie conservée | Artefact |
|---|---|---|
| Le constat : deux harnais, A stage 0 de B | A = `.claude/` (outillage de l'équipe) ; B = harnais d'exécution en production, dont le contrat public et les types sont déjà posés ici | `.claude/*`, `.claude/rules/outils-types.md`, `eval/schemas/run_contract.schema.json`, `eval/policy/agents.json` |
| Ligne rouge : jamais un agent du système évalué comme juge | Juge = agent Claude Code hors du système, bloqué par hook s'il lit `src/` ; l'eval ne lit que le contrat de run | `.claude/agents/juge-taches.md`, `guard-paths.sh eval-only`, `.claude/rules/eval.md`, sonde P-08 |
| L'ordre : le harnais d'abord, « l'oracle avant l'agent » | Le schéma existe avant le code qui l'utilise ; l'eval asserte dessus | `eval/gen_tool_schemas.py` → `eval/schemas/tools/*.schema.json` |
| Un outil est un type, pas une description | Schéma généré depuis le type, servant 3 fois ; supprimer une propriété casse validation et eval au même commit | `src/Nexus.Tools/Contracts/*.cs` (types), `gen_tool_schemas.py`, `check_tool_schemas.py`, `jsonschema_lite.py`, `run_tasks_eval.py` (`tool_io_valid`, `tool_io_prop`) |
| `RunPolicy` par exécution | Allow-list, quotas, identité, idempotence, annulation, provenance — déclarés dans `policy/agents.json`, recopiés dans chaque run, vérifiés de l'extérieur | `eval/policy/agents.json`, `run_contract.schema.json` (`policy`, `provenance`, `idempotency_key`, `cancelled`), assertions `policy_*`, `quota_*`, `idempotency_key`, `provenance_*` |
| Test du garde-fou (« si seul le prompt empêche… ») | Liste de sondes d'actions dangereuses ; chacune doit être arrêtée par un hook, un deny ou une assertion | `eval/guardrail_probes.jsonl` (14 sondes), `.claude/hooks/guard-eval-files.sh`, `guard-paths.sh`, `settings.json` deny, `check_nodes.py --node 1` |
| Deux couches d'eval | Retrieval : oracle mécanique 0 LLM. Tâches : assertions + juge extérieur, accord (`agreement`, `kappa`) mesuré | `eval/run_eval.py` + `golden_set.jsonl` ; `eval/run_tasks_eval.py` + `tasks_golden.jsonl` + `tasks/*.md` + `fixtures/` |
| Les 10 tâches, 3 lignes chacune | Entrée / sortie attendue / critère de justesse, 3e ligne à signer par le métier (`verified_by`) | `eval/tasks/T-01…T-10.md`, contrôle nœud 0 |
| Cas négatifs, baseline + tolérance, golden en deny, codes 0/1/2 | Repris tels quels, étendus à l'eval de tâches | `golden_set.jsonl` (q-0005), `tasks_golden.jsonl` (T-05, T-07), `baseline.json` (blocs `retrieval` / `tasks`), `settings.json`, `.github/workflows/eval.yml` |
| Observabilité ≠ eval : l'eval lit un contrat public versionné | `contract_version`, `additionalProperties: false` (un champ interne ajouté au run casse le contrat), scan statique des imports réseau/base de l'eval | `run_contract.schema.json`, `check_nodes.py --node 2` |
| Écart 2 : l'axe `run → step → agent → tool call` appartient au produit | Le contrat de run porte `tool_calls[]` avec latence et coût par appel ; c'est la projection publique de la table de spans | `run_contract.schema.json` |
| Écart 3 : la démo, critère « corvée qui disparaît » | Skill de préparation + champ `demo` du rapport de largeur, critère du nœud 3 | `.claude/skills/demo/SKILL.md`, `width_report.py --demo-*` |
| Écart 4 : la deuxième personne = l'étalon | `verified_by` ≠ `author` sur ≥ 5 tâches (nœud 0) ; `reviewed_by` ≠ `author` sur les rapports (nœud 3) | `eval/tasks/*.md`, `width_report.py`, `check_nodes.py` |
| Élargir, pas ajouter : 4 mesures au même endroit | Compte + invariant par mesure, un fichier par cycle | `eval/width_report.py` → `eval/width/cycle-NN.{json,md}`, skill `largeur` |
| Paralléliser les branches, avancer le tronc d'un nœud à la fois | Vagues parallèles dans `/livrer`, séquence par nœuds dans `/noeuds` | `.claude/skills/livrer`, `.claude/skills/noeuds`, `check_nodes.py` |
| Séquence en 4 nœuds | Un critère binaire par nœud, une commande, exit 0/1 | `eval/check_nodes.py [--node N]` |
| Les deux invariants vérifiables | Sur un commit : `check_nodes.py --node 1` (rien de dangereux n'est arrêté par le seul prompt). Sur deux cycles : `check_nodes.py --node 3` | idem |
| « À tester avant d'écrire les agents » | Reformulés en tests exécutables | §4 ci-dessous |

**Écarté, et pourquoi**

| Élément source | Raison |
|---|---|
| Toute la couche d'images (positions, degrés, plantes, figures) | Aucun contenu technique une fois la traduction faite ; purge vérifiée par `grep` (critère d'acceptation 1). |
| `read_traces.py` (lecture des transcripts Claude Code) | Format non garanti et ne trace que le développement ; remplacé par `human_interventions` dans le contrat de run, qui alimente la mesure de largeur n°4 depuis le produit. |
| `probe_retrieval.py` / skill `debug-retrieval` | Dépend d'un endpoint `/explain` hypothétique ; à écrire quand la forme de l'API est connue (test n°3). |
| `corpus_health.py` | Utile mais hors du périmètre « harnais + eval » ; n'entre dans aucun nœud. |
| Ancienne séquence par semaines | Remplacée par les nœuds dans la source elle-même. |
| Matrice de prix modèles, pari « Opus fait tout » | Décisions de coût, pas des artefacts qui tournent ; conservées dans le dossier d'origine. |

## 2. Arborescence

```
harnais/
├── CLAUDE.md                                   # 24 lignes
├── LIVRABLE.md                                 # ce fichier
├── .claude/
│   ├── settings.json                           # env, allow, deny, hooks PreToolUse (global) + SubagentStop (codeur)
│   ├── rules/ csharp.md · angular.md · outils-types.md · eval.md
│   ├── agents/ testeur.md · codeur.md · relecteur.md · eval-retrieval.md · juge-taches.md
│   ├── skills/ eval-retrieval/ (seule visible du modèle) · eval-taches/ · cadrer/ · livrer/ · noeuds/ · largeur/ · demo/
│   └── hooks/ guard-paths.sh · guard-eval-files.sh · gate-tests.sh
├── eval/
│   ├── golden_set.jsonl                        # eval retrieval, 5 cas dont 1 négatif
│   ├── tasks_golden.jsonl                      # eval de tâches, 10 cas dont 2 négatifs
│   ├── tasks/T-01.md … T-10.md                 # 3 lignes + frontmatter (verified_by)
│   ├── run_eval.py · run_tasks_eval.py         # les deux oracles, stdlib, exit 0/1/2
│   ├── gen_tool_schemas.py · check_tool_schemas.py · jsonschema_lite.py
│   ├── check_nodes.py · width_report.py
│   ├── guardrail_probes.jsonl                  # 14 sondes du test du garde-fou
│   ├── baseline.json                           # blocs retrieval / tasks, source_run = null tant qu'aucun run réel
│   ├── schemas/ run_contract.schema.json · tools/*.schema.json (générés)
│   ├── policy/agents.json                      # RunPolicy déclarée + exceptions
│   ├── fixtures/ results_retrieval.jsonl · runs_tasks.jsonl · judge.jsonl · make_fixtures.py
│   ├── tests/test_eval.py                      # 17 tests unittest
│   ├── runs/{retrieval,tasks,judge}/ · width/  # sorties
├── src/Nexus.Tools/Contracts/*.cs              # 3 types d'outils annotés (source des schémas)
└── .github/workflows/eval.yml                  # la CI lit les codes de sortie
```

Écarts par rapport à l'arborescence demandée, une ligne chacun :
- `eval/gen_tool_schemas.py` + `src/Nexus.Tools/Contracts/*.cs` : sans un type source et un générateur, « supprimer une propriété casse validation et eval au même commit » n'est pas testable ; le générateur Python est le stage 0 du source generator C#.
- `eval/check_tool_schemas.py`, `jsonschema_lite.py` : validation de schéma sans dépendance (stdlib imposé).
- `eval/check_nodes.py`, `guardrail_probes.jsonl` : le critère d'acceptation 5 exige une commande par nœud.
- `eval/width_report.py`, `eval/width/` : les 4 mesures « au même endroit chaque cycle » ont besoin d'un endroit.
- `eval/policy/agents.json`, `eval/schemas/run_contract.schema.json` : la `RunPolicy` et le contrat public sont ce que l'eval asserte ; ils devaient exister sous forme de fichiers.
- `eval/fixtures/`, `eval/tests/` : « exécutables, testables » sans serveur.
- `.github/workflows/eval.yml` : « codes de sortie lus par la CI ».
- Agent `juge-taches` et skills `noeuds`, `largeur`, `demo` : portent la seconde eval, la séquence par nœuds, les mesures de largeur et le critère de démo, absents du dossier initial.
- Le contenu intégral des fichiers est dans les fichiers eux-mêmes plutôt que recopié ici : c'est la seule version qui ne peut pas diverger.

## 3. État à la livraison (`python3 eval/check_nodes.py`)

- Nœud 1 (le passage est typé) : **franchi** — 14 sondes, schémas à jour, tout outil accordé a un type.
- Nœud 0 : non franchi, 3 `[KO]` attendus : `baseline.*.source_run` vaut `null` (aucun run réel encore), aucune tâche signée. C'est la première décision du premier cycle : faire signer 5 tâches, lancer un run réel, `--write-baseline` depuis un terminal.
- Nœuds 2 et 3 : non franchis (aucun run réel, aucun cycle publié) — normal.

## 4. Les 3 points à vérifier avant d'écrire les agents, en tests exécutables

**Test 1 — le code 2 sur `SubagentStop` bloque-t-il l'arrêt ?**
```sh
# Hook qui refuse toujours l'arrêt, sur un agent jetable
mkdir -p /tmp/t1/.claude/agents /tmp/t1/.claude/hooks && cd /tmp/t1 && git init -q
printf '#!/bin/sh\nA=$(jq -r .stop_hook_active); [ "$A" = true ] && exit 0; echo "CONTINUE: écris le mot PREUVE dans out.txt" >&2; exit 2\n' > .claude/hooks/refuse.sh && chmod +x .claude/hooks/refuse.sh
cat > .claude/settings.json <<'J'
{"hooks":{"SubagentStop":[{"matcher":"sonde","hooks":[{"type":"command","command":"\"$CLAUDE_PROJECT_DIR\"/.claude/hooks/refuse.sh"}]}]}}
J
printf -- '---\nname: sonde\ndescription: sonde\ntools: Write\n---\nRéponds seulement OK.\n' > .claude/agents/sonde.md
claude -p "Lance l'agent sonde et rapporte sa réponse" --max-turns 6
# Attendu : out.txt contient PREUVE (l'agent a repris après le refus). Sinon : le code 2 ne bloque pas sur SubagentStop
test -f out.txt && grep -q PREUVE out.txt && echo "TEST 1 OK" || echo "TEST 1 KO → déplacer gate-tests.sh dans hooks.Stop du frontmatter de codeur.md"
```
Vérifie aussi que `matcher: "codeur"` filtre bien par type d'agent (remplace `sonde` par `autre` : le hook ne doit plus se déclencher).

**Test 2 — `.claude/rules/*.md` + `paths:` se chargent-ils par fichier touché, y compris en sous-agent ?**
```sh
mkdir -p /tmp/t2/.claude/rules /tmp/t2/.claude/agents /tmp/t2/src && cd /tmp/t2 && git init -q
printf -- '---\npaths:\n  - "src/**/*.cs"\n---\nRÈGLE-SONDE : toute réponse mentionnant un fichier .cs commence par le mot CANARI.\n' > .claude/rules/sonde.md
echo 'class A {}' > src/A.cs
printf -- '---\nname: lecteur\ndescription: lit un fichier\ntools: Read\n---\nLis le fichier demandé et résume-le en une ligne.\n' > .claude/agents/lecteur.md
claude -p "Lance l'agent lecteur sur src/A.cs et recopie sa réponse mot pour mot" --max-turns 6 | tee r.txt
grep -q CANARI r.txt && echo "TEST 2 OK (règle chargée en sous-agent)" || echo "TEST 2 KO → recopier les règles critiques dans le corps des agents"
```

**Test 3 — la forme réelle de l'API de recherche (endpoint, champ `docId`)**
```sh
curl -s -X POST "$NEXUS_API_URL/api/retrieval/search" -H 'Content-Type: application/json' \
  -d '{"query":"Combien coûte un badge d'\''entreprise ?","k":5}' | tee /tmp/t3.json | python3 -c '
import json,sys; p=json.load(sys.stdin); r=p.get("results") or []
assert r, "pas de champ results : adapter SEARCH_PATH / la clé dans run_eval.py"
keys=set(r[0]); print("clés d'"'"'un résultat :", sorted(keys))
field=next((k for k in ("docId","doc_id","documentId") if k in keys), None)
print("TEST 3", "OK — champ", field, "(passer --doc-id-field si ≠ docId)" if field else "KO → aucun identifiant de document : bloque run_eval.py --api")'
```

## 5. Hypothèses prises

a. `SubagentStop` accepte un `matcher` sur le type d'agent et honore le code 2 (test 1) ; le hook ignore un `agent_type` absent du JSON d'entrée.
b. `permissions.deny` avec `Edit(eval/schemas/**)` accepte le glob ; sinon lister les fichiers un à un (le hook Bash couvre de toute façon).
c. Le système déployé sait exporter ses runs au format du contrat public (`run_contract.schema.json`) — c'est la première tâche du harnais B ; jusque-là, `eval/fixtures/runs_tasks.jsonl` sert de fumée.
d. Le domaine des 10 tâches (badges d'accès) est celui des exemples du dossier ; les 3e lignes sont des brouillons à faire signer, pas des critères validés.
e. Les 3 types C# sont des maquettes de contrat, pas du code compilé : ils fixent la grammaire que le source generator devra reconnaître.
f. `--write-baseline` n'est jamais lancé depuis un agent (hook) ; il l'est depuis un terminal humain — c'est le sens de « décision humaine ».
