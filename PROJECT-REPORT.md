# DSH Web 一键启动器 — 项目报告

> 版本：1.22（2026-11-20，第十六轮补丁：关闭 DSH 后托盘菜单鼠标划过不跟手——端口探测移出 UI 线程；另含启动耗时观测 BootTrace，见 §5.15、§5.16）
> 上一版：1.21（2026-11-20，第十五轮补丁：P2 等 ready 再开页——等 token 地址落盘 + HTTP 探活通过才开浏览器，见 §5.14）；再上一版 1.20（第十四轮：B1 停止/退出期间菜单冻结——QuitAll 慢活后台化、停止中跳过端口探测、_quitting 防重入，见 §5.13）
> 更早：1.17（2026-11-20，第十一轮补丁：状态球与呼吸外圈整体收小，见 §5.10）；1.16（2026-11-20，第十轮补丁：状态球恢复浅色外圈，并做成 2.6 秒一张一缩的呼吸特效，见 §5.10）；1.15（2026-11-20，第九轮补丁：状态球改为单圆——去掉光晕，与下方图标列对齐并垂直居中，见 §5.10）；1.14（2026-11-20，第九轮：状态行改两行——第一行状态、第二行已运行时长，菜单宽度回到 380，见 §5.10）；1.13（第八轮：状态行加已运行时长）；1.12（第七轮：辅助功能接上 opencode-go 代理，见 §5.9）；v1.10（第五轮：日志 / 通知 / 自启 / 菜单宽度 / 访问缓存 / 模块扫描 / 构建脚本）；v1.6（第四轮：新增「退出启动器并停止服务」+ 停止服务改为按记录收进程树，见 §4.17）
> 历史：v1.10（2026-11-16，第五轮：非中断类清理——日志 / 通知 / 自启 / 菜单宽度 / 访问缓存 / 模块扫描 / 构建脚本）；v1.6（2026-10-27，第四轮：新增「退出启动器并停止服务」+ 停止服务改为按记录收进程树，见 §4.17）
> 位置：`<仓库目录>\`
> 装机位置：`%LOCALAPPDATA%\Programs\DshLauncher\`
> 用途：**双击一个图标就能起 dsh web**，不再需要开 cmd / PowerShell 敲命令。

**源码规模**：`DshLauncher.cs` 2058 行 + `DshMenu.cs` 960 行 + `rebuild.ps1` 122 行，主程序 `/warn:4` 零警告；
装机 exe 与源码目录 exe 的 SHA-256 一致（由 `rebuild.ps1` 校验），启动器常驻托盘，3080 由 node 监听。

---

## 1. 背景与目标

**原始痛点**：每次用 DSH Web 都得先开 PowerShell（或 cmd），敲 `dsh web`，再等它自己弹浏览器。
关掉终端 = 服务挂掉；重启电脑 = 全部重来。

**目标（用户原话归纳）**：

1. 桌面上双击一个图标就能启动，全程不出现命令行窗口；
2. 启动后自动打开 DSH 的网页界面；
3. 右下角托盘留一个图标（像 `Echotrophy/dsh_web_launcher` 那样），右键能**停止 / 重启**服务；
4. 重复双击不能多开服务、不能多开浏览器标签页。

以上 4 条现在全部达成（第 3 条的托盘图标在 10-27 下午才真正修好，见 §4.5）。

---

## 2. 最终方案

一个自己写的小型 C# 托盘程序 `DshLauncher.exe`，**没有任何第三方依赖**，用系统自带的 .NET Framework 编译器编译。

```
桌面「DSH Web」快捷方式
        │  双击
        ▼
DshLauncher.exe (WinExe 子系统 · 无控制台 · 托盘常驻)
        │
        ├─ 命名 Mutex「Local\DshWebLauncher.Single」──→ 已有实例？→ 只是唤起它开页面（单实例）
        ├─ TcpClient 探测 127.0.0.1:3080 ──→ 通？→ 直接开浏览器（不重启服务）
        │                                   └─ 不通？→ 启动服务
        │
        ├─ 起服务：写 %LOCALAPPDATA%\DshLauncher\start-dsh-web.cmd（纯 ASCII）
        │          内容 = cd /d 工作目录 + 调 dsh.cmd web --port 3080 --no-open，路径一律 8.3 短名
        │          不切 chcp：批处理字节与 cmd 代码页保持一致（§4.15）
        │          以 CreateNoWindow + Hidden 启动，输出重定向到 dsh-web.log
        │          轮询端口（最长 120 秒）→ 再等日志里的 token 地址 + HTTP 探活通过（最长 90 秒）→ 用 explorer.exe 打开
        │
        └─ 托盘菜单（iOS 风格自绘、按鼠标所在屏 DPI 缩放、每行带图标槽）：
           状态球 / 打开界面 / 重启 / 停止 / 开机自启 / 辅助功能▸（模块目录）/ 打开日志 / 退出 / 退出并停止
           托盘图标 = DeepSeek 鲸鱼 + 右下角状态点（运行中绿 / 启动中橙 / 失败红 / 未运行灰）
```

**为什么是 exe 而不是脚本**：脚本要探测端口就得拉 powershell/netstat → 每次都闪黑框；
VBScript 没有 GUI 子系统、没有真正的单实例保护、错误信息（800A01B6 / 800A0046）完全不可诊断。
换成编译过的 WinExe 之后这三个问题一次性消失。

---

## 3. 文件清单

### 项目目录 `<仓库目录>\`

| 文件 | 作用 |
| --- | --- |
| `DshLauncher.cs` | 启动器主程序源码（托盘、启停、探测端口、取 token、开页面、图标合成） |
| `DshMenu.cs` | **iOS 风格托盘菜单**（自绘 + DPI 缩放），与主程序分开编译 |
| `app.manifest` | 声明 **PerMonitorV2** DPI 感知 + Windows 10/11 supportedOS（不加这个菜单必然发虚） |
| `logo256.png` | DeepSeek 鲸鱼标（由 `deepseek-color.svg` 经 Edge 无头渲染得到），编译时以 `/resource` 内嵌 |
| `dsh.ico` | 多尺寸图标（16/20/24/32/48/64/128/256），只给 exe 与快捷方式用；托盘图标是运行时合成的 |
| `toast-inverted.png` | 反色版鲸鱼，安装成 `dsh-toast.png` 后专给 Windows 通知用（原因见 §4.18） |
| `icons\` | 菜单行首图标（`<菜单文字>.png`，8 张 64px 透明 PNG + `说明.txt`），构建时随 exe 一起装到安装目录 |
| `DshLauncher.exe` | 编译产物（**注意：低完整性标签，直接双击会没有托盘图标**，见 §4.5） |
| `launcher.ini` | **运行期配置**（端口 / 工作目录 / dsh 路径 / 模块目录 / verbose），安装目录里那份生效，本目录这份是模板 |
| `rebuild.ps1` | 五步「编译 → 停旧进程 → 安装带 Medium 标签的副本 → **静默**重启 → 自检」；安装后与启动后都校验 SHA-256，复制失败直接报错 |
| `dist\` | 便携版成品：`DSH-Web-启动器\`（exe + ini + 使用说明 + diagnose.cmd）与 v1.19 便携 zip（66,537 字节，4 个文件）；v1.18 / v1.6 旧包保留作历史 |
| `docs\` | 设计图与证据图：`menu-final.png`（150% 悬停态）、`menu-states.png`（四状态）、`icon-states.png`（图标四态）、`menu-aux-module.png`（辅助功能二级菜单）、`menu-two-line.png`（两行状态 + 运行时长 + 呼吸外圈）、`icon-log-notepad.png`、`icon-autostart.png`、`notification-icon-before/after.png`（通知图标取证） |
| `tools\MenuPreview.*` | 单独预览菜单外观（`MenuPreview.exe 1.5 [run\|start\|stop\|err] [hover]`），不属于运行链路 |
| `tools\MakeIco.*` | 由 `logo256.png` 生成多尺寸 `dsh.ico`（≤64 用 DIB，128/256 用 PNG 压缩） |
| `tools\IconPreview.*` | 把托盘四状态图标渲染成 `docs\icon-states.png` 供核对 |
| `README.md` | 面向使用者的简明说明 |
| `PROJECT-REPORT.md` | 本文件（面向使用者 + 面向后续维护者） |
| `HANDOVER.md` | 交接文档：v1.19 改动、构建/验证、当前 git 状态、剩余风险与下一轮建议 |

### 安装目录 `%LOCALAPPDATA%\Programs\DshLauncher\`

| 文件 | 说明 |
| --- | --- |
| `DshLauncher.exe` + `dsh.ico` + `dsh-toast.png` | **Medium 完整性**副本，桌面/开始菜单快捷方式指向这里；`dsh-toast.png` 专给 Windows 通知用（§4.18） |
| `icons\` | 菜单图标（构建时从仓库 `icons\` 复制） |
| `launcher.ini` | 运行期配置（端口 / 工作目录 / dsh 路径 / 模块目录 / verbose） |

### 运行时状态 `%LOCALAPPDATA%\DshLauncher\`

v1.3 起从 `%TEMP%\.dsh-launcher` 搬到这里（临时目录会被清理、也不适合放"别人的机器"上的状态）；
第一次运行会把旧目录里的 `host.pid / dsh-web.log / url.txt` 自动拷过来。

| 文件 | 说明 |
| --- | --- |
| `start-dsh-web.cmd` | 每次启动服务时重写（含 `cd` 到工作目录、`--no-open`、输出重定向） |
| `dsh-web.log` | **dsh web 的控制台输出**，带 token 的地址就在其中一行 |
| `url.txt` | **最近一次带 token 的地址**（日志被清掉/轮转也不会丢，见 §4.14） |
| `host.pid` | 上次启动的 cmd pid / 端口 / 时间 |
| `launcher.log` | 启动器主日志（UTF-8；超过 1 MB 改名为 `launcher.log.1`，保留上一轮现场） |
| `aux\` | 「辅助功能」模块目录（`aux_dir=` 可改）；首次运行写入 `说明.txt` |

---

## 4. 排障时间线（每个坑的根因）

这一节是本报告最有价值的部分：**同样的现象不要再查第二遍**。

### 4.1 一次点击打开两个（甚至四个）界面

- **症状**：双击一次 → 浏览器出 2 个标签页；点两次 → 4 个。
- **根因**：`dsh web` 自己就会开浏览器（`openBrowser` 默认 `true`，日志会打印
  `dsh web: opening the default browser; pass --no-open to disable`），启动器又开了一次。
- **解法**：拉起服务时固定传 `--no-open`，浏览器只由启动器开一次。
- **证据**：`@deepseek-ai/dsh-web/lib/index.js` 里 `openBrowser: z.boolean().default(true)`。

### 4.2 cmd 黑框闪好几下

- **根因**：旧 VBS 靠 `powershell` / `netstat` 探端口，每次都是可见控制台；
  而且 `shell.Exec("wscript //B ...")` 拿不到 stdout（`//B` 没有管道）。
- **解法**：探测改成进程内 `TcpClient`（毫秒级、无进程）；程序本体是 GUI 子系统（PE Subsystem=2），不创建控制台。

### 4.3 重复启动 / 起出好几个 dsh web

- **根因**：WMI 查端口不可靠（`WHERE LocalPort=...` 与 `.Count` 在端口确实监听时仍返回 0），
  于是每次双击都误判「没在跑」→ 又拉一个新进程。
- **解法**：① `TcpClient` 连接测试；② 命名 Mutex 单实例 + 命名事件唤起已有实例。

### 4.4 VBScript 报错 `800A01B6` / `800A0046`

- `800A01B6`：把 WMI 查询结果（集合）当函数调用 —— 对象不支持此属性或方法。
- `800A0046`（行 0 字符 1，没有权限）：错误发生在 `ExecuteGlobal` 载入的公共库里，
  这种写法没有文件坐标，报错位置永远是 0 行 —— 根本没法定位。
