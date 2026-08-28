# Le rituel du lundi. À planifier dans le Planificateur de tâches Windows,
# tous les lundis matin :
#
#   pwsh -File scripts\bilan-hebdo.ps1
#
# Le bilan porte toujours sur la dernière semaine CLOSE. Lancé un lundi, il
# commente la semaine qui vient de s'achever — jamais celle qui commence.
param(
    [string]$Sortie = "bilans",
    [string]$Lentille = "neutre",
    [switch]$Ouvrir
)

$racine = Split-Path -Parent $PSScriptRoot
Push-Location $racine
try {
    New-Item -ItemType Directory -Force -Path $Sortie | Out-Null
    dotnet run --project src/CoachingIA.Cli -- bilan --lens $Lentille --out "$Sortie/"
    if ($LASTEXITCODE -ne 0) { Write-Host "Le bilan a échoué." -ForegroundColor Red; exit 1 }

    $dernier = Get-ChildItem $Sortie -Filter *.md | Sort-Object Name | Select-Object -Last 1
    if ($dernier -and $Ouvrir) { code $dernier.FullName }
    if ($dernier) { Write-Host "`nÀ lire : $($dernier.FullName)" }
}
finally { Pop-Location }
