# Ruche, le harnais et CoachingIA — état réel et plan d'absorption

Établi le 12 septembre 2026, après inventaire de `D:\CoachingIA` (branche `corpus-maturite`,
5 fichiers modifiés non commités) et de `D:\OlfactoCoachingProject`.

---

## 1. Le constat qui change la question

**Il n'y a pas deux projets à fusionner. Il y en a trois, et deux sont déjà le même.**

| Brique | Où elle est | État |
|---|---|---|
| **CoachingIA** | `D:\CoachingIA` | vivante, avance, a un étalon protégé |
| **Le harnais (Horus)** | importé dans CoachingIA | **fusion déjà faite**, en quarantaine `.claude/_horus/`, avec justification écrite |
| **Ruche** (couche 3D) | `D:\OlfactoCoachingProject\ruche` | squelette autonome, 24 fichiers |

La fusion que tu imagines a déjà eu lieu — commit `1c02e44` « L'import Horus ». Et elle a été
faite proprement : un dossier de quarantaine, un `LISEZ-MOI.md` qui justifie ligne par ligne ce
qui a été écarté et pourquoi. C'est un travail de fusion mieux documenté que la plupart.

Ce qui reste n'est donc pas « fusionner deux projets », c'est **deux choses distinctes** :
solder le résidu de l'import Horus (§2), et absorber Ruche (§3) — qui ne s'importe pas comme
un projet, parce que sa moitié basse existe déjà dans CoachingIA, en mieux.

### Ruche et CoachingIA sont le même objet vu par deux bouts

| Couche Ruche | Ce que le squelette écrit | Ce que CoachingIA a déjà | Verdict |
|---|---|---|---|
| **1 · Capture** | `tools/hook-emit.sh`, 15 lignes de shell | `POST /hooks/{eventName}` + `plugin/coachingia/hooks/hooks.json`, chargeable en un clic | **CoachingIA**, sans discussion |
| **2 · Journal** | `parseJsonl` + le contrat `RunEvent` | `TranscriptRecord`/`TranscriptReader` (flux tolérant, lignes illisibles comptées, jamais fatal) → `SessionBuilder` → `ConversationModel` | **CoachingIA** pour la lecture, **Ruche** pour le contrat de sortie |
| **3 · Moteur** | `reduce()` + images-clés + marche arrière | `TranscriptIngestor` : rejeu en spans **aux horodatages d'origine** — mais à sens unique, vers Phoenix | **Ruche** |
| **4 · Détecteurs** | 5 règles pures (boucle, ping-pong, quota projeté) | `SignalExtractor` : signaux de coaching (`context_pressure`, `cache_read_ratio`…) — autre objet, même flux | **Ruche**, à poser à côté et non à la place |
| **5 · Rendu** | ruche / rivière en Three.js | `web/prototype/graphe.html` : le graphe des **paliers d'apprenant**, autre sujet | **Ruche** |

Et surtout — l'arbre que CoachingIA produit déjà **est** l'arbre de Ruche :

```
session (AGENT)                      →  run.start + agent.spawn (depth 0)
 └─ task (CHAIN)                     →  le découpage du TaskSegmenter
     ├─ turn (CHAIN)                 →  think.start / think.end
     │   ├─ tool.Grep (TOOL, 180 ms) →  tool.start / tool.end { ok, ms }
     │   └─ tool.Bash (TOOL, erreur) →  tool.end { ok: false }
     └─ turn (AGENT)   ← sous-agent  →  agent.spawn (depth 1), via Turn.IsSidechain
```

`Turn.IsSidechain` porte déjà la distinction sous-agent / conversation principale, et
`AssistantStep` porte déjà le bloc `usage` (`input_tokens`, `cache_read_input_tokens`,
`output_tokens`). Il ne manque **rien** dans le modèle. Il manque une projection.

> `ConversationModel.cs` dit de lui-même : « C'est la seule frontière à redessiner le jour où
> Claude Code change de format. » C'est exactement le rôle que le contrat `RunEvent` doit
> jouer côté Ruche. Les deux disent la même chose ; il faut qu'ils se rejoignent en un point,
> pas en deux.

---

## 2. Trois choses à trancher **avant** d'ajouter quoi que ce soit

### 2.1 La CI est verte sur un projet qui n'est pas celui-là

`.github/workflows/eval.yml` s'exécute à chaque push et chaque PR, en cinq étapes :