- **结论**：这条路直接放弃，换编译型程序（可定位、可断点、有异常栈）。

### 4.5 托盘图标死活不出现（最隐蔽的一个）

- **症状**：程序明明在跑，日志正常，`NotifyIcon.Visible = true`，但右下角没有图标；
  而且 `HKCU\Control Panel\NotifyIconSettings` 里**根本没有**它的条目 —— 不是被折叠，是压根没注册成功。
- **根因**：**文件完整性标签**。凡是从 DSH 会话里创建的文件都会被打上 `Low Mandatory Level`，
  而 **可执行文件带 Low 标签时，进程会以 Low 完整性运行**（哪怕 explorer.exe 用 Medium 去拉起它）。
  Low 进程往 Medium 的任务栏发 `Shell_NotifyIcon` 会被 UIPI 拦掉，返回 `err=5`（拒绝访问）。
- **验证**：探针程序打印 `Shell_NotifyIcon(NIM_ADD) => False err=5`；同一进程自报完整性 `Low (0x1000)`，
  explorer.exe 是 `Medium (0x2000)`。
- **解法**：装到 `%LOCALAPPDATA%\Programs\DshLauncher\`，并由**中完整性进程**写文件 + 打标签：
  `schtasks /Create ... /RL LIMITED` 跑一个 `.cmd`，里面 `icacls <dir> /setintegritylevel "(OI)(CI)Medium"`。
  之后 `NotifyIconSettings` 立刻出现条目，再用 `IsPromoted=1` 把它固定显示在托盘上。
- **推论**：`D:\...\dsh-launcher\DshLauncher.exe`（源码目录那份）是 Low 标签，
  **直接双击它不会有托盘图标**；要跑请跑安装目录那份或走快捷方式。`rebuild.ps1` 已经处理了这一点。

### 4.6 SmartScreen「已保护你的电脑 / 发布者未知」

- **根因**：exe 没有数字签名（`Get-AuthenticodeSignature` → `NotSigned`），且是新编译的、没有信誉积累。
  没有 Mark-of-the-Web（只有 `:$DATA` 流），所以纯粹是 SmartScreen 对陌生未签名程序的拦截。
- **处理**：点「仍要运行」即可。**每次重新编译 exe 后 hash 变了，可能再弹一次**，属正常。
  自签名证书解决不了这个问题（SmartScreen 只认受信任的 CA 信誉）。

### 4.7 `dsh web authentication required; reopen the URL printed by dsh web`

- **根因**：新版 dsh web 只认**带 token 的地址**。裸地址 `http://127.0.0.1:3080/` 返回 **401**；
  带 token 的地址返回 **303 + Set-Cookie**（实测）。而启动器之前开的就是裸地址。
- token 每次启动都变，宿主会把它打进日志最后一行：
  `dsh web: http://127.0.0.1:3080/?token=xxxxxxxx`
- **解法**：启动器每次开页面前从 `dsh-web.log` 里取**最后一条**带 token 的地址；
  重启服务时清掉 URL 缓存（旧 token 立即失效）。
- **坑中坑**：读这个日志必须用 `FileShare.ReadWrite` 打开 —— 宿主进程一直占着它在写，
  默认的 `File.ReadAllLines` 会抛「文件正被另一进程使用」。
- **唯一例外**：如果你自己在终端里敲 `dsh web` 起服务，启动器读不到那份日志，只能开裸地址（会 401），
  这时请直接复用终端里打印的地址。

### 4.8 重启服务后新进程起不来（`EADDRINUSE`）

- **症状**（历史日志里出现过）：`dsh: startup failed ... EADDRINUSE: address already in use 127.0.0.1:3080`。
- **根因**：`taskkill` 之后 node 进程退出与端口真正释放之间有间隙，立刻重启会撞端口。
- **解法**（本次 review 修复）：停止后轮询等待端口真正关闭（最长 15 秒）再启动，日志会写「端口已释放」。

### 4.9 右键菜单发虚、字挤在一起（DPI 未声明）

- **症状**：弹出的菜单文字糊、行距忽宽忽窄，和系统其它菜单不是一套观感（用户原话「右键菜单分辨率好奇怪」）。
- **根因**：exe 没有声明 DPI 感知（没有 manifest），Windows 把 96 dpi 布局的窗口整体位图拉伸到 150%——字体是小字号放大，必然糊；同时 WinForms 默认菜单是白底直角、条目宽度各自为政。
- **解法**：
  1. 加 `app.manifest`：`dpiAware = true/pm` + `dpiAwareness = PerMonitorV2`，让窗口按 150% 的真实像素自己排布；
  2. 新增 `DshMenu.cs` 全部自绘（圆角、悬浮胶囊、细分割线、语义色：主操作蓝 `#007AFF`、危险操作红 `#FF3B30`、次要文字灰 `#8E8E93`）；
  3. 所有尺寸都是「设计像素 × 缩放比」，缩放比取**鼠标所在那块屏**的有效 DPI（`GetDpiForMonitor`，失败退 `GetDeviceCaps`），多屏不同缩放也能各自正确；
  4. 条目统一按最宽条目定宽（ToolStrip 默认不会拉齐，否则悬浮胶囊长短不一、右侧对勾被裁掉）。
- **踩坑记录**（下次改菜单别重犯）：
  - 设了自定义 `Renderer` 之后**不能**再设 `RenderMode = Professional`，会把 Renderer 换回系统实现，强转时抛 `InvalidCastException`；
  - `OnRenderMenuItemBackground` / `OnRenderItemText` 里的 **Graphics 原点已经是条目左上角**（item 内部坐标），再叠 `Bounds.Top/Left` 会把对勾画到条目外 255 px 处，表现为「死活看不见」；
  - 条目背景事件的裁剪区是 `ContentRectangle`（不含右侧对勾留白），要让胶囊铺满整行必须临时放宽 clip。
- **验证**：同一份代码在不同缩放下实测菜单尺寸 `1.0→202×306`、`1.25→236×370`、`1.5→278×418`、`2.0→350×548`，同比放大；`tools\MenuPreview.exe 1.25` 可随时复查外观。

### 4.10 菜单留白怎么都对不上（`Menu.Padding` 被吃掉）

- **症状**：用户三次反馈同一个问题，换着说法：「分割线之间留白太多」「最下面的『退出启动器』选中后下方没留白」。
- **根因**：`ToolStripDropDownMenu` **根本不理会 `Menu.Padding`**——把它临时改成 16×缩放重编译，菜单尺寸 278×416 一个像素都没变。所以「顶部/底部离边缘的留白」从来没有存在过；而条目之间的空隙来自 `ToolStripItem.Margin` + 分割线自身高度，两边量的根本不是同一段距离。
- **解法**：
  1. 首尾各插一条**透明占位条目**（`Tag="spacer"`、`Enabled=false`、`AutoSize=false`、高度 `sepV + 2×缩放`）撑出上下边框留白；
  2. 全菜单**只留一个变量** `sepV = 3×缩放`：分割线上下间距、菜单上下边框留白都由它推导，改一个数三处一起变；
  3. 状态行上下内边距取同值，于是「球 + 文字」自动落在「顶边 → 第一条分割线」这一段的正中。
- **验证方法**（比肉眼靠谱）：截图后用 `System.Drawing` 沿 `x=100` 一列逐行判色（背景 250,250,252 / 悬浮 238,238,243 / 分割线 229,229,234），打印每段起止行。150% 下最终三处间距 8 / 9 / 9 px。
- **别再犯**：想靠 `ToolStripItem.Padding` 做缩进——在「关掉图片栏 + 强制等宽」的组合下它也不生效，文字位置只能自己用 `TextRenderer` 画。

### 4.11 托盘图标换成 DeepSeek 鲸鱼 + 状态点

- **需求**：用户给了 `deepseek-color.svg`，要求「替换快捷方式和托盘的图标，托盘图标右下角加个小圈同步显示 DSH 状态」。
- **做法**：
  1. 本机没有 ImageMagick/Inkscape → 用 **Edge 无头**把 SVG 渲成 256×256 透明 PNG（`--headless=new --default-background-color=00000000`）；
  2. `tools\MakeIco.cs` 手写多尺寸 ICO：≤64 用 DIB（BitmapInfoHeader + BGRA + AND 掩码），128/256 用 PNG 压缩条目；
  3. 鲸鱼标作为 `/resource:logo256.png,DshLogo.png` 编进 exe，运行时与状态点合成为 16/20/24/32/48 五个尺寸（`badge = 34%`、白圈 `ring = 6%`，右下角留位）；
  4. 状态色沿用菜单语义：运行中绿、启动中橙、出错红、未运行灰。
- **踩坑**：`System.Drawing.Icon` **不认纯 PNG 条目的 ICO**（报 `Requested range extends past the end of the array`），必须至少有一个 DIB 条目；另外 `Assembly.LoadFrom` 反射调私有方法会抛 `0x80131515`，所以单独写了 `tools\IconPreview.cs`（`/main:IconPreview.Program`）来出图。

### 4.12 每次改代码就多出一个 dsh web 标签页

- **症状**：用户问「你刚刚修的时候，或者说每次你改的时候都会创造新的dsh web标签页，为啥呢」。
- **根因**：`Refresh_()` 的规则是「端口通 + 这次还没开过页 + 非静默 → 自动开页」，这正是双击快捷方式能进界面的原因。但 `rebuild.ps1` 重启启动器时**没带 `--silent`**，于是每重建一次就等于替用户双击了一次；而重建往往顺带重启 dsh web，token 变了，旧的标签页直接 401。
- **解法**：
  1. `rebuild.ps1` 重启步骤改成跑一个 `.cmd` 包一层、内容是 `"…\DshLauncher.exe --silent"`；
  2. 自动开页再收紧：只有「这次是我把服务拉起来的」（`_startedByUs`）才自动开页，服务本来就在跑就不擅自弹窗；
  3. 托盘六项菜单与 `OpenFromSecondInstance()` 一律先 `_silent = false` 再执行——否则开机自启的静默实例点「重启服务」不会开页。
- **验证**：重建后日志只有 `=== 进程启动 pid=… ===` 与 `启动器已运行（登录自启，静默模式）`，**没有** `打开界面` 行。

### 4.13 `rebuild.ps1` 自己出的两个错（`schtasks` 与 BOM）

- **错 1**：`schtasks /Create /TR "\"路径\DshLauncher.exe\" --silent"` 报 `ERROR: The system cannot find the file specified.`，启动器直接没起来（自检抛 `launcher did not start`）。改成让计划任务去跑一个 `.cmd` 才干净。
- **错 2**：脚本里 `Set-Content -Path $startCmd …` 报 `Cannot bind argument to parameter 'Path' because it is null.`，且**报错行号总比文件实际行号少 3 行**。
- **真因**：`rebuild.ps1` 存成了**无 BOM 的 UTF-8**，PowerShell 5.1 按 ANSI(GBK) 解析中文注释，语句边界错位、变量赋值落进字符串，才会出现这种完全不搭界的错误。
- **规矩**：`.ps1` 里只要有中文，就必须 **UTF-8 带 BOM**（`[System.Text.UTF8Encoding]::new($true)` 写入）；`.cmd` 路径写死成 `"$dir\_start.cmd"` 字面量，别依赖 `$PSScriptRoot`/`Join-Path`。
- **v1.10 起**：`rebuild.ps1` 已改成**全 ASCII**（注释也用英文，路径靠 `%~dp0` / `%LOCALAPPDATA%` 运行时展开），不再依赖 BOM；
  脚本里一旦混入非 ASCII 字符会主动 `throw`。以后往里加中文注释，仍要存成带 BOM 的 UTF-8。

### 4.14 「换台电脑也能用」与「发给别人」（v1.3）

起因：用户问「能不能发给别人用，别人已经装了 dsh，但可能是客户端、不一定是 web 端」。

