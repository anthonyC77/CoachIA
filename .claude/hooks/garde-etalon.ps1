# Garde-fou de l'étalon — PreToolUse sur Edit|Write|MultiEdit|NotebookEdit|Bash.
#
# Protège ce qui sert de référence à la brique d'évaluation : le jeu d'épreuves
# versionné (evals/cas/) et l'état approuvé (evals/verdict.json). On ne les
# modifie pas pour faire passer une campagne.
#
# Écrit en PowerShell et non en sh : « jq » n'est pas installé sur ce poste, et
# la version sh d'origine sortait en code 0 quand il manquait — elle autorisait
# tout, en silence. Une action dangereuse empêchée uniquement par le prompt
# n'est pas empêchée ; une garde qui échoue en autorisant ne l'est pas non plus.
#
# Sortie 0 = autorisé, 2 = bloqué (stderr renvoyé à l'agent).

try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding $false } catch { }
$raw = [Console]::In.ReadToEnd()
try { $h = $raw | ConvertFrom-Json -ErrorAction Stop }
catch { [Console]::Error.WriteLine("garde-etalon : entrée illisible, appel laissé passer."); exit 0 }

$protege = '^evals/(cas/|verdict\.json)'
$sep = [string][char]92

function Relatif([string]$chemin) {
    if ([string]::IsNullOrWhiteSpace($chemin)) { return '' }
    $c = $chemin.Replace($sep, '/')
    $racine = $env:CLAUDE_PROJECT_DIR
    if ($racine) {
        $racine = $racine.Replace($sep, '/').TrimEnd('/')
        if ($c.StartsWith($racine, [System.StringComparison]::OrdinalIgnoreCase)) {
            $c = $c.Substring($racine.Length).TrimStart('/')
        }
    }
    return $c
}

$outil = [string]$h.tool_name

if ($outil -match '^(Edit|Write|MultiEdit|NotebookEdit)$') {
    $f = Relatif ([string]$h.tool_input.file_path)
    if (-not $f) { $f = Relatif ([string]$h.tool_input.notebook_path) }
    if ($f -match $protege) {
        [Console]::Error.WriteLine("BLOQUÉ : $f est l'étalon de la brique d'évaluation.")
        [Console]::Error.WriteLine("Le jeu d'épreuves et l'état approuvé ne se modifient pas pour faire passer une campagne.")
        [Console]::Error.WriteLine("Si un écart est un vrai progrès, il s'approuve depuis un terminal humain.")
        exit 2
    }
}

if ($outil -eq 'Bash' -or $outil -eq 'PowerShell') {
    $cmd = [string]$h.tool_input.command

    # Réapprouver l'état de référence est une décision humaine, jamais un geste
    # d'agent. C'est l'équivalent exact d'un --write-baseline.
    if ($cmd -match 'COACHINGIA_APPROUVER_EVALS') {
        [Console]::Error.WriteLine("BLOQUÉ : COACHINGIA_APPROUVER_EVALS réécrit evals/verdict.json.")
        [Console]::Error.WriteLine("Un nouveau point de référence se pose depuis un terminal humain, pas depuis un agent.")
        exit 2
    }

    $ecrit = '(sed\s+-i|tee\s|>>|>|cp\s|mv\s|rm\s|truncate|python3?\s+-c|perl\s+-|Set-Content|Out-File|Add-Content)'
    if ($cmd -match 'evals/(cas/|verdict\.json)' -and $cmd -match $ecrit) {
        [Console]::Error.WriteLine("BLOQUÉ : commande qui écrit dans l'étalon : $cmd")
        exit 2
    }
}

exit 0
