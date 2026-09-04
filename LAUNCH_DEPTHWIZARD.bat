@echo off
title DepthWizard Launcher
echo ===================================================
echo             DepthWizard Launcher
echo ===================================================
echo.

:: 1. Check if backend is already listening on port 8000
netstat -ano | findstr /R /C:":8000 .*LISTENING" >nul 2>&1
if %errorlevel% equ 0 (
    echo [OK] Backend server is already running on port 8000.
) else (
    echo [*] Starting DepthWizard local AI backend...
    start /min "DepthWizard Backend" cmd /c "call START_BACKEND.bat"
    echo [*] Waiting 3 seconds for server to initialize...
    timeout /t 3 /nobreak >nul
)

:: 2. Search and launch DepthWizard.exe
if exist "Build\DepthWizard.exe" (
    echo [*] Launching DepthWizard app...
    start "" "Build\DepthWizard.exe"
) else if exist "DepthWizard.exe" (
    echo [*] Launching DepthWizard app...
    start "" "DepthWizard.exe"
) else if exist "unity-client\Build\DepthWizard.exe" (
    echo [*] Launching DepthWizard app...
    start "" "unity-client\Build\DepthWizard.exe"
) else (
    echo.
    echo [!] DepthWizard.exe not found.
    echo Please build the project in Unity Editor first:
    echo   File -^> Build Settings -^> Build -^> Choose 'Build' folder.
    echo.
    pause
)
