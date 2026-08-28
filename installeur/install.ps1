<#
.SYNOPSIS
    Installe CoachingIA pour l'utilisateur courant, sans droits administrateur.

.DESCRIPTION
    Deux situations, un seul script :

      * Lancé depuis les sources (le dépôt), il publie un exe autonome puis
        l'installe.
      * Lancé depuis le paquet portable (un dossier contenant déjà
        coachingia.exe), il se contente d'installer — aucun SDK requis.

    Tout se pose dans %LOCALAPPDATA%\CoachingIA : pas de droits admin, pas de
    Program Files, rien à désinstaller qu'un dossier à supprimer.

.PARAMETER Dest
    Où installer. Par défaut %LOCALAPPDATA%\CoachingIA.

.PARAMETER Sorties
    Où atterrissent les bilans et rétrospectives.
    Par défaut Documents\CoachingIA.

.PARAMETER Port
    Le port de la console locale. 5099 par défaut.

.PARAMETER SansRaccourci
    N'ajoute ni raccourci Bureau ni entrée au menu Démarrer.

.PARAMETER Demarrer
    Ouvre la console dans la foulée.

.EXAMPLE
    .\install.ps1
.EXAMPLE
    .\install.ps1 -Dest D:\Outils\CoachingIA -Port 5100 -Demarrer
#>
[CmdletBinding()]
param(
    [string] $Dest = (Join-Path $env:LOCALAPPDATA 'CoachingIA'),
    [string] $Sorties = (Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'CoachingIA'),
    [int]    $Port = 5099,
    [switch] $SansRaccourci,
    [switch] $Demarrer
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Etape($texte) { Write-Host "`n  $texte" -ForegroundColor Cyan }
function Bon($texte)   { Write-Host "    $texte" -ForegroundColor Green }
function Note($texte)  { Write-Host "    $texte" -ForegroundColor DarkGray }
function Halte($texte) { Write-Host "`n  $texte`n" -ForegroundColor Red; exit 1 }

Write-Host ""
Write-Host "  CoachingIA — installation" -ForegroundColor White
Write-Host "  ─────────────────────────" -ForegroundColor DarkGray

$ici = Split-Path -Parent $MyInvocation.MyCommand.Path
$racine = Split-Path -Parent $ici          # le dépôt, quand on est dans installeur\

# ---------------------------------------------------------------- 1. la source

$exePret = Join-Path $ici 'coachingia.exe'
$solution = Join-Path $racine 'CoachingIA.slnx'
$publication = $null

if (Test-Path $exePret) {
    Etape 'Paquet portable détecté — aucun SDK nécessaire.'
    $publication = $ici
}
elseif (Test-Path $solution) {
    Etape 'Sources détectées — publication d''un exécutable autonome.'

    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $dotnet) {
        Halte @'
Le SDK .NET est introuvable.

  Installez-le (une fois) puis relancez ce script :
      winget install Microsoft.DotNet.SDK.10
  ou https://dotnet.microsoft.com/download

  Autre possibilité : demandez le paquet portable, produit par
  installeur\package.ps1 sur une machine qui a déjà le SDK. Il contient
  l'exe et ne dépend de rien.
'@
    }

    $version = (& dotnet --version 2>$null)
    Note "SDK .NET $version"
    $majeur = 0
    if ($version -match '^(\d+)\.') { $majeur = [int]$Matches[1] }
    if ($majeur -lt 10) {
        Halte "Le projet vise .NET 10 et le SDK installé est en $version. Mettez-le à jour : winget install Microsoft.DotNet.SDK.10"
    }

    $publication = Join-Path ([System.IO.Path]::GetTempPath()) ("coachingia-" + [guid]::NewGuid().ToString('N'))
    Note 'Compilation en cours (une minute la première fois)…'

    foreach ($projet in @('src\CoachingIA.Cli', 'src\CoachingIA.Mcp')) {
        & dotnet publish (Join-Path $racine $projet) `
            -c Release -r win-x64 --self-contained true `
            -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
            -o $publication --nologo -v quiet
        if ($LASTEXITCODE -ne 0) { Halte "La compilation de $projet a échoué. La sortie ci-dessus dit pourquoi." }
    }
    if (-not (Test-Path (Join-Path $publication 'coachingia.exe'))) {
        Halte 'La compilation s''est terminée sans produire coachingia.exe.'
    }
    Bon 'Exécutable produit.'
}
else {
    Halte @'
Ni sources ni exe à côté de ce script.

  Placez install.ps1 dans le dossier installeur\ du dépôt, ou dans le
  paquet portable à côté de coachingia.exe.
'@
}

# ------------------------------------------------------------- 2. l'installation

Etape "Installation dans $Dest"

# Une console qui tourne encore verrouillerait l'exe : on le dit plutôt que
# d'échouer sur un message Windows incompréhensible.
$occupe = Get-Process -Name 'coachingia' -ErrorAction SilentlyContinue
if ($occupe) {
    Halte 'Une console CoachingIA tourne déjà. Fermez sa fenêtre, puis relancez l''installation.'
}

New-Item -ItemType Directory -Force -Path $Dest | Out-Null

# Cas particulier : le paquet a été décompressé directement dans le dossier
# d'installation. Se copier sur soi-même échoue — et n'apporterait rien.
$memeEndroit = (Resolve-Path $publication).Path -eq (Resolve-Path $Dest).Path
if (-not $memeEndroit) {
    Copy-Item (Join-Path $publication 'coachingia.exe') $Dest -Force
    # Le serveur MCP voyage avec le CLI : il lit le même corpus, et l'installer
    # à part reviendrait à maintenir deux copies des mêmes faits.
    $mcp = Join-Path $publication 'coachingia-mcp.exe'
    if (Test-Path $mcp) { Copy-Item $mcp $Dest -Force }
}