**改成可移植**（不再把本机路径编译进程序）：

- 新增 `launcher.ini`（exe 同目录，首次运行自动生成带注释的模板）：`port` / `workdir` / `dsh` / `node` / `bin`。
  改端口再也不用重新编译。
- `workdir` 留空时用「文档\DSH_Workspace」并自动创建（在 ini 里把 `workdir=` 指到自己的项目目录即可）。
- 找 `dsh` 的顺序：`launcher.ini` 的 `dsh=` → 环境变量 `DSH_CMD` → **PATH 里的 `dsh.cmd/dsh.exe/dsh.bat`** → `node + bin.js`。
  能走 `dsh.cmd` 就走它，比硬编码 `bin.js` 适应更多安装方式（npm / pnpm / nvm / 自定义 prefix）。
- 连 `dsh` 都找不到时，弹人话提示：先 `npm i -g @deepseek-ai/dsh`；**只装了桌面客户端、没有命令行的用不了**——这个工具干的就是替你敲 `dsh web`。

**状态目录搬家**：`%TEMP%\.dsh-launcher` → `%LOCALAPPDATA%\DshLauncher`（临时目录会被清理），带一次性迁移。
**新增 `url.txt`**：把带 token 的地址单独存一份。以前只从 `dsh-web.log` 里抓，日志一被清空就只能开裸地址、页面报 401。
**便携包**：`dist\DSH-Web-启动器\`（exe + launcher.ini + 使用说明.txt + diagnose.cmd），压成 `DSH-Web-启动器-便携版.zip`（54 KB）。

`diagnose.cmd` 踩的三个坑（都记下来，免得下次重犯）：

1. **`.cmd` 里不能写中文**：cmd 按字节解析批处理，在 936 代码页下 UTF-8 的中文会被拆坏，报 `'周二' 不是内部或外部命令`。诊断脚本一律 ASCII。
2. **括号块里不能再出现裸括号**：`if errorlevel 1 echo …(dsh web is not running)` 会让 `( … )` 重定向块提前闭合，结果内容全打到屏幕上而不是文件里。
3. **`netstat | findstr ":3080"` 噪声极大**（远端临时端口也含 3080），必须再串一个 `findstr "LISTENING"`。

### 4.15 中文用户名下「系统找不到指定的路径」（v1.4 修复）

现象（另一台机器，用户名是中文的比如 `张三`）：双击启动器，托盘一直是灰色，日志里 `dsh-web.log` 一个字节都没有，
`diagnose` 能看到 `dsh web --port 3099` 手工敲是正常的——说明服务本身没问题，是启动器生成的批处理跑飞了。

根因：`StartHost()` 会把启动命令写成一个批处理 `start-dsh-web.cmd`，老代码是这么拼的——

```csharp
"@echo off\r\n" +
"chcp 65001 > nul\r\n" +                    // ← 罪魁
"cd /d \"" + Cfg.WorkDir + "\"\r\n" +        // ← 中文路径
...
File.WriteAllText(Cfg.CmdFile, script, System.Text.Encoding.Default);   // ← 按系统 ANSI(GBK) 落盘
```

**写进去的是 GBK 字节，脚本第二行又把代码页切成 UTF-8**，于是 `cd /d "C:\Users\张三\..."` 这一行
被 cmd 按 UTF-8 解码成乱码 → `cd` 失败 → 后面的重定向命令也没跑起来。

我自己这边永远复现不了：我的用户名与工作目录全是 ASCII，
GBK 和 UTF-8 编出来是同一串字节。**这类 bug 只有拿到"全中文环境"才暴露**，所以第一版发布时漏了。

修法（两条一起上，互为兜底）：

1. **不再切代码页**：删掉 `chcp 65001`。批处理的字节编码和 cmd 的默认代码页（系统 ANSI）从此一致。
   Node 往 `dsh-web.log` 写的本来就是 UTF-8 字节，与 `chcp` 无关，日志照旧能正确读。
2. **脚本里尽量不出现非 ASCII**：新增 `Cfg.Short()`（`GetShortPathName`）把所有路径换成 8.3 短名
   （把 `C:\Users\张三\AppData` 这类路径换成系统生成的 8.3 短名，具体形式由 Windows 决定），`ShortCmd()` 负责把整条命令行里的带引号路径都换掉；
   卷上关了 8.3 时自动退回原路径，靠第 1 条兜底。

实测（本机造了中文目录 `D:\测试区\工作目录` + 中文名假 `dsh`）：

| 脚本编码 | 里面有没有 `chcp` | 结果 |
| --- | --- | --- |
| GBK | 有（切 65001） | `The system cannot find the path specified` ← 复现用户故障 |
| GBK | 无 | `cd` 成功，进入 `D:\测试区\工作目录` |
| GBK | 无 + 短名 | 全部 ASCII，`cd` 成功 |

整机联调：装一份隔离测试版（换 Mutex 名 + 换状态目录），`launcher.ini` 指到中文工作目录与中文名假 dsh，
日志走完 `已启动 dsh web（cmd pid=…）` → `端口已就绪`，端口 3999 真起来了，生成的 `.cmd` 里 26 个非 ASCII 字节
按 GBK 解码路径完全正确。

### 4.16 `launcher.ini` 的编码容错（v1.5）

另一台机器上出现过「`dsh=` 明明填了却不生效」：编辑器把 `launcher.ini` 存成了 GBK，
而 `File.ReadAllLines()` 默认按 UTF-8 解，中文路径被解成一串替换字符，
于是那个 `dsh=` 指向不存在的路径（日志上表现为 `dsh=null`）。

修法：新增 `Cfg.ReadIniLines()`——先按 UTF-8 **严格模式**解（`new UTF8Encoding(false, true)`），
抛异常就退回 `Encoding.Default`（系统 ANSI/GBK），最后剥掉可能的 BOM。
UTF-8 带/不带 BOM、GBK、ANSI 都能读，用户用什么编辑器保存都不影响。

实测（隔离测试版，状态目录挪到工作区里绕开沙箱权限）：ini 存成 GBK（首字节 `23 20 44 53`，无 BOM），
内容 `port=3997` / 中文 `workdir` / 中文 `dsh` 路径，启动后生成的批处理里
`cd /d` 的中文路径与 dsh 路径全部正确，日志出现 `已启动 dsh web（cmd pid=…），等待端口 3997`，
中文工作目录被自动创建。（假 dsh 自身是 `node -e` 的小服务器，脚本写错导致端口没起来，与启动器无关。）

---

### 4.17 「退出启动器并停止服务」与「停止服务收不到便携版 dsh」（v1.6）

用户的两个需求叠在一起：①托盘里想有一条「退出时连 dsh web 一起收掉」的入口——因为多数人关机前
不想留个服务占着 3080；②我按惯例做了一轮 review，发现旧的停止逻辑对**便携版 dsh** 是失效的。

**需求①**：菜单在「退出启动器」下面加一条红字危险条目 `退出启动器并停止服务`（`MenuSpec.QuitAll`），
点了先弹确认框（默认按钮是「否」），确认后依次：停定时器 → 收 dsh web 进程树 → 清 token 缓存 →
等端口真正释放（最多 8 秒）→ 隐藏托盘 → 退出。服务没在跑时这一条会置灰，
否则它和「退出启动器」完全等价，容易让人以为漏点了一次。

**review 发现②**：`KillDshHosts()` 原来只认「进程名 `node.exe` + 命令行含 `dsh\lib\bin.js` + 含 ` web`」。
v1.3 之后支持在 `launcher.ini` 里写 `dsh=` 指向任意 dsh 命令（很可能不是 npm 全局那份、也没有
`node_modules\...\lib\bin.js`），这类进程**一个都匹配不到**：按「停止服务」看着像成功了，其实服务还在跑；
紧接着「重启服务」就会起第二个实例，撞 `EADDRINUSE`——正是 §4.8 的症状以另一种姿势复现。

**修法**：停止时先收「我们自己拉起的那棵树」——`host.pid` 里记着 `cmd=<pid>`（启动时写的第一层 cmd 包装器），
用 `CmdLineOf(pid)` 查它的命令行，**确认里面含 `start-dsh-web` 或 `dsh` 才算数**（防 pid 复用误杀别人），
然后 `taskkill /F /T` 连子孙一起收；收完再跑原来那条 node 命令行启发式当兜底，去重后逐个处理。
日志会打出 `按 host.pid 收进程树：<pid> → <命令行>`，出问题一眼能看出来。

实测：本机 `host.pid` → `cmd=15728`，`Win32_Process` 查到的命令行是
`C:\WINDOWS\system32\cmd.exe /c ""%LOCALAPPDATA%\DshLauncher\start-dsh-web.cmd""`，两个匹配条件都命中，
`/T` 会把它下面的 node 一起带走。

### 4.18 通知图标是「像素风 D」/ 金黄色鲸鱼（v1.7）

现象：启动时弹出的那条 Windows 通知，托盘里明明是鲸鱼，通知里却是个灰底白字 `D`；把图标换成鲸鱼之后又变成**金黄色**。

根因两层：

1. **进程没有应用身份**。旧式 `Shell_NotifyIcon` 气泡在 Win11 上会被转成 toast，其标题和图标来自进程的
   AppUserModelID；没注册时 Windows 只能填系统通用占位图标（灰底白 D），标题也只能显示 `DshLauncher.exe`。
2. **Win11 会把这类气泡的图标整体取反**。注册身份、`IconUri` 指向鲸鱼 PNG 后，形状对了但颜色是 `#B29401`——
   正好是品牌蓝 `#4D6BFE` 的 RGB 补色（255-77=178、255-107=148、255-254=1）。换 `.ico` 一样、换新 AUMID 也一样，
   说明不是图标缓存，而是渲染时取反。

修法（`Program.RegisterAppIdentity()`，在 `RunMain` 第一行调用）：

- 启动时写 `HKCU\Software\Classes\AppUserModelId\DSH.Web.Launcher`：`DisplayName=DSH Web 启动器`、
  `IconUri=<安装目录>\dsh-toast.png`，再 `SetCurrentProcessExplicitAppUserModelID("DSH.Web.Launcher")`。
- 通知图标使用**预先反色**的 PNG（仓库里 `toast-inverted.png`，安装成 `dsh-toast.png`），系统再取反一次恰好还原原色。
- 托盘图标 / exe 图标 / 快捷方式图标走的是另一条链路、不取反，保持原样（§4.11 的鲸鱼+状态点未改）。

顺带发现的既有缺陷（本次未动）：`tools\MakeIco` 写出的 `dsh.ico` 里 128/256 两帧是坏的（`.NET Icon` 读取抛
「请求的范围扩展超过了数组的结尾」），Explorer 会回退到 ≤64 帧，所以平时看不出来。

### 4.19 「辅助功能」做成模块目录（v1.8）

需求起初是「二级菜单第一条：运行某个 .bat」，随后用户改口：**要模块化**——发布出去以后，
别人也能自己往里加功能。于是不做「一条写死的脚本」，改成目录驱动。

- 菜单内容不写在代码里：`MenuSpec.RefreshAux()` 每次弹出前扫一遍
  `%LOCALAPPDATA%\DshLauncher\aux`（`launcher.ini` 的 `aux_dir=` 可改）。
  `.bat/.cmd/.exe/.ps1` → 一条菜单项（文件名＝菜单文字，按名字排序）；子文件夹 → 下一级菜单（只展开一层，避免无底洞）。
- 不用重启：`m.Opening` 里先 `RefreshAux()` 再 `ApplyScale()`，丢文件进去下次右键就出现。
- 执行：`Launcher.RunAuxScript(path)`——bat/cmd 走 `cmd /c`，ps1 走
  `powershell -NoProfile -ExecutionPolicy Bypass -File`，exe 直接起；统一隐藏窗口、
  不提权、工作目录＝脚本目录，失败写日志 + 气泡。
