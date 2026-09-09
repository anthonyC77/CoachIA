# Porte de sortie du codeur — SubagentStop (déclarée dans settings.json).
#
# Refuse la fin de tâche tant qu'une vérification NOUVELLE est rouge. Une seule
# exécution par passage, pas vingt.
#
# Note d'adaptation, et elle est essentielle. Le harnais de CoachingIA sort en
# code 1 dès qu'une vérification échoue, et trois échouent déjà aujourd'hui pour
# une raison sans rapport avec le code : les suites de lentille visent des
# variantes que VariantPicker fait tourner par semaine, et cette semaine-ci
# elles tombent à côté. Une porte qui bloquerait sur « code de sortie non nul »
# bloquerait donc *toujours*, et finirait désactivée dans la journée.
#
# Elle compare donc à un état accepté — .claude/tests-attendus.txt — exactement
# comme la campagne d'évaluation compare à evals/verdict.json. Seule une
# vérification qui n'y figure pas fait échouer. Le jour où les trois lentilles
# sont réparées, on retire les lignes du fichier et la porte se resserre d'elle-
# même.
#
# Sortie 0 = l'agent peut s'arrêter, 2 = il continue (stderr lui est renvoyé).

try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding $false } catch { }
$raw = [Console]::In.ReadToEnd()
try { $h = $raw | ConvertFrom-Json -ErrorAction Stop } catch { exit 0 }

# Anti-boucle : on ne re-bloque pas un second arrêt.
if ($h.stop_hook_active -eq $true) { exit 0 }

$agent = [string]$h.agent_type
if (-not $agent) { $agent = [string]$h.agent_name }
if ($agent -and $agent -ne 'codeur') { exit 0 }

$racine = $env:CLAUDE_PROJECT_DIR
if (-not $racine) { exit 0 }
Set-Location $racine

# Rien de modifié : rien à vérifier.
$changes = @(& git diff --name-only HEAD) + @(& git ls-files --others --exclude-standard)
if (-not ($changes | Where-Object { $_ -like '*.cs' })) { exit 0 }

$sortie = & dotnet run --project tests/CoachingIA.Harness.Tests | Out-String
$code = $LASTEXITCODE

if ($code -eq 0) { exit 0 }

# Les libellés en échec : les lignes « - <phrase> » du bilan de fin.
$rouges = @()
foreach ($ligne in ($sortie -split "`r?`n")) {
    if ($ligne -match '^\s+- (.+)$') { $rouges += $Matches[1].Trim() }
}

$attendus = @()
$fichier = Join-Path $racine '.claude/tests-attendus.txt'
if (Test-Path $fichier) {
    $attendus = @(Get-Content $fichier -Encoding UTF8 |
        Where-Object { $_.Trim() -and -not $_.StartsWith('#') } |
        ForEach-Object { $_.Trim() })
}

$nouveaux = @($rouges | Where-Object { $attendus -notcontains $_ })

if ($nouveaux.Count -eq 0) {
    # Seuls des échecs déjà connus : on laisse passer, sans faire semblant.
    if ($rouges.Count -gt 0) {
        [Console]::Error.WriteLine("($($rouges.Count) vérification(s) rouge(s), toutes déjà connues et acceptées.)")
    }
    exit 0
}

[Console]::Error.WriteLine("TESTS ROUGES — $($nouveaux.Count) vérification(s) que rien n'expliquait avant ta tâche :")
foreach ($n in $nouveaux) { [Console]::Error.WriteLine("  - $n") }
[Console]::Error.WriteLine("")
[Console]::Error.WriteLine("Répare, ou termine par « TEST_CONTESTÉ: <nom> — <raison> » si le test est faux.")
exit 2