# Les lentilles voyagent avec l'exe : sans elles, le coach retombe en neutre.
$lentilles = @(
    (Join-Path $ici 'lenses'),
    (Join-Path $racine 'lenses')
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if ($lentilles) {
    $cible = Join-Path $Dest 'lenses'
    New-Item -ItemType Directory -Force -Path $cible | Out-Null
    if ((Resolve-Path $lentilles).Path -ne (Resolve-Path $cible).Path) {
        Copy-Item (Join-Path $lentilles '*.json') $cible -Force
    }
    $n = (Get-ChildItem $cible -Filter *.json).Count
    Bon "$n lentille(s) installée(s)."

    # Le corpus suit les lentilles : c'est lui que le serveur MCP sert, et un
    # serveur qui démarre sans rien savoir est pire qu'un serveur absent.
    $sourceCorpus = Join-Path $lentilles 'corpus'
    if (Test-Path $sourceCorpus) {
        $cibleCorpus = Join-Path $cible 'corpus'
        New-Item -ItemType Directory -Force -Path $cibleCorpus | Out-Null
        if ((Resolve-Path $sourceCorpus).Path -ne (Resolve-Path $cibleCorpus).Path) {
            Copy-Item (Join-Path $sourceCorpus '*.json') $cibleCorpus -Force
        }
        Bon "$((Get-ChildItem $cibleCorpus -Filter *.json).Count) corpus de faits installé(s)."
    }
} else {
    Note 'Aucune lentille trouvée — le coach parlera en neutre.'
}

New-Item -ItemType Directory -Force -Path $Sorties | Out-Null
Bon "Bilans et rétrospectives : $Sorties"

if ($publication -ne $ici) { Remove-Item $publication -Recurse -Force -ErrorAction SilentlyContinue }

# --------------------------------------------------------------- 3. le confort

Etape 'Raccourcis et PATH'

$chemin = [Environment]::GetEnvironmentVariable('PATH', 'User')
if ($chemin -notlike "*$Dest*") {
    [Environment]::SetEnvironmentVariable('PATH', "$chemin;$Dest", 'User')
    Bon 'Ajouté au PATH (actif dans les nouveaux terminaux).'
} else {
    Note 'Déjà dans le PATH.'
}

$arguments = "web --port $Port --out `"$Sorties`""

if (-not $SansRaccourci) {
    $shell = New-Object -ComObject WScript.Shell
    foreach ($dossier in @([Environment]::GetFolderPath('Desktop'),
                           (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'))) {
        if (-not (Test-Path $dossier)) { continue }
        $lien = $shell.CreateShortcut((Join-Path $dossier 'CoachingIA.lnk'))
        $lien.TargetPath = Join-Path $Dest 'coachingia.exe'
        $lien.Arguments = $arguments
        $lien.WorkingDirectory = $Dest
        $lien.Description = 'Console locale CoachingIA'
        $lien.Save()
    }
    Bon 'Raccourci sur le Bureau et au menu Démarrer.'
}

# Un lanceur dans le dossier d'installation, pour ceux qui préfèrent un .bat.
@"
@echo off
title CoachingIA
cd /d "%~dp0"
coachingia.exe $arguments
pause
"@ | Set-Content -Path (Join-Path $Dest 'Demarrer-CoachingIA.bat') -Encoding ASCII

# ------------------------------------------------------------------ 4. la suite

$transcripts = Join-Path $env:USERPROFILE '.claude\projects'
Write-Host ""
Write-Host "  Installé." -ForegroundColor Green
Write-Host ""
Write-Host "    Console        http://127.0.0.1:$Port" -ForegroundColor White
Write-Host "    Exécutable     $(Join-Path $Dest 'coachingia.exe')" -ForegroundColor DarkGray
Write-Host "    Transcripts    $transcripts" -ForegroundColor DarkGray

if (-not (Test-Path $transcripts)) {
    Write-Host ""
    Write-Host "    Ce dossier n'existe pas encore sur cette machine : c'est normal si" -ForegroundColor Yellow
    Write-Host "    Claude Code n'y a jamais tourné. Le coach n'aura rien à lire tant que" -ForegroundColor Yellow
    Write-Host "    vous n'aurez pas travaillé une session ici, ou copié vos transcripts." -ForegroundColor Yellow
}

Write-Host ""
$serveur = Join-Path $Dest 'coachingia-mcp.exe'
if (Test-Path $serveur) {
    Write-Host ""
    Write-Host "  Pour rendre le corpus StarCraft II disponible dans vos sessions Claude Code :" -ForegroundColor White
    Write-Host "    claude mcp add coachingia-corpus -- `"$serveur`"" -ForegroundColor DarkGray
    Write-Host "  Le serveur ne lit que des fichiers locaux et n'ouvre aucune connexion." -ForegroundColor DarkGray
}

Write-Host ""
Write-Host "  Ensuite, au choix :" -ForegroundColor White
Write-Host "    - double-cliquez le raccourci CoachingIA (la console s'ouvre dans le navigateur)"
Write-Host "    - ou, dans un nouveau terminal :  coachingia bilan --lens starcraft2 --race zerg"
Write-Host ""

if ($Demarrer) {
    Start-Process -FilePath (Join-Path $Dest 'coachingia.exe') `
        -ArgumentList "web", "--port", "$Port", "--out", $Sorties -WorkingDirectory $Dest
}