- 首次运行 `EnsureAuxDir()` 建目录并写 `说明.txt`（UTF-8 BOM）；「打开模块文件夹」一键到位。
- 代码侧仍留 `AddAuxItem(text, onClick)` 做编译期扩展，条目存在 `_auxCustom` 里，不随目录扫描丢失。
- 踩到的坑：`ToolStripDropDownMenu` 是独立窗口，二级菜单不套 `IosRenderer` + `StylePopup`
  就会退回系统灰框；子菜单箭头要自己画（`IosRenderer.DrawSubArrow`）并在右侧留 `14px × 缩放`；
  等宽要逐层算，所以样式抽成了递归的 `MenuSpec.StyleDropDown(...)`。
- 验证：展开「辅助功能 ▸」是「启动代理（opencode-go）」+ 分割线 +「打开模块文件夹」；
  反射调用安装版的 `Launcher.RunAuxScript` 跑一个写标记文件的 .bat，标记文件生成、
  `launcher.log` 记录 `辅助功能：已运行 …（pid …）`。截图：`docs\menu-aux-module.png`。

### 4.20 每行菜单加图标槽（v1.9）

- 渲染：`IosRenderer.IconRoom`（21×缩放）+ `DrawIconSlot`。有图画图（悬停放大 8%），
  没图只在悬停时画一枚淡蓝圆角槽 + 中心小点——平时不画，菜单不会变成一排空盒子。
- 排版：`ApplyScale` / `StyleDropDown` 把 `padH + iconRoom` 加进左边距，主菜单和
  「辅助功能」各级子菜单统一缩进；文字定位同步改成 `PadH + IconRoom`。
- **坑**：一开始把图标挂在 `ToolStripItem.Image` 上，64px 的图把整条菜单的行高全部撑到 64px
  （ImageScaling 默认 SizeToFit，且叠在 ShowImageMargin=false 上照样参与排版）。
  改成 `IosRenderer.Icons`（条目→图 的旁表，只画不排版）后行高恢复。
- 配图约定：exe 同目录 `icons\<菜单文字>.png`，`MenuSpec.ApplyIcons()` 每次弹菜单前按名字查一遍，
  带缓存（`_iconCache`），新建目录时写一份 `说明.txt`。
- **「打开日志」图标**：MDL2 的 `E887` 在 64px 画布上只剩一道弧线（字形被裁），改成自绘记事本——
  浅蓝页 + 4 条蓝横线 + 右侧深蓝书脊 + 底部米色页边（`note.ps1` 生成 256px 再缩到 64px），
  与菜单里的扁平蓝色字形同一套色（#2E7BE0 / #C99B5C），22px 下仍能认出是「本子」。
- **「开机自动启动」图标**：原来用的 `E768` 是播放三角（语义更像「播放」）。改成电源符号（圆环留口 + 竖线）
  加右下角蓝色 `auto` 胶囊徽标（白描边把徽标和圆环隔开，缩到 22px 不粘连）；64px 以上字母清晰，菜单槽位 22px
  里字母会糊成一道，但「电源 + 小标签」的轮廓仍可辨。
- **坑（再记一次，v1.10 已从根上绕开）**：`.ps1` 里只要写中文就必须 **UTF-8 带 BOM**。用编辑器/工具改完会丢 BOM，
  PowerShell 5.1 按 GBK 读中文注释，某行末尾的乱码字节会变成续行符（反引号），把下一行并进注释，
  于是 `$startCmd` 未赋值 → `Set-Content : Cannot bind argument to parameter 'Path' because it is null`，
  而且报错行号和实际内容差三行（因为续行改变计数）。改完一律重存一次 BOM。
  v1.10 把 `rebuild.ps1` 改成全 ASCII（路径用 `%~dp0` / `%LOCALAPPDATA%` 运行时展开），这条坑对它已经不存在，但其它 .ps1 仍适用。

---

## 5. 本次全量 Review 的结论

共做了五轮全量 review（第三轮在 v1.5 发布后，第四轮在 v1.6 改动前后，第五轮在 v1.10 前，见 §5.6 / §5.7）。每轮都是「通读全部源码 + 三份工具源码 + 脚本」，
`/warn:4` 编译零警告，改完即重装验证。

### 5.1 已修复（第一轮）

| # | 严重度 | 问题 | 处理 |
| --- | --- | --- | --- |
| 1 | 高 | 重启服务可能因端口未释放而启动失败（EADDRINUSE） | `StopHost` 增加 `WaitPortClosed(15000)`，`RestartHost` 不再盲等 600 ms |
| 2 | 中 | 托盘双击 / 重复实例 / 重启同时触发时可能拉起第二个 dsh web | `StartHost` 增加「已在启动中则忽略」的守卫（加锁判断） |
| 3 | 中 | 一次操作可能打开两个标签页（状态刷新与双击前后脚各开一次） | `OpenUi` 增加 8 秒内同 URL 去重 |
| 4 | 低 | `launcher.log` 无限增长 | 超过 1 MB 自动重开（v1.10 改为改名 `launcher.log.1` 保留一份，见 §5.6） |
| 5 | 低 | 退出时定时器仍可能在跑 | `Quit()` 先 `_timer.Stop()` |
| 6 | 低 | 只支持 `--port=3080`，不支持 `--port 3080` | 两种写法都支持，并校验 1–65535 |

### 5.2 已修复（第二轮，图标与菜单定稿后）

| # | 严重度 | 问题 | 处理 |
| --- | --- | --- | --- |
| 1 | 高 | 状态变成 Running 时在 **UI 线程**上 `Cfg.WaitForUrl(6000)`，最多冻 6 秒（托盘菜单点不动、图标不刷新） | 丢到 `ThreadPool.QueueUserWorkItem`，取到 token 再 `Post(OpenUi)` 回 UI 线程 |
| 2 | 中 | 只要端口通就自动开页——服务是你自己在终端起的、或 3080 被别的程序占着，也会擅自弹页面 | 收紧为「只有本次是我拉起的（`_startedByUs`）才自动开页」 |
| 3 | 中 | `StartHost()` 不检查端口，极端时序下会再拉一个 node 抢同一端口 | 开头先 `if (PortOpen()) { Running; Refresh_(); return; }` |
| 4 | 中 | 停止服务后 token 缓存仍在，下次若服务没换 token 反而用了过期地址 | `StopHost()` 补 `Cfg.ClearUrlCache()` |
| 5 | 低 | 登录自启（`--silent`）也会弹气泡提示 | 静默模式下不弹 |
| 6 | 低 | 死代码：`IosMenu.Acrylic`、`IosRenderer.CenterStatus`、`MenuSpec.CenterStatus`、`MenuSpec._fontSmall`、重复的 `ShowImageMargin = false` | 全部删除，`tools\MenuPreview.cs` 的 `center` 参数同步删掉 |
| 7 | 低 | `OnOpened` 里给 `Region` 换新对象，旧的从未 Dispose（每次弹菜单泄漏一个 GDI 区域） | 赋值前先 `Dispose` 旧对象 |

> 本轮**逐行看过**但确认没问题的部位（记下来省得下次再查一遍）：ICO 合成（`MakeIcon` 走纯内存 ICO + `new Icon(ms)`，
> 没有 `GetHicon` 句柄泄漏）、`ReadTokenUrl` 的 `FileShare.ReadWrite` 打开方式、`OpenUi` 的 8 秒去重、
> 单实例 Mutex + EventWaitHandle 握手、`ApplyScale` 末尾的 `PerformLayout()` + `Invalidate()`、
> 日志 1 MB 轮转、`host.pid` 的读写与清理。

### 5.3 复核后确认「不是 bug」的疑点

