<#
.SYNOPSIS
    Fabrique le paquet portable : un zip qui s'installe sur un PC sans SDK .NET.

.DESCRIPTION
    À lancer depuis la machine de développement, celle qui a le SDK. Le zip
    produit contient l'exécutable autonome, les lentilles et l'installeur. Sur
    l'autre PC : décompresser, double-cliquer Installer-CoachingIA.bat. Rien
    d'autre à installer, pas même .NET.

    L'exe est « self-contained » : il embarque son runtime. Il pèse plus lourd
    (~70 Mo) qu'une version qui suppose .NET déjà présent, et c'est exactement
    ce qu'on veut pour une machine dont on ne sait rien.

.PARAMETER Sortie
    Où déposer le zip. Par défaut, à côté du dépôt.

.EXAMPLE
    .\package.ps1
    .\package.ps1 -Sortie D:\Partage
#>
[CmdletBinding()]
param(
    [string] $Sortie
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Etape($t) { Write-Host "`n  $t" -ForegroundColor Cyan }
function Bon($t)   { Write-Host "    $t" -ForegroundColor Green }
function Halte($t) { Write-Host "`n  $t`n" -ForegroundColor Red; exit 1 }

$ici = Split-Path -Parent $MyInvocation.MyCommand.Path
$racine = Split-Path -Parent $ici
if (-not $Sortie) { $Sortie = Split-Path -Parent $racine }

if (-not (Test-Path (Join-Path $racine 'CoachingIA.slnx'))) {
    Halte 'Ce script doit vivre dans installeur\, à l''intérieur du dépôt.'
}
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Halte 'Le SDK .NET est nécessaire pour fabriquer le paquet : winget install Microsoft.DotNet.SDK.10'
}

Write-Host ""
Write-Host "  CoachingIA — paquet portable" -ForegroundColor White
Write-Host "  ────────────────────────────" -ForegroundColor DarkGray

# On ne met pas dans un paquet un code dont on ne sait pas s'il passe ses tests.
Etape 'Banc d''essai'
& dotnet run --project (Join-Path $racine 'tests\CoachingIA.Harness.Tests') --nologo -v quiet | Select-Object -Last 3
if ($LASTEXITCODE -ne 0) { Halte 'Des vérifications échouent : le paquet n''est pas fabriqué.' }
Bon 'Tout est vert.'

Etape 'Publication autonome win-x64'
$travail = Join-Path ([System.IO.Path]::GetTempPath()) ("coachingia-pkg-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $travail | Out-Null

foreach ($projet in @('src\CoachingIA.Cli', 'src\CoachingIA.Mcp')) {
    & dotnet publish (Join-Path $racine $projet) `
        -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -o $travail --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { Halte "La publication de $projet a échoué." }
}

# Le publish laisse des .pdb : inutiles dans un paquet qu'on transporte.
Get-ChildItem $travail -Filter *.pdb | Remove-Item -Force
$poids = [math]::Round((Get-Item (Join-Path $travail 'coachingia.exe')).Length / 1MB, 1)
Bon "coachingia.exe — $poids Mo, runtime embarqué."

Etape 'Assemblage'
New-Item -ItemType Directory -Force -Path (Join-Path $travail 'lenses') | Out-Null
Copy-Item (Join-Path $racine 'lenses\*.json') (Join-Path $travail 'lenses') -Force

# Le corpus voyage avec les lentilles : sans lui, le serveur MCP démarre et ne
# sait rien, ce qui est pire qu'un serveur absent.
$corpus = Join-Path $racine 'lenses\corpus'
if (Test-Path $corpus) {
    New-Item -ItemType Directory -Force -Path (Join-Path $travail 'lenses\corpus') | Out-Null
    Copy-Item (Join-Path $corpus '*.json') (Join-Path $travail 'lenses\corpus') -Force
}
Copy-Item (Join-Path $ici 'install.ps1') $travail -Force
Copy-Item (Join-Path $ici 'Installer-CoachingIA.bat') $travail -Force

@"
CoachingIA — paquet portable
============================

Sur la machine d'arrivée :

  1. Décompressez ce dossier où vous voulez.
  2. Double-cliquez Installer-CoachingIA.bat.
  3. La console s'ouvre dans le navigateur.

Rien d'autre à installer : l'exécutable embarque son runtime .NET.
L'installation se fait dans %LOCALAPPDATA%\CoachingIA, sans droits
administrateur. Pour désinstaller, supprimez ce dossier.

Le coach lit les transcripts de Claude Code, par défaut dans
%USERPROFILE%\.claude\projects. Aucun texte de prompt ne quitte la machine.

Produit le $(Get-Date -Format 'dd/MM/yyyy à HH:mm').
"@ | Set-Content -Path (Join-Path $travail 'LISEZ-MOI.txt') -Encoding UTF8

$nom = "CoachingIA-portable-win-x64-$(Get-Date -Format 'yyyyMMdd').zip"
$zip = Join-Path $Sortie $nom
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $travail '*') -DestinationPath $zip
Remove-Item $travail -Recurse -Force

$taille = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host ""
Write-Host "  Paquet prêt." -ForegroundColor Green
Write-Host "    $zip  ($taille Mo)" -ForegroundColor White
Write-Host ""
Write-Host "  Copiez-le sur l'autre PC, décompressez, double-cliquez Installer-CoachingIA.bat."
Write-Host ""
