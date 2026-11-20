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

        /// <summary>
        /// 「辅助功能」模块目录：丢进去的 .bat/.cmd/.exe/.ps1 会自动出现在托盘菜单里。
        /// 留空 = %LOCALAPPDATA%\DshLauncher\aux；要换位置就改 launcher.ini 的 aux_dir=。
        /// </summary>
        public static string AuxDir;

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

        /// <summary>
        /// 读 ini 的每一行。编码容错：优先按 UTF-8（严格）解，失败就当 ANSI/GBK 解，并去掉 BOM。
        /// 很多编辑器（Notepad2/老 VS、以及中文系统上的记事本另存）会把 ini 存成 GBK，
        /// 直接 File.ReadAllLines 会把中文路径读成一串替换字符，表现为「dsh= 明明填了却不生效」。
        /// </summary>
        private static string[] ReadIniLines()
        {
            byte[] bytes = File.ReadAllBytes(IniFile);
            string text;
            try { text = new System.Text.UTF8Encoding(false, true).GetString(bytes); }
            catch { text = System.Text.Encoding.Default.GetString(bytes); }
            if (text.Length > 0 && text[0] == '\uFEFF') text = text.Substring(1);
            return text.Replace("\r\n", "\n").Split('\n');
        }

        private static Dictionary<string, string> LoadIni()
        {
            Dictionary<string, string> d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(IniFile)) return d;
                foreach (string raw in ReadIniLines())
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
            catch (Exception ex)
            {
                // 配置读坏了会静默退回默认值，用户只会觉得「填了没用」，至少留一行现场
                Log.Write("读取 launcher.ini 失败（按默认值启动）：" + ex.Message);
            }
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
                    "# 「辅助功能」模块目录；留空 = %LOCALAPPDATA%\\DshLauncher\\aux\r\n" +
                    "# 把 .bat/.cmd/.exe/.ps1 丢进去，菜单里就多一条（文件名 = 菜单文字，子文件夹 = 分组）\r\n" +
                    "aux_dir=\r\n" +
                    "\r\n" +
                    "# 只有在自动探测失败时才需要填下面两行（node + dsh 的 bin.js）\r\n" +
                    "# node=C:\\Program Files\\nodejs\\node.exe\r\n" +
                    "# bin=%APPDATA%\\npm\\node_modules\\@deepseek-ai\\dsh\\lib\\bin.js\r\n";
                File.WriteAllText(IniFile, t, new System.Text.UTF8Encoding(false));
            }
            catch (Exception ex) { Log.Write("写 launcher.ini 模板失败：" + ex.Message); }
        }

        static Cfg()
        {
            int p;
            if (int.TryParse(Ini("port"), out p) && p > 0 && p < 65536) Port = p;
            string ad = Ini("aux_dir");
            AuxDir = string.IsNullOrEmpty(ad) ? Path.Combine(StateDir, "aux") : ad;
            string wd = Ini("workdir");
            // 没配就落到「文档\DSH_Workspace」，换台电脑也不用改代码
            WorkDir = string.IsNullOrEmpty(wd) ? DefaultWorkDir : wd;
            try { Directory.CreateDirectory(WorkDir); }
            catch (Exception ex) { Log.Write("创建工作目录失败（" + WorkDir + "）：" + ex.Message); }
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
                if (u != null) { Cfg._cachedUrl = u; SaveUrl(u); return u; }   // SaveUrl 内部已去重
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
        private static string _savedUrl;
        public static void SaveUrl(string url)
        {
            // 同一个地址不必每次访问 Url 都写一遍盘
            if (url == _savedUrl) return;
            try { File.WriteAllText(UrlFile, url, new System.Text.UTF8Encoding(false)); _savedUrl = url; } catch { }
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
            _savedUrl = null;
        }

        /// <summary>
        /// 读最后一条带 token 的地址；读不到返回 null。
        /// 必须用 FileShare.ReadWrite 打开：宿主进程一直占着这个日志文件写入，
        /// 默认的 File.ReadAllLines 会抛“文件正被另一进程使用”。
        /// </summary>
        private static readonly object _urlLock = new object();
        private static long _urlLen = -1;
        private static DateTime _urlStamp;
        private static string _urlFound;
        private static bool _urlScanned;

        public static string ReadTokenUrl()
        {
            try
            {
                if (!File.Exists(LogFile)) return null;
                FileInfo fi = new FileInfo(LogFile);
                lock (_urlLock)
                {
                    // 日志没变化就直接给上次的结果：这个属性每次打开界面都会读一次，
                    // 服务跑久了日志到 MB 级时，整份扫一遍要几十毫秒。
                    if (_urlScanned && fi.Length == _urlLen && fi.LastWriteTimeUtc == _urlStamp) return _urlFound;
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
                    _urlLen = fi.Length;
                    _urlStamp = fi.LastWriteTimeUtc;
                    _urlFound = found;
                    _urlScanned = true;
                    return found;
                }
            }
            catch { return null; }
        }

        /// <summary>
        /// 读 dsh-web.log 的最后一段，用于启动失败时把真正的报错带给用户。
        /// 必须用 FileShare.ReadWrite 打开：启动失败的瞬间宿主可能还在写日志。
        /// </summary>
        public static string ReadLogTail(int maxChars)
        {
            try
            {
                if (!File.Exists(LogFile) || maxChars <= 0) return null;
                using (FileStream fs = new FileStream(LogFile, FileMode.Open, FileAccess.Read,
                                                      FileShare.ReadWrite | FileShare.Delete))
                {
                    long len = fs.Length;
                    int take = (int)Math.Min(len, (long)maxChars);
                    if (take <= 0) return null;
                    fs.Seek(-take, SeekOrigin.End);
                    byte[] buf = new byte[take];
                    int n = fs.Read(buf, 0, take);
                    if (n <= 0) return null;
                    return System.Text.Encoding.UTF8.GetString(buf, 0, n).Trim();
                }
            }
            catch { return null; }
        }

        /// <summary>
        /// 等「带 token 的地址」落盘，并对它做一次 HTTP 探活；成功返回该地址，超时返回最后读到的地址（可能为 null）。
        /// 为什么不能只看端口：dsh web 先 bind 端口、再起 MCP 子进程和插件，最后才打印带 token 的地址；
        /// 这台机器上「端口就绪 → token 落盘」实测差 20 秒以上，固定等几秒必然读不到 token。
        /// </summary>
        public static string WaitReadyUrl(int ms)
        {
            DateTime end = DateTime.Now.AddMilliseconds(ms);
            string seen = null;
            int probes = 0;
            bool markedSeen = false;
            while (DateTime.Now < end)
            {
                if (seen == null) seen = ReadTokenUrl();
                if (seen != null)
                {
                    if (!markedSeen) { markedSeen = true; BootTrace.Mark("日志里已出现 token 地址"); }
                    probes++;
                    if (ProbeHttp(seen, 2500)) { BootTrace.Mark("token 地址探活通过"); return seen; }
                    // 地址已经在日志里了，探活失败多半是探测环境（代理/防火墙）问题，别为此耽误开页
                    if (probes >= 3) return seen;
                }
                Thread.Sleep(400);
            }
            return seen;
        }

        /// <summary>对本机地址做一次 GET 探活：拿到 HTTP 响应（非 5xx）就算服务已在应答。</summary>
        public static bool ProbeHttp(string url, int timeoutMs)
        {
            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "GET";
                req.Proxy = null;                  // 本机地址，别让系统代理插一脚
                req.Timeout = timeoutMs;
                req.ReadWriteTimeout = timeoutMs;
                req.AllowAutoRedirect = false;
                req.KeepAlive = false;
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                    return (int)resp.StatusCode < 500;
            }
            catch (WebException wex)
            {
                HttpWebResponse r = wex.Response as HttpWebResponse;
                if (r == null) return false;
                int code = (int)r.StatusCode;
                r.Close();
                return code < 500;
            }
            catch { return false; }
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

        // ---------- 把路径换成纯 ASCII 的 8.3 短名 ----------
        // 生成的 start-dsh-web.cmd 是给 cmd.exe 读的批处理。批处理的编码必须跟 cmd 当前的
        // 代码页一致：我们写的是系统 ANSI（中文机上是 GBK），但如果路径里带中文（比如
        // C:\Users\张三\...），任何编码切换——尤其是老版本里那行 chcp 65001——都会让
        // cmd 按错编码解析，cd /d 直接报"系统找不到指定的路径"。
        // 最稳的做法不是猜编码，而是让脚本里根本不出现非 ASCII 字符：全部换成 8.3 短名。
        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode,
            SetLastError = true)]
        private static extern uint GetShortPathName(string longName, System.Text.StringBuilder shortName, uint size);

        /// <summary>返回只含 ASCII 的短路径；已经全 ASCII、或系统没开 8.3 时原样返回。</summary>
        public static string Short(string p)
        {
            if (string.IsNullOrEmpty(p)) return p;
            bool ascii = true;
            for (int i = 0; i < p.Length; i++) if (p[i] > 127) { ascii = false; break; }
            if (ascii) return p;
            try
            {
                System.Text.StringBuilder sb = new System.Text.StringBuilder(1024);
                uint n = GetShortPathName(p, sb, (uint)sb.Capacity);
                if (n > 0 && n < (uint)sb.Capacity)
                {
                    string s = sb.ToString();
                    ascii = true;
                    for (int i = 0; i < s.Length; i++) if (s[i] > 127) { ascii = false; break; }
                    if (ascii) return s;
                }
            }
            catch { }
            return p;   // 短名不可用：保持原样，靠写文件时用 ANSI 编码兜底
        }

        /// <summary>把命令行里所有带引号的路径换成短名（dsh.cmd / node.exe / bin.js）。</summary>
        public static string ShortCmd(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return cmd;
            return System.Text.RegularExpressions.Regex.Replace(cmd, "\"([^\"]+)\"",
                delegate (System.Text.RegularExpressions.Match m) { return "\"" + Short(m.Groups[1].Value) + "\""; });
        }

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

    /// <summary>
    /// 启动阶段观测（只读、只写日志）：记录 node 进程何时出现、CPU 每 2 秒涨多少、端口与 token 何时就绪。
    /// 用途是分清「端口就绪前那几十秒」到底在算还是在等：CPU 涨=在算，几乎不涨=在等网络/IO。
    /// 任何异常都被吞掉，不影响启动；不想要这份日志时删掉本类及其调用点即可。
    /// </summary>
    internal static class BootTrace
    {
        private static readonly object _gate = new object();
        private static int _gen;
        private static DateTime _t0 = DateTime.Now;
        private static int _rootPid;
        private static int _nodePid;
        private static readonly HashSet<int> _seenPids = new HashSet<int>();

        public static double ElapsedSeconds
        {
            get { lock (_gate) { return (DateTime.Now - _t0).TotalSeconds; } }
        }

        /// <summary>每次拉起 dsh web 时调用：重置计时与已见进程，然后开一个后台线程采样。</summary>
        public static void Start(int cmdPid)
        {
            int gen;
            lock (_gate)
            {
                _gen++;
                gen = _gen;
                _t0 = DateTime.Now;
                _rootPid = cmdPid;
                _nodePid = 0;
                _seenPids.Clear();
                _seenPids.Add(cmdPid);
            }
            Log.Write("启动观测：开始（cmd pid=" + cmdPid + "）");
            ThreadPool.QueueUserWorkItem(delegate { Loop(gen); });
        }

        /// <summary>在启动流程的关键点打一条带「距开始多少秒」的日志。</summary>
        public static void Mark(string what)
        {
            Log.Write("启动观测 +" + ElapsedSeconds.ToString("0.0") + "s  " + what);
        }

        private static bool Alive(int gen)
        {
            lock (_gate) { return gen == _gen; }
        }

        private static void Loop(int gen)
        {
            TimeSpan last = TimeSpan.Zero;
            DateTime lastCpuAt = DateTime.Now;
            DateTime lastFindAt = DateTime.MinValue;
            int loops = 0;
            while (true)
            {
                if (!Alive(gen)) return;
                if (ElapsedSeconds > 300) { Mark("观测结束（满 5 分钟）"); lock (_gate) { if (gen == _gen) _gen++; } return; }
                try
                {
                    int node;
                    lock (_gate) { node = _nodePid; }
                    if (node == 0)
                    {
                        // 找不到 node 时也别 0.5 秒一次查 WMI：两秒一次足够，别把观测本身变成负担。
                        if ((DateTime.Now - lastFindAt).TotalSeconds >= 2) { lastFindAt = DateTime.Now; node = FindNode(); }
                        else node = 0;
                        if (node > 0)
                        {
                            lock (_gate) { _nodePid = node; }
                            Mark("发现 node 进程 pid=" + node);
                            lastCpuAt = DateTime.Now;
                        }
                    }
                    else if ((DateTime.Now - lastCpuAt).TotalSeconds >= 2)
                    {
                        TimeSpan cpu = ReadCpu(node);
                        if (cpu < TimeSpan.Zero)
                        {
                            lock (_gate) { if (_nodePid == node) _nodePid = 0; }
                            Mark("node 进程不见了（pid=" + node + "），继续观察");
                        }
                        else
                        {
                            Mark("node pid=" + node + " 近2秒CPU=" + (cpu - last).TotalSeconds.ToString("0.00") + "s 累计=" + cpu.TotalSeconds.ToString("0.0") + "s");
                            last = cpu;
                        }
                        lastCpuAt = DateTime.Now;
                    }
                    // 每 4 秒扫一次子孙进程：MCP 服务器（python/node）什么时候出现一目了然。
                    if (++loops % 8 == 0) ScanChildren();
                }
                catch { }
                Thread.Sleep(500);
            }
        }

        private static TimeSpan ReadCpu(int pid)
        {
            try
            {
                using (Process p = Process.GetProcessById(pid))
                {
                    p.Refresh();
                    return p.TotalProcessorTime;
                }
            }
            catch { return TimeSpan.MinValue; }
        }

        private static int FindNode()
        {
            int node = 0;
            try
            {
                int root;
                lock (_gate) { root = _rootPid; }
                using (ManagementObjectSearcher s = new ManagementObjectSearcher(
                    "SELECT ProcessId,ParentProcessId,CommandLine FROM Win32_Process WHERE ParentProcessId=" + root))
                using (ManagementObjectCollection list = s.Get())
                {
                    foreach (ManagementObject o in list)
                    {
                        int pid = ToInt(o["ProcessId"]);
                        string cmdline = Convert.ToString(o["CommandLine"]);
                        if (pid <= 0) continue;
                        NoteChild(pid, cmdline);
                        if (node == 0 && cmdline.IndexOf("node", StringComparison.OrdinalIgnoreCase) >= 0) node = pid;
                    }
                }
            }
            catch { }
            return node;
        }

        private static void ScanChildren()
        {
            try
            {
                int root, node;
                lock (_gate) { root = _rootPid; node = _nodePid; }
                Scan(root);
                if (node > 0) Scan(node);
            }
            catch { }
        }

        private static void Scan(int parentPid)
        {
            try
            {
                using (ManagementObjectSearcher s = new ManagementObjectSearcher(
                    "SELECT ProcessId,ParentProcessId,CommandLine FROM Win32_Process WHERE ParentProcessId=" + parentPid))
                using (ManagementObjectCollection list = s.Get())
                    foreach (ManagementObject o in list) NoteChild(ToInt(o["ProcessId"]), Convert.ToString(o["CommandLine"]));
            }
            catch { }
        }

        private static int ToInt(object v)
        {
            try { return v == null ? 0 : Convert.ToInt32(v); } catch { return 0; }
        }

        private static void NoteChild(int pid, string cmdline)
        {
            bool fresh = false;
            lock (_gate)
            {
                if (pid > 0 && !_seenPids.Contains(pid)) { _seenPids.Add(pid); fresh = true; }
            }
            if (!fresh) return;
            string head = (cmdline == null ? "" : cmdline.Replace("\r", " ").Replace("\n", " ")).Trim();
            if (head.Length > 120) head = head.Substring(0, 120);
            Mark("子进程出现 pid=" + pid + "  " + head);
        }
    }

    internal static class Log
    {
        private const long MaxBytes = 1024 * 1024;   // 超过 1 MB 就换一份，避免日志无限增长
        private static readonly object _gate = new object();

        public static void Write(string msg)
        {
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg + Environment.NewLine;
            // UI 线程和线程池都会写日志：串行化 + FileShare.ReadWrite，避免并发 Append 抛异常丢行。
            lock (_gate)
            {
                // 首选状态目录（和 dsh-web.log、url.txt 放一起，只在这一处）；
                // 万一那里不可写，再退回到 exe 同目录的 logs。
                string[] files = new string[] { Cfg.LauncherLog, ExeDirLog };
                foreach (string f in files)
                {
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(f));
                        try
                        {
                            if (File.Exists(f) && new FileInfo(f).Length > MaxBytes)
                            {
                                string bak = f + ".1";
                                if (File.Exists(bak)) File.Delete(bak);
                                File.Move(f, bak);      // 留一份上一轮，别把出事的现场直接删掉
                            }
                        }
                        catch { }
                        using (FileStream fs = new FileStream(f, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                        using (StreamWriter sw = new StreamWriter(fs, new System.Text.UTF8Encoding(false)))
                            sw.Write(line);
                        return;
                    }
                    catch { }
                }
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

    internal enum St { Unknown, Stopped, Starting, Running, Stopping, Error }

    internal sealed class Launcher : ApplicationContext
    {
        private readonly NotifyIcon _ni;
        private readonly MenuSpec _menu;
        private readonly ToolStripMenuItem _iOpen, _iRestart, _iStop, _iAuto, _iLog, _iQuit, _iQuitAll;
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
        private bool _tokenWarned;
        private float _menuScale = 1f;   // 菜单已套用的 DPI 比例（Create 里先按 1.0 建）
        private DateTime? _runningSince;        // 服务这次真正开始运行的时间（本地时间）
        private bool _runningSinceTried;        // 已经找过一次启动时间，找不到就不再反复查 WMI
        private int _runToken;                  // 每次停/起自增，防止后台查到的旧启动时间落到新进程上
        private int _startToken;                // 每次启动/停止自增，旧 watchdog 不得改写新状态
        private bool _restartAfterStop;        // 停止完成后是否自动接着启动（菜单“重启服务”）
        private bool _quitting;                 // 「退出并停止服务」已确认、正在后台收尾
        private string _lastStatusText = "";    // 状态行变长后要重排菜单宽度，先记住上一次的文字

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
            DshMenu.Verbose = Cfg.Ini("verbose") == "1";   // 菜单开合之类的调试日志默认关，ini 里写 verbose=1 打开
            // 只要用户动了托盘菜单，就说明这次是「有人在用」，静默标记一律作废——
            // 否则开机自启（--silent）起来的实例被点「重启服务」后不会打开页面。
            MenuSpec spec = DshMenu.Create(
                delegate { _silent = false; OpenUi(); },
                delegate { _silent = false; RestartHost(); },
                delegate { _silent = false; StopHost(true); },
                delegate { ToggleAutostart(); },
                delegate { OpenLog(); },
                delegate { Quit(); },
                delegate { QuitAll(); });
            // 「辅助功能」是模块化的：目录里的脚本自动变成菜单条目，这里只提供「怎么跑」和「目录在哪」
            EnsureAuxDir();
            EnsureIconDir();
            spec.IconDir = Path.Combine(Application.StartupPath, "icons");
            spec.AuxDir = Cfg.AuxDir;
            spec.AuxRun = delegate (string path) { RunAuxScript(path); };
            spec.AuxOpenDir = delegate { OpenAuxDir(); };
            spec.RefreshAux();
            spec.ApplyIcons();
            _menu = spec;
            _iOpen = spec.Open;
            _iRestart = spec.Restart;
            _iStop = spec.Stop;
            _iAuto = spec.Auto;
            _iLog = spec.Log;
            _iQuit = spec.Quit;
            _iQuitAll = spec.QuitAll;
            _ni.ContextMenuStrip = spec.Menu;
            // 见 PreScaleMenu：鼠标一进入图标就把菜单尺寸算到位，右键弹出时才不会长过光标
            _ni.MouseMove += delegate { PreScaleMenu(); };
            PreScaleMenu();   // 启动时先排一次，覆盖“鼠标没再动就直接右键”的情况
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
            StartPortWatch();   // 端口探测放到后台，UI 线程只读结果

            Refresh_();
            if (!_silent) Log.Write("启动器已运行（端口 " + Cfg.Port + "）");
            else Log.Write("启动器已运行（登录自启，静默模式）");

            // 已停止则自动拉起
            if (!PortOpen()) StartHost();
        }

        /// <summary>
        /// 鼠标进入托盘图标时，先把菜单按当前屏的 DPI 排好。
        /// 原因：WinForms 的 NotifyIcon 用 ContextMenuStrip.ShowInTaskbar(光标) 弹菜单，
        /// 它把光标当菜单右下角锚定；若菜单此刻还是 100% 尺寸、到 Opening 里才放大，
        /// 定位已按小尺寸算完，放大后只会被夹进屏幕（向下长），刚好盖住光标。
        /// 预缩放后右键时尺寸已是最终值，锚点一次算对。
        /// </summary>
        private void PreScaleMenu()
        {
            try
            {
                float s = Dpi.CursorScale();
                if (Math.Abs(s - _menuScale) < 0.001f) return;
                _menu.ApplyScale(s);
                _menuScale = s;
            }
            catch { }
        }

        // ---------- 状态刷新（UI 线程） ----------
        private void Refresh_()
        {
            // 停止中不再探测端口：停止期间连接可能挂住、单次最多等 700ms，一秒一
            // tick 正是「停止中菜单一卡一卡」的来源；停止态的判定也不依赖探测结果。
            St prior;
            lock (_lock) prior = _state;
            // 端口探测只读后台线程的缓存：本机"没人监听"的端口不会立刻回绝，
            // 而是耗满超时（实测 700ms）。放在这个一秒一次的回调里，UI 线程
            // 每秒被吃掉 700ms，托盘菜单的悬浮高亮就会"不跟手"。
            bool up = prior != St.Stopping && PortUpCached();
            St s;
            lock (_lock)
            {
                if (_state == St.Stopping) s = St.Stopping;
                else if (_state == St.Starting && !up) s = St.Starting;
                else if (up) s = St.Running;
                else if (_state == St.Error) s = St.Error;
                else s = St.Stopped;
                _state = s;
            }
            // 服务一旦停下就把计时清掉；下次再起来重新找启动时间。
            // Starting 不能清：StartHost 刚把这次的启动时刻放进 _runningSince。
            if (s == St.Running) EnsureRunningSince();
            else if (s == St.Stopped || s == St.Error)
            {
                if (_runningSince.HasValue || _runningSinceTried) _runToken++;
                _runningSince = null;
                _runningSinceTried = false;
            }

            Color dot;
            string txt;
            Icon want;
            switch (s)
            {
                case St.Running: want = _iGreen; txt = "运行中"; dot = Ios.Green; break;
                case St.Starting: want = _iOrange; txt = "启动中…"; dot = Ios.Orange; break;
                case St.Stopping: want = _iGray; txt = "停止中…"; dot = Ios.Gray; break;
                case St.Error: want = _iRed; txt = "启动失败"; dot = Ios.Red; break;
                default: want = _iGray; txt = "未运行"; dot = Ios.Gray; break;
            }
            // 图标没变就别重设：每次赋值都会触发一次 Shell_NotifyIcon(NIM_MODIFY)
            if (_ni.Icon != want) _ni.Icon = want;
            string tip = "DSH Web · " + txt;
            if (s == St.Running) tip += " · 端口 " + Cfg.Port;
            // 状态行主文字照常截短；「已运行 X」作为第二行交给菜单用小字号画，
            // 不再把菜单撑宽（否则下面的选项区会显得空）。
            string detail = _detail ?? "";
            if (detail.Length > 24) detail = detail.Substring(0, 24) + "…";
            string status = "状态：" + txt;
            string statusSubtext = null;
            if (s == St.Running && _runningSince.HasValue)
                statusSubtext = "已运行 " + ElapsedText(DateTime.Now - _runningSince.Value);
            if (detail.Length > 0 && s == St.Error) status += "（" + detail + "）";
            _menu.SetStatus(status, statusSubtext, dot);
            string fullStatus = status + (statusSubtext == null ? "" : " · " + statusSubtext);
            // 主文字变化时仍要预排宽度（尤其错误详情）；第二行本身不参与宽度。
            if (fullStatus != _lastStatusText)
            {
                _lastStatusText = fullStatus;
                try { _menu.ApplyScale(_menuScale); } catch { }
            }
            _iOpen.Enabled = s == St.Running;
            _iStop.Enabled = s == St.Running || s == St.Starting;
            _iQuitAll.Enabled = _iStop.Enabled;   // 服务没在跑时这条等同「退出启动器」，禁用免得误导
            _iRestart.Enabled = s != St.Stopping;
            try { if (_ni.Text != tip) _ni.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip; } catch { }

            // 只为自己刚拉起来的服务自动开页：服务若是被外部（终端/别的脚本）拉起的，
            // 这里就静静等着，别擅自弹标签页——之前"每次改代码多一个页"就是这么来的。
            if (s == St.Running && !_browserOpenedThisRun && _startedByUs)
            {
                _browserOpenedThisRun = true;
                if (!_silent)
                {
                    // 等 ready 再开页：端口通 ≠ 能打开（服务要先起 MCP 子进程和插件，最后才打印 token 地址），
                    // 因此在线程池里等到「token 地址出现 + HTTP 探活通过」再封送回 UI 线程开页，最多 90 秒；
                    // 真等不到才退回裸地址并提示（见 OpenUi）。
                    ThreadPool.QueueUserWorkItem(delegate
                    {
                        string readyUrl = Cfg.WaitReadyUrl(90000);
                        if (readyUrl == null) Log.Write("等待 token 地址超时（90 秒），将打开裸地址");
                        Post(delegate
                        {
                            if (_state != St.Running) return;   // 等待期间被停掉/重启了，别开旧地址
                            OpenUi(readyUrl);
                        });
                    });
                }
            }
        }

        /// <summary>
        /// 状态第一次变成「运行中」时找一次服务启动时间。
        /// 优先读 host.pid 里启动器自己记的 started（启动器重启、服务留着时也能对上）；
        /// 没有记录（服务是终端/别的脚本拉起的）再按进程创建时间兜底。
        /// 找不到就不显示时长——宁可不写，也不拿“启动器发现它的时间”冒充运行时长。
        /// </summary>
        private void EnsureRunningSince()
        {
            if (_runningSince.HasValue || _runningSinceTried) return;
            _runningSinceTried = true;
            DateTime? t = ReadPidStart();
            if (t.HasValue) { SetRunningSince(t.Value); return; }

            // 没有 host.pid 时才走 WMI。实测这台机器上冷启动的 Win32_Process 查询要 ~10 秒，
            // 直接放在 Refresh_ 里会把启动器卡住十秒，所以丢到线程池，查到再封送回来。
            int token = _runToken;
            ThreadPool.QueueUserWorkItem(delegate
            {
                DateTime? found = FindDshStartTime();
                if (!found.HasValue)
                {
                    Log.Write("未能确定 dsh web 的启动时间，状态行暂不显示运行时长");
                    return;
                }
                Post(delegate
                {
                    if (token != _runToken || _state != St.Running || _runningSince.HasValue) return;
                    SetRunningSince(found.Value);
                    Refresh_();   // 立刻把时长补进状态行并重排宽度
                });
            });
        }

        private void SetRunningSince(DateTime t)
        {
            DateTime now = DateTime.Now;
            if (t > now) t = now;   // 系统时间被往回拨时别显示负数
            _runningSince = t;
        }

        /// <summary>读 host.pid 的 started=，并确认里面记的 cmd 进程还在（避免 pid 复用读到过期文件）。</summary>
        private static DateTime? ReadPidStart()
        {
            try
            {
                if (!File.Exists(Cfg.PidFile)) return null;
                int pid = 0, port = 0;
                string started = null;
                foreach (string raw in File.ReadAllLines(Cfg.PidFile))
                {
                    string ln = raw.Trim();
                    if (ln.StartsWith("cmd=", StringComparison.Ordinal)) int.TryParse(ln.Substring(4).Trim(), out pid);
                    else if (ln.StartsWith("port=", StringComparison.Ordinal)) int.TryParse(ln.Substring(5).Trim(), out port);
                    else if (ln.StartsWith("started=", StringComparison.Ordinal)) started = ln.Substring(8).Trim();
                }
                if (pid <= 0 || started == null) return null;
                if (port != 0 && port != Cfg.Port) return null;
                // 只做快速的存活检查；别用 CmdLineOf 查 WMI，冷启动要 ~10 秒，会把托盘卡住。
                // host.pid 是我们自己写的，端口又对得上，pid 被复用的概率已经很低。
                using (Process p = Process.GetProcessById(pid))
                {
                    if (p.HasExited) return null;
                    if (p.ProcessName.IndexOf("cmd", StringComparison.OrdinalIgnoreCase) < 0) return null;
                }
                DateTime t;
                if (!DateTime.TryParseExact(started, "s", System.Globalization.CultureInfo.InvariantCulture,
                                            System.Globalization.DateTimeStyles.None, out t)) return null;
                if (t > DateTime.Now.AddMinutes(1)) return null;
                return t;
            }
            catch { return null; }
        }

        /// <summary>没有 host.pid 时，从正在运行的 dsh web node 进程创建时间兜底。</summary>
        private static DateTime? FindDshStartTime()
        {
            try
            {
                DateTime? latest = null;
                using (ManagementObjectSearcher s = new ManagementObjectSearcher(
                    "SELECT ProcessId,CreationDate,CommandLine FROM Win32_Process WHERE Name='node.exe'"))
                {
                    foreach (ManagementBaseObject o in s.Get())
                    {
                        string cl = o["CommandLine"] as string;
                        if (cl == null) continue;
                        if (cl.IndexOf(@"dsh\lib\bin.js", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        if (cl.IndexOf(" web", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        // 只找当前端口的那一个，否则多端口并存时会显示别的实例的启动时间。
                        if (!CommandLineHasPort(cl, Cfg.Port)) continue;
                        string cd = o["CreationDate"] as string;
                        if (cd == null) continue;
                        try
                        {
                            DateTime t = ManagementDateTimeConverter.ToDateTime(cd);
                            if (!latest.HasValue || t > latest.Value) latest = t;
                        }
                        catch { }
                    }
                }
                return latest;
            }
            catch { return null; }
        }

        /// <summary>把运行时长写成菜单里好读的短句（菜单每秒刷新，只到分钟，不闪秒）。</summary>
        private static string ElapsedText(TimeSpan t)
        {
            if (t < TimeSpan.Zero) t = TimeSpan.Zero;
            if (t.TotalMinutes < 1) return "不到 1 分钟";
            if (t.TotalHours < 1) return ((int)t.TotalMinutes).ToString() + " 分钟";
            if (t.TotalDays < 1)
            {
                string h = ((int)t.TotalHours).ToString() + " 小时";
                return t.Minutes > 0 ? h + " " + t.Minutes.ToString() + " 分" : h;
            }
            string d = ((int)t.TotalDays).ToString() + " 天";
            return t.Hours > 0 ? d + " " + t.Hours.ToString() + " 小时" : d;
        }

        // ---------- 启动主机 ----------
        private void StartHost()
        {
            // 停止流程可能还在等端口释放；此时用户又点启动，不能和旧 token 抢状态。
            lock (_lock)
            {
                if (_state == St.Stopping)
                {
                    Log.Write("正在停止服务，忽略启动请求");
                    return;
                }
            }
            // 端口已经通了就别再拉一个：两个 node 抢同一个端口，第二个会 EADDRINUSE 直接死，
            // 日志里留下一串报错，还会让"重启服务"看起来失败。
            if (PortOpen())
            {
                lock (_lock) { _state = St.Running; }
                Refresh_();
                return;
            }
            int token;
            lock (_lock)
            {
                // 已经在启动中（托盘双击、重复实例、重启都可能同时触发），别再拉第二个进程。
                // 这里不做 PortOpen() 探测：连没人监听的端口要耗满超时（实测 700ms），
                // 锁也跟着持 700ms，UI 线程和 watchdog/停止回调全要排队等这把锁；
                // 端口是否已通，入口的 PortOpen()（锁外）和 Refresh_ 的缓存已经管了。
                if (_state == St.Starting)
                {
                    Log.Write("已在启动中，忽略重复的启动请求");
                    return;
                }
                token = ++_startToken;
                _state = St.Starting;
                _detail = "";
            }
            _browserOpenedThisRun = false;
            _startedByUs = false;
            _tokenWarned = false;               // 令牌警告按“每一轮启动”算：新一轮 90 秒等不到时还能再提示一次
            Cfg.ClearUrlCache();

            // 两条路：优先直接调 dsh 命令（各种安装方式都通用），退而用 node + bin.js。
            string dshCmd = Cfg.DshCmd;
            string node = null, bin = null;
            string runCmd;
            if (dshCmd != null)
                runCmd = "\"" + dshCmd + "\" web --port " + Cfg.Port + " --no-open";
            else
            {
                // 只有找不到 dsh 命令时才去扫 node/bin.js：Cfg.NodeExe/DshBin 会枚举 PATH
                // 和 pnpm 目录，没必要在每条正常启动路径上都付这个开销。
                node = Cfg.NodeExe;
                bin = Cfg.DshBin;
                if (node != null && bin != null)
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
            }

            try { if (File.Exists(Cfg.LogFile)) File.Delete(Cfg.LogFile); } catch { }
            // 工作目录先建出来，8.3 短名转换和 cmd 的 cd 都要它真实存在
            try { if (!string.IsNullOrEmpty(Cfg.WorkDir) && !Directory.Exists(Cfg.WorkDir)) Directory.CreateDirectory(Cfg.WorkDir); } catch { }
            // 脚本里一律用短名，保证全 ASCII；不写 chcp，避免批处理编码与代码页打架
            string wd = Cfg.Short(Cfg.WorkDir);
            string logTo = Cfg.Short(Cfg.LogFile);
            string home = Cfg.Short(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh"));
            string script =
                "@echo off\r\n" +
                "cd /d \"" + wd + "\"\r\n" +
                "if not defined DSH_HOME set \"DSH_HOME=" + home + "\"\r\n" +
                Cfg.ShortCmd(runCmd) + " > \"" + logTo + "\" 2>&1\r\n";
            try { File.WriteAllText(Cfg.CmdFile, script, System.Text.Encoding.Default); }
            catch (Exception ex)
            {
                lock (_lock) { _state = St.Error; _detail = "写启动脚本失败"; }
                Log.Write("写 cmd 失败：" + ex.Message);
                Notify("启动失败", "无法写入启动脚本：\n" + Cfg.CmdFile + "\n\n" + ex.Message);
                return;
            }

            Process proc = null;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = Cfg.CmdFile;
                psi.WorkingDirectory = Cfg.WorkDir;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                DateTime startedAt = DateTime.Now;
                proc = Process.Start(psi);
                if (proc == null) throw new Exception("Process.Start 返回空");
                _startedByUs = true;
                _runToken++;
                _runningSince = startedAt;
                _runningSinceTried = true;
                Log.Write("已启动 dsh web（cmd pid=" + proc.Id + "），等待端口 " + Cfg.Port);
                BootTrace.Start(proc.Id);
                try { File.WriteAllText(Cfg.PidFile, "cmd=" + proc.Id + "\r\nport=" + Cfg.Port + "\r\nstarted=" + startedAt.ToString("s")); } catch { }
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
                    // 停止/重启已经发生：旧 watchdog 不能再把新状态改成 Running/Error。
                    lock (_lock) { if (token != _startToken || _state != St.Starting) return; }
                    if (PortOpen())
                    {
                        lock (_lock)
                        {
                            if (token != _startToken || _state != St.Starting) return;
                            _state = St.Running;
                        }
                        Log.Write("端口已就绪");
                        BootTrace.Mark("端口已就绪，服务开始应答");
                        Post(delegate { Refresh_(); });
                        return;
                    }
                    // dsh.cmd 是同步等待 node 的：cmd 先退出通常就是 dsh web 起不来。
                    // 不再干等 120 秒；立刻置 Error 并把 dsh-web.log 尾巴带给用户。
                    bool exited = false;
                    try { exited = proc.HasExited; } catch { }
                    if (exited)
                    {
                        int pid = 0;
                        try { pid = proc.Id; } catch { }
                        lock (_lock)
                        {
                            if (token != _startToken || _state != St.Starting) return;
                            _state = St.Error;
                            _detail = "启动进程已退出";
                        }
                        Log.Write("启动进程已退出（cmd pid=" + pid + "），详见 " + Cfg.LogFile);
                        string tail = Cfg.ReadLogTail(500);
                        Post(delegate
                        {
                            Refresh_();
                            Notify("启动失败", string.IsNullOrEmpty(tail)
                                ? "dsh web 启动进程已退出，日志是空的。\n\n日志：" + Cfg.LogFile
                                : "dsh web 启动进程已退出。日志最后几行：\n\n" + tail + "\n\n完整日志：" + Cfg.LogFile);
                        });
                        return;
                    }
                    Thread.Sleep(700);
                }
                lock (_lock)
                {
                    if (token != _startToken || _state != St.Starting) return;
                    _state = St.Error;
                    _detail = "120 秒未就绪";
                }
                Log.Write("超时：120 秒内端口 " + Cfg.Port + " 未监听，详见 " + Cfg.LogFile);
                // 这是线程池线程，封送回 UI 线程再弹气泡
                Post(delegate { Notify("启动超时", "dsh web 没能在 120 秒内开始监听，点托盘图标看日志。"); });
            });
        }

        private void RestartHost()
        {
            Log.Write("请求重启");
            StopHost(false, true);   // 停止完成后自动 StartHost，避免 UI 线程等端口
        }

        private void StopHost(bool ask)
        {
            StopHost(ask, false);
        }

        private void StopHost(bool ask, bool restartAfter)
        {
            if (ask)
            {
                DialogResult r = MessageBox.Show("停止 DSH Web 服务？\n所有正在对话的页面会断开。",
                    "DSH 启动器", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (r != DialogResult.Yes) return;
            }

            int token;
            lock (_lock)
            {
                if (_state == St.Stopping)
                {
                    // 已经在停：只合并“停完是否重启”，不重复起后台线程。
                    _restartAfterStop |= restartAfter;
                    return;
                }
                token = ++_startToken;
                _restartAfterStop = restartAfter;
                _state = St.Stopping;
                _detail = "";
            }
            try { _menu.SetStatus("状态：停止中…", Ios.Gray); } catch { }
            Refresh_();   // 按钮/状态立即进入停止态

            ThreadPool.QueueUserWorkItem(delegate
            {
                int n = 0;
                bool closed = false;
                string err = null;
                try
                {
                    n = KillDshHosts();
                    try { if (File.Exists(Cfg.PidFile)) File.Delete(Cfg.PidFile); } catch { }
                    // node 退出到端口真正关闭之间有一段间隙。不等就立刻重启，新进程会因为
                    // EADDRINUSE: address already in use 127.0.0.1:3080 而起不来（实测踩过）。
                    closed = WaitPortClosed(15000);
                }
                catch (Exception ex) { err = ex.Message; }

                Post(delegate
                {
                    lock (_lock)
                    {
                        // 停止期间又发生了新的启动/退出：这次停止的结果已经过期。
                        if (token != _startToken) return;
                        _state = St.Stopped;
                        _detail = "";
                    }
                    _browserOpenedThisRun = false;
                    _startedByUs = false;
                    _runToken++;
                    _runningSince = null;
                    _runningSinceTried = false;
                    Cfg.ClearUrlCache();   // 旧 token 随服务一起失效，别把过期地址留给下次
                    if (err != null) Log.Write("停止过程出错：" + err);
                    Log.Write("已停止 " + n + " 个 dsh web 进程");
                    Log.Write(closed ? "端口已释放" : "警告：15 秒内端口仍未释放");
                    Refresh_();

                    bool restart;
                    lock (_lock)
                    {
                        restart = _restartAfterStop;
                        _restartAfterStop = false;
                    }
                    if (restart) StartHost();
                    else if (ask) Notify("已停止", n > 0 ? "已关闭 " + n + " 个 DSH Web 进程。" : "没有找到正在运行的进程。");
                });
            });
        }

        /// <summary>取某个进程的命令行（拿不到就返回 null）。</summary>
        private static string CmdLineOf(int pid)
        {
            try
            {
                using (ManagementObjectSearcher s = new ManagementObjectSearcher(
                    "SELECT CommandLine FROM Win32_Process WHERE ProcessId=" + pid.ToString()))
                {
                    foreach (ManagementBaseObject o in s.Get())
                    {
                        string cl = o["CommandLine"] as string;
                        if (cl != null) return cl;
                    }
                }
            }
            catch { }
            return null;
        }

        /// <summary>命令行里的 --port 是否指向当前服务端口；dsh web 不写 --port 时默认 3080。</summary>
        private static bool CommandLineHasPort(string cl, int port)
        {
            if (string.IsNullOrEmpty(cl)) return false;
            System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(
                cl, @"(?:^|\s)--port(?:=|\s+)(\d+)(?=\s|$)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (m.Success)
            {
                int p;
                return int.TryParse(m.Groups[1].Value, out p) && p == port;
            }
            return port == 3080;   // 没写 --port，dsh web 用默认端口
        }

        private static int KillDshHosts()
        {
            int killed = 0;
            List<int> pids = new List<int>();

            // ① 先收「我们自己拉起的那棵树」：cmd 包装器 → dsh(.cmd) → node → 子进程。
            //    这一条对便携版 dsh（不是 npm 全局的 bin.js）同样有效，靠 host.pid 里记的 cmd pid。
            try
            {
                if (File.Exists(Cfg.PidFile))
                {
                    int recordedPort = 0;
                    int cmdPid = 0;
                    foreach (string raw in File.ReadAllLines(Cfg.PidFile))
                    {
                        string ln = raw.Trim();
                        if (ln.StartsWith("cmd=", StringComparison.Ordinal))
                            int.TryParse(ln.Substring(4).Trim(), out cmdPid);
                        else if (ln.StartsWith("port=", StringComparison.Ordinal))
                            int.TryParse(ln.Substring(5).Trim(), out recordedPort);
                    }
                    // 端口对不上说明 host.pid 是别的端口留下的，不能拿它去杀当前端口的服务。
                    if (cmdPid > 0 && (recordedPort == 0 || recordedPort == Cfg.Port))
                    {
                        string cl = CmdLineOf(cmdPid);
                        // 只认我们自己写的那条批处理，避免 pid 复用误杀别人的进程
                        if (cl != null &&
                            (cl.IndexOf("start-dsh-web", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             cl.IndexOf("dsh", StringComparison.OrdinalIgnoreCase) >= 0))
                        {
                            pids.Add(cmdPid);
                            Log.Write("按 host.pid 收进程树：" + cmdPid + " → " + (cl.Length > 90 ? cl.Substring(0, 90) + "…" : cl));
                        }
                    }
                    else if (recordedPort != 0 && recordedPort != Cfg.Port)
                        Log.Write("host.pid 端口 " + recordedPort + " 与当前端口 " + Cfg.Port + " 不符，忽略");
                }
            }
            catch (Exception ex) { Log.Write("读 host.pid 失败：" + ex.Message); }

            // ② 再按命令行启发式兜底（别的实例起的、或旧版本留下的）。必须同时匹配端口，
            //    否则会把用户在 4000 端口另开的 dsh web 一起杀掉。
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
                        if (!CommandLineHasPort(cl, Cfg.Port)) continue;
                        try
                        {
                            int p1 = Convert.ToInt32(o["ProcessId"]);
                            if (!pids.Contains(p1)) pids.Add(p1);
                        }
                        catch { }
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
                    using (Process tk = Process.Start(psi))
                    {
                        if (tk == null) { Log.Write("无法启动 taskkill：pid=" + pid); continue; }
                        if (!tk.WaitForExit(5000))
                        {
                            try { tk.Kill(); } catch { }
                            Log.Write("结束进程 " + pid + " 超时");
                            continue;
                        }
                        if (tk.ExitCode == 0) killed++;
                        else Log.Write("taskkill " + pid + " 返回 " + tk.ExitCode + "（可能已提前退出）");
                    }
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

        private void OpenUi(string url = null)
        {
            BootTrace.Mark("打开浏览器");
            if (url == null) url = Cfg.Url;
            if (url.IndexOf("token=", StringComparison.Ordinal) < 0)
            {
                // 读不到带 token 的地址是 dsh 日志格式变了/日志被清了的信号，
                // 光打开裸地址只会看到 authentication required，先说清楚
                Log.Write("警告：没在 " + Cfg.LogFile + " 里找到带 token 的地址，将打开裸地址");
                if (!_tokenWarned)
                {
                    _tokenWarned = true;
                    Notify("没找到登录令牌", "没能从日志里取到带 token 的地址，页面可能会提示 authentication required。\r\n右键 →「打开日志」可复制完整地址。");
                }
            }
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

        /// <summary>
        /// 跑「辅助功能」里的一个模块脚本：bat/cmd 走 cmd /c，ps1 走 powershell -File，exe 直接起。
        /// 一律隐藏窗口、不提权，工作目录设成脚本所在目录（脚本里常用 %~dp0 相对路径）。
        /// 路径校验沿用现有约定：文件不存在只写日志 + 弹气泡提示，不抛异常、不影响服务状态。
        /// </summary>
        private void RunAuxScript(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    Log.Write("辅助功能：脚本不存在 " + (path == null ? "(空)" : path));
                    Notify("找不到脚本", "路径：\r\n" + (string.IsNullOrEmpty(path) ? "(空)" : path));
                    return;
                }

                string ext = Path.GetExtension(path).ToLowerInvariant();
                ProcessStartInfo psi;
                if (ext == ".ps1")
                    psi = new ProcessStartInfo("powershell.exe",
                        "-NoProfile -ExecutionPolicy Bypass -File \"" + path + "\"");
                else if (ext == ".bat" || ext == ".cmd")
                    psi = new ProcessStartInfo("cmd.exe", "/c \"" + path + "\"");
                else
                    psi = new ProcessStartInfo(path);   // .exe

                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;                      // 别闪黑框
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                psi.WorkingDirectory = Path.GetDirectoryName(path);
                Process p = Process.Start(psi);
                Log.Write("辅助功能：已运行 " + path + "（pid " + (p == null ? 0 : p.Id) + "）");
            }
            catch (Exception ex)
            {
                Log.Write("辅助功能：运行脚本失败 " + ex.Message);
                Notify("运行脚本失败", ex.Message);
            }
        }

        /// <summary>打开「辅助功能」模块目录（第一次点的时候顺手建出来）。</summary>
        private void OpenAuxDir()
        {
            try
            {
                Directory.CreateDirectory(Cfg.AuxDir);
                Process.Start("explorer.exe", "\"" + Cfg.AuxDir + "\"");
            }
            catch (Exception ex) { Log.Write("辅助功能：打开模块目录失败 " + ex.Message); }
        }

        /// <summary>
        /// 首次运行时把模块目录建好，并放一份说明——别人拿到这个 exe，
        /// 看说明就知道怎么往菜单里加自己的功能，不用读源码。
        /// </summary>
        private static void EnsureAuxDir()
        {
            try
            {
                Directory.CreateDirectory(Cfg.AuxDir);
                string readme = Path.Combine(Cfg.AuxDir, "说明.txt");
                if (File.Exists(readme)) return;
                File.WriteAllText(readme,
                    "这里是「辅助功能」模块目录。\r\n\r\n" +
                    "规则很简单：\r\n" +
                    "  1. 把 .bat / .cmd / .exe / .ps1 文件丢进这个目录，托盘菜单「辅助功能」里就会多一条；\r\n" +
                    "  2. 文件名（去掉扩展名）就是菜单文字，按文件名排序；\r\n" +
                    "  3. 新建子文件夹可以把模块分组，子文件夹会变成下一级菜单；\r\n" +
                    "  4. 不用重启启动器：每次右键弹出菜单都会重新扫描这个目录。\r\n\r\n" +
                    "脚本以隐藏窗口运行，工作目录 = 脚本所在目录，不提权。\r\n" +
                    "删除同名文件即可从菜单里去掉。\r\n",
                    new System.Text.UTF8Encoding(true));
            }
            catch (Exception ex) { Log.Write("辅助功能：准备模块目录失败 " + ex.Message); }
        }

        /// <summary>
        /// 图标目录：exe 同目录的 icons\，第一次运行建出来并放一张说明，
        /// 别人一看就知道怎么给菜单行配图标（文件名 = 菜单文字）。
        /// </summary>
        private static void EnsureIconDir()
        {
            try
            {
                string dir = Path.Combine(Application.StartupPath, "icons");
                Directory.CreateDirectory(dir);
                string readme = Path.Combine(dir, "说明.txt");
                if (File.Exists(readme)) return;
                File.WriteAllText(readme,
                    "菜单图标目录。\r\n\r\n" +
                    "规则：\r\n" +
                    "  1. 放一张 PNG，文件名就用菜单上的文字，例如「打开 DSH 界面.png」；\r\n" +
                    "  2. 建议 32×32 或 64×64 透明底，四边留一点余量，显示时按行高自动缩放；\r\n" +
                    "  3. 「辅助功能」里的模块也能配：文件名 = 脚本名（不带扩展名）；\r\n" +
                    "  4. 新丢进去的图标下次右键就生效；换掉同名的 PNG 要重启启动器才会重新读。\r\n",
                    new System.Text.UTF8Encoding(true));
            }
            catch (Exception ex) { Log.Write("图标目录准备失败：" + ex.Message); }
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

        /// <summary>关掉 dsh web 再退出启动器。会断开正在看的页面，所以先问一句。</summary>
        private void QuitAll()
        {
            if (_quitting) return;              // 已在退出流程里：别再弹一次确认框
            _silent = false;
            DialogResult r = MessageBox.Show(
                "将关闭正在运行的 dsh web（浏览器里那个页面会断开），然后退出启动器。\r\n\r\n确定吗？",
                "DSH Web 启动器", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (r != DialogResult.Yes) return;
            _quitting = true;
            Log.Write("退出启动器并停止服务（用户确认）");
            try { _timer.Stop(); } catch { }
            lock (_lock)
            {
                _startToken++;          // 迟到启动的 watchdog 不能再改状态
                _restartAfterStop = false;
                _state = St.Stopping;
            }
            try { _menu.SetStatus("状态：关闭中…", Ios.Gray); } catch { }
            // 杀进程（WMI 枚举）与等端口释放都是慢活：挪到后台线程，UI 线程保持响应，
            // 退出期间菜单仍可正常弹出，收尾完成后再真正退出。
            _iOpen.Enabled = false;
            _iStop.Enabled = false;
            _iQuitAll.Enabled = false;
            _iRestart.Enabled = false;
            ThreadPool.QueueUserWorkItem(delegate
            {
                int n = 0;
                try { n = KillDshHosts(); } catch (Exception ex) { Log.Write("关闭失败：" + ex.Message); }
                try { if (File.Exists(Cfg.PidFile)) File.Delete(Cfg.PidFile); } catch { }
                try { Cfg.ClearUrlCache(); } catch { }
                try { WaitPortClosed(8000); } catch { }
                Post(delegate
                {
                    Log.Write("已关闭 " + n + " 个 dsh web 进程，启动器退出");
                    try { _ni.Visible = false; } catch { }
                    ExitThread();
                });
            });
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
                    string v = k.GetValue(RunName) as string;
                    if (v == null) return false;
                    // 只认指向当前这份 exe 的启动项：换过目录/换过机器后旧路径还在，
                    // 勾选状态会骗人（显示已开启，登录却不启动）
                    return v.IndexOf(Application.ExecutablePath, StringComparison.OrdinalIgnoreCase) >= 0;
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
        /// <summary>最近一次后台探测的结论（最多约 1 秒旧）。UI 线程只读这个。</summary>
        private static volatile bool _portUp;
        private static int _portWatchStarted;

        /// <summary>
        /// 后台线程轮询端口。为什么非要挪出 UI 线程：本机连一个"没人监听"的回环端口
        /// 不会立刻收到 RST，而是要耗满超时（实测 700ms），停止服务后每秒一次的探测
        /// 就把 UI 线程吃掉七成，托盘菜单鼠标划过要等半秒才加深——这就是"不跟手"。
        /// </summary>
        private static void StartPortWatch()
        {
            if (Interlocked.CompareExchange(ref _portWatchStarted, 1, 0) != 0) return;
            ThreadPool.QueueUserWorkItem(delegate
            {
                while (true)
                {
                    bool up = PortOpenTimed(350);
                    _portUp = up;
                    try { Thread.Sleep(up ? 900 : 350); } catch { }
                }
            });
        }

        private static bool PortUpCached() { return _portUp; }

        /// <summary>连通性探测。注意：端口关着时会耗满 timeout，别在 UI 线程上调。</summary>
        public static bool PortOpenTimed(int timeoutMs)
        {
            try
            {
                using (TcpClient c = new TcpClient())
                {
                    IAsyncResult ar = c.BeginConnect(IPAddress.Loopback, Cfg.Port, null, null);
                    bool ok = ar.AsyncWaitHandle.WaitOne(timeoutMs);
                    if (ok) c.EndConnect(ar);
                    ar.AsyncWaitHandle.Close();   // 以前这个等待句柄没人关，每秒一次会一直攒
                    return ok && c.Connected;
                }
            }
            catch { return false; }
        }

        public static bool PortOpen() { return PortOpenTimed(700); }

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
        // ── 通知身份 ────────────────────────────────────────────────────
        // 没有 AppUserModelID 的 exe，Windows 通知只能给它配一个系统通用占位图标
        // （灰底白 D），标题也只能显示 exe 文件名。注册应用身份后，通知标题用
        // DisplayName、图标用 IconUri 指向的 exe（就是打包进去的鲸鱼），
        // 跟托盘图标保持一致。
        private const string AumId = "DSH.Web.Launcher";

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SetCurrentProcessExplicitAppUserModelID(
            [MarshalAs(UnmanagedType.LPWStr)] string appId);

        private static void RegisterAppIdentity()
        {
            try
            {
                // 通知图标：优先用同目录的 dsh-toast.png，退而求其次用 dsh.ico，再退到 exe 自身。
                // 注意 dsh-toast.png 里存的是**反色**的鲸鱼（金鲸鱼）：Windows 11 会把旧式
                // 气泡通知的应用图标整体取反（蓝 #4D6BFE ↔ 金 #B29401），预先反色才能显示成蓝色。
                string png = Path.Combine(Application.StartupPath, "dsh-toast.png");
                string ico = Path.Combine(Application.StartupPath, "dsh.ico");
                string icon = File.Exists(png) ? png : (File.Exists(ico) ? ico : Application.ExecutablePath);
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(@"Software\Classes\AppUserModelId\" + AumId))
                {
                    if (k != null)
                    {
                        k.SetValue("DisplayName", "DSH Web 启动器", RegistryValueKind.String);
                        k.SetValue("IconUri", icon, RegistryValueKind.String);
                    }
                }
                SetCurrentProcessExplicitAppUserModelID(AumId);
            }
            catch (Exception ex) { Log.Write("注册通知身份失败：" + ex.Message); }
        }

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
            RegisterAppIdentity();

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

            // 菜单/定时器回调里的异常不该让整个托盘消失：记下来，继续跑
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate (object s, ThreadExceptionEventArgs e)
            {
                Fatal(e.Exception);
                Log.Write("界面线程异常（已记录，未退出）：" + e.Exception.Message);
            };

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
