# DSH Web 启动器 — 交接文档

> 日期：2026-11-20
> 版本：v1.18（第十二轮全量 review 修复）
> 仓库：D:\DSH_WorkSpace\Default_WorkSpace\dsh-launcher
> 安装位置：%LOCALAPPDATA%\Programs\DshLauncher\
> 当前运行：启动器 pid=6032，dsh web 监听 3080，host.pid 记录 cmd=13892 / port=3080 / started=2026-11-20T09:40:09

## 1. 这次做了什么

v1.17 交付后按用户要求做全量 review，重点处理了 §5.7 里推迟的「停止/重启卡 UI」「误杀别的 dsh web」「taskkill 计数不准」，以及并发启停、watchdog 过期回写、菜单字体释放等边界。

### 1.1 状态机与启动令牌

- St 枚举增加 Stopping：Unknown / Stopped / Starting / Running / Stopping / Error。
- Refresh_ 在锁内先判断 Stopping，停止期间不会再被端口探测改回 Running；按钮同步置灰。
- 新增 _startToken：StartHost / StopHost / QuitAll 每次自增；watchdog 每轮以及落 Running/Error 前都校验 token 与当前 St.Starting，旧启动线程不能覆盖新的启停状态。
- StartHost 写启动脚本失败、Process.Start 返回 null 都立即置 Error 并提示，不再空等 120 秒。
- 有 dsh.cmd 时不再求值 Cfg.NodeExe / DshBin（会枚举 PATH 与 pnpm 目录）；只有回退到 node + bin.js 才扫描。

### 1.2 停止 / 重启异步化（B1）

- StopHost(bool ask, bool restartAfter)：先在 UI 线程置 Stopping 并显示「停止中…」，再丢线程池执行 KillDshHosts + 删除 host.pid + WaitPortClosed(15000)。
- 完成后 Post 回 UI，校验 token，置 Stopped，再根据 _restartAfterStop 决定 StartHost。
- 停止中再次点停止只合并 _restartAfterStop，不重复起线程。
- RestartHost 走 StopHost(false, true)；UI 不再冻结最多 15 秒。
- QuitAll 仍同步（最多 8 秒），因为它是退出路径，进程随后结束；已在 §5.4 记录。

### 1.3 按端口收进程（B3 / B6）

- KillDshHosts 解析 host.pid 的 cmd= 与 port=；只有 cmdPid > 0 且 recordedPort 与 Cfg.Port 一致（或没有 port=）才使用。
- CmdLineOf 仍校验命令行含 start-dsh-web 或 dsh，防 pid 复用误杀。
- 启发式枚举 node.exe 时只收 dsh\lib\bin.js + " web" + CommandLineHasPort(cl, Cfg.Port) 的进程。
- CommandLineHasPort 支持 --port 4000 与 --port=4000；没有 --port 时只对默认 3080 放行。
- FindDshStartTime 也按端口过滤，避免多端口下显示错启动时间。
- taskkill 只有 WaitForExit(5000) 且 ExitCode == 0 才 killed++；超时 / 非零退出写日志。

### 1.4 菜单与预览

- DshMenu.ApplyScale 释放旧的第二行字体；两行高度改用 Graphics.GetHeight 计算，避免高 DPI 下文字重叠。
- tools/MenuPreview.cs 截图不再把背景窗体置顶/抢焦点，改为 DoEvents + 短暂等待 + CopyFromScreen，避免预览时菜单自动关闭。
- 150% 下菜单 380×528（v1.17 是 380×532）；两行「状态：运行中 / 已运行 57 分钟」完整。

### 1.5 便携与诊断

- dist\DSH-Web-启动器\diagnose.cmd 从同目录 launcher.ini 读取 port=（默认 3080），不再写死；解决改端口后诊断查错端口。
- 根目录补上 launcher.ini 模板（此前 README 声称有、实际缺失）。
- README 更新到 v1.18 便携包；使用说明更新端口与旧版本措辞。

## 2. 构建与验证

构建命令：

~~~powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "D:\DSH_WorkSpace\Default_WorkSpace\dsh-launcher\rebuild.ps1"
~~~

本次结果：

- 2026-11-20 11:29:57 五步全部通过。
- 源码 exe / 安装 exe / dist exe 的 SHA-256 均为：
  7EEF137B3EC04AF87E9BC3D1C0ABECF5B65938D7B8F33F2CFBB83E9D3FF2455C