| 疑点 | 结论 |
| --- | --- |
| 注册表 Run 项里没有 DshWebLauncher | **正常**：日志显示 13:23:45 开启、13:24:01 又关闭了开机自启 |
| 托盘图标不在「隐藏图标」折叠区 | 正常：修好后已用 `IsPromoted=1` 固定显示 |
| 日志里两条一模一样的「打开界面」 | 就是 §5.1 第 3 条，已修 |
| 启动器日志出现双份 | 设计如此：状态目录一份 + exe 同目录 `logs\` 一份（仅状态目录不可写时才回退），防止日志丢失 |

### 5.4 已知限制（可接受，不修）

1. **`停止服务`/`重启服务`会断开正在对话的页面**，刷新即可 —— 这正是这两个菜单项的语义，停止前有确认框。
2. 启动器不管 `dsh web` 的**版本升级**；`npm i -g` 升级后无需重编译启动器（它只调 `bin.js`）。
3. 若 3080 被**别的程序**占用，启动器会认为「服务已运行」并打开页面 —— 属于罕见情况，
   看 `dsh-web.log` 或托盘的「打开日志」能立刻发现。
4. 端口若用 `--port=` 改成非 3080，token 解析同样按该端口匹配，功能正常，但桌面快捷方式不带参数。
5. 未做自动更新、未签名、未做多用户支持（写的是 HKCU，只对当前用户生效）。
6. `Quit()` 只把托盘对象隐藏、没有 `Dispose` 图标与菜单 —— 进程紧接着就退出，系统回收，无实际影响，故不改。
7. 服务的 token 若被**外部**重启（在终端里自己跑 `dsh web`）换掉，启动器缓存的地址会过期，
   此时点「打开界面」可能拿到 401 页；在托盘点一次「重启服务」就会刷新。
8. 图标 16px 下细节有限（鲸鱼 + 状态点只能看个大概），这是 Windows 托盘 16px 的物理限制，不是渲染问题。
9. **「退出并停服务」v1.20 已修**：`KillDshHosts` + `WaitPortClosed(8s)` 移到线程池，四个菜单项先禁用、收尾完成后再退出；`Refresh_` 在停止中也不再做端口探测（见 §5.13）。
10. 状态行里的错误摘要超过 24 字会用「…」截断（菜单宽度保护），完整信息看日志。

---


### 5.5 已修复（第四轮，v1.6）

| # | 问题 | 影响 | 处理 |
| --- | --- | --- | --- |
| 1 | `KillDshHosts()` 只认 npm 全局的 `dsh\lib\bin.js`，便携版 dsh 收不到 | 停止服务形同虚设，重启服务撞 `EADDRINUSE` | 先按 `host.pid` 里的 `cmd=` 收整棵树（命令行校验防误杀），再走原启发式兜底 |
| 2 | 没有「退出时连服务一起收」的入口 | 关机前要连点两次（停服务 + 退启动器） | 新增 `退出启动器并停止服务`，带确认框 |
| 3 | 新条目在服务未运行时与「退出启动器」等价 | 容易误点、以为漏操作 | 跟随停止服务的可用性一起置灰 |
| 4 | `tools\MenuPreview.cs` 截图抓的是屏幕像素 | 后台窗口压在上面时会把桌面/聊天窗口拍进文档图（已发生过一次隐私泄露） | 背景窗体 `TopMost` + 截图前 `BringToFront/Activate` 并等一帧 |

### 5.6 已修复（第五轮，v1.10：非中断类清理）

本轮按「不碰当前 dsh 进程」的范围改了 12 处，改完 `/warn:4` 零警告；`rebuild.ps1` 重装并用 SHA-256 校验「装上去的就是编出来的」，
在线的 dsh web（node pid 18284，08:50 启动）全程未中断，端口 3080 与带 token 的页面照常可用。

| # | 类型 | 问题 | 处理 |
| --- | --- | --- | --- |
| 1 | bug | 状态行拼异常消息会把菜单撑到屏幕外（实测 378→1771px） | `Refresh_` 里 `_detail` 截到 24 字符再拼 |
| 2 | bug | 每秒无条件重设 `_ni.Icon`，每次都触发 `NIM_MODIFY` | 先 `if (_ni.Icon != want)` 再赋值 |
| 3 | bug | 启动超时通知从线程池直接调 `NotifyIcon` | 改 `Post(...)` 封送回 UI 线程 |
| 4 | bug | UI 线程与线程池并发写日志会抛异常丢行 | `Log.Write` 加锁 + `FileShare.ReadWrite` 追加 |
| 5 | bug | 日志轮转直接 `File.Delete`，出事现场一起没 | 改名为 `launcher.log.1` 保留一份 |
| 6 | bug | `IsAutostart` 只看注册表值存在、不看路径 | 比较值与 `Application.ExecutablePath` |
| 7 | bug | 菜单/定时器里的异常会弹 .NET 错误框、可能退出 | `SetUnhandledExceptionMode(CatchException)` + `ThreadException` 记日志 |
| 8 | 优化 | `Cfg.Url` 每次访问整份扫 `dsh-web.log` 并写 `url.txt`（8.5KB×200 次 = 152ms） | 按 Length+mtime 缓存扫描结果、`SaveUrl` 去重；实测 ×200 降到 41ms |
| 9 | 优化 | `RefreshAux` 每次右键全量重建条目（20 模块实测 136ms/次，且弹菜单会调两次） | 按目录内容指纹跳过重建；实测 ×50 从 6816ms 降到 31ms；同时删掉弹菜单时重复的 `ApplyScale` |
| 10 | 优化 | `PortOpen` 的 `AsyncWaitHandle` 用完没关 | 显式 `Close()`；×20 从 27ms 降到 18ms |
| 11 | 优化 | 配置/目录创建失败全被 `catch {}` 吞掉 | `LoadIni` / `EnsureIni` / 静态构造的 catch 改为写日志 |
| 12 | 优化 | `rebuild.ps1`：步骤编号 4/4 与 5/5 混用、固定 `Start-Sleep 4`、复制不校验、安装脚本写死路径 | 统一 [1/5]…[5/5]；改为轮询等待；安装后与运行后都用 SHA-256 校验；`_install.cmd`/`_start.cmd` 改为纯 ASCII + `%~dp0`/`%LOCALAPPDATA%` 运行时展开（脚本里一旦出现非 ASCII 字符直接 throw） |

调试开关：`launcher.ini` 写 `verbose=1` 才会记「菜单已打开」这类高频日志（默认关闭；以前每次右键都是一行，实测几小时就 116 行）。

### 5.7 原推迟项的处理状态（v1.19）

| # | 状态 | 处理 |
| --- | --- | --- |
| B1 | 已修复 | 停止/重启不再在 UI 线程等：状态先置「停止中…」，`KillDshHosts` + `WaitPortClosed(15s)` 丢线程池，完成后 `Post` 回 UI；`_startToken` 让过期结果失效。`QuitAll` 于 v1.20 后台化（见 §5.13）；关闭态（Stopped）的端口探测于 v1.22 移到后台缓存（见 §5.16）。 |
| B2 | 已修复（v1.19） | `StartHost` 保留 cmd 的 `Process` 句柄；watchdog 每轮查 `HasExited`，cmd 提前退出立即置 Error、读 `dsh-web.log` 尾部并提示，不再空等 120 秒；仍保留 token 校验（见 §5.12）。 |
| B3 | 已修复 | `host.pid` 的 `port=` 校验 + `CommandLineHasPort` 过滤，只收当前端口的 dsh web，不再误杀另开 4000 的实例。 |
| B5 | 仍未修 | 非中文 Windows + 中文路径的 8.3 短名/编码边界未动，见 §5.4。 |
| B6 | 已修复 | taskkill 只有 `WaitForExit(5000)` 且退出码 0 才计数，超时/失败写日志。 |

### 5.8 已修复（第六轮，v1.11：托盘菜单第一次弹出盖住光标）

**现象**：重启启动器后第一次右键托盘图标，菜单从光标上方一直铺到屏幕底部、把鼠标盖住；从第二次起菜单正常落在光标左上角。每次重启都复现“第一次不准、第二次就对”。

**根因**：`NotifyIcon` 弹菜单不走 `ContextMenuStrip.Show(Point)`，而是 `ShowInTaskbar(x, y)`（`NotifyIcon.cs:604-618`），把光标点当成**菜单右下角**锚定。菜单在 `DshMenu.Create` 末尾先按 100% 建好（`ApplyScale(1f)`，`DshMenu.cs:830`），真正的 DPI 缩放放在 `Opening` 里（`DshMenu.cs:818-826`）。第一次弹出时 WinForms 已按 269×378 把位置算完，`Opening` 随后才放大到 380×518；尺寸变化只会让窗口重新夹进屏幕，不会重新以光标为右下角锚定，于是菜单向下长过光标。第二次弹出时尺寸已是 380×518，一次算对。

**复现数据**（临时探针，编译真实 `DshMenu.cs`，模拟托盘点 (1860,1580)，屏幕 2560×1600、工作区高 1528、150%）：

| | 定位时尺寸 | Opening 前 bounds | 放大后 | 含光标 |
| --- | --- | --- | --- | --- |
| 第一次（修复前） | 269×378 | (1591,1202) | (1591,1081) 380×518，底边 1599 | 是 |
| 第二次（修复前） | 380×518 | (1480,1062) | 不变，右下角压在光标上 | 否 |

对照：普通 `Show(Point)`（光标当左上角）两次结果一致，不产生该现象——问题只在 `ShowInTaskbar` 这条托盘专用路径上。

**修法**：新增 `Launcher.PreScaleMenu()`，在启动时和 `_ni.MouseMove` 里各调一次：按当前屏 DPI `ApplyScale` 一次，用 `_menuScale` 守卫只在比例变化时重排（约 1.3ms）。鼠标一进入托盘图标尺寸就已是最终值，右键时锚点一次算对；启动时那次覆盖“鼠标没再动就直接右键”，`Opening` 里的缩放保留作兜底。注意 .NET Framework 4.x 的 `NotifyIcon` **没有** `MouseHover` 事件（编译报 CS1061），只能用 `MouseMove`。

**验证**：重启后连续两次右键，弹出窗口矩形都是 (1452,1064) 380×518，右下角 (1832,1582) 正好压在光标上，两次一致；不移动鼠标直接右键也同样正确（启动预缩放生效）。截图见 `docs\menu-anchor-fix.png`。`rebuild.ps1` 五步通过，pid=28048。

### 5.9 已修复（第七轮，v1.12：辅助功能里的代理，启动即打印模型清单）

**需求**：WorkBuddy 一次只能接一个自定义供应商、模型名要手抄。用户希望从托盘「辅助功能 → 启动代理（opencode-go）」点开后，窗口里直接列出 opencode-go 当前可用的模型，复制名字就能填。

**事实**：opencode.ai 的 Go 模型清单可匿名拉取，只要带一个任意的 `x-opencode-session`：
`GET https://opencode.ai/inference/go/openai/v1/models` → 32 个 chat 模型，每个模型带
`id / name / limit.context / modalities.input / cost / go.monthly_usage.go`。

**改动**（都在 `D:\DSH_WorkSpace\Default_WorkSpace\opencode-go-proxy`，不是本仓库）：

- `proxy.mjs`：启动先 `listen` 再同步清单，WorkBuddy 不用等网络；打印「模型名称 / 显示名 / 上下文 / 月额度 / 图片」五列，模型名可直接复制。
- 同步失败回落到同目录 `models.json` 缓存（上次成功结果），再失败只给默认模型并提示原因；新增 `--list` 只打印清单、不占端口。
- 清单末尾按 `last-model.txt` 打印最近使用的模型（每次转发时从请求体的 `model` 字段更新）；没有记录就只给示例，不假装知道 WorkBuddy 当前选了哪个。
- 端口被占用时不再只报一句错：同样把清单打出来（配合 `cmd /k`，窗口不会闪退）。
- 月额度来自每个模型的 `go.monthly_usage.go`：glm-5.3 $500、deepseek-v4.1-flash $100、多数 $60 / $30 / $15；
  beam-free / longcat-2.5-free / hy4-flash-free / iceman-contributor-free 为 0 价、不占额度。**不是所有模型共用 $100**。
- `%LOCALAPPDATA%\DshLauncher\aux\启动代理（opencode-go）.bat` 由 `cmd /c` 改为 `cmd /k`，端口占用或同步失败时窗口留在屏幕上。

**坑**：Node v24.18 + Windows 下，`fetch` 结束后调 `process.exit(0)` 会触发 libuv 断言
（`src\win\async.c` 第 94 行，退出码 3221226505）；改成 `process.exitCode` 让事件循环自然退出即可。

**验证**：`node proxy.mjs --list` 1339 ms 退出码 0、32 行完整；占用 8787 时打印清单并 exit 1；
带假 Key 请求 `/v1/models` 转发到上游返回 401（401 来自上游鉴权，说明转发链路正常）。临时测试端口 8799 已确认无残留监听。

**边界**：清单只在代理重启后打印；当前 8787 上运行的若还是旧进程，要先关掉那个窗口再从托盘启动。

### 5.10 新增（第十一轮补丁，v1.17：状态行改两行，已运行时长下移，状态球单圆 + 紧凑呼吸外圈）

**需求**：状态行要显示已运行时长，一眼看出服务跑了多久。第八轮先把它横向拼在状态右侧，结果状态行成了菜单最宽一行，150% 下菜单从 380 撑到 454，下面的选项区显得空；用户要求改成第一行状态、第二行运行时长，并控制菜单宽度。

**实现**（`DshLauncher.cs` / `DshMenu.cs`）：

- 起点优先读 `host.pid` 的 `started=`（启动器自己起的服务时写入，精确到秒）。只有 pid 对应的进程还在、且文件里的 `port=` 与当前端口一致才采用；存活检查用 `Process.GetProcessById` + 进程名含 `cmd`，不查 WMI。
- `host.pid` 不存在或过期（例如启动器是接管一个已在运行的 `dsh web`）时，异步查 WMI `Win32_Process` 里的 `node.exe`，命令行需含 `dsh\lib\bin.js` 与 `web`，取最新的 `CreationDate`。查询丢线程池，不阻塞 UI；查不到就不显示，不拿「启动器发现它的时间」冒充运行时长。
- `MenuSpec.SetStatus(状态文字, 第二行文字, 颜色)` 把两者分开：第一行状态用 13.5px × 缩放，第二行「已运行 X」用 11px × 缩放。状态行不再横向拼接，宽度不随运行时长增长。
- 宽度：`ApplyScale` 计算最宽条目时跳过 Status，只由下面的选项行决定；状态第一行仍参与，保证「状态：运行中」不被截。150% 下菜单宽回到 380（第八轮单行版 454）。
- 高度：有第二行时 `statusH = mainH + subH + 2px × 缩放 + 12px × 缩放`，Status 上下内边距改为 6px × 缩放。停止服务或启动失败时第二行清空，行高恢复单行。
- 状态球（v1.15）：去掉原来的光晕，只画一个实心圆（15px × 缩放，150% 约 23px）；圆心 x 与下方图标槽中心一致，y 取 `e.Item.Height / 2`，即整块状态行正中，不再贴在第一行中心。光晕和实心圆叠在一起会被看成两个圈。
- 状态球（v1.16 初版）：恢复浅色外圈，但只画一个细环围绕实心球，不再叠第二个实心圆（否则又会看成两个圈）。`IosRenderer.BreathPhase`（0..1）由 `IosMenu` 的 40ms 计时器推进，周期 2.6 秒；环直径 `d × (1.45 + 0.50 × pulse)`，透明度 `84 - 38 × pulse`，线宽 `max(1.4px, 1.8px × 缩放)`，扩张时同步变淡。`dotRoom` 从 20px 改为 24px × 缩放。计时器只在菜单打开时启动，`OnClosed` 停止并复位，`Dispose` 释放。
- 状态球（v1.17）：用户反馈「状态运行图标又太大了」，整体收小：实心球 `15px → 12px × 缩放`（150% 约 18px），外圈直径改为 `d × (1.34 + 0.36 × pulse)`（150% 约 24–31px，最大从 1.95 倍降到 1.70 倍），线宽 `max(1.2px, 1.4px × 缩放)`，`dotRoom` 从 24px 收到 22px × 缩放。呼吸周期、透明度曲线、圆心位置和垂直居中都不变；环仍在球外，150% 下菜单仍是 380×532，不压「状态」文字。
- 格式不变：`<1 分钟` → 「不到 1 分钟」；`<1 小时` → 「N 分钟」；`<1 天` → 「N 小时」或「N 小时 M 分」；`≥1 天` → 「N 天」或「N 天 M 小时」。
- 状态文字或第二行变化时，仍在同一次刷新里先 `ApplyScale` 预排，不等 `Opening`；即使宽度不变，行高也可能从单行变两行，晚排会让菜单先按旧高度锚定。
- 停止服务 / 退出并停服务 / 启动失败时清零；再次启动从新进程的启动时刻重新计时。
- **坑**：自绘上下文里 `TextRenderer.MeasureText(..., Size.Empty, ...)` 在高 DPI 下返回的 height 明显偏小（144 DPI 实测 15/12px，而字体实际行高 28/23px），两行会叠在一起。改用 `Font.GetHeight(e.Graphics)` 算行高后正常；静态 `TextRenderer.MeasureText` 只在 `ApplyScale` 里做宽度/高度预算。

