# Garde-fou de périmètre — PreToolUse, déclaré dans le frontmatter d'un agent.
#
#   garde-perimetre.ps1 tests-only  -> testeur : n'écrit que dans tests/
#   garde-perimetre.ps1 no-tests    -> codeur  : n'écrit ni dans tests/ ni dans evals/
#
# Note d'adaptation. La version d'origine interdisait aussi au testeur de *lire*
# l'implémentation, en s'appuyant sur une couche de contrats publics
# (src/*/Contracts, I*.cs). CoachingIA n'a pas cette couche : les suites de
# tests manipulent directement WeeklyReviewBuilder, LensCatalog, TaskSegmenter.
# Interdire la lecture ici ne produirait que des « SPEC_INCOMPLÈTE » en boucle.
# On garde donc la contrainte d'écriture, qui transfère, et on abandonne celle
# de lecture, qui ne transfère pas.
#
# Sortie 0 = autorisé, 2 = bloqué (stderr renvoyé à l'agent).

param([string]$Mode)

try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding $false } catch { }
$raw = [Console]::In.ReadToEnd()
try { $h = $raw | ConvertFrom-Json -ErrorAction Stop }
catch { [Console]::Error.WriteLine("garde-perimetre : entrée illisible, appel laissé passer."); exit 0 }

$outil = [string]$h.tool_name
if ($outil -notmatch '^(Edit|Write|MultiEdit|NotebookEdit)$') { exit 0 }

$chemin = [string]$h.tool_input.file_path
if (-not $chemin) { $chemin = [string]$h.tool_input.notebook_path }
if ([string]::IsNullOrWhiteSpace($chemin)) { exit 0 }

$sep = [string][char]92
$f = $chemin.Replace($sep, '/')
$racine = $env:CLAUDE_PROJECT_DIR
if ($racine) {
    $racine = $racine.Replace($sep, '/').TrimEnd('/')
    if ($f.StartsWith($racine, [System.StringComparison]::OrdinalIgnoreCase)) {
        $f = $f.Substring($racine.Length).TrimStart('/')
    }
}

$estTest = $f -match '^tests/'
$estEtalon = $f -match '^evals/'

switch ($Mode) {
    'tests-only' {
        if (-not $estTest) {
            [Console]::Error.WriteLine("BLOQUÉ (testeur) : écriture hors de tests/ interdite — $f")
            [Console]::Error.WriteLine("Tu poses l'oracle. Le code de production, c'est le travail du codeur.")
            exit 2
        }
    }
    'no-tests' {
        if ($estTest) {
            [Console]::Error.WriteLine("BLOQUÉ (codeur) : $f est un test, tu ne peux pas le modifier.")
            [Console]::Error.WriteLine("Si un test te semble faux, termine par « TEST_CONTESTÉ: <nom> — <raison> ». C'est un résultat valide.")
            exit 2
        }
        if ($estEtalon) {
            [Console]::Error.WriteLine("BLOQUÉ (codeur) : $f est l'étalon d'évaluation, hors de ton périmètre.")
            exit 2
        }
    }
}

exit 0
