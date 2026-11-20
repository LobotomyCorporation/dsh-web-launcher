# rebuild.ps1 - rebuild DshLauncher.exe and install the Medium-integrity copy
# Why this exists: any file created from a DSH session inherits a LOW integrity
# label. A Low-label exe always runs at Low integrity, and the taskbar runs at
# Medium, so Shell_NotifyIcon is blocked by UIPI (error 5) and NO tray icon ever
# appears. The installed copy must therefore be written and labelled by a
# Medium-integrity process - which is what the scheduled-task trick below does.
#
# Note: every helper script written here is pure ASCII on purpose. cmd.exe reads
# .cmd bytes with the console code page, so non-ASCII paths must arrive through
# environment variables at run time (%~dp0 / %LOCALAPPDATA%), never as literals.

$ErrorActionPreference = 'Stop'
$dir   = $PSScriptRoot
$csc   = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$dst   = Join-Path $env:LOCALAPPDATA 'Programs\DshLauncher'
$state = Join-Path $env:LOCALAPPDATA 'DshLauncher'
$nl    = [Environment]::NewLine

function Wait-Until {
    param([scriptblock]$Cond, [int]$TimeoutMs)
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $TimeoutMs) {
        try { if (& $Cond) { return $true } } catch { }
        Start-Sleep -Milliseconds 200
    }
    return $false
}

Write-Host '[1/5] compiling ...'
$cscArgs = @(
    '/nologo'
    '/codepage:65001'
    '/target:winexe'
    '/platform:anycpu'
    '/optimize+'
    "/win32icon:$dir\dsh.ico"
    "/win32manifest:$dir\app.manifest"
    "/out:$dir\DshLauncher.exe"
    "/resource:$dir\logo256.png,DshLogo.png"
    '/r:System.dll'
    '/r:System.Core.dll'
    '/r:System.Windows.Forms.dll'
    '/r:System.Drawing.dll'
    '/r:System.Management.dll'
    "$dir\DshLauncher.cs"
    "$dir\DshMenu.cs"
)
& $csc @cscArgs
if ($LASTEXITCODE -ne 0) { throw "csc failed: $LASTEXITCODE" }
$srcHash = (Get-FileHash (Join-Path $dir 'DshLauncher.exe') -Algorithm SHA256).Hash
Write-Host ('  sha256=' + $srcHash)

Write-Host '[2/5] stopping running launcher ...'
Get-Process DshLauncher -ErrorAction SilentlyContinue | Stop-Process -Force
if (-not (Wait-Until { -not (Get-Process DshLauncher -ErrorAction SilentlyContinue) } 10000)) {
    throw 'launcher still running after 10s'
}

Write-Host '[3/5] installing Medium-integrity copy ...'
$install = @"
@echo off
set "SRC=%~dp0"
set "DST=%LOCALAPPDATA%\Programs\DshLauncher"
if not exist "%DST%" mkdir "%DST%"
set /a tries=0
:copyexe
copy /y "%SRC%DshLauncher.exe" "%DST%\DshLauncher.exe" >nul 2>&1
if not errorlevel 1 goto copiedexe
set /a tries+=1
if %tries% geq 10 exit /b 1
ping -n 2 127.0.0.1 >nul
goto copyexe
:copiedexe
copy /y "%SRC%dsh.ico" "%DST%\dsh.ico" >nul
copy /y "%SRC%toast-inverted.png" "%DST%\dsh-toast.png" >nul
if not exist "%DST%\icons" mkdir "%DST%\icons"
copy /y "%SRC%icons\*.png" "%DST%\icons\" >nul
icacls "%DST%" /setintegritylevel "(OI)(CI)Medium" >nul 2>&1
icacls "%DST%\DshLauncher.exe" /setintegritylevel Medium >nul 2>&1
icacls "%DST%\dsh.ico" /setintegritylevel Medium >nul 2>&1
icacls "%DST%\dsh-toast.png" /setintegritylevel Medium >nul 2>&1
icacls "%DST%\icons" /setintegritylevel "(OI)(CI)Medium" >nul 2>&1
exit /b 0
"@
foreach ($ch in $install.ToCharArray()) { if ([int]$ch -gt 127) { throw 'install script must stay ASCII' } }
$cmdPath = "$dir\_install.cmd"
Set-Content -Path $cmdPath -Value $install -Encoding ASCII
schtasks /Create /TN 'DshLauncherInstall' /TR $cmdPath /SC ONCE /ST 23:59 /F /RL LIMITED | Out-Null
schtasks /Run /TN 'DshLauncherInstall' | Out-Null
$copied = Wait-Until {
    $exe = Join-Path $dst 'DshLauncher.exe'
    if (-not (Test-Path $exe)) { return $false }
    return ((Get-FileHash $exe -Algorithm SHA256).Hash -eq $srcHash)
} 20000
schtasks /Delete /TN 'DshLauncherInstall' /F | Out-Null
Remove-Item $cmdPath -Force -ErrorAction SilentlyContinue
if (-not $copied) { throw 'installed exe did not match the build (hash mismatch) - check ' + $dst }

Write-Host '[4/5] restarting launcher ...'
# --silent matters: otherwise the launcher finds the port open and opens a new
# browser tab on every rebuild.
$startCmd = "$dir\_start.cmd"
$startBody = '@echo off' + $nl + 'start "" "%LOCALAPPDATA%\Programs\DshLauncher\DshLauncher.exe" --silent' + $nl
Set-Content -Path $startCmd -Value $startBody -Encoding ASCII
$r1 = schtasks /Create /TN 'DshLauncherStart' /TR "$startCmd" /SC ONCE /ST 23:59 /F /IT /RL LIMITED 2>&1 | Out-String
if ($LASTEXITCODE -ne 0) { throw "schtasks create failed: $r1" }
$r2 = schtasks /Run /TN 'DshLauncherStart' 2>&1 | Out-String
if ($LASTEXITCODE -ne 0) { throw "schtasks run failed: $r2" }
$up = Wait-Until { [bool](Get-Process DshLauncher -ErrorAction SilentlyContinue) } 15000
schtasks /Delete /TN 'DshLauncherStart' /F | Out-Null
Remove-Item $startCmd -Force -ErrorAction SilentlyContinue
if (-not $up) { throw 'launcher did not start' }

Write-Host '[5/5] verifying ...'
$p = Get-Process DshLauncher -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $p) { throw 'launcher process missing' }
Write-Host ('  pid=' + $p.Id + '  started=' + $p.StartTime)
$runHash = (Get-FileHash $p.Path -Algorithm SHA256).Hash
if ($runHash -ne $srcHash) { throw ('running exe hash mismatch: ' + $runHash) }
$acl = icacls (Join-Path $dst 'DshLauncher.exe') | Select-String 'Mandatory'
if ($acl) { Write-Host ('  label: ' + $acl.Line.Trim()) }
Get-Content (Join-Path $state 'launcher.log') -Tail 3