**验证**：2026-11-20 11:13 重建部署（pid=22576，exe SHA-256 `613FEBA5…C05D1`，源码目录与安装目录一致；`host.pid` 仍记录 `started=2026-11-20T09:40:09`）。实机托盘菜单 380×532，第一行「状态：运行中」，第二行「已运行 1 小时 33 分」；状态球为实心绿球 + 一圈浅绿细环，150% 下核心约 18px、外圈约 24–31px，环未压到「状态」文字；右键菜单右下角仍与光标对齐（菜单 1448,1058，光标 1828,1590）。v1.16 已用两帧截图确认呼吸计时器在跑；v1.17 只改了尺寸常数，计时器和绘制路径未变，本次实机截图确认收小后的样子。`MenuPreview` 在 1.0 / 1.25 / 1.5 / 2.0 下仍为 269×388、318×470、380×532、480×699。截图 `docs\menu-two-line.png`。格式边界 0 / 59 / 60 / 3599 / 3600 / 3660 / 86399 / 86400 / 90000 秒的输出不变；`ReadPidStart` 仍返回 09:40:09。

### 5.11 已修复（第十二轮，v1.18：状态机、启停异步化、按端口收进程）

**背景**：v1.17 交付后做了一次全量 review，重点看 §5.7 里推迟的「停止/重启卡 UI」「启动失败卡 120 秒」「误杀别的 dsh web」，以及并发启停时的状态竞争、watchdog 过期回写、菜单字体泄漏等边界。

**改动**（`DshLauncher.cs` / `DshMenu.cs`）：

- 状态机显式化：`St` 增加 `Stopping`；`Refresh_` 在锁内先判 `Stopping`，停止期间不再被端口探测改回 `Running`。按钮联动：停止/重启/退出在停止中置灰，重启按钮在停止中禁用。
- 启动令牌 `_startToken`：每次 Start/Stop/QuitAll 自增；watchdog 每轮和落状态前都校验 `token == _startToken && _state == St.Starting`，过期的启动线程不能再把新状态改回 Running/Error。写启动脚本失败、`Process.Start` 返回 null 都立即置 Error 并提示，不干等 120 秒。
- 停止/重启异步化：`StopHost(ask, restartAfter)` 先在 UI 线程置 `Stopping` 并显示「停止中…」，再把 `KillDshHosts` + 删 `host.pid` + `WaitPortClosed(15000)` 丢线程池；完成后 `Post` 回 UI 校验 token，置 Stopped，再决定是否 `StartHost`。停止期间再次点停止只合并 `_restartAfterStop`，不重复起线程。`RestartHost` 走 `StopHost(false, true)`，UI 不再冻结 15 秒。
- 按端口收进程：`KillDshHosts` 解析 `host.pid` 的 `cmd=`/`port=`，端口不一致直接忽略；启发式枚举 `node.exe` 时只收 `dsh\lib\bin.js` + `" web"` + `CommandLineHasPort(cl, Cfg.Port)` 的进程。`CommandLineHasPort` 支持 `--port 4000` / `--port=4000`，没有 `--port` 时只对默认 3080 放行。`FindDshStartTime` 同样按端口过滤。
- taskkill 计数修正：只有 `WaitForExit(5000)` 且 `ExitCode==0` 才 `killed++`，超时/非零退出写日志。
- 启动开销：有 `dsh.cmd` 时不再求值 `Cfg.NodeExe/DshBin`（会枚举 PATH 和 pnpm 目录），只有回退到 node + bin.js 才扫。
- 菜单：`ApplyScale` 释放旧的第二行字体，并用 `Graphics.GetHeight` 算两行高度；`MenuPreview` 截图不再抢焦点（改用 `Application.DoEvents` + 短暂等待后 `CopyFromScreen`），避免预览时菜单自动关闭。
- 便携诊断：`dist\...\diagnose.cmd` 从 `launcher.ini` 读 `port=`（默认 3080），不再写死；根目录补上 `launcher.ini` 模板。

**验证**：

- `rebuild.ps1` 2026-11-20 11:29:57 五步通过；安装目录 exe SHA-256 `7EEF137B…F2455C`，pid=6032，Medium 标签正常；`host.pid` 仍是 `cmd=13892 / port=3080 / started=2026-11-20T09:40:09`。
- `MenuPreview 1.5` 输出 380×528；两行「状态：运行中 / 已运行 57 分钟」完整，状态球和文字不重叠；旧版 380×532，高度差来自两行高度按 `Graphics` 实测重算。
- 代码审查覆盖并发启动/停止、旧 watchdog、跨端口进程、taskkill 失败、菜单字体释放等路径；主程序编译通过，preview 的 5 个 CS0649 是预览不链接主程序导致，不是产品代码问题。
- `diagnose.cmd` 用临时目录实测：`launcher.ini` 写 `port=3999` 时输出 `[4] port 3999` 与 `nothing is listening on 3999`；删掉 ini 时回到默认 3080，并看到当前 `127.0.0.1:3080 LISTENING`。根 `launcher.ini` 与 dist 模板内容一致。

**剩余风险**：

- B2 已在 v1.19 修复：启动进程提前退出会立即转 Error 并带日志尾部，见 §5.12；失败路径尚未实机触发。
- B5 未修：非中文 Windows + 中文路径仍依赖 8.3 短名/ANSI 兜底。
- `QuitAll` 已在 v1.20 后台化（见 §5.13）；停止/退出路径仍未实机触发，待可打断服务的窗口回归一次。
- 3080 被非 dsh 程序占用时仍会被当成「服务已运行」；token 被外部终端重启换掉时缓存地址会过期（§5.4 第 3、7 条）。

### 5.12 已修复（第十三轮，v1.19：启动失败快失败 + 安装复制重试）

**背景**：§5.7 的 B2 遗留——`dsh.cmd` 立即退出时，watchdog 仍轮询到 120 秒才报超时，用户看不到真正的报错。

**改动**（`DshLauncher.cs` / `rebuild.ps1`）：

- `StartHost` 保留 cmd 的 `Process` 句柄 `proc`（不再只记 pid）；stdout/stderr 仍重定向到 `dsh-web.log`。
- watchdog 每轮在 `PortOpen` 之后检查 `proc.HasExited`（try/catch）：cmd 退出且 token/state 仍是本次启动时，立即置 `St.Error`、`_detail="启动进程已退出"`，不再等 120 秒；仍保留 token 校验，旧启动不会覆盖新状态。
- 新增 `Cfg.ReadLogTail(maxChars)`：用 `FileShare.ReadWrite | Delete` 读 `dsh-web.log` 尾部；启动失败通知带最后约 500 字符；日志读不到时退回「日志是空的」。
- `rebuild.ps1` 的 `_install.cmd` 对 exe 复制加 10 次重试（每次等 1 秒）：刚编译的 exe 可能被 Defender/索引短暂占用，第一次 11:49 的 `[3/5]` 推测是复制失败导致 hash mismatch（安装脚本输出已删除，无法完全证实）；加重试后 11:50:31 重跑通过。

**验证**：

- 2026-11-20 11:50:31 `rebuild.ps1` 五步通过；源码 exe 与安装 exe 的 SHA-256 均 `8F8DDF97…73CEB5F7`（full: `8F8DDF97B4E9456B2F75E6A4A7AAF770C93B941B697D7A0B3A8ED79673CEB5F7`），pid=1284，Medium 标签正常；`host.pid` 仍是 `cmd=13892 / port=3080 / started=2026-11-20T09:40:09`，dsh web 未被重启，3080 仍由 node pid 28652 监听。
- 反射调用 `ReadLogTail(300)` 返回 dsh-web.log 末尾 287 字符（最近的模型列表日志），说明共享读打开正常。
- 正常启动时 cmd 不会提前退出：`host.pid` 的 cmd pid=13892 从 09:40 一直存活，命令行是 `cmd.exe /c ""…\start-dsh-web.cmd""`，说明 `HasExited` 不会对当前服务误判。
- 启动失败路径没有实机触发（会断开当前 3080 会话）；以编译 + 代码审查为准，待可打断服务的窗口回归。
- 便携包 v1.19：66,537 字节，4 个文件（exe 129,536 / ini 614 / 使用说明 3,664 / diagnose.cmd 1,367），dist exe 与源码 exe 同 hash。

### 5.13 已修复（第十四轮，v1.20：B1 停止/退出期间的菜单冻结）

**背景**：§5.4 第 9 条遗留——「退出并停止服务」在 UI 线程同步跑 `KillDshHosts`（WMI 查命令行 + 枚举 node.exe + taskkill 每个等 5 秒）和 `WaitPortClosed(8000)`，最坏 13–20 秒托盘全冻；停止期间每秒一次的 `Refresh_` 还会先做 `PortOpen()` 探测（单次最多 700ms），正是「一秒一卡」的来源。

**改动**（`DshLauncher.cs`）：

- `QuitAll` 慢活全部移到线程池：置 `St.Stopping` + 状态「关闭中…」后先禁用四个菜单项（打开界面/停止/重启/退出并停止），`KillDshHosts`、删 `host.pid`、`WaitPortClosed(8000)` 在后台跑，完成后 `Post` 回 UI 隐藏图标并 `ExitThread`；UI 线程全程可响应。
- 新增 `_quitting` 防重入：退出流程已开始时再次点击「退出并停止」不再弹确认框。
- `Refresh_` 在 `St.Stopping` 时跳过 `PortOpen()` 探测——停止态的判定不依赖探测结果，去掉停止期间每秒最多 700ms 的卡顿。
- WMI 全部离开 UI 线程：全文件仅存的 3 处 `ManagementObjectSearcher`（`FindDshStartTime`、`CmdLineOf`、`KillDshHosts` 枚举）此后都只在线程池里执行。

**验证**：

