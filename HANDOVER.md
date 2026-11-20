# DSH Web 启动器 — 交接文档

> 日期：2026-11-20
> 版本：v1.22（第十六轮补丁：关闭 DSH 后菜单不跟手——端口探测移出 UI 线程；另含启动观测 BootTrace；v1.21/v1.20 记录保留）
> 仓库：D:\DSH_WorkSpace\Default_WorkSpace\dsh-launcher
> 安装位置：%LOCALAPPDATA%\Programs\DshLauncher\
> 当前运行：启动器 pid=47864（20:46:51 启动，exe `E99CB870…`），dsh web 监听 3080（node pid=10296），host.pid 记录 cmd=16268 / port=3080 / started=2026-11-20T20:39:18

## 1. 这次做了什么

v1.18 是全量 review 修复（异步启停、状态机、按端口收进程）；v1.19 补掉 B2「启动失败干等 120 秒」；v1.20 收掉最后一个 UI 冻结点——B1 停止/退出期间的菜单卡顿（QuitAll 后台化、停止中跳过端口探测、防重入，见 §1.6）；v1.21 修「没找到登录令牌」——端口就绪后不再固定等 8 秒，而是等 token 地址落盘并探活通过再开页（见 §1.7）；v1.22 修「DSH 停止后菜单不跟手」——端口探测移到后台缓存，并加入启动观测 BootTrace（见 §1.8、§1.9）。

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

### 1.6 B1 停止/退出卡顿（v1.20）

- QuitAll 慢活移到线程池：置 Stopping + 「关闭中…」后禁用四个菜单项，KillDshHosts、删 host.pid、WaitPortClosed(8000) 后台跑，完成后 Post 回 UI 隐藏图标并 ExitThread；UI 全程可响应。
- 新增 _quitting 防重入：退出流程已开始时再次点击「退出并停止」不再弹确认框。
- Refresh_ 在 St.Stopping 时跳过 PortOpen()：停止态判定不依赖探测，去掉停止期间每秒最多 700ms 的卡顿。
- WMI 全部离开 UI 线程（3 处 ManagementObjectSearcher 此后都只在线程池执行）。
- 停止/退出路径未实机触发（会断开当前 3080 会话）；以编译 + 代码审查为准，待可打断窗口回归。

### 1.7 等 ready 再开页（P2，v1.21）

- 现象：v1.20 之后每次由启动器拉起服务，都会弹「没找到登录令牌 / 没能从日志里取到带 token 的地址」，打开裸地址。
- 根因：`Refresh_` 端口一通就认为可开页，线程池里只固定等 8 秒 token（`Cfg.WaitForUrl(8000)`）；本机 dsh web 是「先 bind 端口 → 起 MCP 子进程与插件 → 最后才打印带 token 的地址」，中间实测差 20 秒以上。日志证据：15:58:49 起服务、15:59:39 端口就绪、15:59:48 就弹警告，而 token 行 16:00:02 才落盘（晚 14 秒）。
- `Cfg.WaitForUrl(int)` 换成 `Cfg.WaitReadyUrl(int)`：轮询日志等 token 地址，拿到后再 `Cfg.ProbeHttp` GET 探活，服务真的应答（非 5xx）才返回；地址已在日志但探活连着失败 3 次也直接返回（探测环境问题不该耽误开页）。
- 自动开页等待上限 8 秒 → **90 秒**，全程线程池；封送回 UI 前再确认 `_state == St.Running`，等待期间被停掉就丢弃这次开页。
- 新增 `Cfg.ProbeHttp(url, ms)`：`HttpWebRequest` + `Proxy = null`、`AllowAutoRedirect = false`、`KeepAlive = false`，2.5 秒超时；401/403 视为「服务在应答」。
- `OpenUi()` 改为 `OpenUi(string url = null)`，把等到的地址直接传进去；真等满 90 秒才退回裸地址并提示。
- 反射实测：带 token 地址 303 → True，裸地址 401 → True，死端口 3999 → False；`WaitReadyUrl(6000)` 在日志已有 token 时 1 ms 返回。真实启动路径待下次重启回归。
### 1.8 启动耗时观测 BootTrace（v1.22，本轮）

- `internal static class BootTrace`：启动后起线程池循环（500 ms 一轮、5 分钟自停），WMI 找 cmd 下命令行含 `node` 的子进程，每 2 秒记 `TotalProcessorTime` 增量，每 4 秒记新出现的子进程（去重、命令行截断 120 字符）。
- 5 个标记点：已启动 dsh web / 日志里已出现 token 地址 / token 地址探活通过 / 端口就绪，服务已应答 / 打开浏览器。
- 只写 `launcher.log`、全程 try/catch，不改变启动逻辑；部署后每轮启动都有 `启动观测 +Ns` 行，用于把启动耗时拆成「CPU 密集」还是「等网络」。

