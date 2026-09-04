@echo off
echo ========================================
echo   DepthWizard Backend Server
echo ========================================
echo.

set KMP_DUPLICATE_LIB_OK=TRUE

if exist .venv\Scripts\activate.bat (
    call .venv\Scripts\activate.bat
) else (
    echo [!] Virtual environment not found. Creating...
    python -m venv .venv
    call .venv\Scripts\activate.bat
    pip install -r requirements.txt
    pip install -r backend\requirements.txt
)

echo Starting server on http://localhost:8000 ...
echo Press Ctrl+C to stop.
echo.
uvicorn backend.app.main:app --host 0.0.0.0 --port 8000
pause