- 2026-11-20 15:40:49 `rebuild.ps1` 五步通过；源码 exe 与安装 exe（`%LOCALAPPDATA%\Programs\DshLauncher\`）SHA-256 均 `501A252C…71EAFB5D3`，pid=36968，Medium 标签正常；dsh web 未动，3080 仍由 node 监听。
- 停止/退出路径会断开当前 3080 会话，未实机触发；以编译 + 代码审查为准，待用户在可打断窗口点一次「退出并停止」/「停止服务」回归。
- 便携包 v1.20：66,701 字节，4 个文件，dist exe 与源码 exe 同 hash。

### 5.14 已修复（第十五轮，v1.21：等 ready 再开页，不再「没找到登录令牌」）

**现象**：v1.20 之后每次由启动器拉起 dsh web，都会弹一个「没找到登录令牌 / 没能从日志里取到带 token 的地址」的托盘气泡，打开的是裸地址。

**根因**：`Refresh_` 判定端口通就认为「可以开页」，线程池里只固定等 8 秒 token（`Cfg.WaitForUrl(8000)`）。但这台机器上 dsh web 的启动顺序是「先 bind 端口 → 起 MCP 子进程（windows-mcp、kb）和插件 → 最后才打印带 token 的地址」，端口就绪到 token 落盘实测差 20 秒以上。`launcher.log` 证据：15:58:49 起服务、15:59:39 端口就绪、15:59:48 就弹出「没找到登录令牌」，而 `dsh-web.log` 里那条 `dsh web: http://127.0.0.1:3080/?token=…` 直到 16:00:02 才出现——比启动器放弃等待晚了 14 秒。

**改动**（`DshLauncher.cs`）：

- `Cfg.WaitForUrl(int)` 换成 `Cfg.WaitReadyUrl(int)`：轮询日志等 token 地址出现，拿到后再用 `Cfg.ProbeHttp` 对该地址做一次 GET 探活，只有服务真的应答（非 5xx）才返回；地址已经在日志里、只是探活连续失败 3 次，也直接返回（探测环境问题不该耽误开页）。
- 自动开页的等待上限从 8 秒提到 **90 秒**，全程在线程池里跑；封送回 UI 线程前再确认一次 `_state == St.Running`，等待期间被停掉/重启就丢弃这次开页。
- 新增 `Cfg.ProbeHttp(url, ms)`：`HttpWebRequest` + `Proxy = null`（本机地址不让系统代理插一脚）、`AllowAutoRedirect = false`、`KeepAlive = false`，2.5 秒超时；401/403 也算「服务在应答」。
- `OpenUi()` 改为 `OpenUi(string url = null)`，把等到的地址直接传进去，不再开页时重扫一遍日志；真等满 90 秒仍没有 token 才退回裸地址并提示。

**验证**：

- 2026-11-20 16:07:58 `rebuild.ps1` 五步通过；源码 exe 与安装 exe（`%LOCALAPPDATA%\Programs\DshLauncher\`）SHA-256 均 `83EF1065…928E5F50`（full: `83EF10651F33CA72CB7CE718F7EA2FCB5C69182D416B031CC2958844928E5F50`），pid=22760，Medium 标签正常；dsh web 未动，3080 仍由 node pid 17976 监听。
- 反射实测（`Assembly.Load` 后的 `DshLauncher.Cfg`）：`ProbeHttp` 对当前带 token 地址 = `True`（303）、裸地址 = `True`（401，服务在应答）、死端口 3999 = `False`；`WaitReadyUrl(6000)` 在日志已有 token 时 1 ms 返回 `http://127.0.0.1:3080/?token=Hu-tPZd…`。
- 真实启动路径（端口就绪后还要等 20 秒才有 token）未实机复跑——那会断开当前 3080 会话；以日志时间线 + 反射实测为准，待下次重启回归。
### 5.15 新增（第十六轮，v1.22：启动耗时观测 BootTrace）

**背景**：排查「启动到底慢在哪一段」需要一条能事后回溯的时间线。v1.22 在启动器里加了一组只读观测：不改变启动逻辑、不阻塞启动、失败只写日志，随版本发布，以后用户说「某次启动特别慢」时可直接取数。

**实现**（`DshLauncher.cs`，`internal static class BootTrace`，约 180 行）：

- `Start(cmdPid)` 起一个线程池循环，500 毫秒一轮、5 分钟自停；先用 WMI 查 `Win32_Process WHERE ParentProcessId=<cmdPid>`，命令行含 `node` 的那个子进程即 dsh web。
- 每 2 秒记一次该 node 进程的 `TotalProcessorTime` 增量（累计 CPU 秒）；每 4 秒扫 cmd 与 node 的子进程，新出现的记一行（命令行截断 120 字符，`NoteChild` 去重），MCP / sidecar 的启动顺序一目了然。
- 5 个标记点：`已启动 dsh web（cmd pid=…）`、`日志里已出现 token 地址`、`token 地址探活通过`、`端口就绪，服务已应答`、`打开浏览器`。
- 全程 try/catch，只写 `launcher.log`；观测自身出异常不影响启动。

**用途**：v1.22 对启动耗时的定性就来自它——某轮从 `启动观测 +2.3s：CPU 累计 1.8s` 到 `+81s：91s`，累计 CPU 几乎线性增长，说明这 80 秒是 CPU 密集（模块解析/编译），不是等网络。

**验证**：

- 反射测试（`%TEMP%\bt-test\Test.exe`，`Assembly.LoadFrom` 安装目录 exe + `GetMethod NonPublic|Static`）确认 cmd→node 发现、CPU 采样、退出识别正常。
- 部署后每轮启动 `launcher.log` 都有 `启动观测 +Ns` 行（17:23、17:29、20:39 等轮可见），5 分钟后自动停止记录。

### 5.16 已修复（第十六轮，v1.22：关闭 DSH 后托盘菜单鼠标划过不跟手）

**现象**（用户反馈）：DSH 停止后右键托盘，鼠标划过菜单项，胶囊高亮要等一会才加深；服务运行时没有这个现象。

**根因**：

- 实测（`%TEMP%\porttest\PortTest.cs`，与 `PortOpen` 同写法，各 8 次）：连 3080（有服务）0–31 ms；连 3999（无人监听）**710 / 702 / 702 / 706 / 714 / 702 / 703 / 703 ms**——这台机器上「没人监听的回环端口」不会立刻回 RST，只能等满超时。
- `Refresh_`（1 秒一次 WinForms Timer）第一行就是 `bool up = prior != St.Stopping && PortOpen();`，而 `PortOpen` 是 `Synchronous BeginConnect + WaitOne(700)`。服务停着时 UI 线程每 1000 ms 被占 700 ms，菜单的 `WM_MOUSEMOVE` 与重绘排队，胶囊高亮慢半拍；服务运行时探测 0 ms，所以只在停止后出现。
- §5.13 只豁免了 `St.Stopping`，`St.Stopped` 仍在 UI 线程里做阻塞探测。

**改动**（`DshLauncher.cs`，约 +50 行）：

- 新增 `private static volatile bool _portUp;` 与 `_portWatchStarted`；`StartPortWatch()` 用 `Interlocked.CompareExchange` 保证只启动一次，线程池死循环：`PortOpenTimed(350)` → 写 `_portUp` → `Thread.Sleep(up ? 900 : 350)`。
- `PortOpenTimed(int timeoutMs)` 承接原 `PortOpen` 主体（超时参数化）；`public static bool PortOpen() => PortOpenTimed(700);` 保留给启动/停止/`WaitPortClosed` 等必须实时的路径。
- `Refresh_` 改读 `PortUpCached()`，UI 线程不再有任何阻塞探测；构造函数在 `_timer.Start()` 后启动探测线程。
- 未改：`StartHost` 入口与双击托盘的 `if (PortOpen())`、`WaitPortClosed`——停止态点击仍有一次 700 ms 真实探测，避免误判「没在跑」后拉起第二个 node 撞端口。

**验证**：

- 2026-11-20 20:46:51 `rebuild.ps1` 五步通过；源码 / 安装 / dist exe SHA-256 均 `E99CB870…118967D04`（full: `E99CB8702F5DD6D4E84DD0ECF889D20BB58BBEDEC254B9A6074F042118967D04`），pid=47864，Medium 标签正常；dsh web 未动（node pid=10296，host.pid cmd=16268 / started=2026-11-20T20:39:18）。
- 资源实测（10 秒采样）：`TotalProcessorTime` 1.047 → 1.047 秒（0 秒 CPU）、WorkingSet 43.1 MB、11 线程、24 核——后台探测的等待不烧 CPU。
- 停止态手感待用户下次停服务时回归（会断开当前 3080 会话）。

## 6. 已清理的废弃方案

### 6.1 第一轮：VBS 方案 + 调试残留（23 项）