### 1.9 关闭后菜单跟手（v1.22，本轮）

- 现象：DSH 停止后，托盘菜单鼠标划过，胶囊高亮要迟半秒。
- 根因：`Refresh_` 每秒调一次 `PortOpen()`（`Synchronous BeginConnect + WaitOne(700)`）；本机「没人监听」的回环端口不会立刻回绝，实测每次耗满 700 ms（710/702/702/706/714/702/703/703），UI 线程每秒被占七成。
- 改法：新增后台 `StartPortWatch()`（`PortOpenTimed(350)`，结果写 `_portUp`，运行时 900 ms / 停止时 350 ms 一轮）；`Refresh_` 只读 `PortUpCached()`。`PortOpen()` 保留 700 ms，给启动/停止等必须实时的路径。
- 未改：停止态点「启动服务」/ 双击托盘仍有一次 700 ms 真实探测（防止误判后起第二个 node）。
- 部署：20:46:51 rebuild 通过，sha `E99CB870…`，pid=47864；10 秒采样 CPU 0.000 秒、内存 43.1 MB、11 线程。
- 实机（2026-11-20 21:04）：停止服务后划动菜单即时加深、无卡顿（用户确认）；随后重启，21:06:33 打开带 token 页面，未弹令牌警告。

## 2. 构建与验证

构建命令：

~~~powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "D:\DSH_WorkSpace\Default_WorkSpace\dsh-launcher\rebuild.ps1"
~~~

本次结果（v1.22）：

- 2026-11-20 20:46:51 五步全部通过。
- 源码 exe / 安装 exe / dist exe 的 SHA-256 均为 E99CB8702F5DD6D4E84DD0ECF889D20BB58BBEDEC254B9A6074F042118967D04。
- 安装进程 pid=47864，Medium 完整性标签正常；dsh web 未动（node pid=10296 监听 3080，host.pid cmd=16268 / started=2026-11-20T20:39:18）。
- 资源采样（10 秒）：CPU 0.000 秒、WorkingSet 43.1 MB、11 线程。
- 便携包 v1.22：69,289 字节，4 个文件（exe 135,168 / diagnose.cmd 1,367 / launcher.ini 614 / 使用说明.txt 3,664）。
- 停止态已实机：21:04:08 停止服务 → 划动菜单无卡顿（用户确认）；21:04:12 重启，21:06:07 端口就绪、21:06:33 打开带 token 页面（无令牌警告）。

v1.21 结果（保留）：

- 2026-11-20 16:07:58 五步全部通过。
- 源码 exe / 安装 exe / dist exe 的 SHA-256 均为 83EF10651F33CA72CB7CE718F7EA2FCB5C69182D416B031CC2958844928E5F50。
- 安装进程 pid=22760，Medium 完整性标签正常；dsh web 未动（node pid=17976，host.pid started=2026-11-20T15:58:49），3080 仍监听。
- 反射实测：`ProbeHttp` 带 token 地址 = True（303）、裸地址 = True（401，在应答）、死端口 3999 = False；`WaitReadyUrl(6000)` 在日志已有 token 时 1 ms 返回。
- 启动/停止/重启路径未实机触发（会断开当前 3080 会话），以编译 + 代码审查为准。

v1.20 结果（保留）：

- 2026-11-20 15:40:49 五步全部通过。
- 源码 exe / 安装 exe / dist exe 的 SHA-256 均为 501A252CA1A13119C0D6755C0BE53EDDF5A4DC8AA3C657BC3CD4B5D71EAFB5D3。
- 安装进程 pid=36968，Medium 完整性标签正常；dsh web 未动（host.pid started=2026-11-20T15:13:35），3080 仍监听。

v1.19 结果（保留）：

- 2026-11-20 11:50:31 五步全部通过。
- 源码 exe / 安装 exe / dist exe 的 SHA-256 均为：
  8F8DDF97B4E9456B2F75E6A4A7AAF770C93B941B697D7A0B3A8ED79673CEB5F7
- 安装进程 pid=1284，Medium 完整性标签正常。
- 当前 dsh web 没被重启（host.pid 仍是 started=2026-11-20T09:40:09）；3080 仍由 node pid=28652 监听。
- ReadLogTail 反射测试返回 dsh-web.log 末尾 287 字符。
- 主程序编译通过；预览程序有 5 个 CS0649（AuxDir / AuxRun / AuxOpenDir / IconDir / Verbose），是预览不链接 DshLauncher.cs 的正常现象。

没有实机触发「停止服务 / 重启服务 / 启动失败」的破坏性路径，因为会断开当前正在使用的 dsh web；这些路径以代码审查 + 编译 + ReadLogTail 验证为准。若下一轮要实测，请选一个没有正在对话的时段。

## 3. 当前 git 状态

