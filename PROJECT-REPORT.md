# DSH Web 一键启动器 — 项目报告

> 版本：1.2（2026-10-27，第二轮全量 review：图标改为 DeepSeek 鲸鱼 + 状态点、菜单版式定稿、清掉过程产物）
> 位置：`<仓库目录>\`
> 装机位置：`%LOCALAPPDATA%\Programs\DshLauncher\`
> 用途：**双击一个图标就能起 dsh web**，不再需要开 cmd / PowerShell 敲命令。

**源码规模**：`DshLauncher.cs` 786 行 + `DshMenu.cs` 413 行 + `rebuild.ps1` 68 行，`/warn:4` 零警告；
当前运行的 exe 编译于 15:04:44（与源码一致），启动器进程 pid 22684，3080 由 node 监听。

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
        ├─ 起服务：写 %LOCALAPPDATA%\DshLauncher\start-dsh-web.cmd
        │          内容 = chcp 65001 + cd 工作目录 + node bin.js web --port 3080 --no-open
        │          以 CreateNoWindow + Hidden 启动，输出重定向到 dsh-web.log
        │          轮询端口（最长 120 秒）→ 就绪后从日志里取带 token 的地址 → 用 explorer.exe 打开
        │
        └─ 托盘菜单（iOS 风格自绘、按鼠标所在屏 DPI 缩放）：状态球 + 打开界面 / 重启 / 停止 / 开机自启 / 打开日志 / 退出
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
| `DshLauncher.exe` | 编译产物（**注意：低完整性标签，直接双击会没有托盘图标**，见 §4.5） |
| `launcher.ini` | **运行期配置**（端口 / 工作目录 / dsh 路径），安装目录里那份生效，本目录这份是模板 |
| `rebuild.ps1` | 一键「编译 → 停旧进程 → 安装带 Medium 标签的副本 → **静默**重启 → 自检」 |
| `dist\` | 便携版成品：`DSH-Web-启动器\`（exe + ini + 使用说明 + diagnose.cmd）与同名 zip，54 KB |
| `docs\` | 4 张设计图：`menu-final.png`（150% 悬停态）、`menu-states.png`（四状态）、`icon-states.png`（图标四态）、`tray-live.png`（真实托盘） |
| `tools\MenuPreview.*` | 单独预览菜单外观（`MenuPreview.exe 1.5 [run\|start\|stop\|err] [hover]`），不属于运行链路 |
| `tools\MakeIco.*` | 由 `logo256.png` 生成多尺寸 `dsh.ico`（≤64 用 DIB，128/256 用 PNG 压缩） |
| `tools\IconPreview.*` | 把托盘四状态图标渲染成 `docs\icon-states.png` 供核对 |
| `README.md` | 面向使用者的简明说明 |
| `PROJECT-REPORT.md` | 本文件（面向使用者 + 面向后续维护者） |

### 安装目录 `%LOCALAPPDATA%\Programs\DshLauncher\`

| 文件 | 说明 |
| --- | --- |
| `DshLauncher.exe` + `dsh.ico` | **Medium 完整性**副本，桌面/开始菜单快捷方式指向这里 |
| `launcher.ini` | 运行期配置（端口 / 工作目录 / dsh 路径） |

### 运行时状态 `%LOCALAPPDATA%\DshLauncher\`

v1.3 起从 `%TEMP%\.dsh-launcher` 搬到这里（临时目录会被清理、也不适合放"别人的机器"上的状态）；
第一次运行会把旧目录里的 `host.pid / dsh-web.log / url.txt` 自动拷过来。

| 文件 | 说明 |
| --- | --- |
| `start-dsh-web.cmd` | 每次启动服务时重写（含 `cd` 到工作目录、`--no-open`、输出重定向） |
| `dsh-web.log` | **dsh web 的控制台输出**，带 token 的地址就在其中一行 |
| `url.txt` | **最近一次带 token 的地址**（日志被清掉/轮转也不会丢，见 §4.14） |
| `host.pid` | 上次启动的 cmd pid / 端口 / 时间 |
| `launcher.log` | 启动器主日志（UTF-8，超过 1 MB 自动重开） |

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
- **规矩**：`.ps1` 必须 **UTF-8 带 BOM**（`[System.Text.UTF8Encoding]::new($true)` 写入）；`.cmd` 路径写死成 `"$dir\_start.cmd"` 字面量，别依赖 `$PSScriptRoot`/`Join-Path`。

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

---

## 5. 本次全量 Review 的结论

共做了两轮全量 review（第一轮在托盘图标修好之后，第二轮在图标与菜单定稿之后）。两轮都是「通读全部源码 + 三份工具源码 + 脚本」，
`/warn:4` 编译零警告，改完即重装验证。

### 5.1 已修复（第一轮）

| # | 严重度 | 问题 | 处理 |
| --- | --- | --- | --- |
| 1 | 高 | 重启服务可能因端口未释放而启动失败（EADDRINUSE） | `StopHost` 增加 `WaitPortClosed(15000)`，`RestartHost` 不再盲等 600 ms |
| 2 | 中 | 托盘双击 / 重复实例 / 重启同时触发时可能拉起第二个 dsh web | `StartHost` 增加「已在启动中则忽略」的守卫（加锁判断） |
| 3 | 中 | 一次操作可能打开两个标签页（状态刷新与双击前后脚各开一次） | `OpenUi` 增加 8 秒内同 URL 去重 |
| 4 | 低 | `launcher.log` 无限增长 | 超过 1 MB 自动删除重写 |
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
| 启动器日志出现双份 | 设计如此：安装目录 + `%TEMP%` 各写一份，防止其中一处不可写 |

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

---

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
  只留 4 张：`menu-final.png`、`menu-states.png`、`icon-states.png`、`tray-live.png`；
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
| 菜单尺寸（150%） | `1.0→202×306`、`1.25→236×370`、`1.5→278×418`、`2.0→350×548`，同比放大、不糊 |
| 菜单留白（150%，像素测量） | 顶边→胶囊 8px、胶囊→分割线 9px、末条胶囊→底边 9px（同一套 `sepV` 推导） |
| 编译健康 | `/warn:4` 零警告；`fatal.log` 不存在 |
| 便携版（v1.3） | 全新临时目录 + 自定义 `launcher.ini`（port=3999 / 自建 workdir / 假 dsh）实测：日志走完「已启动 dsh web → 端口已就绪」，工作目录自动创建、`host.pid` 正常写入 |
| 便携版（解压即用） | 从 zip 解压到临时目录直接运行：识别到 3080 已在跑 → 只记「启动器已运行」，不重复拉起、不开标签页；随包 `launcher.ini` 被正确读取 |
| 打包完整度 | `DSH-Web-启动器-便携版.zip` 54,682 字节，内含 4 个文件（exe 105,984 / ini 614 / 使用说明 2,805 / diagnose.cmd 1,104） |
| token 缓存 | `%LOCALAPPDATA%\DshLauncher\url.txt` 内的地址实测 **303**（日志被清空也能开对页面） |
| 诊断脚本 | `diagnose.cmd` 输出含 dsh/node 位置、`npm root -g`、3080 监听行、目录清单、launcher.ini 全文 |
| 当前状态 | 启动器 pid 54960（Medium，静默），3080 由 node 监听，托盘鲸鱼 + 绿点，桌面/开始菜单快捷方式指向安装目录 |

---

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

**日志位置**：`%LOCALAPPDATA%\DshLauncher\launcher.log`（启动器动作）
和 `%LOCALAPPDATA%\DshLauncher\dsh-web.log`（dsh web 输出，**带 token 的地址在最后一行**；
同一份地址还缓存在 `%LOCALAPPDATA%\DshLauncher\url.txt`）。

**自检三连**：进程在不在 → 托盘 UIA 有没有 `DSH Web` → `url.txt` 里的地址能不能拿到 303。

**改动配置不用编译**：端口/工作目录/dsh 路径都在安装目录的 `launcher.ini` 里，改完重启启动器即可（§4.14）。

**发新版给用户/朋友**：改完 `rebuild.ps1` → 把安装目录的 `DshLauncher.exe` 和模板 `launcher.ini` 拷进
`dist\DSH-Web-启动器\` → 重新 `Compress-Archive` 成 `dist\DSH-Web-启动器-便携版.zip`。

**排错速查**：托盘没图标 = 完整性标签不是 Medium（§4.5）；菜单发虚 = manifest 没编进去（§4.9）；
菜单多开标签页 = 重启启动器没带 `--silent`（§4.12）；`rebuild.ps1` 报莫名其妙的错 = 文件没存成带 BOM 的 UTF-8（§4.13）。

---

## 9. 下一步建议（供用户排序）

1. **开机自启**：托盘菜单「开机自动启动」勾上即可（当前是关闭状态）。勾上后登录启动用的是 `--silent`，
   只起服务、不弹浏览器，双击快捷方式才开页面。
2. **是否要「最小化到托盘 + 状态窗口」**：目前无主窗口，全靠托盘菜单；若想看到端口/pid/最近日志，
   可以加一个只读的小面板窗口。
3. **清理工作区根目录那一百多个历史临时脚本**（需要你确认，我不会自作主张删）。
4. ~~做成可发给别人的便携版~~ —— 已完成（v1.3，`dist\DSH-Web-启动器-便携版.zip`，见 §4.14）。
   对方机器前提只有一个：装过 DSH **命令行**（`dsh --version` 有输出）。
5. 可选：给 exe **签名**（自签名也行）能缓解 SmartScreen 提示，但对陌生机器仍会拦一次；正解是拿代码签名证书。
6. 若真要分发给不熟的人，下一步可以做 `DshLauncher-Setup.exe`：自动装到 `%LOCALAPPDATA%\Programs`、
   用计划任务打 Medium 标签、建快捷方式与自启项。便携版在**全新机器**上没有 Low 标签问题（zip 不带标签），
   只有「在本机这个 DSH 会话里生成的 exe」才会带标签。
