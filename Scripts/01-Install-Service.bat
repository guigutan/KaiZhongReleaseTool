@echo off
fltmc >nul 2>&1 || (powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath '%~f0' -Verb RunAs" & exit /b)
set "SERVICE_NAME=KaiZhongReleaseToolServer"
set "SERVICE_EXE=%~dp0KaiZhongReleaseTool.Server.exe"

if not exist "%SERVICE_EXE%" (
  echo Service executable not found: %SERVICE_EXE%
  pause
  exit /b 1
)

sc.exe query "%SERVICE_NAME%" >nul 2>&1
if not errorlevel 1 (
  sc.exe stop "%SERVICE_NAME%" >nul 2>&1
  timeout /t 2 /nobreak >nul
  sc.exe delete "%SERVICE_NAME%" >nul 2>&1
  timeout /t 2 /nobreak >nul
)

sc.exe create "%SERVICE_NAME%" binPath= "\"%SERVICE_EXE%\"" start= auto DisplayName= "KaiZhong Release Tool Server"
if errorlevel 1 goto :failed
sc.exe description "%SERVICE_NAME%" "KaiZhong release tool HTTP server on port 5050."
sc.exe failure "%SERVICE_NAME%" reset= 86400 actions= restart/5000/restart/10000/restart/30000
sc.exe start "%SERVICE_NAME%"
if errorlevel 1 goto :failed
echo Service installed and started successfully.
pause
exit /b 0

:failed
echo Service installation failed. ErrorLevel=%errorlevel%
pause
exit /b 1