| Étape | Ce qu'elle mesure | État réel dans CoachingIA |
|---|---|---|
| `unittest discover -s eval/tests` | le code Python des oracles | **valide** — ce code existe |
| `check_tool_schemas.py` | que les schémas dérivent des types C# | lit `src/Nexus.Tools/` — **qui n'est pas dans `CoachingIA.slnx`**, donc ne compile jamais |
| `run_eval.py --from-file` | le retrieval | **il n'y a pas de retrieval dans CoachingIA** ; passe sur `eval/fixtures/` |
| `run_tasks_eval.py` | l'orchestrateur de bout en bout | **il n'y a pas d'orchestrateur** ; passe sur les fixtures |
| `check_nodes.py --node 1` | le nœud « le passage est typé » | mesure Horus |

Quatre étapes sur cinq passent au vert **grâce aux fixtures**, et ne disent rien de CoachingIA.

C'est précisément la faute que le harnais existe pour empêcher : un chiffre qui monte devant
témoin sans rien mesurer. Le `LISEZ-MOI.md` justifie avec soin ce qui est parti sous `_horus/` —
mais il ne dit pas un mot de `eval/`, qui est resté à la racine **et** branché sur la CI.
C'est l'angle mort de l'import.

**Ce qu'il ne faut pas faire : supprimer.** `eval/run_tasks_eval.py` lit un *contrat de run
public* (`eval/schemas/run_contract.schema.json`). Or c'est exactement ce que l'exporteur de
Ruche s'apprête à produire, à partir de tes vraies sessions. La chaîne n'est pas morte : elle
attend son entrée.

**Décision recommandée**

1. Une seule zone de quarantaine, à la racine, cohérente avec `_a_supprimer/` :
   `_horus/` accueille `eval/`, `.claude/_horus/*` et `src/Nexus.Tools/`.
2. La CI garde **la seule étape qui teste du code réel** (`unittest discover -s _horus/eval/tests`)
   et commente les quatre autres avec, en clair, **la condition de réouverture** :
   *« se rouvre le jour où `coachingia exporter-run` produit un fichier conforme à
   `run_contract.schema.json` ».*
3. `LISEZ-MOI.md` gagne la ligne qui lui manque, sur `eval/`.

Une CI qui mesure moins mais qui mesure vraiment vaut mieux qu'une CI verte sur des fixtures.

### 2.2 `eval/` et `evals/` — une lettre d'écart, deux sens opposés

- `evals/` (5 fichiers) = **l'étalon réel de CoachingIA**. En `permissions.deny`, plus un hook
  PowerShell, plus une variable d'environnement pour l'approuver depuis un terminal humain.
- `eval/` (50 fichiers) = la chaîne Horus, dormante, protégée par rien.

Deux dossiers dont les noms diffèrent d'une lettre et dont l'un est sacré et l'autre inerte :
c'est une erreur qui arrivera, et elle arrivera un jour de fatigue ou par un agent qui complète
un chemin. Le déplacement sous `_horus/` la supprime au passage.

### 2.3 `CaptureContent: false` s'applique à Ruche, et c'est une bonne nouvelle

`appsettings.json` porte une coupure dure : à `false`, aucun texte de prompt ni de sortie
d'outil ne quitte le processus ; seule la structure part. Le `CLAUDE.md` la classe parmi les
règles à **préserver**, pas seulement à faire marcher.

Ça tombe bien : le contrat `RunEvent` a été conçu avec `ref` — le gros (prompt, sortie, diff)
est porté par empreinte et stocké ailleurs, jamais dans le journal. **La discipline
`CaptureContent: false` et le champ `ref` disent la même chose.** Il ne reste qu'à trancher la
zone grise :

| Donnée | Dans le journal Ruche | Pourquoi |
|---|---|---|
| nom d'outil, ok/ko, durée, coût, profondeur | **oui** | c'est de la structure |
| chemin ou commande visée (`args`) | **oui, tronqué** | sans ça on ne voit pas la boucle : R1 repose dessus |
| texte du prompt, sortie d'outil, message final | **non** — `ref` seulement | c'est du contenu |
| le résumé d'un tour (`summary`) | **non par défaut** | c'est une reformulation de contenu |

Et un interrupteur `RucheCaptureArgs` par défaut à `true` en local, `false` dès que le journal
sort du poste. La démo actuelle montre des `args` en clair : c'est légitime en local, ça ne
l'est plus si tu publies un run.

---

## 3. Ce que Ruche devient : une amputation, pas un import

Ruche n'a pas de « moitié basse » à perdre : elle lit le contrat du harnais, et
chaque producteur l'écrit. Depuis le 12 septembre elle est un dépôt frère,
`HorusCoachIA/ruche/`, et n'est plus sous `web/` (voir
`harnais/docs/superpowers/specs/2026-09-12-harnais-partage-design.md`).

