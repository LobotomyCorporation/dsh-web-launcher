// DSH Web 一键启动器（托盘常驻）
// 编译：csc.exe /target:winexe /out:DshLauncher.exe /r:System.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Management.dll DshLauncher.cs
// 设计要点：
//   1) WinExe 子系统 —— 全程不创建任何控制台窗口（解决 cmd 黑框闪现）
//   2) 命名 Mutex 单实例 —— 重复双击只会唤起已运行实例并打开界面（解决多开）
//   3) TcpClient 探测端口 —— 不依赖 WMI（解决端口误判导致重复启动）
//   4) 启动 dsh web 时强制 --no-open —— 浏览器只由本程序打开一次（解决一次点出两个界面）
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Management;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace DshLauncher
{
    internal static class Cfg
    {
        public static int Port = 3080;
        public static string BaseUrl { get { return "http://127.0.0.1:" + Port.ToString() + "/"; } }

        // ---------- 可移植配置：exe 同目录的 launcher.ini ----------
        // 换机器时只改这个文本文件，不用重新编译；不写就按下面的顺序自动探测。
        public static readonly string IniFile = Path.Combine(
            Path.GetDirectoryName(Application.ExecutablePath), "launcher.ini");
        private static readonly Dictionary<string, string> _ini = LoadIni();

        public static string Ini(string key)
        {
            string v;
            return _ini.TryGetValue(key, out v) ? v : null;
        }

        private static Dictionary<string, string> LoadIni()
        {
            Dictionary<string, string> d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(IniFile)) return d;
                foreach (string raw in File.ReadAllLines(IniFile))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#' || line[0] == ';' || line[0] == '[') continue;
                    int k = line.IndexOf('=');
                    if (k <= 0) continue;
                    string key = line.Substring(0, k).Trim();
                    string val = line.Substring(k + 1).Trim().Trim('"');
                    if (key.Length > 0) d[key] = Environment.ExpandEnvironmentVariables(val);
                }
            }
            catch { }
            return d;
        }

        /// <summary>首次运行时写一份带注释的配置模板，方便别人直接改。</summary>
        public static void EnsureIni()
        {
            try
            {
                if (File.Exists(IniFile)) return;
                string t =
                    "# DSH Web 启动器配置（改完保存，重新双击启动器即可生效）\r\n" +
                    "# 所有项都可以留空：留空 = 用默认值 / 自动探测。只改你需要的行。\r\n" +
                    "\r\n" +
                    "# 网页端口（默认 3080）\r\n" +
                    "port=" + Port + "\r\n" +
                    "\r\n" +
                    "# dsh web 启动时的工作目录；留空 = 文档目录下的 DSH_Workspace（不存在会自动创建）\r\n" +
                    "workdir=\r\n" +
                    "\r\n" +
                    "# dsh 命令的完整路径（dsh.cmd）。留空 = 自动在 PATH 里找\r\n" +
                    "dsh=\r\n" +
                    "\r\n" +
                    "# 只有在自动探测失败时才需要填下面两行（node + dsh 的 bin.js）\r\n" +
                    "# node=C:\\Program Files\\nodejs\\node.exe\r\n" +
                    "# bin=%APPDATA%\\npm\\node_modules\\@deepseek-ai\\dsh\\lib\\bin.js\r\n";
                File.WriteAllText(IniFile, t, new System.Text.UTF8Encoding(false));
            }
            catch { }
        }

        static Cfg()
        {
            int p;
            if (int.TryParse(Ini("port"), out p) && p > 0 && p < 65536) Port = p;
            string wd = Ini("workdir");
            // 没配就落到「文档\DSH_Workspace」，换台电脑也不用改代码
            WorkDir = string.IsNullOrEmpty(wd) ? DefaultWorkDir : wd;
            try { Directory.CreateDirectory(WorkDir); } catch { }
        }

        /// <summary>换机器时的兜底工作目录：文档\DSH_Workspace。</summary>
        public static string DefaultWorkDir
        {
            get
            {
                try
                {
                    string my = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "DSH_Workspace");
                    return my;
                }
                catch { return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile); }
            }
        }

        /// <summary>
        /// dsh web 只接受带 token 的地址，裸地址会提示
        /// "dsh web authentication required; reopen the URL printed by dsh web"。
        /// 宿主启动时把带 token 的地址打印进日志，这里取最后一条；
        /// 取不到（旧日志/首次启动）才退回裸地址。
        /// </summary>
        public static string Url
        {
            get
            {
                string u = ReadTokenUrl();
                if (u != null) { Cfg._cachedUrl = u; SaveUrl(u); return u; }
                string cached = LoadUrl();
                if (cached != null) { Cfg._cachedUrl = cached; return cached; }
                return Cfg._cachedUrl != null ? Cfg._cachedUrl : BaseUrl;
            }
        }

        private static string _cachedUrl;

        /// <summary>服务重启后旧的 token 立即失效，必须清掉缓存。</summary>
        public static void ClearUrlCache() { _cachedUrl = null; ClearUrl(); }

        private static string UrlFile { get { return Path.Combine(StateDir, "url.txt"); } }

        /// <summary>
        /// 带 token 的地址另存一份：日志可能被清空/轮转/删除，网址不该因此丢失。
        /// </summary>
        public static void SaveUrl(string url)
        {
            try { File.WriteAllText(UrlFile, url, new System.Text.UTF8Encoding(false)); } catch { }
        }

        public static string LoadUrl()
        {
            try
            {
                if (!File.Exists(UrlFile)) return null;
                string u = File.ReadAllText(UrlFile).Trim();
                if (u.Length == 0) return null;
                if (u.IndexOf(":" + Port.ToString() + "/", StringComparison.Ordinal) < 0) return null;
                return u;
            }
            catch { return null; }
        }

        public static void ClearUrl()
        {
            try { if (File.Exists(UrlFile)) File.Delete(UrlFile); } catch { }
        }

        /// <summary>
        /// 读最后一条带 token 的地址；读不到返回 null。
        /// 必须用 FileShare.ReadWrite 打开：宿主进程一直占着这个日志文件写入，
        /// 默认的 File.ReadAllLines 会抛“文件正被另一进程使用”。
        /// </summary>
        public static string ReadTokenUrl()
        {
            try
            {
                if (!File.Exists(LogFile)) return null;
                string found = null;
                using (FileStream fs = new FileStream(LogFile, FileMode.Open, FileAccess.Read,
                                                      FileShare.ReadWrite | FileShare.Delete))
                using (StreamReader sr = new StreamReader(fs, System.Text.Encoding.UTF8))
                {
                    string line;
                    while ((line = sr.ReadLine()) != null)
                    {
                        int k = line.IndexOf("http://", StringComparison.Ordinal);
                        if (k < 0) continue;
                        string u = line.Substring(k).Trim();
                        if (u.IndexOf("token=", StringComparison.Ordinal) < 0) continue;
                        if (u.IndexOf(":" + Port.ToString() + "/", StringComparison.Ordinal) < 0) continue;
                        found = u;
                    }
                }
                return found;
            }
            catch { return null; }
        }

        /// <summary>刚拉起服务时，等日志里出现带 token 的地址（最多 ms 毫秒）。</summary>
        public static void WaitForUrl(int ms)
        {
            DateTime end = DateTime.Now.AddMilliseconds(ms);
            while (DateTime.Now < end)
            {
                if (ReadTokenUrl() != null) return;
                System.Threading.Thread.Sleep(250);
            }
        }
        public static string StateDir
        {
            get
            {
                // 放在 %LOCALAPPDATA%\DshLauncher：比 %TEMP% 稳（不会被清理），换机器也是每用户一份。
                string d = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DshLauncher");
                try { Directory.CreateDirectory(d); MigrateOldState(d); } catch { }
                return d;
            }
        }

        private static bool _migrated;

        /// <summary>把旧版放在 %TEMP%\.dsh-launcher 里的状态搬过来（只搬一次）。</summary>
        private static void MigrateOldState(string newDir)
        {
            if (_migrated) return;
            _migrated = true;
            try
            {
                string oldDir = Path.Combine(Path.GetTempPath(), ".dsh-launcher");
                if (!Directory.Exists(oldDir)) return;
                foreach (string name in new string[] { "host.pid", "dsh-web.log", "url.txt" })
                {
                    try
                    {
                        string src = Path.Combine(oldDir, name);
                        string dst = Path.Combine(newDir, name);
                        if (File.Exists(src) && !File.Exists(dst)) File.Copy(src, dst, true);
                    }
                    catch { }
                }
            }
            catch { }
        }
        public static string LogDir
        {
            get
            {
                // 保留给旧代码引用：日志现在统一写在状态目录，见 LauncherLog
                return StateDir;
            }
        }
        public static string LogFile { get { return Path.Combine(StateDir, "dsh-web.log"); } }
        public static string LauncherLog { get { return Path.Combine(StateDir, "launcher.log"); } }
        public static string CmdFile { get { return Path.Combine(StateDir, "start-dsh-web.cmd"); } }
        public static string PidFile { get { return Path.Combine(StateDir, "host.pid"); } }
        public static string WorkDir = "";

        /// <summary>在 PATH 里找一个可执行文件（Windows 的 where 语义，只看 .cmd/.exe/.bat）。</summary>
        private static string FindOnPath(string baseName)
        {
            string path = Environment.GetEnvironmentVariable("PATH") ?? "";
            string[] exts = new string[] { ".cmd", ".exe", ".bat", "" };
            foreach (string raw in path.Split(';'))
            {
                string dir = raw.Trim().Trim('"');
                if (dir.Length == 0) continue;
                foreach (string ext in exts)
                {
                    try
                    {
                        string full = Path.Combine(dir, baseName + ext);
                        if (File.Exists(full)) return full;
                    }
                    catch { }
                }
            }
            return null;
        }

        /// <summary>
        /// dsh 命令：优先 launcher.ini 的 dsh=，其次环境变量 DSH_CMD，最后在 PATH 里找 dsh.cmd。
        /// 找到它就不必关心 node / bin.js 的具体位置，各种安装方式（npm -g、nvm、pnpm）都能用。
        /// </summary>
        public static string DshCmd
        {
            get
            {
                string v = Ini("dsh");
                if (!string.IsNullOrEmpty(v) && File.Exists(v)) return v;
                string env = Environment.GetEnvironmentVariable("DSH_CMD");
                if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;
                return FindOnPath("dsh");
            }
        }

        public static string NodeExe
        {
            get
            {
                string ini = Ini("node");
                if (!string.IsNullOrEmpty(ini) && File.Exists(ini)) return ini;
                string env = Environment.GetEnvironmentVariable("DSH_NODE");
                if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;
                string onPath = FindOnPath("node");
                if (onPath != null) return onPath;
                string[] cand = new string[] {
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"nodejs\node.exe"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"nodejs\node.exe"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\nodejs\node.exe"),
                    @"C:\Program Files\nodejs\node.exe"
                };
                foreach (string c in cand) { try { if (File.Exists(c)) return c; } catch { } }
                return null;
            }
        }

        public static string DshBin
        {
            get
            {
                string ini = Ini("bin");
                if (!string.IsNullOrEmpty(ini) && File.Exists(ini)) return ini;
                string env = Environment.GetEnvironmentVariable("DSH_BIN");
                if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;

                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
                const string tail = @"node_modules\@deepseek-ai\dsh\lib\bin.js";
                List<string> cand = new List<string>();
                // npm 默认全局目录
                cand.Add(Path.Combine(appData, @"npm\" + tail));
                // npm 装在 node 目录下（部分安装器会这样）
                cand.Add(Path.Combine(pf, "nodejs", tail));
                cand.Add(Path.Combine(pf86, "nodejs", tail));
                // pnpm 全局
                try
                {
                    string pnpm = Path.Combine(local, @"pnpm\global");
                    if (Directory.Exists(pnpm))
                        foreach (string v in Directory.GetDirectories(pnpm))
                            cand.Add(Path.Combine(v, tail));
                }
                catch { }
                // nvm-windows 的符号链接目录
                try
                {
                    if (Directory.Exists(@"C:\Program Files\nodejs\node_modules"))
                        cand.Add(Path.Combine(@"C:\Program Files\nodejs", tail));
                }
                catch { }

                foreach (string c in cand) { try { if (File.Exists(c)) return c; } catch { } }
                return null;
            }
        }
    }

    internal static class Log
    {
        private const long MaxBytes = 1024 * 1024;   // 超过 1 MB 就重开一份，避免日志无限增长

        public static void Write(string msg)
        {
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg + Environment.NewLine;
            // 首选状态目录（和 dsh-web.log、url.txt 放一起，只在这一处）；
            // 万一那里不可写，再退回到 exe 同目录的 logs。
            string[] files = new string[] { Cfg.LauncherLog, ExeDirLog };
            foreach (string f in files)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(f));
                    try { if (File.Exists(f) && new FileInfo(f).Length > MaxBytes) File.Delete(f); } catch { }
                    File.AppendAllText(f, line, new System.Text.UTF8Encoding(true));
                    return;
                }
                catch { }
            }
        }

        /// <summary>兜底日志位置：程序目录下的 logs\launcher.log。</summary>
        public static string ExeDirLog
        {
            get
            {
                return Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "logs", "launcher.log");
            }
        }
    }

    internal enum St { Unknown, Stopped, Starting, Running, Error }

    internal sealed class Launcher : ApplicationContext
    {
        private readonly NotifyIcon _ni;
        private readonly MenuSpec _menu;
        private readonly ToolStripMenuItem _iOpen, _iRestart, _iStop, _iAuto, _iLog, _iQuit, _iStatus;
        private readonly System.Windows.Forms.Timer _timer;
        private readonly Form _anchor;   // 隐藏宿主窗体，仅用于跨线程封送（不显示）
        private St _state = St.Unknown;
        private string _detail = "";
        private readonly object _lock = new object();
        private bool _silent;
        private bool _browserOpenedThisRun;
        private bool _startedByUs;
        private DateTime _lastOpenAt = DateTime.MinValue;
        private string _lastOpenUrl = "";

        private Icon _iGray, _iGreen, _iOrange, _iRed;

        public Launcher(bool silent)
        {
            _silent = silent;

            _anchor = new Form();
            _anchor.ShowInTaskbar = false;
            _anchor.FormBorderStyle = FormBorderStyle.None;
            _anchor.StartPosition = FormStartPosition.Manual;
            _anchor.Location = new Point(-4000, -4000);
            _anchor.Size = new Size(1, 1);
            _anchor.Opacity = 0;
            _anchor.Show();
            _anchor.Hide();

            _iGray = MakeIcon(Color.FromArgb(130, 138, 152));
            _iGreen = MakeIcon(Color.FromArgb(41, 165, 96));
            _iOrange = MakeIcon(Color.FromArgb(226, 148, 34));
            _iRed = MakeIcon(Color.FromArgb(210, 66, 66));

            _ni = new NotifyIcon();
            _ni.Icon = _iGray;
            _ni.Text = "DSH Web";
            _ni.Visible = true;
            _ni.DoubleClick += delegate
            {
                if (PortOpen()) OpenUi();
                else { if (_silent) _silent = false; StartHost(); }
            };

            DshMenu.Trace = delegate (string msg) { Log.Write(msg); };
            // 只要用户动了托盘菜单，就说明这次是「有人在用」，静默标记一律作废——
            // 否则开机自启（--silent）起来的实例被点「重启服务」后不会打开页面。
            MenuSpec spec = DshMenu.Create(
                delegate { _silent = false; OpenUi(); },
                delegate { _silent = false; RestartHost(); },
                delegate { _silent = false; StopHost(true); },
                delegate { ToggleAutostart(); },
                delegate { OpenLog(); },
                delegate { Quit(); });
            _menu = spec;
            _iStatus = spec.Status;
            _iOpen = spec.Open;
            _iRestart = spec.Restart;
            _iStop = spec.Stop;
            _iAuto = spec.Auto;
            _iLog = spec.Log;
            _iQuit = spec.Quit;
            _ni.ContextMenuStrip = spec.Menu;
            _iAuto.Checked = IsAutostart();
            spec.Menu.Invalidate();
            // 登录自启时不弹气泡：用户没在电脑前，弹了他也看不到，还会盖住右下角
            if (!_silent)
            {
                try { _ni.ShowBalloonTip(6000, "DSH Web 启动器", "已在后台运行。右键托盘图标可以打开界面、停止或重启服务。", ToolTipIcon.None); } catch { }
            }

            _timer = new System.Windows.Forms.Timer();
            _timer.Interval = 1000;
            _timer.Tick += delegate { Refresh_(); };
            _timer.Start();

            Refresh_();
            if (!_silent) Log.Write("启动器已运行（端口 " + Cfg.Port + "）");
            else Log.Write("启动器已运行（登录自启，静默模式）");

            // 已停止则自动拉起
            if (!PortOpen()) StartHost();
        }

        // ---------- 状态刷新（UI 线程） ----------
        private void Refresh_()
        {
            bool up = PortOpen();
            St s;
            lock (_lock)
            {
                if (_state == St.Starting && !up) s = St.Starting;
                else if (up) s = St.Running;
                else if (_state == St.Error) s = St.Error;
                else s = St.Stopped;
                _state = s;
            }
            Color dot;
            string txt;
            switch (s)
            {
                case St.Running: _ni.Icon = _iGreen; txt = "运行中"; dot = Ios.Green; break;
                case St.Starting: _ni.Icon = _iOrange; txt = "启动中…"; dot = Ios.Orange; break;
                case St.Error: _ni.Icon = _iRed; txt = "启动失败"; dot = Ios.Red; break;
                default: _ni.Icon = _iGray; txt = "未运行"; dot = Ios.Gray; break;
            }
            string tip = "DSH Web · " + txt;
            if (s == St.Running) tip += " · 端口 " + Cfg.Port;
            _menu.SetStatus("状态：" + txt + (_detail.Length > 0 && s == St.Error ? "（" + _detail + "）" : ""), dot);
            _iOpen.Enabled = s == St.Running;
            _iStop.Enabled = s == St.Running || s == St.Starting;
            _iRestart.Enabled = true;
            try { if (_ni.Text != tip) _ni.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip; } catch { }

            // 只为自己刚拉起来的服务自动开页：服务若是被外部（终端/别的脚本）拉起的，
            // 这里就静静等着，别擅自弹标签页——之前"每次改代码多一个页"就是这么来的。
            if (s == St.Running && !_browserOpenedThisRun && _startedByUs)
            {
                _browserOpenedThisRun = true;
                if (!_silent)
                {
                    // 等 token 地址落盘可能要几秒，绝不能在 UI 线程上等（那会让托盘菜单卡住），
                    // 因此丢到线程池里等，等到了再封送回 UI 线程打开。
                    ThreadPool.QueueUserWorkItem(delegate
                    {
                        if (!PortOpen()) return;
                        Cfg.WaitForUrl(8000);
                        Post(delegate { OpenUi(); });
                    });
                }
            }
        }

        // ---------- 启动主机 ----------
        private void StartHost()
        {
            // 端口已经通了就别再拉一个：两个 node 抢同一个端口，第二个会 EADDRINUSE 直接死，
            // 日志里留下一串报错，还会让"重启服务"看起来失败。
            if (PortOpen())
            {
                lock (_lock) { _state = St.Running; }
                Refresh_();
                return;
            }
            lock (_lock)
            {
                // 已经在启动中（托盘双击、重复实例、重启都可能同时触发），别再拉第二个进程
                if (_state == St.Starting && !PortOpen())
                {
                    Log.Write("已在启动中，忽略重复的启动请求");
                    return;
                }
                _state = St.Starting;
                _detail = "";
            }
            _browserOpenedThisRun = false;
            _startedByUs = true;
            Cfg.ClearUrlCache();

            // 两条路：优先直接调 dsh 命令（各种安装方式都通用），退而用 node + bin.js。
            string dshCmd = Cfg.DshCmd;
            string node = Cfg.NodeExe, bin = Cfg.DshBin;
            string runCmd;
            if (dshCmd != null)
                runCmd = "\"" + dshCmd + "\" web --port " + Cfg.Port + " --no-open";
            else if (node != null && bin != null)
                runCmd = "\"" + node + "\" \"" + bin + "\" web --port " + Cfg.Port + " --no-open";
            else
            {
                lock (_lock) { _state = St.Error; _detail = "找不到 dsh"; }
                Log.Write("错误：dsh=" + (dshCmd ?? "null") + " node=" + (node ?? "null") + " bin=" + (bin ?? "null"));
                Notify("启动失败", "没找到 DSH 命令行（dsh）。\n\n" +
                    "请先安装：npm i -g @deepseek-ai/dsh\n" +
                    "如果装的是桌面客户端（没有 dsh 命令），这个启动器用不了；\n" +
                    "也可以在程序同目录的 launcher.ini 里把 dsh= 指到 dsh.cmd 的完整路径。");
                return;
            }

            try { if (File.Exists(Cfg.LogFile)) File.Delete(Cfg.LogFile); } catch { }
            string script =
                "@echo off\r\n" +
                "chcp 65001 > nul\r\n" +
                "cd /d \"" + Cfg.WorkDir + "\"\r\n" +
                "if not defined DSH_HOME set \"DSH_HOME=" + Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh") + "\"\r\n" +
                runCmd + " > \"" + Cfg.LogFile + "\" 2>&1\r\n";
            try { File.WriteAllText(Cfg.CmdFile, script, System.Text.Encoding.Default); }
            catch (Exception ex) { Log.Write("写 cmd 失败：" + ex.Message); }

            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = Cfg.CmdFile;
                psi.WorkingDirectory = Cfg.WorkDir;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                Process p = Process.Start(psi);
                Log.Write("已启动 dsh web（cmd pid=" + p.Id + "），等待端口 " + Cfg.Port);
                try { File.WriteAllText(Cfg.PidFile, "cmd=" + p.Id + "\r\nport=" + Cfg.Port + "\r\nstarted=" + DateTime.Now.ToString("s")); } catch { }
            }
            catch (Exception ex)
            {
                lock (_lock) { _state = St.Error; _detail = ex.Message; }
                Log.Write("启动进程失败：" + ex.Message);
                Notify("启动失败", ex.Message);
                return;
            }

            ThreadPool.QueueUserWorkItem(delegate
            {
                DateTime end = DateTime.Now.AddSeconds(120);
                while (DateTime.Now < end)
                {
                    if (PortOpen()) { lock (_lock) { _state = St.Running; } Log.Write("端口已就绪"); return; }
                    Thread.Sleep(700);
                }
                lock (_lock) { _state = St.Error; _detail = "120 秒未就绪"; }
                Log.Write("超时：120 秒内端口 " + Cfg.Port + " 未监听，详见 " + Cfg.LogFile);
                Notify("启动超时", "dsh web 没能在 120 秒内开始监听，点托盘图标看日志。");
            });
        }

        private void RestartHost()
        {
            Log.Write("请求重启");
            StopHost(false);   // 内部会等端口真正释放
            StartHost();
        }

        private void StopHost(bool ask)
        {
            if (ask)
            {
                DialogResult r = MessageBox.Show("停止 DSH Web 服务？\n所有正在对话的页面会断开。",
                    "DSH 启动器", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (r != DialogResult.Yes) return;
            }
            int n = KillDshHosts();
            Log.Write("已停止 " + n + " 个 dsh web 进程");
            try { if (File.Exists(Cfg.PidFile)) File.Delete(Cfg.PidFile); } catch { }
            try { _menu.SetStatus("状态：停止中…", Ios.Gray); } catch { }
            // node 退出到端口真正关闭之间有一段间隙。不等就立刻重启，新进程会因为
            // EADDRINUSE: address already in use 127.0.0.1:3080 而起不来（实测踩过）。
            bool closed = WaitPortClosed(15000);
            Log.Write(closed ? "端口已释放" : "警告：15 秒内端口仍未释放");
            lock (_lock) { _state = St.Stopped; _detail = ""; }
            _browserOpenedThisRun = false;
            _startedByUs = false;
            Cfg.ClearUrlCache();   // 旧 token 随服务一起失效，别把过期地址留给下次
            Refresh_();
            if (ask) Notify("已停止", n > 0 ? "已关闭 " + n + " 个 DSH Web 进程。" : "没有找到正在运行的进程。");
        }

        private static int KillDshHosts()
        {
            int killed = 0;
            List<int> pids = new List<int>();
            try
            {
                using (ManagementObjectSearcher s = new ManagementObjectSearcher(
                    "SELECT ProcessId,CommandLine FROM Win32_Process WHERE Name='node.exe'"))
                {
                    foreach (ManagementBaseObject o in s.Get())
                    {
                        string cl = o["CommandLine"] as string;
                        if (cl == null) continue;
                        if (cl.IndexOf(@"dsh\lib\bin.js", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        if (cl.IndexOf(" web", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        try { pids.Add(Convert.ToInt32(o["ProcessId"])); } catch { }
                    }
                }
            }
            catch (Exception ex) { Log.Write("枚举进程失败：" + ex.Message); }
            foreach (int pid in pids)
            {
                // 用 taskkill /T 连子进程一起收掉，避免遗留 powershell / subprocess 孤儿进程
                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo("taskkill.exe", "/F /T /PID " + pid.ToString());
                    psi.UseShellExecute = false;
                    psi.CreateNoWindow = true;
                    psi.WindowStyle = ProcessWindowStyle.Hidden;
                    using (Process tk = Process.Start(psi)) { tk.WaitForExit(5000); }
                    killed++;
                }
                catch (Exception ex) { Log.Write("结束进程 " + pid + " 失败：" + ex.Message); }
            }
            return killed;
        }

        // ---------- 动作 ----------
        /// <summary>把委托封送到 UI 线程执行。</summary>
        public void Post(MethodInvoker a)
        {
            try { _anchor.BeginInvoke(a); } catch { }
        }

        /// <summary>第二个实例被双击时调用：已运行就直接开界面，没运行就拉起来。</summary>
        public void OpenFromSecondInstance()
        {
            Log.Write("重复双击：唤起已有实例");
            _silent = false;
            if (!PortOpen()) StartHost();
            else { _browserOpenedThisRun = true; OpenUi(); }
        }

        private void OpenUi()
        {
            string url = Cfg.Url;
            // 同一次启动里「状态刷新」和「双击打开」可能前后脚各开一次，只留一个标签页
            if (url == _lastOpenUrl && (DateTime.Now - _lastOpenAt).TotalSeconds < 8)
            {
                Log.Write("界面已在 8 秒内打开过，忽略重复请求");
                return;
            }
            _lastOpenUrl = url;
            _lastOpenAt = DateTime.Now;

            // 走 explorer.exe 打开：即使启动器本身被以管理员身份运行，浏览器也会以普通权限起来，
            // 不会触发 Edge 的"现有实例正在以提升的权限运行"提示。
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("explorer.exe", "\"" + url + "\"");
                psi.UseShellExecute = false;
                Process.Start(psi);
                Log.Write("打开界面 " + url);
                return;
            }
            catch (Exception ex) { Log.Write("explorer 打开失败，改用 ShellExecute：" + ex.Message); }

            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                Log.Write("打开界面（ShellExecute）" + url);
            }
            catch (Exception ex) { Log.Write("打开浏览器失败：" + ex.Message); }
        }

        private void OpenLog()
        {
            try
            {
                if (File.Exists(Cfg.LogFile)) Process.Start("notepad.exe", "\"" + Cfg.LogFile + "\"");
                else MessageBox.Show("还没有日志：服务尚未启动过。\n\n日志位置：\n" + Cfg.LogFile,
                    "DSH 启动器", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex) { Log.Write("打开日志失败：" + ex.Message); }
        }

        private void Notify(string title, string body)
        {
            try { _ni.ShowBalloonTip(4000, title, body, ToolTipIcon.Info); } catch { }
        }

        private void Quit()
        {
            Log.Write("启动器退出（服务保持运行）");
            try { _timer.Stop(); } catch { }
            try { _ni.Visible = false; } catch { }
            ExitThread();
        }

        // ---------- 开机自启 ----------
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunName = "DshWebLauncher";
        private static bool IsAutostart()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey))
                {
                    if (k == null) return false;
                    return k.GetValue(RunName) != null;
                }
            }
            catch { return false; }
        }
        private void ToggleAutostart()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (k == null) { MessageBox.Show("无法写入注册表启动项。"); return; }
                    if (IsAutostart())
                    {
                        k.DeleteValue(RunName, false);
                        Log.Write("已关闭开机自启");
                        Notify("开机自启", "已关闭。");
                    }
                    else
                    {
                        string exe = Application.ExecutablePath;
                        k.SetValue(RunName, "\"" + exe + "\" --silent");
                        Log.Write("已开启开机自启：" + exe);
                        Notify("开机自启", "已开启，下次登录自动在后台运行。");
                    }
                }
            }
            catch (Exception ex) { Log.Write("开机自启设置失败：" + ex.Message); }
            _iAuto.Checked = IsAutostart();
            try { _menu.Menu.Invalidate(); } catch { }
        }

        // ---------- 工具 ----------
        public static bool PortOpen()
        {
            try
            {
                using (TcpClient c = new TcpClient())
                {
                    IAsyncResult ar = c.BeginConnect(IPAddress.Loopback, Cfg.Port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(700)) return false;
                    c.EndConnect(ar);
                    return c.Connected;
                }
            }
            catch { return false; }
        }

        /// <summary>等端口从“通”变成“不通”（停止服务后给系统一点时间释放端口）。</summary>
        public static bool WaitPortClosed(int ms)
        {
            DateTime end = DateTime.Now.AddMilliseconds(ms);
            while (DateTime.Now < end)
            {
                if (!PortOpen()) return true;
                Thread.Sleep(250);
            }
            return !PortOpen();
        }

        // 生成真正的多尺寸 ICO（DIB 格式），避免 Icon.FromHandle 在托盘里显示不出来。
        // 图标 = DeepSeek 鲸鱼（内嵌资源 DshLogo.png） + 右下角状态小圆点。
        private static Icon MakeIcon(Color c)
        {
            int[] sizes = new int[] { 16, 20, 24, 32, 48 };
            Bitmap logo = LoadLogo();
            List<byte[]> blobs = new List<byte[]>();
            for (int i = 0; i < sizes.Length; i++) blobs.Add(DibFor(c, sizes[i], logo));
            if (logo != null) logo.Dispose();

            MemoryStream ms = new MemoryStream();
            BinaryWriter w = new BinaryWriter(ms);
            w.Write((ushort)0);
            w.Write((ushort)1);
            w.Write((ushort)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                w.Write((byte)0);
                w.Write((byte)0);
                w.Write((ushort)1);
                w.Write((ushort)32);
                w.Write(blobs[i].Length);
                w.Write(offset);
                offset += blobs[i].Length;
            }
            for (int i = 0; i < blobs.Count; i++) w.Write(blobs[i]);
            w.Flush();
            ms.Position = 0;
            return new Icon(ms);
        }

        /// <summary>内嵌的 DeepSeek 标志（256px PNG，随 exe 一起编译进去）。</summary>
        private static Bitmap LoadLogo()
        {
            try
            {
                Assembly asm = Assembly.GetExecutingAssembly();
                using (Stream st = asm.GetManifestResourceStream("DshLogo.png"))
                {
                    if (st == null) return null;
                    using (Image img = Image.FromStream(st)) return new Bitmap(img);
                }
            }
            catch { return null; }
        }

        private static byte[] DibFor(Color c, int s, Bitmap logo)
        {
            using (Bitmap bmp = new Bitmap(s, s, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = TextRenderingHint.AntiAlias;
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.Clear(Color.Transparent);

                    // 标记铺满画布，右下角让出状态点的位置（小尺寸下鲸鱼才有存在感）
                    float badge = s * 0.34f;
                    float logoBox = s * 0.94f;
                    if (logo != null)
                        g.DrawImage(logo, new RectangleF(0, 0, logoBox, logoBox));
                    else
                    {
                        // 资源丢了也不至于没图标：退回蓝底白字
                        using (SolidBrush b = new SolidBrush(Color.FromArgb(77, 107, 254)))
                            g.FillEllipse(b, 0, 0, logoBox, logoBox);
                        using (Font f = new Font("Segoe UI", s * 0.5f, FontStyle.Bold, GraphicsUnit.Pixel))
                        using (SolidBrush tb = new SolidBrush(Color.White))
                        using (StringFormat sf = new StringFormat())
                        {
                            sf.Alignment = StringAlignment.Center;
                            sf.LineAlignment = StringAlignment.Center;
                            g.DrawString("D", f, tb, new RectangleF(0, 0, logoBox, logoBox), sf);
                        }
                    }

                    // 右下角状态点：先描一圈白边（在深色任务栏上也能分清），再填状态色
                    float ring = Math.Max(1f, s * 0.06f);
                    float bx = s - badge - s * 0.02f;
                    float by = s - badge - s * 0.02f;
                    using (SolidBrush wb = new SolidBrush(Color.White))
                        g.FillEllipse(wb, bx - ring, by - ring, badge + 2 * ring, badge + 2 * ring);
                    using (SolidBrush cb = new SolidBrush(c))
                        g.FillEllipse(cb, bx, by, badge, badge);
                }

                BitmapData bd = bmp.LockBits(new Rectangle(0, 0, s, s), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                byte[] pixels = new byte[s * s * 4];
                for (int y = 0; y < s; y++)
                {
                    IntPtr src = new IntPtr(bd.Scan0.ToInt64() + (long)(s - 1 - y) * bd.Stride);
                    Marshal.Copy(src, pixels, y * s * 4, s * 4);
                }
                bmp.UnlockBits(bd);

                int maskRow = ((s + 31) / 32) * 4;
                byte[] mask = new byte[maskRow * s];

                MemoryStream ms = new MemoryStream();
                BinaryWriter w = new BinaryWriter(ms);
                w.Write(40);
                w.Write(s);
                w.Write(s * 2);
                w.Write((ushort)1);
                w.Write((ushort)32);
                w.Write(0);
                w.Write(0);
                w.Write(0);
                w.Write(0);
                w.Write(0);
                w.Write(0);
                w.Write(pixels);
                w.Write(mask);
                w.Flush();
                return ms.ToArray();
            }
        }
    }

    internal static class Program
    {
        private static void Fatal(Exception ex)
        {
            try
            {
                string d = Path.Combine(Path.GetTempPath(), ".dsh-launcher");
                Directory.CreateDirectory(d);
                File.AppendAllText(Path.Combine(d, "fatal.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + ex.ToString() + Environment.NewLine);
            }
            catch { }
        }

        [STAThread]
        private static void Main(string[] args)
        {
            AppDomain.CurrentDomain.UnhandledException += delegate (object s, UnhandledExceptionEventArgs e)
            {
                Fatal(e.ExceptionObject as Exception ?? new Exception("unknown"));
            };
            try
            {
                RunMain(args);
            }
            catch (Exception ex)
            {
                Fatal(ex);
                MessageBox.Show("DSH 启动器启动失败：\n\n" + ex.Message + "\n\n详情见 " +
                    Path.Combine(Path.GetTempPath(), ".dsh-launcher", "fatal.log"),
                    "DSH 启动器", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private static void RunMain(string[] args)
        {
            Log.Write("=== 进程启动 pid=" + Process.GetCurrentProcess().Id + " ===");

            bool silent = false;
            for (int i = 0; i < args.Length; i++)
            {
                string s = args[i].ToLowerInvariant();
                if (s == "--silent" || s == "/silent") silent = true;
                else if (s.StartsWith("--port="))
                {
                    int p;
                    if (int.TryParse(s.Substring(7), out p) && p > 0 && p < 65536) Cfg.Port = p;
                }
                else if (s == "--port" && i + 1 < args.Length)
                {
                    int p;
                    if (int.TryParse(args[i + 1], out p) && p > 0 && p < 65536) { Cfg.Port = p; i++; }
                }
            }

            Cfg.EnsureIni();

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            bool createdEvent;
            EventWaitHandle openEvt = new EventWaitHandle(false, EventResetMode.AutoReset,
                @"Local\DshWebLauncher.Open", out createdEvent);

            bool createdMutex;
            Mutex mx = new Mutex(true, @"Local\DshWebLauncher.Single", out createdMutex);
            if (!createdMutex)
            {
                try { openEvt.Set(); } catch { }
                return;
            }

            Launcher app = new Launcher(silent);

            Thread t = new Thread(delegate ()
            {
                while (true)
                {
                    openEvt.WaitOne();
                    app.Post(new MethodInvoker(delegate { app.OpenFromSecondInstance(); }));
                }
            });
            t.IsBackground = true;
            t.Start();

            Application.Run(app);
            GC.KeepAlive(mx);
        }
    }
}
