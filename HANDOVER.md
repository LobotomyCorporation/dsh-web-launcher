# DSH Web 启动器 — 交接文档

> 日期：2026-11-20
> 版本：v1.19（第十三轮补丁：启动失败快失败 + 安装复制重试）
> 仓库：D:\DSH_WorkSpace\Default_WorkSpace\dsh-launcher
> 安装位置：%LOCALAPPDATA%\Programs\DshLauncher\
> 当前运行：启动器 pid=1284（11:50:31 启动），dsh web 监听 3080（node pid=28652），host.pid 记录 cmd=13892 / port=3080 / started=2026-11-20T09:40:09

## 1. 这次做了什么

v1.18 是全量 review 修复（异步启停、状态机、按端口收进程）；v1.19 在其上补掉 B2「启动失败干等 120 秒」，并修了构建脚本在 exe 被短暂占用时复制失败的问题。

### 1.1 启动失败快失败（B2，v1.19）

- StartHost 保留 cmd 的 Process 句柄 proc；watchdog 每轮在 PortOpen 之后检查 proc.HasExited。
- cmd 提前退出、且 token/state 仍是本次启动时，立即置 St.Error、_detail="启动进程已退出"，不再等 120 秒；仍保留 _startToken 校验，旧启动不能覆盖新状态。
- 新增 Cfg.ReadLogTail(maxChars)：用 FileShare.ReadWrite | FileShare.Delete 打开 dsh-web.log，读尾部约 500 字符；通知里带日志尾巴，日志读不到时提示「日志是空的」。
- 反射调用 ReadLogTail(300) 返回 287 字符，验证共享读正常。
- 正常启动时 cmd 不会提前退出：host.pid 的 cmd pid=13892 从 09:40 一直存活，命令行是 cmd.exe /c ""…\start-dsh-web.cmd""，说明 HasExited 不会对当前服务误判。
- 失败路径没有实机触发（会断开当前 3080 会话），以编译 + 代码审查为准；需在可打断服务的窗口回归。

### 1.2 安装复制重试（v1.19）

- 第一次 11:49 rebuild 在 [3/5] 报 installed exe did not match the build；旧 exe 未被替换（仍是 v1.18 的 7EEF…）。推测是刚 csc 出来的 exe 被 Defender/索引短暂占用，计划任务里的 copy 一次性失败；安装脚本输出已删除，无法完全证实。
- rebuild.ps1 的 _install.cmd 对 DshLauncher.exe 复制加 10 次重试（每次 ping 等 1 秒），其余文件不变；SchTasks 仍负责 Medium 完整性。
- 11:50:31 重跑五步通过，源码 / 安装 exe 的 SHA-256 均 8F8DDF97…73CEB5F7，pid=1284，Medium 标签正常。

### 1.3 v1.18 的状态机与异步启停（保留记录）

- St 枚举增加 Stopping；Refresh_ 在锁内先判 Stopping，停止期间不会被端口探测改回 Running。
- 新增 _startToken：Start/Stop/QuitAll 每次自增；watchdog 每轮和落 Running/Error 前校验 token 与 St.Starting。
- StopHost(bool ask, bool restartAfter)：先置 Stopping 显示「停止中…」，再把 KillDshHosts + 删 host.pid + WaitPortClosed(15000) 丢线程池；完成后 Post 回 UI 校验 token，置 Stopped，再按 _restartAfterStop 决定 StartHost。
- QuitAll 仍同步（最多 8 秒），因为它是退出路径，进程随后结束。

### 1.4 按端口收进程（B3 / B6，v1.18）

- KillDshHosts 解析 host.pid 的 cmd= 与 port=；端口不一致直接忽略。
- 启发式枚举 node.exe 时只收 dsh\lib\bin.js + " web" + CommandLineHasPort(cl, Cfg.Port) 的进程。
- CommandLineHasPort 支持 --port 4000 与 --port=4000；没有 --port 时只对默认 3080 放行。
- FindDshStartTime 同样按端口过滤。
- taskkill 只有 WaitForExit(5000) 且 ExitCode == 0 才 killed++；超时 / 非零退出写日志。

### 1.5 菜单与便携（v1.18）

- DshMenu.ApplyScale 释放旧的第二行字体；两行高度改用 Graphics.GetHeight，避免高 DPI 下文字重叠。
- MenuPreview 截图不再把背景窗体置顶/抢焦点，改为 DoEvents + 短暂等待 + CopyFromScreen。
- diagnose.cmd 从同目录 launcher.ini 读 port=（默认 3080）；根目录补 launcher.ini 模板。
- 150% 下菜单 380×528，两行「状态：运行中 / 已运行 57 分钟」完整。

