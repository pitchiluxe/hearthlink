@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0HearthLink-Connection-Check.ps1"
echo.
echo The check is complete. Copy the report text or use the saved report file.
pause
