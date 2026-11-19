@echo off
setlocal
set "ERA_AI_BIN_DIR=%~dp0"
set "ERA_AI_BINARY=EmueraAI-0.10.exe"
if exist "%ERA_AI_BIN_DIR%%ERA_AI_BINARY%" goto launch
set "ERA_AI_BINARY=EmueraAI-0.9.exe"
if exist "%ERA_AI_BIN_DIR%%ERA_AI_BINARY%" goto launch
set "ERA_AI_BINARY=EmueraAI-0.8.exe"
if exist "%ERA_AI_BIN_DIR%%ERA_AI_BINARY%" goto launch
set "ERA_AI_BINARY=EmueraAI-0.7.1.exe"
if exist "%ERA_AI_BIN_DIR%%ERA_AI_BINARY%" goto launch
set "ERA_AI_BINARY=EmueraAI-0.7.exe"
if exist "%ERA_AI_BIN_DIR%%ERA_AI_BINARY%" goto launch
set "ERA_AI_BINARY=EmueraAI.exe"
if exist "%ERA_AI_BIN_DIR%EmueraAI.exe" goto launch
set "ERA_AI_BIN_DIR=%~dp0dist\"
set "ERA_AI_BINARY=EmueraAI-0.10.exe"
if exist "%ERA_AI_BIN_DIR%%ERA_AI_BINARY%" goto launch
set "ERA_AI_BINARY=EmueraAI-0.9.exe"
if exist "%ERA_AI_BIN_DIR%%ERA_AI_BINARY%" goto launch
set "ERA_AI_BINARY=EmueraAI-0.8.exe"
if exist "%ERA_AI_BIN_DIR%%ERA_AI_BINARY%" goto launch
set "ERA_AI_BINARY=EmueraAI-0.7.1.exe"
if exist "%ERA_AI_BIN_DIR%%ERA_AI_BINARY%" goto launch
set "ERA_AI_BINARY=EmueraAI-0.7.exe"
if exist "%ERA_AI_BIN_DIR%%ERA_AI_BINARY%" goto launch
set "ERA_AI_BINARY=EmueraAI.exe"
if exist "%ERA_AI_BIN_DIR%EmueraAI.exe" goto launch
echo EmueraAI.exe was not found beside this script or in the dist folder.
echo Extract the complete Windows package, or run build.ps1 in the source folder.
pause
exit /b 1
:launch
start "" /D "%ERA_AI_BIN_DIR%" "%ERA_AI_BIN_DIR%%ERA_AI_BINARY%"