上一轮提交：2284d47（v1.19：启动失败快失败 + 安装复制重试）与 017c6b1（docs：WorkBuddy 测试交接补充）。

v1.20 = 51c03f1（B1 停止/退出期间菜单冻结修复）。

v1.21 已提交，提交信息：

~~~text
v1.21：等 ready 再开页（等 token 落盘 + HTTP 探活通过才开浏览器）
~~~

v1.22 已提交，提交信息：

~~~text
v1.22：关闭 DSH 后菜单不跟手修复（端口探测移出 UI 线程）+ 启动观测 BootTrace
~~~

提交 hash 以 `git log -1` 为准；提交后 `git status --short` 应为空。

本次提交包含：DshLauncher.cs、DshLauncher.exe、PROJECT-REPORT.md、README.md、HANDOVER.md、dist/DSH-Web-启动器/DshLauncher.exe、dist/DSH-Web-Launcher-portable-v1.22.zip。

dist 里旧的 v1.18 / v1.6 zip 保留作历史，未删除。

## 4. 已知风险与未完成项

1. B5 未修：非中文 Windows + 中文路径仍依赖 8.3 短名 / ANSI 兜底。
2. QuitAll 已在 v1.20 后台化：KillDshHosts/WaitPortClosed 在线程池执行，UI 收尾期间保持响应；「停止服务」路径已实机（21:04:08 停止、菜单无卡顿）；「退出并停止」仍未实机触发。
3. 开页最多等 90 秒（v1.21）：若 dsh web 打印 token 地址晚于 90 秒（机器更慢、MCP 卡住），仍会退回裸地址并提示；此时页面会 401，需要手动补 token。
4. 3080 被非 dsh 程序占用时仍会被当成「服务已运行」；token 被外部终端重启换掉时缓存地址会过期。
5. 启动失败快失败路径未实机触发；如果 Process 句柄拿到的不是 cmd（例如以后改成直接起 node），需要重新核对 HasExited 语义。
6. rebuild 的复制重试是概率性保护；若目标目录被 AV 长时间锁定，仍可能失败，日志会指出 hash mismatch。
7. dist 里保留 v1.18 / v1.20 / v1.21 / v1.6 旧包；如需清理历史产物要先征得用户同意。
8. 关闭态点「启动服务」/ 双击托盘仍有一次 700 ms 真实探测（有意保留：避免误判后拉起第二个 node 撞端口）；后台缓存最多晚约 1 秒反映状态变化。
9. BootTrace 只写日志、5 分钟自停，会让 launcher.log 每轮多几十行；排查启动时 `Select-String 启动观测`。

## 5. 下一轮建议

1. 已验收（2026-11-20 21:04，用户确认）：停止服务后划过菜单，胶囊立即加深，不再等半秒。
2. 在可打断服务的窗口实测：运行中 → 停止 → 重启 → 再停止，确认 UI 不冻结、状态行进入「停止中…」后回「未运行」，并检查 host.pid 清理。
3. 实测启动失败：临时把 launcher.ini 的 dsh 指向一个立即退出的命令，确认 1 秒级转 Error 且通知带日志尾部；测完恢复配置。
4. 若要修 B5：改为 ProcessStartInfo 直接起 dsh.cmd，stdout/stderr 重定向，不再生成 cmd 文件。
5. 版本号规则：v1.22 是当前 exe / 文档 / 便携包版本；README 和 PROJECT-REPORT 已同步。

## 6. 关键文件

- DshLauncher.cs：托盘、启停、WMI、端口、token 等待与探活、日志尾部、图标合成；2058 行（含 BootTrace 与后台端口探测）。
- DshMenu.cs：iOS 风格自绘菜单、DPI、两行状态、呼吸球；960 行。
- rebuild.ps1：编译 → 停旧进程 → Medium 副本（含复制重试）→ 静默重启 → hash/标签自检；122 行。
- PROJECT-REPORT.md：§5.16 是 v1.22 端口探测；§5.15 是 v1.22 BootTrace；§5.14 是 v1.21；§5.13 是 v1.20；§5.11 是 v1.18 review；§5.7 是原推迟项状态。
- README.md：用户向说明；v1.22 便携包与两行状态说明。
- dist\DSH-Web-启动器\：便携包目录（exe / ini / 使用说明 / diagnose.cmd）。
- dist\DSH-Web-Launcher-portable-v1.22.zip：69,289 字节，4 个文件（exe 135,168 / diagnose.cmd 1,367 / launcher.ini 614 / 使用说明.txt 3,664）。

## 7. 快速自检

~~~powershell
Get-Process DshLauncher | Select-Object Id,StartTime,Path
Get-NetTCPConnection -LocalPort 3080 -State Listen
Get-Content (Join-Path $env:LOCALAPPDATA 'DshLauncher\launcher.log') -Tail 20
~~~
