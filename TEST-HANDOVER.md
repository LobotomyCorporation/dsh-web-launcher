# WorkBuddy 测试交接：B5 + QuitAll

> 交接时间：2026-11-20 13:28（Asia/Shanghai）
> 仓库：D:\DSH_WorkSpace\Default_WorkSpace\dsh-launcher
> 版本：v1.19（代码提交 2284d47）
> 当前运行快照：启动器 pid=1284；dsh web 监听 127.0.0.1:3080（node pid=28652）；host.pid 为 cmd=13892 / port=3080 / started=2026-11-20T09:40:09
> 任务：验证 B5（非中文 Windows + 中文路径）和 QuitAll（退出启动器并停止服务）；只测试，不改代码、不提交、不重打包。

## 0. 先看这几条

1. 不要从 DSH Web 会话里执行 QuitAll。它会杀掉 3080 上的 dsh web，当前对话会断。请从独立的 PowerShell / CMD 窗口或 WorkBuddy 自己的终端执行。
2. 不要改系统区域设置、语言或“非 Unicode 程序语言”。B5 的真复现需要英文 Windows；本机只做静态验证和编码模拟（见第 2 节）。改区域要重启，还会影响用户其他软件。
3. 只动测试需要的文件：安装目录 launcher.ini、DshLauncher 进程、3080 和测试端口。不要删仓库、不要动 dist、不要改源码。
4. 先备份再测试。QuitAll 会删除 host.pid；恢复时让启动器重新生成，不要拿旧 host.pid 回填。
5. 当前状态只是交接时快照；动手前用第 1 节的命令重新确认。

## 1. 准备与基线

在独立 PowerShell 窗口执行：

