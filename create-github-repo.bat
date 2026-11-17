@echo off
setlocal
cd /d "%~dp0"
title WinTune - create GitHub repository

rem Needs: git and GitHub CLI (gh). Installs them with winget if missing.
where git >nul 2>nul || winget install --id Git.Git -e --accept-source-agreements --accept-package-agreements
where gh >nul 2>nul || winget install --id GitHub.cli -e --accept-source-agreements --accept-package-agreements
set "PATH=%ProgramFiles%\Git\cmd;%ProgramFiles%\GitHub CLI;%PATH%"

gh auth status >nul 2>nul
if errorlevel 1 (
  echo Log in to GitHub first...
  gh auth login
  if errorlevel 1 exit /b 1
)

set "REPO=WinTune"
set "DESC=A simple AI vibe-coded program I made to speed up all the tweaks and settings I use on Windows."

choice /c PR /m "Repository visibility: [P]ublic or p[R]ivate"
if errorlevel 2 (set "VIS=--private") else (set "VIS=--public")

if not exist ".git" (
  git init -b main
)
rem Commit with the GitHub no-reply address so your real email is not exposed.
for /f "delims=" %%u in ('gh api user --jq .login') do set "GHUSER=%%u"
for /f "delims=" %%i in ('gh api user --jq .id') do set "GHID=%%i"
git config user.name "%GHUSER%"
git config user.email "%GHID%+%GHUSER%@users.noreply.github.com"

git add -A
git commit -m "Initial commit"
gh repo create %REPO% %VIS% --description "%DESC%" --source . --remote origin --push
if errorlevel 1 (
  echo.
  echo Repository creation failed. See the message above.
  pause
  exit /b 1
)
echo.
echo Done. Your repository is online.
gh repo view --web
pause