## 2. 构建与验证

构建命令：

~~~powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "D:\DSH_WorkSpace\Default_WorkSpace\dsh-launcher\rebuild.ps1"
~~~

本次结果：

- 2026-11-20 11:50:31 五步全部通过。
- 源码 exe / 安装 exe / dist exe 的 SHA-256 均为：
  8F8DDF97B4E9456B2F75E6A4A7AAF770C93B941B697D7A0B3A8ED79673CEB5F7
- 安装进程 pid=1284，Medium 完整性标签正常。
- 当前 dsh web 没被重启（host.pid 仍是 started=2026-11-20T09:40:09）；3080 仍由 node pid=28652 监听。
- ReadLogTail 反射测试返回 dsh-web.log 末尾 287 字符。
- 主程序编译通过；预览程序有 5 个 CS0649（AuxDir / AuxRun / AuxOpenDir / IconDir / Verbose），是预览不链接 DshLauncher.cs 的正常现象。

没有实机触发「停止服务 / 重启服务 / 启动失败」的破坏性路径，因为会断开当前正在使用的 dsh web；这些路径以代码审查 + 编译 + ReadLogTail 验证为准。若下一轮要实测，请选一个没有正在对话的时段。

## 3. 当前 git 状态

上一轮提交：7eb4c73（v1.18：review 修复、文档、便携包）。

v1.19 已提交，提交信息：

~~~text
v1.19：启动失败快失败 + 安装复制重试
~~~

当前 `git status --short` 为空（工作区 clean）。提交 hash 以 `git log -1` 为准。

本次提交包含：DshLauncher.cs、DshLauncher.exe、rebuild.ps1、PROJECT-REPORT.md、README.md、HANDOVER.md、dist/DSH-Web-启动器/DshLauncher.exe、dist/DSH-Web-Launcher-portable-v1.19.zip。

dist 里旧的 v1.18 / v1.6 zip 保留作历史，未删除。

## 4. 已知风险与未完成项

1. B5 未修：非中文 Windows + 中文路径仍依赖 8.3 短名 / ANSI 兜底。
2. QuitAll 仍在 UI 线程同步收进程并 WaitPortClosed(8000)，最坏约 8 秒。
3. 3080 被非 dsh 程序占用时仍会被当成「服务已运行」；token 被外部终端重启换掉时缓存地址会过期。
4. 启动失败快失败路径未实机触发；如果 Process 句柄拿到的不是 cmd（例如以后改成直接起 node），需要重新核对 HasExited 语义。
5. rebuild 的复制重试是概率性保护；若目标目录被 AV 长时间锁定，仍可能失败，日志会指出 hash mismatch。
6. dist 里保留 v1.18 / v1.6 旧包；如需清理历史产物要先征得用户同意。

## 5. 下一轮建议

1. 提交本次改动，确保工作区干净。
2. 在可打断服务的窗口实测：运行中 → 停止 → 重启 → 再停止，确认 UI 不冻结、状态行进入「停止中…」后回「未运行」，并检查 host.pid 清理。
3. 实测启动失败：临时把 launcher.ini 的 dsh 指向一个立即退出的命令，确认 1 秒级转 Error 且通知带日志尾部；测完恢复配置。
4. 若要修 B5：改为 ProcessStartInfo 直接起 dsh.cmd，stdout/stderr 重定向，不再生成 cmd 文件。
5. 版本号规则：v1.19 是当前 exe / 文档 / 便携包版本；README 和 PROJECT-REPORT 已同步。

## 6. 关键文件

- DshLauncher.cs：托盘、启停、WMI、端口、token、日志尾部、图标合成；1681 行。
- DshMenu.cs：iOS 风格自绘菜单、DPI、两行状态、呼吸球；902 行。
- rebuild.ps1：编译 → 停旧进程 → Medium 副本（含复制重试）→ 静默重启 → hash/标签自检；122 行。
- PROJECT-REPORT.md：§5.12 是 v1.19 记录；§5.11 是 v1.18 review；§5.7 是原推迟项状态。
- README.md：用户向说明；v1.19 便携包与两行状态说明。
- dist\DSH-Web-启动器\：便携包目录（exe / ini / 使用说明 / diagnose.cmd）。
- dist\DSH-Web-Launcher-portable-v1.19.zip：66,537 字节，4 个文件。

## 7. 快速自检

~~~powershell
Get-Process DshLauncher | Select-Object Id,StartTime,Path
Get-NetTCPConnection -LocalPort 3080 -State Listen
Get-Content (Join-Path $env:LOCALAPPDATA 'DshLauncher\launcher.log') -Tail 20
~~~