- `dsh-launcher\vbs-legacy\`（7 个：`launch-dsh.vbs`、`launch-dsh.cmd`、`stop-dsh.vbs`、`stop-dsh.ps1`、
  `dsh-launcher-common.vbs`、`_stop-now.ps1`、`_t.vbs`）—— 整目录删除；
- `%TEMP%\.dsh-launcher\` 里的探针残留：`probe.vbs`、`probe.out`、`tcpcheck*.vbs`、`fake-start.cmd`、
  `fakesrv.js`、`fake.log`、`e.txt`、`o.txt`；
- `%TEMP%` 里排查托盘时产生的截图与脚本：`tray-*.png`、`virtual-desktop.png`、`probe.ps1`、
  `probe2.ps1`、`dsh-stop-check.vbs`、临时 `DSH Web.lnk`。

### 6.2 第二轮：定稿后的过程产物（本轮，19 项 + 65 个临时文件）

- `docs\` 里 14 张迭代过程的菜单截图（`menu-150`、`menu-final-150`、`menu-nohover-150`、
  `menu-v7×3`、`menu-v8`、`menu-v9×2`、`menu-v10×2`、`menu-v11×2`、`menu-gap-compare`）；
  只留 4 张：`menu-final.png`、`menu-states.png`、`icon-states.png`、`tray-live.png`
  （这是第二轮当时的清单；后续又补了辅助功能、图标与通知的取证图，v1.10 的完整清单见 §3）；
- `tools\` 里两个一次性探针：`_d.cs/_d.exe`（查 DPI）、`_m.cs/_m.exe`（查菜单几何）；
- 源码目录下陈旧的 `logs\launcher.log`（日志已统一到 `%LOCALAPPDATA%\DshLauncher\launcher.log`）；
- `%TEMP%` 里我自己的 65 个临时截图 / 脚本 / `mp.out`、`mp.err`。

保留：`DshLauncher.cs`、`DshMenu.cs`、`app.manifest`、`dsh.ico`、`logo256.png`、`DshLauncher.exe`、
`launcher.ini`、`rebuild.ps1`、`README.md`、本报告、`docs\`（4 张）、`tools\`（3 组工具）、`dist\`（便携包）。

### 6.3 第三轮：VBS 时代的快捷方式（本轮补充，2 项）

第一轮只清了 `vbs-legacy\` 目录，**开始菜单里还留着两条指向已删脚本的快捷方式**，
双击只会弹「找不到 launch-dsh.vbs」——本轮一并删除：

- `%APPDATA%\Microsoft\Windows\Start Menu\Programs\DSH Web.lnk`（→ `launch-dsh.vbs`）
- `%APPDATA%\Microsoft\Windows\Start Menu\Programs\DSH Web - 停止.lnk`（→ `stop-dsh.vbs`）

现在只保留两条正确的：桌面 `DshLauncher.lnk` 与开始菜单 `DSH Web\DSH Web.lnk`（都指向安装目录的 exe）。

> 备注：作者的 DSH 工作区根目录里还有大量**与启动器无关**的历史临时脚本
> （`_*.py`、`_*.txt`、`diag-*.ps1` 等，约 130 个）。那些是以前做文档/图表任务留下的，不属本次清理范围，
> 没敢动。需要的话可以单独让我按「文件类型 + 时间」批量归档。

---

## 7. 验证记录（实测，不是推测）

| 项目 | 结果 |
| --- | --- |
| 单实例 | 第二个进程 71–95 ms 内退出，日志写「重复双击：唤起已有实例」，只开一个标签页 |
| 端口探测 | 3080 通 / 3997 不通，稳定 |
| 无控制台 | 进程 `MainWindowHandle=0`，PE Subsystem=2（GUI），全程无黑框 |
| 托盘注册 | `NotifyIconSettings\4834671031873293732`（`IsPromoted=1`）；UIA 能在任务栏读到 `DSH Web · 运行中 · 端口 3080`，实拍鲸鱼 + 绿点正常 |
| 认证 | 裸地址 → **401**；带 token → **303 + Set-Cookie** |
| 重启后换 token | 重启后打开的是新 token（`vk-uxzix…` → `0ROzzVx…` → `zEfo6w…` 一路都验证过） |
| 重建不多开标签页 | 15:04:50 重建后日志只有 `=== 进程启动 pid=22684 ===` 与 `启动器已运行（登录自启，静默模式）`，**没有** `打开界面` 行 |
| 菜单尺寸（150%，v1.6 时） | `1.0→202×306`、`1.25→236×370`、`1.5→278×418`、`2.0→350×548`，同比放大、不糊；v1.10 加图标槽后 150% 下为 **380×518** |
| 菜单留白（150%，像素测量） | 顶边→胶囊 8px、胶囊→分割线 9px、末条胶囊→底边 9px（同一套 `sepV` 推导） |
| 编译健康 | `/warn:4` 零警告；`fatal.log` 不存在 |
| 编译健康（v1.10） | `/warn:4` 零警告；`rebuild.ps1` 装上去的 exe 与编译产物 SHA-256 一致（`98D0838F…`） |
| 访问缓存（v1.10） | `Cfg.Url` ×200：152ms → **41ms**（8.5KB 日志，按 Length+mtime 缓存） |
| 模块扫描（v1.10） | `RefreshAux` ×50（20 个模块）：6816ms → **31ms**（目录指纹未变则跳过重建） |
| 句柄（v1.10） | 200 次 `ApplyScale` + 50 次 `RefreshAux` 后 GDI/USER 8→8，无泄漏；图标 PNG 加载后可删 |
| 不打断服务（v1.10） | 重装两次只重启启动器：dsh node pid 18284（08:50 启动）不变，3080 通，带 token 页面 HTTP **200 / 47107 字节** |
| 菜单实拍（v1.10） | 用户截图确认：9 条菜单、行首图标、状态绿球、自启对勾、辅助功能二级菜单均正常；日志无报错 |
| 便携版（v1.3） | 全新临时目录 + 自定义 `launcher.ini`（port=3999 / 自建 workdir / 假 dsh）实测：日志走完「已启动 dsh web → 端口已就绪」，工作目录自动创建、`host.pid` 正常写入 |
| 便携版（解压即用） | 从 zip 解压到临时目录直接运行：识别到 3080 已在跑 → 只记「启动器已运行」，不重复拉起、不开标签页；随包 `launcher.ini` 被正确读取 |
| 打包完整度 | `DSH-Web-启动器-便携版.zip` 54,682 字节，内含 4 个文件（exe 105,984 / ini 614 / 使用说明 2,805 / diagnose.cmd 1,104） |
| token 缓存 | `%LOCALAPPDATA%\DshLauncher\url.txt` 内的地址实测 **303**（日志被清空也能开对页面） |
| 诊断脚本 | `diagnose.cmd` 输出含 dsh/node 位置、`npm root -g`、3080 监听行、目录清单、launcher.ini 全文 |
| 中文路径（v1.4） | 造 `D:\测试区\工作目录` + 中文名假 dsh：老写法（GBK + `chcp 65001`）报 `The system cannot find the path specified`；新写法 `cd` 成功。整机联调日志 `已启动 dsh web → 端口已就绪`，3999 真实监听 |
| 打包完整度（v1.4） | `DSH-Web-Launcher-portable-v1.4.zip` 55,123 字节，内含 4 个文件（exe 106,496 / ini 614 / 使用说明 3,154 / diagnose.cmd 1,104） |
| 当前状态（v1.10） | 启动器 pid 16156（Medium，静默，11:55 启动），dsh node pid 18284（08:50 启动、全程未重启），3080 通，托盘鲸鱼 + 绿点 |

---

| 菜单（v1.10 起） | 150% 下 380×518，9 行（比 v1.6 多「辅助功能▸」与「退出并停止」）；100%–200% 四档同比缩放；行首图标槽 21×缩放 |
| 菜单（v1.18） | 150% 下 380×528；两行状态「状态：运行中 / 已运行 57 分钟」完整，状态球不压文字；高度按 Graphics 实测重算 |
| review 修复（v1.18） | 停止/重启异步，UI 不再冻结；`CommandLineHasPort` 只收当前端口；watchdog 校验启动令牌；taskkill 按退出码计数；`diagnose.cmd` 动态端口 |
| 等 ready 再开页（v1.21） | `WaitReadyUrl(90000)` 等 token + `ProbeHttp` 探活；反射实测 token 地址 303 → `True`、裸地址 401 → `True`、死端口 → `False`；日志已有 token 时 1 ms 返回 |
| 端口探测（v1.22） | 无人监听的本机端口每次等满 700 ms（实测 710/702/702/706/714/702/703/703 ms）；`Refresh_` 改读后台缓存后 UI 线程零阻塞；10 秒采样 CPU **0.000 秒**、内存 43.1 MB、11 线程 |
| 启动观测（v1.22） | `launcher.log` 每轮出现 `启动观测 +Ns` 与 5 个阶段标记；5 分钟自停；反射测试通过；用于把启动耗时定性为 CPU 密集 |
| 启动失败（v1.19） | cmd 提前退出约 1 秒内转 Error；通知带 `dsh-web.log` 尾部；`ReadLogTail` 反射测试返回末尾 287 字符；失败路径未实机触发，待可打断服务的窗口 |
| 停止逻辑（v1.6） | `host.pid` → `cmd=15728`，`CmdLineOf(15728)` = `cmd.exe /c ""…\start-dsh-web.cmd""`，`start-dsh-web`/`dsh` 两个条件均命中 |
| 文档配图（v1.6） | `docs\menu-final.png`（560×700）与 `docs\menu-states.png`（620×800）由 `MenuPreview.exe` 在纯色渐变背景上生成，**不含任何桌面内容**；预览器已加 `TopMost`，防止再抓到后台窗口 |

## 8. 维护手册

**改端口**：改安装目录 `launcher.ini` 的 `port=8080` 后重启启动器（v1.3 起不用重编译）；
临时试用也可以 `DshLauncher.exe --port=8080`。

**重编译 + 重装（一步到位）**：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "<仓库目录>\rebuild.ps1"
```

> 执行策略会拦 `.ps1`，所以必须走 `-ExecutionPolicy Bypass`；直接 `& rebuild.ps1` 会报「不是数字签名」。
> 脚本顺序是「编译 → 停旧进程 → 装 Medium 副本 → 重启 → 自检」，**必须先停进程**，
> 否则正在运行的 exe 覆盖不了，重启起来的还是旧版本（踩过）。

**手动编译（等价）**：

```
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /codepage:65001 /target:winexe ^
  /platform:anycpu /optimize+ /win32icon:dsh.ico /win32manifest:app.manifest ^
  /resource:logo256.png,DshLogo.png /out:DshLauncher.exe ^
  /r:System.dll /r:System.Core.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Management.dll ^
  DshLauncher.cs DshMenu.cs
```

三个参数少一个都会出问题：`/codepage:65001` 少了界面中文变乱码；`/win32manifest:app.manifest` 少了菜单又被系统位图拉伸（§4.9）；
`/resource:logo256.png,DshLogo.png` 少了图标退回「蓝底白 D」保底样式（§4.11）。

手动编译只产出 exe；`icons\`、`dsh.ico`、`dsh-toast.png` 需要自己复制到安装目录（`rebuild.ps1` 第 3 步会一起做，并校验 SHA-256）。

**换图标**（想改成别的标）：
1. 把新图存成 256×256 透明 PNG，覆盖 `logo256.png`；
2. 跑 `tools\MakeIco.exe logo256.png dsh.ico`（参数是「输入 PNG、输出 ICO」；≤64 用 DIB、128/256 用 PNG 压缩）；
3. 跑 `rebuild.ps1` —— 快捷方式/exe 图标与托盘图标一起换掉（托盘是运行时合成的，不必单独处理）。

**改完后必须做的三件事**（顺序不能反）：`rebuild.ps1` → 看日志有没有 `启动器已运行` → UIA/肉眼确认托盘图标在。

**单独预览菜单外观**（改完 `DshMenu.cs` 想先看看效果，不必重装）：

```
csc /nologo /codepage:65001 /target:winexe /win32manifest:app.manifest /out:tools\MenuPreview.exe ^
  /r:System.Windows.Forms.dll /r:System.Drawing.dll DshMenu.cs tools\MenuPreview.cs
tools\MenuPreview.exe 1.5 hover          :: 参数：缩放倍数 [run|start|stop|err] [hover]
```

**日志位置**：`%LOCALAPPDATA%\DshLauncher\launcher.log`（启动器动作；超过 1 MB 自动改名为 `launcher.log.1`）
和 `%LOCALAPPDATA%\DshLauncher\dsh-web.log`（dsh web 输出，**带 token 的地址在最后一行**；
同一份地址还缓存在 `%LOCALAPPDATA%\DshLauncher\url.txt`）。

**自检三连**：进程在不在 → 托盘 UIA 有没有 `DSH Web` → `url.txt` 里的地址能不能拿到 303。

**改动配置不用编译**：端口/工作目录/dsh 路径都在安装目录的 `launcher.ini` 里，改完重启启动器即可（§4.14）。

**发新版给用户/朋友**：改完 `rebuild.ps1` → 把安装目录的 `DshLauncher.exe` 和模板 `launcher.ini` 拷进
`dist\DSH-Web-启动器\` → 用 `Compress-Archive -Path "dist\DSH-Web-启动器\*" -DestinationPath "dist\DSH-Web-Launcher-portable-vX.Y.zip"` 打包
（zip 名用纯 ASCII，GitHub 会把中文资产名抹成 `DSH-Web-.-.zip`）。

**排错速查**：托盘没图标 = 完整性标签不是 Medium（§4.5）；菜单发虚 = manifest 没编进去（§4.9）；
菜单多开标签页 = 重启启动器没带 `--silent`（§4.12）；`rebuild.ps1` 报莫名其妙的错 = 脚本里混了中文却丢了 BOM（§4.13；v1.10 起该脚本已全 ASCII，正常不会再犯）；
中文用户名下「系统找不到指定的路径」= 批处理里混了 GBK 与 `chcp 65001`，见 §4.15（v1.4 已修）。

---

## 9. 下一步建议（供用户排序）

1. **开机自启**：托盘菜单「开机自动启动」勾上即可（当前是关闭状态）。勾上后登录启动用的是 `--silent`，
   只起服务、不弹浏览器，双击快捷方式才开页面。
2. **是否要「最小化到托盘 + 状态窗口」**：目前无主窗口，全靠托盘菜单；若想看到端口/pid/最近日志，
   可以加一个只读的小面板窗口。
3. **清理工作区根目录那一百多个历史临时脚本**（需要你确认，我不会自作主张删）。
4. ~~做成可发给别人的便携版~~ —— 已完成（v1.3 起，v1.4 修掉中文路径 bug，见 §4.14 / §4.15）。
   对方机器前提只有一个：装过 DSH **命令行**（`dsh --version` 有输出）。
5. 可选：给 exe **签名**（自签名也行）能缓解 SmartScreen 提示，但对陌生机器仍会拦一次；正解是拿代码签名证书。
6. 若真要分发给不熟的人，下一步可以做 `DshLauncher-Setup.exe`：自动装到 `%LOCALAPPDATA%\Programs`、
   用计划任务打 Medium 标签、建快捷方式与自启项。便携版在**全新机器**上没有 Low 标签问题（zip 不带标签），
   只有「在本机这个 DSH 会话里生成的 exe」才会带标签。
7. **§5.7 的剩余项**：B5（非中文 Windows + 中文路径）和 `QuitAll` 同步退出尚未处理；B1/B2/B3/B6 已修。
   停止/重启/启动失败三条路径仍建议在「可以打断服务」的窗口做一次实机回归（本次没触发，避免断开当前 3080 会话）。
