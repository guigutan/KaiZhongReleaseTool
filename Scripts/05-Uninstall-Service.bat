@echo off
fltmc >nul 2>&1 || (powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath '%~f0' -Verb RunAs" & exit /b)
sc.exe stop "KaiZhongReleaseToolServer" >nul 2>&1
timeout /t 2 /nobreak >nul
sc.exe delete "KaiZhongReleaseToolServer"
echo ProgramData was preserved.
pause
