# CoachingIA — harnais d'observation

Phases **00** (le tuyau), **01** (le parseur d'historique) et la brique temps
réel de la phase **03**, d'après `docs/architecture-v0.3.html`.

À ce stade le système **observe et ne coache pas encore**. C'est délibéré : on ne
coache pas ce qu'on ne mesure pas, et une semaine de vos vraies sessions dans
Phoenix vaut mieux que trois rubriques écrites à l'aveugle.

---

## Ce que ça fait aujourd'hui

**Deux voies de collecte, un seul modèle en sortie.**

La voie principale lit les transcripts JSONL que Claude Code dépose sous
`~/.claude/projects/`. Elle est rétroactive — au premier lancement, elle analyse
tout votre historique existant — et tolérante aux pannes : le harnais peut être
arrêté une semaine, il rattrape au passage suivant.

La voie temps réel reçoit les hooks en HTTP pour ce que le lot ne peut pas
donner : l'instant, la compaction, la friction de permission.

On obtient, par session, un arbre de ce genre :

```
session (AGENT)
 └─ task (CHAIN)                     ← l'unité du bilan et des défis
     ├─ turn (CHAIN)                 ← palier 1 : le prompt
     │   ├─ tool.Grep (TOOL, 180 ms) ← palier 3 : le harnais
     │   └─ tool.Bash (TOOL, erreur)
     └─ turn (AGENT)                 ← palier 5 : un sous-agent
```

Chaque span porte `coaching.level` et `coaching.signal`. Chaque tâche porte en
plus ses signaux mesurés **et la phrase qui les explique** (`signal.X` et
`signal.X.why`) — c'est ce qui permettra au bilan de citer vos vraies sessions
plutôt que des moyennes.

### Ce que les transcripts ont donné en plus

La sonde a montré que le bloc `usage` de chaque appel au modèle est présent dans
les transcripts : `input_tokens`, `cache_read_input_tokens`,
`cache_creation_input_tokens`, `output_tokens`. **Les signaux de contexte
(`context_pressure`, `cache_read_ratio`) ne dépendent donc plus de la télémétrie
OpenTelemetry native** — une dépendance entière retirée de la voie principale.

## Prérequis

- .NET SDK 10 (`dotnet --version`)
- Docker Desktop, pour Phoenix
- Le CLI Claude Code autonome, si vous voulez utiliser `claude -p` comme juge
  plus tard (l'extension VS Code embarque sa propre copie, réservée à son panneau)

## L'outil en ligne de commande

Sans dépendance externe : il tourne avant même que Docker ou Phoenix ne soient
en place.

```powershell
# Ce que contiennent vos transcripts, sans recopier une ligne de vos conversations.
dotnet run --project src/CoachingIA.Cli -- probe

# Sessions, tâches et signaux mesurés.
dotnet run --project src/CoachingIA.Cli -- analyze

# La découpe en tâches, avec la justification de chaque décision.
dotnet run --project src/CoachingIA.Cli -- segment

# Jetons par semaine, modèles, types de travail, alertes.
dotnet run --project src/CoachingIA.Cli -- usage --budget 40000000

# Sous-utilisation des sièges, depuis l'export de dépense de l'organisation.
dotnet run --project src/CoachingIA.Cli -- team --csv rapport.csv --budget 40000000 --weeks 4

# Les lentilles disponibles, et comment elles parlent.
dotnet run --project src/CoachingIA.Cli -- lens --lens starcraft2

# Le défi de la semaine, choisi sur vos propres signaux.
dotnet run --project src/CoachingIA.Cli -- defi --lens starcraft2

# Ce que le coach dirait après vos dernières sessions.
dotnet run --project src/CoachingIA.Cli -- moment

# Le bilan de la dernière semaine close, en page web.
dotnet run --project src/CoachingIA.Cli -- bilan --lens starcraft2 --out bilans/

# Idem, avec la réécriture du prompt confiée à Claude.
dotnet run --project src/CoachingIA.Cli -- bilan --juge

# La rétrospective des mois écoulés, en page web.
dotnet run --project src/CoachingIA.Cli -- retro --lens starcraft2 --race zerg --mois 6
```

`probe` est le livrable de la phase 00 : son rapport est **anonyme et
partageable** — noms de champs, comptages, versions, noms d'outils. Jamais le
texte d'un prompt, jamais le contenu d'un fichier. Il sort en code 1 si un champ
dont dépend le parseur a disparu.

### Le bilan du lundi

`bilan` assemble la semaine : ce qui a progressé, trois observations, le défi
qui suit. Il porte toujours sur la **dernière semaine close** — juger une
semaine en cours reviendrait à noter une partie non terminée.

Il produit une **page web autonome** (`bilans/2026-W34.html`) et sa version
Markdown. La page est le format qui compte : c'est là que se lisent côte à côte
le prompt écrit et le prompt qu'il aurait fallu écrire.

L'ordre des sections est fixe et il n'est pas anodin : la réussite d'abord, le
prompt de la semaine, les observations, le défi à la fin. Un bilan qui ouvre sur
les reproches se lit une fois.

#### Le prompt de la semaine

La partie la plus utile. Le bilan retient **le prompt qui a coûté le plus cher**
— reprises, abandon, échecs d'outil — et l'affiche en vis-à-vis de sa réécriture,
avec la grille des sept critères cochés ou manqués et, pour chaque manque, le
geste qui le comble.

Deux niveaux de critique. **Hors ligne** (par défaut) : la structure manquante
est ajoutée en crochets à compléter — moins bon qu'un modèle, assumé, mais le
squelette enseigne déjà la forme. **Avec `--juge`** : `claude -p` rédige la
réécriture en respectant votre registre, sans inventer de détail technique
absent de l'original. Si le CLI manque ou dépasse le délai, la critique hors
ligne reprend la main et le dit.

Un prompt qui n'a rien coûté n'est jamais critiqué, même s'il est pauvre sur le
papier. Reprocher sa forme à une demande qui a marché du premier coup, c'est le
pendant exact de la félicitation de politesse.

#### Les conseils

Chaque observation porte un « **comment franchir** » : un geste précis, pas un
principe. « Terminez chaque prompt par une phrase qui commence par *c'est fini
quand* — si vous n'arrivez pas à la finir, c'est que la tâche n'est pas encore
définie. » Il y en a un par signal, treize au total.

Deux promesses sont tenues par le code. **Aucune observation sans exemple** :
chaque constat cite une tâche réelle, sa date, et la phrase exacte qui explique
le chiffre — une moyenne ne fait changer personne, « mardi, sur la tâche X » si.
Et **aucune félicitation de politesse** : la réussite n'apparaît que si un
signal a réellement progressé au-dessus du bruit *et* franchi sa cible. Sinon le
bilan le dit — un compliment gratuit dévalue tous les autres.

Aucun juge LLM à ce stade : tout se déduit de compteurs et de la grille de
`SignalSpecs`. Le juge viendra là où l'heuristique se trompe de façon mesurée,
pas avant.

Pour en faire un rituel, planifiez `scripts/bilan-hebdo.ps1` le lundi matin.

### La rétrospective

`bilan` répond à « qu'est-ce que je corrige lundi ? ». `retro` répond à l'autre
question, celle qu'une semaine ne peut pas trancher : **est-ce que je progresse
vraiment, ou est-ce que je tourne ?**

La commande rejoue tout l'historique semaine par semaine et range chaque signal
dans l'une de cinq cases : **acquis** (la cible était perdue, elle est tenue
depuis au moins trois semaines mesurées), **en recul**, **en progrès**,
**inchangé**, ou **trop peu de données** — moins de trois semaines mesurées, et
le coach se tait. Chaque trajectoire est dessinée en clair : une courbe, la
cible en pointillés, la valeur de départ et celle d'arrivée.

Deux garde-fous portent tout le reste :

- **Un trou n'est pas un zéro.** Les points ne couvrent que les semaines
  réellement mesurées, et la courbe se coupe dès qu'il en manque deux. Relier
  deux points de part et d'autre d'un mois de congé raconterait une progression
  qui n'a jamais eu lieu. Les périodes sans trace ont leur propre section,
  parce qu'elles expliquent des courbes — pas parce qu'elles sont un reproche.
- **Début et fin se comparent par tiers**, jamais par points isolés : une
  semaine chargée ou creuse ne décide pas d'un verdict à elle seule.

Les **bascules** datent ce qui a changé pour de bon : « franchi la semaine du
13 juillet, tenu depuis ». C'est la seule forme de félicitation que le projet
s'autorise, parce que c'est la seule qui soit vérifiable.

Le tableau **mois par mois** donne le volume, le mélange Haiku/Sonnet/Opus et la
part des signaux de chaque palier qui tiennent leur cible. Une cellule vide veut
dire « pas mesuré ce mois-là », jamais « zéro ».

```
retro --depuis 2026-03-01 --jusqua 2026-08-31   # une fenêtre explicite
retro --mois 6                                  # les six derniers mois
```

### Les bilans ne s'écrasent pas

Le nom canonique — `bilans/2026-W34.html` — reste stable : c'est celui qu'on met
en favori et que les scripts cherchent. Quand une nouvelle génération diffère de
la précédente, l'ancienne part dans `bilans/archives/`, horodatée **à sa propre
date de modification** : on y lit « la version du 20 août », pas « la version
archivée aujourd'hui ». Une régénération à l'identique ne crée rien — relancer
trois fois la même commande ne doit pas noyer l'historique dans le bruit.

### Les indicateurs d'usage

`usage` répond à une question économique. Sur un abonnement au siège, la
capacité non consommée d'une semaine ne se reporte pas : elle est perdue. Une
personne à 50 % de son enveloppe paie plein tarif pour la moitié de l'outil — et
c'est un problème d'accompagnement, pas de facturation.

Par semaine : jetons lus et produits, part de l'enveloppe, taux de cache, jours
actifs, sessions, tâches. Puis la répartition **par famille de modèle** — en part
de jetons *et* en part d'appels, parce que les deux ne racontent pas la même
histoire : beaucoup d'appels Haiku pour peu de jetons, c'est un usage sain du
modèle léger ; peu d'appels Opus pour l'essentiel des jetons, c'est le contraire.
Et la répartition **par type de travail** : orchestration, édition, exécution,
recherche, exploration, conversation.

**Aucune limite n'est codée en dur.** Les plafonds réels ne sont pas publiés et
changent. `--budget` est une valeur de référence que vous renseignez d'après ce
que `/usage` vous montre dans Claude Code. Sans elle, les tendances et les
comparaisons d'une semaine à l'autre restent lisibles — simplement pas les
pourcentages d'enveloppe.

Les alertes visent une décision, pas une curiosité : deux semaines consécutives
sous 50 %, enveloppe presque épuisée, un seul jour actif dans la semaine, cache
effondré, monoculture de modèle, usage divisé par deux d'une semaine à l'autre.
La semaine en cours n'en déclenche jamais aucune — elle est incomplète, et la
comparer à une enveloppe pleine produirait une fausse alerte chaque lundi.

`--out carte.json` écrit une carte hebdomadaire sans aucun texte de prompt :
elle se partage telle quelle avec la personne en charge de l'accompagnement.

### La lentille

Le premier obstacle à l'apprentissage n'est pas la difficulté, c'est
l'abstraction. « Votre fenêtre de contexte sature » est exact et ne provoque
rien ; « vous jouez supply block » dit la même chose à quelqu'un qui a déjà
ressenti la sensation. Une **lentille** est un pack de vocabulaire posé sur le
contenu pédagogique : elle ne change ni les mesures, ni les seuils, ni les
conseils — seulement les mots pour les dire.

Trois lentilles sont livrées dans `lenses/` : `starcraft2`, `echecs` et
`neutre`, qui reste le défaut. En ajouter une ne demande qu'un fichier JSON.

#### Plusieurs scènes par signal

Une image entendue six semaines de suite n'est plus une image. Chaque clé d'un
pack accepte donc **une chaîne ou un tableau** — les packs écrits avant cette
évolution restent valables tels quels — et le pack StarCraft II en compte
aujourd'hui près d'une centaine, réparties sur les quatorze signaux mesurés.

Le tirage est **déterministe et sans état**, et les deux tiennent à une raison
précise :

- *Déterministe*, parce que les bilans sont archivés par comparaison
  d'empreinte. Un tirage au sort ferait apparaître une version « différente » à
  chaque régénération, et l'historique se remplirait de bilans identiques au mot
  près sauf l'image.
- *Sans état*, parce que le coach tourne maintenant sur plusieurs machines. Un
  fichier « scènes déjà vues » divergerait d'un poste à l'autre, et deux
  machines raconteraient deux histoires pour la même semaine.

Le rang dans la rotation se déduit donc de la semaine elle-même, décalé par
signal pour que deux images voisines ne restent pas éternellement voisines. Une
semaine donnée raconte toujours la même chose ; la semaine suivante, chaque
signal descend d'un cran.

```powershell
coachingia lens --lens starcraft2 --race zerg --variantes
```

liste toutes les scènes d'un pack, la scène de la semaine marquée d'une flèche.
C'est la commande de relecture : c'est là qu'on corrige un contresens sur une
unité.

#### Le corpus de faits

Les scènes vivent dans la lentille ; les **faits** vivent à côté, dans
`lenses/corpus/starcraft2.json`. Ce fichier ne contient aucune image : 54 unités
avec ce qu'elles font vraiment, 31 bâtiments, 26 mécaniques, 30 termes de
vernaculaire, et 26 **punitions**.

La table des punitions est la partie qui compte, et c'est la seule qu'aucun wiki
ne donne. Chaque ligne est le squelette d'une scène : *situation → ce qui
manquait → ce qui arrive → ce que ça coûte*. Les statistiques d'unités se
récoltent ; « il n'avait pas scouté, alors le Vaisseau de guerre est arrivé sur
une base sans anti-aérien » vient des parties qu'on a perdues.

```powershell
coachingia corpus                      # ce que contient le fond de connaissance
coachingia corpus --valider            # relire les scènes contre les faits
coachingia corpus --recolter --ecrire  # compléter les chiffres depuis Liquipedia
coachingia corpus --generer verification_present --race zerg
coachingia corpus --relire             # accepter ou jeter les propositions
```

**Le validateur vérifie des affirmations, pas du vocabulaire.** Un contrôle mot
à mot rejetterait la moitié du français sans rien attraper d'utile. Celui-ci ne
s'occupe que de ce qu'une scène affirme et que le corpus peut démentir :

| Règle | Ce qu'elle attrape |
| --- | --- |
| mauvais camp | « tes Marines » dans le pack zerg |
| invisible sans nom | une menace invisible que la scène ne nomme jamais |
| détection mal attribuée | une unité qui ne détecte rien présentée comme la réponse |
| anti-aérien sans air | parler d'anti-aérien sans nommer une unité qui vole |
| supply d'avant-patch | « 17 hatch », « 18 pool » — périmés par le départ à huit ouvriers |
| unité inconnue | une unité inventée, même quand la phrase sonne bien |
| sans ancrage | une scène qui pourrait être écrite pour n'importe quel univers |

Les cinq premières lèvent une erreur et font sortir la commande en échec ; les
deux dernières lèvent un doute et se comptent en proportion. Un outil qui crie
pour des questions de goût finit désactivé, et un outil désactivé ne protège de
rien.

**La récolte respecte les règles de l'API par construction** : une requête
toutes les deux secondes, un User-Agent nominatif avec contact, gzip, et surtout
la mise en cache — une unité déjà renseignée n'est jamais redemandée. Ce n'est
pas une optimisation, c'est une condition d'utilisation. L'attribution CC BY-SA
3.0 accompagne le corpus.

**La génération est payée une fois, pas chaque semaine.** `--generer` fait écrire
trois scènes par `claude -p`, à partir du corpus, d'une punition à mettre en
scène et des scènes déjà écrites pour ne pas les répéter. Les propositions
passent le validateur, puis atterrissent dans `lenses/propositions/` — un sas que
le coach ne lit pas. Rien n'entre dans un bilan avant `--relire`. C'est la seule
garantie qui tienne : un validateur attrape un contresens sur une unité, il
n'attrape pas une scène qui sonne faux.

#### Le serveur MCP

Le coach n'en a pas besoin : il lit le corpus directement. Le serveur sert les
**autres** sessions — quand vous codez dans VS Code et voulez une analogie juste,
ou qu'une skill a besoin du vocabulaire, sans être dans le dossier du projet.

```powershell
claude mcp add coachingia-corpus -- "%LOCALAPPDATA%\CoachingIA\coachingia-mcp.exe"
```

Quatre outils, et **des faits seulement** : `chercher_unite`, `chercher_terme`,
`punition_pour`, `patch`. Les scènes de la lentille restent dehors — le coach les
fait tourner sans répétition, les exposer ailleurs les userait plus vite.

**Pourquoi pas un MCP qui interroge Liquipedia en direct.** Leurs conditions
imposent de mettre en cache et de ne pas redemander la même donnée ; un serveur
qu'on interroge librement fait exactement l'inverse, et finit par un bannissement
d'IP. La récolte reste donc un geste explicite (`corpus --recolter`), qui range
le résultat une fois pour toutes. Le serveur, lui, **n'ouvre aucune connexion** :
il lit des fichiers locaux, ce qui le rend aussi instantané et utilisable hors
ligne.

**Écrit à la main, sans SDK** : JSON-RPC 2.0, un message par ligne sur stdio,
environ deux cents lignes. Le projet ne dépend d'aucun paquet, ce qui lui permet
de se compiler et de s'éprouver partout — y compris là où le registre est fermé.
`McpServer.Handle` est une fonction pure : une requête entre, une réponse sort.
C'est ce qui rend le protocole vérifiable sans lancer de processus, et le banc
d'essai couvre la poignée de main, le silence dû aux notifications, l'invariant
« une ligne par message » et le fait qu'un outil qui échoue le rapporte sans
tuer la session.

#### Le pack est daté

Un pack porte la version du jeu à laquelle il se réfère (`"patch": "5.0.16"`),
et le coach l'affiche. Ce n'est pas de la coquetterie : le patch 5.0.16 ramène
le départ **de douze à huit ouvriers**, ce qui périme d'un coup toute ouverture
citée au supply près. Un banc d'essai refuse d'ailleurs les formulations
d'avant-patch, pour que l'erreur ne puisse pas revenir par distraction.

Une lentille peut aussi se décliner **par camp**. `--race zerg` raconte les
scènes de votre côté de la carte, avec vos mécaniques et votre bête noire en
face : le supply block devient une histoire d'injects perdus, l'absence de test
devient deux Dark Templars dans la ligne de drones. Toute clé absente du pack de
race retombe sur la lentille, qui retombe sur le neutre — un camp n'a jamais
besoin d'être complet pour être utile. `skills/coach-starcraft2.md` fixe le
registre : des scènes vécues, jamais du name-dropping d'unités.

**Ni points, ni badges, ni ligues.** La gamification par le score produit une
motivation extrinsèque qui se substitue à l'envie de bien faire, et elle est
gameable — ce qui contredit la règle anti-Goodhart du projet. Ce qui doit
motiver est déjà mesuré ; la lentille rend seulement parlant.

Trois règles sont tenues par le code :

1. **Le fait passe devant.** Une phrase lentillée s'écrit « fait — image »,
   jamais l'inverse. Retirez la lentille, la phrase reste vraie et complète.
2. **La lentille ne mesure rien.** Aucun signal, aucun seuil, aucune
   progression ne dépend d'elle.
3. **La lentille neutre est complète.** Toute clé absente retombe sur elle. Un
   pack partiel dégrade le style, jamais le fond — et un pack malformé ne peut
   pas prendre la place du neutre.

Elle s'applique aux skills de palier, aux défis, au moment opportun, aux
observations du bilan et aux trajectoires de la rétrospective. Une règle
supplémentaire y est tenue par le code : **l'image ne s'affiche que tant que le
défaut existe**. Une scène de signal raconte toujours une défaite ; la coller
sous un acquis reviendrait à annoncer la victoire et à raconter la défaite juste
en dessous.

### La vue d'équipe

`team` lit l'**export de dépense** de votre organisation : Réglages → Analytics
→ « Combien Claude nous coûte » → Exporter. Une ligne par personne et par
modèle, avec le produit (Chat, Claude Code, Cowork), le nombre de requêtes, les
jetons d'entrée et de sortie, et la dépense estimée.

C'est la seule source qui couvre toute l'équipe sur un plan Team. Deux limites à
assumer : l'export est manuel — l'API d'analytics est réservée à Enterprise — et
les données ont un jour de retard. En échange, elle ne demande rien à installer
sur les postes, et elle voit aussi bien Chat que Claude Code ou Cowork.

Les en-têtes du CSV ne sont pas contractuels : la correspondance se fait par
mots-clés. Si le fichier n'est pas celui attendu, la commande le dit au lieu de
produire des chiffres faux.

### Rejouer l'historique vers Phoenix

```powershell
curl -X POST http://localhost:8080/ingest              # tout l'historique
curl -X POST "http://localhost:8080/ingest?days=14"    # les 14 derniers jours
```

Les spans partent **aux dates d'origine**, pas à celle du rejeu.

## Installer, et travailler sur plusieurs machines

Le coach lit des fichiers locaux et n'envoie rien nulle part : « installer »
veut donc dire poser un exécutable et le laisser lire `~/.claude/projects`.
Il n'y a ni compte, ni service, ni base à synchroniser.

### Sur la machine où sont les sources

```powershell
installeur\Installer-CoachingIA.bat      # double-clic, ou :
powershell -ExecutionPolicy Bypass -File installeur\install.ps1 -Demarrer
```

Le script publie un exécutable autonome, l'installe dans
`%LOCALAPPDATA%\CoachingIA`, pose les lentilles à côté, ajoute le dossier au
PATH et crée un raccourci « CoachingIA » sur le Bureau. **Aucun droit
administrateur** n'est demandé, et désinstaller revient à supprimer le dossier.
Options utiles : `-Dest`, `-Sorties`, `-Port`, `-SansRaccourci`.

### Sur une autre machine

Elle n'a pas forcément le SDK .NET, et ce n'est pas à elle de compiler. Depuis
la machine de développement :

```powershell
installeur\package.ps1
# → CoachingIA-portable-win-x64-AAAAMMJJ.zip  (~70 Mo, runtime embarqué)
```

Le paquet ne se fabrique pas si le banc d'essai échoue — on n'emporte pas un
code dont on ne sait rien. Sur le PC d'arrivée : décompresser, double-cliquer
`Installer-CoachingIA.bat`. Il n'y a rien d'autre à installer, pas même .NET.

**Ce qui ne voyage pas :** les transcripts. Chaque machine coache ce qui s'y est
passé. C'est une propriété, pas une limite — un bilan qui mélangerait deux postes
raconterait une semaine qui n'a jamais eu lieu. Pour regrouper malgré tout,
copiez les `.jsonl` d'un poste vers l'autre et visez le dossier avec `--root`.

### La console locale

```powershell
coachingia web                 # http://127.0.0.1:5099, ouvert automatiquement
coachingia web --port 5100
```

Une page sommaire, les mêmes commandes derrière des boutons : réglages en haut
(dossier des transcripts, dossier de sortie, lentille, camp, semaine, mois),
boutons au milieu, sortie du terminal en bas, et la liste des pages produites
avec un lien vers chacune. C'est la réponse à un problème réel : personne ne
retient huit commandes et leurs options pour un rituel hebdomadaire.

Trois précautions, parce qu'un serveur qui exécute des commandes le mérite :

1. **Écoute sur `127.0.0.1` seulement.** Rien n'est joignable depuis le réseau.
2. **Liste blanche.** Seules les commandes et options connues passent, et les
   arguments partent en liste — il n'y a pas de ligne de commande à échapper,
   donc rien à y injecter. Les chemins servis sont réduits à un nom de fichier
   recollé au dossier de sortie : on ne remonte pas ailleurs sur le disque.
3. **Jeton de session**, tiré au démarrage et vérifié à chaque appel : une page
   ouverte ailleurs dans le navigateur ne peut pas déclencher d'exécution.

## Démarrage

```powershell
# 1. Phoenix
docker compose -f docker/docker-compose.yml up -d
#    → http://localhost:6006

# 2. Le harnais
dotnet run --project src/CoachingIA.Harness
#    → http://localhost:8080/health

# 3. Vérifier le tuyau sans toucher à Claude Code
pwsh scripts/smoke-test.ps1

# 4. Les tests
dotnet run --project tests/CoachingIA.Harness.Tests
```

## Brancher vos vraies sessions

Deux voies, au choix.

**Rapide** — coller le contenu de `plugin/coachingia/hooks/hooks.json` dans le
bloc `hooks` de `%USERPROFILE%\.claude\settings.json`. Effet immédiat sur toutes
vos sessions, VS Code compris.

**Propre** — installer le dossier `plugin/coachingia` comme plugin. C'est la voie
qui servira aussi pour Cowork, et celle qui portera les skills de palier. Dans
VS Code : `/plugins`.

Vérifiez ensuite avec `/hooks`, qui liste ce qui est réellement chargé.

## Réglages

`src/CoachingIA.Harness/appsettings.json` :

| Clé | Rôle |
|---|---|
| `OtlpEndpoint` | Collecteur Phoenix. `4317` en gRPC, `6006` en HTTP. |
| `ProjectName` | Projet Phoenix. En gRPC, seul l'attribut de ressource compte. |
| `LearnerId` | Constante tant qu'il n'y a qu'un apprenant ; le schéma est déjà multi. |
| `CaptureContent` | À `false`, plus aucun texte de prompt ni sortie d'outil ne quitte le processus. Seule la structure part. |
| `MaxValueChars` | Troncature des valeurs textuelles. |
| `TranscriptRoot` | Dossier des transcripts. Vide = l'emplacement par défaut de Claude Code. |
| `ContextWindow` | Fenêtre du modèle, référence de `context_pressure`. Un mauvais réglage donne des taux au-dessus de 100 % — le signal le dit dans son evidence au lieu de le taire. |
| `WeeklyTokenBudget` | Enveloppe hebdomadaire de référence, en jetons lus. 0 = pas de référence, lecture en tendance. |
| `Lens` | Lentille de vocabulaire : `neutre` (défaut), `starcraft2`, `echecs`. |

## Ce qui n'est pas encore là

Volontairement absent : le juge LLM, le calcul des scores de palier, le serveur
MCP, le contenu des skills pédagogiques, la persistance SQLite, la boucle
d'auto-apprentissage. Voir la roadmap dans le document d'architecture.

Le **coût** ne se lit pas dans les transcripts : ils donnent les jetons, pas le
tarif. Il vient de l'export de dépense de l'organisation, qui porte déjà le
montant estimé — plutôt qu'une grille de prix recopiée à la main, qui serait
fausse au premier changement.

### La segmentation en tâches est un pari

Le découpage repose sur une heuristique lexicale et temporelle : un prompt qui
ouvre par « non », « en fait », « refais » répare la tâche précédente ; un prompt
court et référentiel la continue ; un long silence en ouvre une nouvelle. Chaque
décision est journalisée — `segment` les affiche — précisément pour être relue et
corrigée sur vos vraies sessions. Attendez-vous à devoir l'ajuster.

## Structure

```
src/CoachingIA.Harness.Core/   logique pure, zéro dépendance externe
  OpenInference.cs             constantes d'attributs
  HookEvent.cs                 charge utile des hooks + options
  SessionRegistry.cs           spans encore ouverts, entre deux requêtes HTTP
  SpanFactory.cs               hook → span (temps réel)
  Transcripts/
    TranscriptRecord.cs        une ligne JSONL, tolérante à l'inconnu
    TranscriptReader.cs        lecture en flux, lignes illisibles comptées
    ConversationModel.cs       sessions, tours, appels d'outils
    SessionBuilder.cs          lignes → conversation
    TaskSegmenter.cs           tours → tâches, avec justification
    SignalExtractor.cs         les signaux calculables sans juge LLM
    UsageAnalyzer.cs           jetons par semaine, modèles, types de travail, alertes
    SpendReportReader.cs       l'export de dépense de l'organisation
  Coaching/
    Lens.cs                    les lentilles : chargement, repli, « fait d'abord »
    LensVariants.cs            plusieurs scènes par clé, et le tirage sans état
    Corpus.cs                  les faits : unités, bâtiments, mécaniques, punitions
    SceneValidator.cs          le garde-fou : des affirmations, pas du vocabulaire
    SceneWriter.cs             génération en cache, sas de relecture, insertion
    LiquipediaHarvester.cs     la récolte des chiffres, aux règles de l'API
  Mcp/
    McpServer.cs               JSON-RPC 2.0 sur stdio, écrit à la main
    CorpusTools.cs             les quatre outils : faits seulement
    ChallengeLibrary.cs        les défis hebdomadaires et le moment opportun
    SignalSpec.cs              cibles et sens de lecture de chaque signal
    WeeklyReview.cs            l'assemblage du bilan
    ReviewRenderer.cs          le bilan en Markdown
    HtmlReviewRenderer.cs      le bilan en page web autonome
    Retrospective.cs           les mois écoulés : trajectoires, bascules, silences
    HtmlRetrospectiveRenderer.cs  la rétrospective en page web
    ReviewArchive.cs           écrire un bilan sans perdre le précédent
    PromptRubric.cs            les sept critères d'un prompt qui n'a pas besoin d'être repris
    PromptCritic.cs            la critique : hors ligne, ou rédigée par claude -p
lenses/                        les packs de vocabulaire, camps compris (JSON, sans code)
    corpus/                    les faits vérifiables de chaque univers
    propositions/              le sas des scènes générées, non relues
skills/                        le registre de voix des lentilles
    TranscriptIngestor.cs      conversation → spans, aux dates d'origine
    TranscriptProbe.cs         la sonde de format, anonyme
src/CoachingIA.Cli/            probe / analyze / segment / usage / team / lens / defi / moment / bilan / retro / web / corpus
    WebConsole.cs              la console locale : liste blanche, jeton, 127.0.0.1
installeur/                    install.ps1, package.ps1, Installer-CoachingIA.bat
src/CoachingIA.Mcp/            le serveur MCP du corpus (coachingia-mcp)
src/CoachingIA.Harness/        l'hôte web, les hooks, /ingest
tests/                         banc d'essai sans dépendance (ActivityListener)
plugin/coachingia/             le plugin Claude
docker/                        Phoenix
scripts/                       rejeu d'une session synthétique, rituel du lundi
```

Le noyau ne référence aucun paquet NuGet : il se compile et se teste hors ligne.
Seul l'hôte web dépend d'OpenTelemetry.

## Notes

**Versions NuGet flottantes.** `CoachingIA.Harness.csproj` déclare `Version="1.*"`
pour les deux paquets OpenTelemetry. Après le premier `dotnet restore`, relevez
les versions résolues et épinglez-les.

**Un hook bloque le tour tant qu'il n'a pas répondu.** Toute la surface HTTP suit
donc une règle unique : répondre vite, répondre 200, ne jamais faire échouer une
session parce que le coach a un problème. Une trace perdue est un incident
mineur ; un tour bloqué ne l'est pas. Si le harnais est éteint, les hooks
échouent en silence et vos sessions continuent normalement.

**Vie privée.** Tout reste sur le poste : transcripts lus localement, Phoenix en
Docker local, spans en OTLP vers `localhost`. `CaptureContent: false` coupe même
l'export du texte. Les rapports `probe` et `usage --out` sont conçus pour être
partagés : ils ne contiennent ni prompt ni contenu de fichier.