```
HorusCoachIA/
  harnais/eval/schemas/run_events.schema.json   ← LE CONTRAT (source)
  harnais/eval/schemas/verdicts.schema.json     ← une ligne de verdict par cas
  harnais/eval/plier_evenements.py              ← événements → runs résumés, pour juger
  harnais/eval/deplier_runs.py                  ← runs résumés → événements, pour voir
  ruche/                                        ← le lecteur, dépôt à lui seul
    src/core/events.ts                          ← MIROIR du schéma, vérifié par test/contrat.test.ts
    src/core/verdicts.ts                        ← ?eval=, la couleur d'un verdict
    tools/make-demo-trace.mjs                   ← la démo, pour les tests
  coachingia/
    _horus/                                     ← quarantaine (§2.1)
    src/CoachingIA.Harness.Core/Transcripts/RunExporter.cs  ← LE CHAÎNON MANQUANT, écrit contre run_events.schema.json
    web/prototype/graphe.html                   ← existant, autre sujet, ne pas toucher
```

Trois points de couture, et trois seulement :

**a. `RunExporter.cs`** — `TranscriptSession` → `RunEvent[]` en JSONL. Il ne parse rien : il
lit le modèle que `SessionBuilder` produit déjà. Contraintes du dépôt à respecter : français
dans les commentaires et les libellés, zéro dépendance NuGet dans `Harness.Core`,
`TreatWarningsAsErrors` actif, `CaptureContent` honoré, une suite
`RunExporterTests.Run(check)` enregistrée par un appel littéral dans `tests/Program.cs`, et le
libellé de chaque vérification écrit comme une phrase française qui énonce la promesse.

**b. `case "exporter-run": return ExporterRun();`** dans `src/CoachingIA.Cli/Program.cs`, au
même motif que les autres (`int Xxx()` local, codes 0/1/2).

**c. Plus tard seulement : `GET /runs/{id}/stream`** en SSE dans `CoachingIA.Harness`, ajouté à
`Routes.Map` — la table explicite segment d'URL → nom canonique, qui ne se dérive pas. La
règle du dépôt (« un hook répond vite et répond 200 ») est exactement la règle de Ruche (« le
tachymètre ne freine jamais la voiture ») : les deux disent qu'une panne côté observation ne
doit jamais tomber sur la session de l'utilisateur.

