# rebuild.ps1 - rebuild DshLauncher.exe and install the Medium-integrity copy
# Why this exists: any file created from a DSH session inherits a LOW integrity
# label. A Low-label exe always runs at Low integrity, and the taskbar runs at
# Medium, so Shell_NotifyIcon is blocked by UIPI (error 5) and NO tray icon ever
# appears. The installed copy must therefore be written and labelled by a
# Medium-integrity process - which is what the scheduled-task trick below does.

$ErrorActionPreference = 'Stop'
$dir  = $PSScriptRoot
$csc  = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$dst  = Join-Path $env:LOCALAPPDATA 'Programs\DshLauncher'
$state = Join-Path $env:LOCALAPPDATA 'DshLauncher'

Write-Host '[1/4] compiling ...'
& $csc /nologo /codepage:65001 /target:winexe /platform:anycpu /optimize+ `
    /win32icon:"$dir\dsh.ico" /win32manifest:"$dir\app.manifest" /out:"$dir\DshLauncher.exe" `
    /resource:"$dir\logo256.png",DshLogo.png `
    /r:System.dll /r:System.Core.dll /r:System.Windows.Forms.dll `
    /r:System.Drawing.dll /r:System.Management.dll `
    "$dir\DshLauncher.cs" "$dir\DshMenu.cs"
if ($LASTEXITCODE -ne 0) { throw "csc failed: $LASTEXITCODE" }

# 必须先停掉正在运行的实例，否则正在使用的 exe 覆盖不了，copy 会静默失败，
# 重启起来的还是旧版本（这个坑踩过一次：日志里看到的仍是旧行为）
Write-Host '[2/4] stopping running launcher ...'
Get-Process DshLauncher -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 900

Write-Host '[3/4] installing Medium-integrity copy ...'
$cmd = @"
@echo off
mkdir "$dst" 2>nul
copy /y "$dir\DshLauncher.exe" "$dst\DshLauncher.exe" >nul
copy /y "$dir\dsh.ico" "$dst\dsh.ico" >nul
icacls "$dst" /setintegritylevel "(OI)(CI)Medium" >nul 2>&1
icacls "$dst\DshLauncher.exe" /setintegritylevel Medium >nul 2>&1
icacls "$dst\dsh.ico" /setintegritylevel Medium >nul 2>&1
"@
$cmdPath = "$dir\_install.cmd"
Set-Content -Path $cmdPath -Value $cmd -Encoding ASCII
schtasks /Create /TN 'DshLauncherInstall' /TR $cmdPath /SC ONCE /ST 23:59 /F /RL LIMITED | Out-Null
schtasks /Run /TN 'DshLauncherInstall' | Out-Null
Start-Sleep -Seconds 4
schtasks /Delete /TN 'DshLauncherInstall' /F | Out-Null
Remove-Item $cmdPath -Force -ErrorAction SilentlyContinue

Write-Host '[4/4] restarting launcher ...'
# 注意必须带 --silent：否则启动器一起来就发现端口是通的，会顺手开一个新的浏览器标签页，
# 每改一次代码就多一个 dsh 页面（这个投诉踩过一次）。
# 用一个小 .cmd 包一层：schtasks 的 /TR 直接塞带引号的命令行很容易被解析坏
#（踩过：ERROR: The system cannot find the file specified），包成脚本就干净了。
$startCmd = "$dir\_start.cmd"
$startBody = "@echo off`r`nstart `"`" `"$dst\DshLauncher.exe`" --silent`r`n"
Set-Content -Path $startCmd -Value $startBody -Encoding ASCII
$r1 = schtasks /Create /TN 'DshLauncherStart' /TR "$startCmd" /SC ONCE /ST 23:59 /F /IT /RL LIMITED 2>&1 | Out-String
if ($LASTEXITCODE -ne 0) { throw "schtasks create failed: $r1" }
$r2 = schtasks /Run /TN 'DshLauncherStart' 2>&1 | Out-String
if ($LASTEXITCODE -ne 0) { throw "schtasks run failed: $r2" }
Start-Sleep -Seconds 4
schtasks /Delete /TN 'DshLauncherStart' /F | Out-Null
Remove-Item $startCmd -Force -ErrorAction SilentlyContinue

Write-Host '[5/5] verifying ...'
$p = Get-Process DshLauncher -ErrorAction SilentlyContinue
if (-not $p) { throw 'launcher did not start' }
Write-Host ("  pid=" + $p.Id + "  started=" + $p.StartTime)
$acl = icacls "$dst\DshLauncher.exe" | Select-String 'Mandatory'
Write-Host ("  label: " + $acl.Line.Trim())
Get-Content (Join-Path $state 'launcher.log') -Tail 3
