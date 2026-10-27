@echo off
rem DSH Web Launcher - diagnostics. ASCII only on purpose: cmd.exe parses
rem batch files byte-by-byte, so non-ASCII text breaks on non-UTF8 code pages.
setlocal
cd /d "%~dp0"
set "OUT=%~dp0diagnose.txt"
(
echo ==== DSH Web Launcher diagnostics ====
echo Date: %DATE% %TIME%
echo Computer: %COMPUTERNAME%   User: %USERNAME%
echo Arch: %PROCESSOR_ARCHITECTURE%
echo.
echo ---- [1] dsh command ----
where dsh
if errorlevel 1 echo    ! dsh NOT found in PATH
echo.
echo ---- [2] node ----
where node
if errorlevel 1 echo    ! node NOT found in PATH
node -v
echo.
echo ---- [3] npm global root ----
npm root -g
echo.
echo ---- [4] port 3080 ----
netstat -ano | findstr /C:":3080" | findstr /C:"LISTENING"
if errorlevel 1 echo    ! nothing is listening on 3080 - dsh web is not running
echo.
echo ---- [5] files in this folder ----
dir /b
echo.
echo ---- [6] launcher.ini ----
if exist launcher.ini (type launcher.ini) else (echo    ! no launcher.ini yet - it is created on first run)
echo.
echo ---- [7] end ----
) > "%OUT%" 2>&1
echo Diagnostics written to: %OUT%
start "" notepad "%OUT%"
endlocal