~~~powershell
$state = Join-Path $env:LOCALAPPDATA 'DshLauncher'
$ini   = Join-Path $env:LOCALAPPDATA 'Programs\DshLauncher\launcher.ini'
$bk    = Join-Path $env:TEMP ('dsh-test-backup-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Force -Path $bk | Out-Null
Copy-Item $ini $bk
Copy-Item (Join-Path $state 'host.pid') $bk -ErrorAction SilentlyContinue
Copy-Item (Join-Path $state 'launcher.log') $bk -ErrorAction SilentlyContinue
"backup=$bk"
Get-Process DshLauncher | Select-Object Id,StartTime
Get-NetTCPConnection -LocalPort 3080 -State Listen | Select-Object LocalAddress,LocalPort,OwningProcess
Get-Content (Join-Path $state 'host.pid') -ErrorAction SilentlyContinue
Get-Content $ini
~~~

记录：启动器 pid、3080 的 node pid、host.pid 的 cmd/port/started、launcher.ini 的 workdir/dsh/port。

关键代码位置，只读核对，不用改：

- Cfg.Short：DshLauncher.cs:347，GetShortPathName 后只接受纯 ASCII；短名仍含中文就原样返回（:361、:366）。
- StartHost 生成脚本：DshLauncher.cs:926-934，workdir/home 走 Cfg.Short，脚本用 Encoding.Default 写入 start-dsh-web.cmd。
- QuitAll：DshLauncher.cs:1421-1445，UI 线程里 KillDshHosts + WaitPortClosed(8000) 后才 ExitThread。
- WaitPortClosed：DshLauncher.cs:1512。

## 2. B5：非中文 Windows + 中文路径

### 2.1 结论先说

B5 不是“路径带中文就必坏”，而是：Cfg.Short 不能保证把中文路径变成 ASCII 时，脚本仍按系统 ANSI 编码写入。

本机实测：中文目录的 8.3 短名仍可能保留中文（例如 中文 工作区 的短名是 中文工~1），Cfg.Short 因此返回长路径；脚本在中文 Windows 上按 GBK 写能跑，但英文 Windows 的 Encoding.Default 是 Windows-1252，中文会变成 ?，cd /d 找不到路径。当前版本预计 B5 仍为 FAIL；如果静态检查意外 PASS，说明该路径的短名恰好全 ASCII，换一个路径再测。

### 2.2 静态验证：不启动、不停止服务

1. 建一个带中文的测试目录，名字必须含中文：

~~~powershell
$wd = Join-Path $env:TEMP 'DSH-B5-Test\中文 工作区'
New-Item -ItemType Directory -Force -Path $wd | Out-Null
"wd=$wd"
~~~

2. 取 8.3 短名，数非 ASCII 字符：

~~~powershell
$fso = New-Object -ComObject Scripting.FileSystemObject
$short = $fso.GetFolder($wd).ShortPath
$bad = @($short.ToCharArray() | Where-Object { [int]$_ -gt 127 }).Count
"short=$short"
"non_ascii=$bad"
~~~

判定：

- non_ascii = 0：这个路径的短名纯 ASCII，不能证明 B5；换一个中文目录重试。
- non_ascii > 0：Cfg.Short 会返回长路径（它的 ascii 检查不通过），B5 的前置条件成立。

3. 模拟英文 Windows 的 ANSI 编码（1252）：

~~~powershell
$line = 'cd /d "' + $short + '"'
$bytes1252 = [Text.Encoding]::GetEncoding(1252).GetBytes($line)
$asEnglish = [Text.Encoding]::GetEncoding(1252).GetString($bytes1252)
"as_english=$asEnglish"
~~~

预期：asEnglish 里的中文变成 ?，例如：

~~~text
cd /d "C:\...\??\??"
~~~

这说明同一份脚本在英文 Windows 上会让 cmd 报“系统找不到指定的路径”。

4. 记录：wd、short、non_ascii、asEnglish，以及 DshLauncher.cs:926-934 的代码依据。不要为了测试去改系统区域设置。

### 2.3 真复现（可选，另找环境）

只有需要端到端证据时才做：

- 一台英文 Windows（VM 或 Windows Sandbox），装 Node + dsh（dsh --version 有输出）。
- 把启动器 exe + launcher.ini 放进去；launcher.ini 的 workdir 指向含中文的目录。
- 启动启动器，点“启动服务”。
- 预期：启动失败、端口不监听；dsh-web.log 里能看到 cd /d 路径错误。
- 若通过，记录 Windows 版本、代码页、短名输出、日志，才能判 B5 修复。

本机不做这一步；静态验证足以证明当前实现的风险点。

## 3. QuitAll：退出启动器并停止服务

### 3.1 触发前

确认只有一个 dsh web 在 3080：

~~~powershell
Get-NetTCPConnection -LocalPort 3080 -State Listen | Select-Object OwningProcess
Get-Process DshLauncher | Select-Object Id
~~~

准备监控脚本，保存为 C:\Temp\quitall-watch.ps1：

~~~powershell
$port = 3080
$log  = Join-Path $env:TEMP 'quitall-timing.txt'
$sw   = [Diagnostics.Stopwatch]::StartNew()

function Test-Port {
    $c = New-Object Net.Sockets.TcpClient
    try {
        $ar = $c.BeginConnect('127.0.0.1', $port, $null, $null)
        if (-not $ar.AsyncWaitHandle.WaitOne(200)) { return $false }
        $c.EndConnect($ar)
        return $true
    } catch {
        return $false
    } finally {
        $c.Close()
    }
}

"start=$($sw.Elapsed.TotalSeconds)" | Set-Content $log
$deadline = (Get-Date).AddSeconds(30)
while ((Get-Process DshLauncher -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 100
}
"launcher_gone=$($sw.Elapsed.TotalSeconds)" | Add-Content $log
while ((Test-Port) -and (Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 100
}
"port_closed=$($sw.Elapsed.TotalSeconds)" | Add-Content $log
Get-Content $log
~~~

在独立窗口运行：

~~~powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\Temp\quitall-watch.ps1
~~~

它会先等启动器消失，再等端口关闭；最多等 30 秒。

### 3.2 触发

右击托盘里的 DSH 鲸鱼图标，菜单最后一项是“退出启动器并停止服务”。不要点上面那项“退出启动器”，那个不会停服务。确认框选“是”。

如果 WorkBuddy 没有桌面鼠标能力：让用户点这一下，WorkBuddy 只负责监控和记录。不要用 Stop-Process 冒充 QuitAll。

### 3.3 判定

读 %TEMP%\quitall-timing.txt：

- 正常：port_closed 略早于 launcher_gone，两个值通常都在 2 秒内。
- 需要关注：launcher_gone > 3 秒，说明 UI 线程被同步收进程或等端口拖住。
- 理论上限不是 8 秒：KillDshHosts 里每个 taskkill 最多等 5 秒，再叠加 WaitPortClosed(8000)，最坏可到约 13 秒。如果接近这个值，记录为 QuitAll 同步问题仍存在。
- 如果 launcher_gone 先于 port_closed，或 30 秒内端口没关，直接判 FAIL，保留日志和 host.pid 现场。

### 3.4 恢复

QuitAll 后启动器和 dsh 都没了，host.pid 已删除。重新启动启动器，不要恢复旧 host.pid：

~~~powershell
Start-Process "$env:LOCALAPPDATA\Programs\DshLauncher\DshLauncher.exe"
~~~

等待并验证：

~~~powershell
$deadline = (Get-Date).AddSeconds(60)
do {
    Start-Sleep -Seconds 1
    $listen = Get-NetTCPConnection -LocalPort 3080 -State Listen -ErrorAction SilentlyContinue
} while (-not $listen -and (Get-Date) -lt $deadline)
$listen | Select-Object LocalAddress,LocalPort,OwningProcess
Get-Content (Join-Path $env:LOCALAPPDATA 'DshLauncher\host.pid') -ErrorAction SilentlyContinue
~~~

启动器正常会自动打开带 token 的页面；没打开就右击托盘，选“打开 DSH 界面”。如果 60 秒内 3080 仍不监听，读 launcher.log 和 dsh-web.log，记录失败，不要反复重启。

## 4. 可选：停止、重启、B2 快失败

既然已经打断服务，可以顺手做；不做也不影响 B5 / QuitAll 的结论。

### 4.1 停止服务

右击托盘，选“停止服务”，确认。预期：菜单先显示“停止中…”，UI 不冻结；随后状态“未运行”，3080 关闭，host.pid 删除。记录从点击到菜单恢复响应的时间。

### 4.2 重启服务

右击托盘，选“重启服务”。预期：先停止再启动，新 cmd/node，3080 恢复，状态“运行中”，host.pid 是新记录。记录是否出现两个 node 抢端口。

### 4.3 B2 启动失败快失败

1. 先停止服务。
2. 退出启动器（托盘里的“退出启动器”，不是 QuitAll）。
3. 新建 C:\Temp\fail-dsh.cmd：

~~~bat
@echo off
echo B2-TEST-FAILURE 1>&2
exit /b 7
~~~

4. 备份并修改安装目录 launcher.ini：把 dsh= 指向 C:\Temp\fail-dsh.cmd。
5. 启动启动器，点“启动服务”。
6. 预期：约 1 秒内状态变 Error，通知标题“启动失败”，正文含 B2-TEST-FAILURE（来自 dsh-web.log 尾部）；不再干等 120 秒。
7. 恢复 launcher.ini 的 dsh= 为空或原值，退出并重启启动器，点“启动服务”恢复 3080。

## 5. 回报格式

请按下面字段回报，不要只写“通过/不通过”：

~~~text
B5
- wd:
- short:
- non_ascii:
- as_english:
- 结论: PASS / FAIL（当前版本预计 FAIL）
- 证据文件:

QuitAll
- 触发时间:
- launcher_gone:
- port_closed:
- 退出前 3080 owning pid:
- host.pid 是否删除:
- 结论: PASS / FAIL / 需关注（>3s）
- 恢复后 3080 owning pid:
- 截图/日志:

可选
- 停止服务耗时:
- 重启服务结果:
- B2 失败通知是否 1 秒级:
~~~

## 6. 边界

- 不要修改源码、不要 git commit、不要重打包。
- 不要把 B5 静态 FAIL 当成“启动器坏了”；它只影响非中文 Windows + 中文路径，本机中文 Windows 仍能跑。
- QuitAll 测试期间当前 DSH 会话会断，这是预期；测试前后都要确认 3080 恢复。
- 如果用户不在场，不要执行 QuitAll；它会断开用户正在用的页面。