**Le contrat en trois.** Le schéma du harnais est la source ; `events.ts` (TypeScript) et
`RunEvent` (C#) n'en sont que les miroirs. Deux définitions qui dérivent, c'est le bug garanti. Le
choix est fait : chaque miroir est tenu par un test qui échoue à la moindre divergence —
`ruche/test/contrat.test.ts` pour le `.ts`, `RunExportTests` pour le C# — jamais par la seule
discipline, qui ne tient jamais.

---

## 4. Comment ça se lance et se teste depuis tes sessions

### Aujourd'hui — la démo seule

```bash
cd D:\OlfactoCoachingProject\ruche
npm install
npm run dev      # http://localhost:5173  — le run T-07 de démonstration
npm test         # 7 tests : l'oracle du rejeu + les détecteurs
```

`Espace` lit, `←`/`→` avancent d'un événement, `−4×`/`−1×` rejouent à l'envers, `V` bascule
ruche ↔ rivière. C'est un scénario fabriqué : utile pour juger l'idée, inutile pour juger tes
runs.

### Sur tes vraies sessions — ce qu'il manque exactement

Rien côté lecture : `TranscriptReader` lit déjà `~/.claude/projects/`, de façon rétroactive et
tolérante. Ce qui manque est la projection :

```
~/.claude/projects/<projet>/<session>.jsonl
        │
        │  TranscriptReader → SessionBuilder          [EXISTE]
        ▼
   TranscriptSession / Turn / ToolCall / AssistantStep [EXISTE]
        │
        │  RunExporter                                 [À ÉCRIRE — ~150 lignes]
        ▼
   runs/<session>.jsonl  (contrat RunEvent)
        │
        │  détecteurs + images-clés                    [EXISTE, côté Ruche]
        ▼
   la ruche en 3D
```

Cible d'usage, une fois l'exporteur écrit :

```powershell
# 1. lister ce qui est lisible, sans recopier une ligne de conversation
dotnet run --project src/CoachingIA.Cli -- probe

# 2. exporter une session au format du contrat
dotnet run --project src/CoachingIA.Cli -- exporter-run --session <id> --out web/ruche/public/runs/

# 3. la regarder
cd web/ruche && npm run dev
#    → http://localhost:5173/?run=runs/<id>.jsonl
```

Le squelette charge pour l'instant `/demo-run.jsonl` en dur : il faut lui ajouter le paramètre
`?run=` et le glisser-déposer d'un `.jsonl`. C'est vingt lignes dans `main.ts`, à faire au
même moment que l'exporteur.

**Comment savoir que c'est juste ?** Le test `test/replay.test.ts` rejoue chaque `seq` deux
fois — une fois depuis l'état vide, une fois par l'image-clé — et exige l'égalité stricte.
Lancé sur une session réelle exportée plutôt que sur la démo, c'est l'oracle qui dit que le
rejeu ne ment pas. C'est aussi, au passage, un test de non-régression du format : le jour où
Claude Code change son JSONL, c'est ce test-là qui tombe en premier.

### En direct — plus tard

`POST /hooks/{eventName}` reçoit déjà les événements en temps réel et `plugin/coachingia/hooks/hooks.json`
se colle en un clic dans `%USERPROFILE%\.claude\settings.json`. Il ne manque que la republication
en SSE (§3.c) et un tampon circulaire borné côté serveur. `LiveSource` côté Ruche est déjà écrit
pour ça.

---

## 5. Fusionner les deux Projects claude.ai

Question distincte de la fusion des dépôts, et la réponse est moins bonne : **une session est
attachée à exactement un Project, et aucune commande ne fusionne deux Projects.** Les documents
sont du markdown ; « fusionner » revient à recopier.

Recommandation : **garder un seul Project, nommé comme le dépôt** — `CoachingIA`. La raison est
mécanique : le rôle d'un Project est d'être trouvé depuis n'importe quelle session ouverte sur
ce dépôt. Deux Projects sur un seul dépôt garantissent qu'une session sur deux cherchera au
mauvais endroit.

Les quatre documents d'`OlfactoCoaching` ne méritent pas d'y être recopiés tels quels : trois
d'entre eux décrivent un import qui a eu lieu, ils sont devenus de l'histoire. Ce qui mérite de
survivre tient en deux documents : **celui-ci**, et la note d'architecture de Ruche
(`ruche-visualisation-runs-2026-09-11.md`) pour le raisonnement sur le rejeu, les détecteurs et
le choix de techno. Le reste peut rester où il est, en archive.

---

## 6. Séquence

Chaque palier est franchi ou non, contre une commande. Un palier non franchi rétrécit.

**Palier A — solder l'import** *(une heure, zéro code neuf)*
`eval/`, `.claude/_horus/` et `src/Nexus.Tools/` sous `_horus/` ; la CI réduite à son étape
utile, avec sa condition de réouverture écrite ; la ligne manquante dans `LISEZ-MOI.md`.
*Franchi quand* : `.github/workflows/eval.yml` ne contient plus une seule étape qui passe grâce
à une fixture.

**Palier B — l'exporteur** *(une demi-journée)*
`RunExporter.cs` + `RunExporterTests` + la sous-commande. `?run=` et le glisser-déposer côté Ruche.
*Franchi quand* : une de tes sessions réelles s'ouvre dans la ruche, et `replay.test.ts` passe
sur ce fichier-là et non sur la démo.

**Palier C — les détecteurs sur du réel** *(une demi-journée)*
Les cinq règles tournent sur une dizaine de tes sessions exportées. Tu annotes à la main ce qui
était une vraie boucle et ce qui ne l'était pas.
*Franchi quand* : tu as un chiffre de précision et de rappel, même mauvais. Un détecteur non
mesuré est une opinion.

**Palier D — le direct** *(une demi-journée)*
`GET /runs/{id}/stream` dans `Routes.Map`, tampon borné, `LiveSource` branché.
*Franchi quand* : une boucle est arrêtée par une reprise humaine déclenchée par l'alerte, pas
par toi qui regardais.

Ensuite seulement : le chemin critique, le rejeu comparatif de deux organisations, la
ré-exécution avec magnétoscope d'outils.

---

## La ligne à ne pas franchir, version CoachingIA

Le dépôt en porte déjà deux, et Ruche s'y range sans rien changer :

> `evals/cas/` et `evals/verdict.json` sont l'étalon. On ne les modifie pas pour faire passer
> une campagne.

> `CaptureContent: false` est une coupure dure.

À quoi Ruche ajoute la sienne, qui est la même vue d'un autre angle :

> La ruche lit les internes — c'est son métier, c'est de l'observabilité. L'eval, non. Le jour
> où une campagne tire son verdict du journal de spans, le juge a pris un rang, et il régressera
> avec ce qu'il mesure.
