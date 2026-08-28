@echo off
rem Double-cliquez ce fichier : il installe CoachingIA pour l'utilisateur courant.
rem Aucun droit administrateur n'est demande.
title Installation de CoachingIA
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" %*
echo.
pause