- 安装进程 pid=6032，Medium 完整性标签正常。
- 当前 dsh web 没被重启（host.pid 仍是 started=2026-11-20T09:40:09）。
- MenuPreview 1.5 实测 380×528，截图 docs\_review-preview.png（临时）。
- 主程序编译通过；预览程序有 5 个 CS0649（AuxDir / AuxRun / AuxOpenDir / IconDir / Verbose），是预览不链接 DshLauncher.cs 的正常现象。

没有实机触发「停止服务 / 重启服务」的破坏性路径，因为会断开当前正在使用的 dsh web；这条路径以代码审查 + 状态机/预览验证为准。若下一轮要实测，请选一个没有正在对话的时段。

## 3. 当前 git 状态

上一次提交：ca38ecd（v1.7–v1.12：托盘菜单重做与 DPI 预缩放、辅助功能模块、通知/自启、构建脚本）。

本次尚未提交，git status 主要项：

- M DshLauncher.cs
- M DshMenu.cs
- M tools/MenuPreview.cs
- M PROJECT-REPORT.md
- M README.md
- M DshLauncher.exe
- M tools/MenuPreview.exe
- M dist/DSH-Web-启动器/DshLauncher.exe
- M dist/DSH-Web-启动器/diagnose.cmd
- M dist/DSH-Web-启动器/使用说明.txt
- ?? launcher.ini（根目录模板，应提交）
- ?? docs/menu-two-line.png（README 引用，应提交）
- ?? dist/DSH-Web-Launcher-portable-v1.18.zip（产物，按仓库习惯提交）
- ?? docs/_review-preview.png（临时预览图，提交前删除）

建议提交信息：

~~~text
v1.13-v1.18：状态行两行/运行时长、代理清单列、review 修复（异步启停/按端口收进程）
~~~

## 4. 已知风险与未完成项

1. B2 未修：dsh 命令存在但进程立即崩溃时，状态仍可能停在「启动中」到 120 秒 watchdog 超时；点停止可用，但不会自动读日志尾部转 Error。
2. B5 未修：非中文 Windows + 中文路径仍依赖 8.3 短名 / ANSI 兜底。
3. QuitAll 仍在 UI 线程同步收进程并 WaitPortClosed(8000)，最坏约 8 秒。
4. 3080 被非 dsh 程序占用时仍会被当成「服务已运行」；token 被外部终端重启换掉时缓存地址会过期。
5. dist 里仍保留旧的 v1.6 zip，README 已指向 v1.18；如要清理旧包需用户确认（历史产物）。
6. docs/_review-preview.png 是临时文件，提交前删除；menu-two-line.png 是文档图，应保留。

## 5. 下一轮建议

1. 提交本次改动（含根 launcher.ini、docs/menu-two-line.png、v1.18 zip），确保工作区干净。
2. 在可打断服务的窗口实测一次：运行中 → 停止 → 重启 → 再停止，确认 UI 不冻结、状态行进入「停止中…」后回「未运行」，并检查 host.pid 清理。
3. 若要继续修 B2：把 cmd pid 存下来，watchdog 每 2 秒查 Process.HasExited；退出即读 dsh-web.log 尾部并置 Error。
4. 若要修 B5：改为 ProcessStartInfo 直接起 dsh.cmd，stdout/stderr 重定向，不再生成 cmd 文件。
5. 版本号规则：v1.18 是当前 exe / 文档 / 便携包版本；README 和 PROJECT-REPORT 已同步。

## 6. 关键文件

- DshLauncher.cs：托盘、启停、WMI、端口、token、图标合成；1634 行。
- DshMenu.cs：iOS 风格自绘菜单、DPI、两行状态、呼吸球；902 行。
- rebuild.ps1：编译 → 停旧进程 → Medium 副本 → 静默重启 → hash/标签自检；114 行。
- PROJECT-REPORT.md：§5.11 是本次 review 的完整记录；§5.7 是原推迟项状态。
- README.md：用户向说明；v1.18 便携包与两行状态说明。
- dist\DSH-Web-启动器\：便携包目录（exe / ini / 使用说明 / diagnose.cmd）。
- dist\DSH-Web-Launcher-portable-v1.18.zip：66,005 字节，4 个文件。

## 7. 快速自检

~~~powershell
Get-Process DshLauncher | Select-Object Id,StartTime,Path
Get-NetTCPConnection -LocalPort 3080 -State Listen
Get-Content (Join-Path $env:LOCALAPPDATA 'DshLauncher\launcher.log') -Tail 20
~~~

预期：启动器进程存在；3080 由 node 监听；日志有「启动器已运行」；托盘右键菜单两行状态完整。
