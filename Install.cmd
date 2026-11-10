@echo off
setlocal
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -ExecutionPolicy RemoteSigned -File "%~dp0Setup.ps1" -Mode Install
set "setupResult=%ERRORLEVEL%"
if not "%setupResult%"=="0" echo Installation did not finish. See docs\installation.md for troubleshooting.
pause
exit /b %setupResult%
