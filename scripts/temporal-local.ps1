# Lance le serveur de développement Temporal en local, lié à 127.0.0.1
# seulement, avec une base SQLite persistante (jamais en mémoire).
#
# Sans --db-filename, `temporal server start-dev` garde tout en mémoire et
# perd l'historique des workflows à l'arrêt du process : c'est précisément
# ce que ce script interdit, puisque le bilan hebdomadaire doit pouvoir
# reprendre après une panne.
#
# Usage :
#
#   powershell -NoProfile -File scripts\temporal-local.ps1
#   powershell -NoProfile -File scripts\temporal-local.ps1 -Afficher
#
# -Afficher n'imprime que la ligne de commande qui serait lancée, sans
# démarrer le serveur (utile pour la vérification automatisée).
#
# Le CLI `temporal` n'est pas forcément dans le PATH d'une session
# `-NoProfile` même après une installation winget : ce script le cherche
# d'abord via Get-Command, puis directement dans le dossier WinGet.
param(
    [string]$DossierDonnees = (Join-Path $env:LOCALAPPDATA 'CoachingIA\temporal'),
    [switch]$Afficher
)

function Trouver-Temporal {
    $commande = Get-Command temporal -ErrorAction SilentlyContinue
    if ($commande) { return $commande.Source }

    $dossierWinGet = Join-Path $env:LOCALAPPDATA 'Microsoft\WinGet\Packages'
    $candidat = Get-ChildItem -Path $dossierWinGet -Filter 'temporal.exe' -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.DirectoryName -like '*\Temporal.TemporalCLI_*' } |
        Select-Object -First 1
    if ($candidat) { return $candidat.FullName }

    return $null
}

$executable = Trouver-Temporal
if (-not $executable) {
    Write-Host "Impossible de trouver l'exécutable temporal.exe." -ForegroundColor Red
    Write-Host "Installer le CLI Temporal avec : winget install Temporal.TemporalCLI"
    Write-Host "Puis relancer ce script (redémarrer le terminal si le PATH n'est pas à jour)."
    exit 1
}

New-Item -ItemType Directory -Force -Path $DossierDonnees | Out-Null

$fichierBase = Join-Path $DossierDonnees 'temporal.db'
$arguments = @(
    'server', 'start-dev',
    '--ip', '127.0.0.1',
    '--db-filename', $fichierBase
)

if ($Afficher) {
    Write-Host "$executable $($arguments -join ' ')"
    exit 0
}

Write-Host "Démarrage du serveur Temporal local (base : $fichierBase)..."
& $executable @arguments
if ($LASTEXITCODE -ne 0) {
    Write-Host "Le serveur Temporal s'est arrêté en erreur." -ForegroundColor Red
    exit 1
}
