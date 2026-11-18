// DshMenu.cs — iOS 风格托盘菜单（自绘 + Per-Monitor DPI 适配）
//
// 设计要点：
//   1) 菜单全部自绘：圆角、悬浮胶囊、细分割线、留白，按 iOS 的视觉密度来
//   2) 尺寸单位都是「设计像素 × DPI 缩放」，配合 app.manifest 的 PerMonitorV2，
//      125% / 150% / 200% 下字号和间距等比例放大，不会被系统位图拉伸成模糊
//   3) 语义色：主操作蓝(007AFF)、破坏性操作红(FF3B30)、次要文字灰(8E8E93)
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DshLauncher
{
    /// <summary>iOS 语义色板（浅色）。</summary>
    internal static class Ios
    {
        public static readonly Color Bg = Color.FromArgb(250, 250, 252);
        public static readonly Color Hover = Color.FromArgb(238, 238, 243);
        public static readonly Color Text = Color.FromArgb(28, 28, 30);
        public static readonly Color Sub = Color.FromArgb(142, 142, 147);
        public static readonly Color Disabled = Color.FromArgb(176, 176, 181);
        public static readonly Color Accent = Color.FromArgb(0, 122, 255);
        public static readonly Color Danger = Color.FromArgb(255, 59, 48);
        public static readonly Color Line = Color.FromArgb(229, 229, 234);
        public static readonly Color Border = Color.FromArgb(214, 214, 220);
        // 状态色（和托盘图标一一对应）
        public static readonly Color Green = Color.FromArgb(52, 199, 89);
        public static readonly Color Orange = Color.FromArgb(255, 149, 0);
        public static readonly Color Red = Color.FromArgb(255, 59, 48);
        public static readonly Color Gray = Color.FromArgb(142, 142, 147);
    }

    /// <summary>按鼠标所在显示器返回 DPI 缩放比例。</summary>
    internal static class Dpi
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);
        [DllImport("shcore.dll")]
        private static extern int GetDpiForMonitor(IntPtr hmon, int dpiType, out uint dpiX, out uint dpiY);
        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("gdi32.dll")]
        private static extern int GetDeviceCaps(IntPtr hdc, int nIndex);

        public static float CursorScale()
        {
            try
            {
                POINT pt;
                pt.X = Cursor.Position.X;
                pt.Y = Cursor.Position.Y;
                IntPtr mon = MonitorFromPoint(pt, 2 /*MONITOR_DEFAULTTONEAREST*/);
                uint dx, dy;
                if (GetDpiForMonitor(mon, 0 /*MDT_EFFECTIVE_DPI*/, out dx, out dy) == 0 && dx > 0)
                    return dx / 96f;
            }
            catch { }
            try
            {
                IntPtr dc = GetDC(IntPtr.Zero);
                int dpi = GetDeviceCaps(dc, 88 /*LOGPIXELSX*/);
                ReleaseDC(IntPtr.Zero, dc);
                if (dpi > 0) return dpi / 96f;
            }
            catch { }
            return 1f;
        }
    }

    /// <summary>iOS 风格自绘渲染器。所有尺寸乘以 S（DPI 缩放）。</summary>
    internal sealed class IosRenderer : ToolStripRenderer
    {
        public float S = 1f;
        /// <summary>左内边距（已按 S 换算），文字与状态球都靠它定位。</summary>
        public int PadH = 16;
        /// <summary>右内边距（已按 S 换算）。</summary>
        public int PadR = 16;
        /// <summary>状态行左侧给彩色球预留的宽度（已按 S 换算）。</summary>
        public int DotRoom = 22;
        /// <summary>每行文字左侧的图标槽宽度（已按 S 换算）；0 = 不预留。</summary>
        public int IconRoom = 0;
        /// <summary>
        /// 行首图标。刻意不用 ToolStripItem.Image：那个属性参与排版，
        /// 一张 64px 的图会把整条菜单的行高全部撑成 64px。这里只做绘制，不碰布局。
        /// </summary>
        public readonly System.Collections.Generic.Dictionary<ToolStripItem, Image> Icons =
            new System.Collections.Generic.Dictionary<ToolStripItem, Image>();
        /// <summary>状态行左侧彩色球的颜色，由 MenuSpec.SetStatus 更新。</summary>
        public Color StatusColor = Ios.Gray;

        private static int Px(float v, float s)
        {
            int i = (int)Math.Round(v * s);
            return i < 1 ? 1 : i;
        }

        // 注意：条目级绘制事件的 Graphics 原点已经是「条目左上角」，
        // 这里的坐标系是条目内部坐标，不能用 Bounds.Top/Bounds.Left 再叠一次。
        private Rectangle PillRect(ToolStripItem item)
        {
            int inset = Px(4f, S);
            int v = Px(1f, S);
            return new Rectangle(inset, v,
                Math.Max(1, item.Width - inset * 2), Math.Max(1, item.Height - v * 2));
        }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using (SolidBrush b = new SolidBrush(Ios.Bg))
                e.Graphics.FillRectangle(b, new Rectangle(0, 0, e.ToolStrip.Width, e.ToolStrip.Height));
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e) { }

        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled) return;
            // 背景事件默认被裁到 ContentRectangle（右侧对勾留白区画不进去），这里放开到整条目
            Region old = e.Graphics.Clip;
            try
            {
                e.Graphics.SetClip(new Rectangle(0, 0, e.Item.Width, e.Item.Height), CombineMode.Replace);
                using (GraphicsPath p = Round(PillRect(e.Item), Px(8f, S)))
                using (SolidBrush b = new SolidBrush(Ios.Hover))
                    e.Graphics.FillPath(b, p);
            }
            finally { e.Graphics.Clip = old; }
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            // 完全自绘文字：ToolStrip 自己的内边距换算在「强制等宽 + 关闭图片栏」时不可靠
            // （实测 TextRectangle.X 只有 8px，Padding 根本不生效），所以位置由这里决定。
            bool isStatus = (e.Item.Tag as string) == "status";
            if ((e.Item.Tag as string) == "spacer") return;      // 上下占位条：不画字也不画槽
            Color c;
            if (!e.Item.Enabled) c = Ios.Disabled;
            else if ((e.Item.Tag as string) == "danger") c = Ios.Danger;
            else if ((e.Item.Tag as string) == "accent") c = Ios.Accent;
            else c = Ios.Text;

            ToolStripMenuItem rowItem = e.Item as ToolStripMenuItem;
            bool hasSub = rowItem != null && rowItem.HasDropDownItems;
            int left = PadH + (isStatus ? DotRoom : IconRoom);
            int right = PadR + (rowItem != null && rowItem.Checked ? Px(18f, S) : 0)
                             + (hasSub ? Px(14f, S) : 0);
            int width = Math.Max(8, e.Item.Width - left - right);
            Rectangle box = new Rectangle(left, 0, width, e.Item.Height);

            if (!isStatus) DrawIconSlot(e.Graphics, e.Item);

            TextRenderer.DrawText(e.Graphics, e.Text, e.TextFont, box, c,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding |
                TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);

            // 状态行左边的彩色球：颜色跟着当前状态走（灰=未运行 / 橙=启动中 / 绿=运行中 / 红=失败）
            if (isStatus)
            {
                float d = Math.Max(7f, 8f * S);
                float cx = left - Px(10f, S);
                float cy = box.Height / 2f;
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                // 先画一层极淡的光晕，再画实心球，看起来像 iOS 的状态灯而不是一个死圆圈
                using (SolidBrush h = new SolidBrush(Color.FromArgb(46, StatusColor)))
                    e.Graphics.FillEllipse(h, cx - d * 0.95f, cy - d * 0.95f, d * 1.9f, d * 1.9f);
                using (SolidBrush b = new SolidBrush(StatusColor))
                    e.Graphics.FillEllipse(b, cx - d / 2f, cy - d / 2f, d, d);
            }

            ToolStripMenuItem mi = e.Item as ToolStripMenuItem;
            if (mi != null && mi.Checked)
            {
                // 右侧对勾（不用系统那个灰框）；坐标是条目内部坐标
                float x = mi.Width - Px(19f, S);
                float cy = mi.Height / 2f;
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (Pen pen = new Pen(Ios.Accent, Math.Max(1.5f, 1.7f * S)))
                {
                    pen.StartCap = LineCap.Round;
                    pen.EndCap = LineCap.Round;
                    pen.LineJoin = LineJoin.Round;
                    e.Graphics.DrawLines(pen, new PointF[] {
                        new PointF(x, cy),
                        new PointF(x + 3.8f * S, cy + 4.0f * S),
                        new PointF(x + 10.6f * S, cy - 4.6f * S)
                    });
                }
            }

            if (hasSub) DrawSubArrow(e.Graphics, e.Item, e.Item.Width, e.Item.Height, e.Item.Enabled);
        }

        /// <summary>
        /// 每行文字前面的图标槽：有图就画图（悬停微微放大），没图时只在悬停那一刻
        /// 露出一枚淡蓝色小槽（圆角框 + 中心小点），提示「这里可以放图标」。
        /// 平时不画，菜单保持干净，不会变成一排空盒子。
        /// </summary>
        private void DrawIconSlot(Graphics g, ToolStripItem item)
        {
            if (IconRoom <= 0) return;
            int box = Math.Max(12, Px(15f, S));
            float cx = PadH + IconRoom / 2f - Px(2f, S);
            float cy = item.Height / 2f;
            bool hot = item.Selected && item.Enabled;

            Image icon;
            Icons.TryGetValue(item, out icon);

            g.SmoothingMode = SmoothingMode.AntiAlias;

            if (icon != null)
            {
                float grow = hot ? 1.08f : 1f;
                float w = box * grow, h = box * grow;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(icon, new RectangleF(cx - w / 2f, cy - h / 2f, w, h));
                return;
            }
            if (!hot) return;

            Rectangle r = new Rectangle((int)Math.Round(cx - box / 2f), (int)Math.Round(cy - box / 2f), box, box);
            using (GraphicsPath p = Round(r, Math.Max(3, Px(5f, S))))
            {
                using (SolidBrush b = new SolidBrush(Color.FromArgb(16, Ios.Accent)))
                    g.FillPath(b, p);
                using (Pen pen = new Pen(Color.FromArgb(38, Ios.Accent), Math.Max(1f, S)))
                    g.DrawPath(pen, p);
            }
            using (SolidBrush b = new SolidBrush(Color.FromArgb(70, Ios.Accent)))
            {
                float d = Math.Max(3f, 3.2f * S);
                g.FillEllipse(b, cx - d / 2f, cy - d / 2f, d, d);
            }
        }

        /// <summary>二级菜单指示箭头：系统的箭头和这套视觉不搭，改在这里画一个细的。</summary>
        private void DrawSubArrow(Graphics g, ToolStripItem item, int width, int height, bool enabled)
        {
            float ax = width - Px(12f, S);
            float cy = height / 2f;
            float h = 4.0f * S;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (Pen pen = new Pen(enabled ? Ios.Sub : Ios.Disabled, Math.Max(1.2f, 1.4f * S)))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                pen.LineJoin = LineJoin.Round;
                g.DrawLines(pen, new PointF[] {
                    new PointF(ax - h * 0.55f, cy - h),
                    new PointF(ax + h * 0.45f, cy),
                    new PointF(ax - h * 0.55f, cy + h)
                });
            }
        }

        // 系统的箭头不画，交给上面的 DrawSubArrow（在条目文字那一遍里顺带画掉）
        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e) { }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            int inset = Px(12f, S);
            int w = e.Item.Width > 0 ? e.Item.Width : e.ToolStrip.Width;
            using (Pen p = new Pen(Ios.Line, Math.Max(1f, S)))
            {
                float y = e.Item.Height / 2f;
                e.Graphics.DrawLine(p, inset, y, w - inset, y);
            }
        }

        internal static GraphicsPath Round(Rectangle r, int radius)
        {
            GraphicsPath p = new GraphicsPath();
            if (radius < 1) { p.AddRectangle(r); return p; }
            int d = radius * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    /// <summary>圆角无边框菜单窗口。</summary>
    internal sealed class IosMenu : ContextMenuStrip
    {
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);

        public float S = 1f;
        public int Radius = 10;

        public IosMenu()
        {
            AutoSize = true;
            AutoClose = true;
            ShowCheckMargin = false;
            ShowItemToolTips = false;
            // 关掉左侧图片栏：默认会白留 25px，文字被推得离左边缘很远（看起来特别“怪”）
            ShowImageMargin = false;
            BackColor = Ios.Bg;
            ForeColor = Ios.Text;
            Padding = new Padding(0);
            Renderer = new IosRenderer();
            // 注意：不要再设 RenderMode = Professional —— 那会把上面挂的自绘 Renderer 换掉，
            // 后续强转 IosRenderer 就会抛 InvalidCastException。
            // Win11 由系统画圆角和投影；旧系统退回 WinForms 的方投影
            DropShadowEnabled = Environment.OSVersion.Version.Build < 22000;
        }

        protected override void OnOpened(EventArgs e)
        {
            base.OnOpened(e);
            if (DshMenu.Verbose) DshMenu.Trace("菜单已打开（缩放 " + S.ToString("0.00") + "，尺寸 " + Width + "x" + Height + "）");
            StylePopup(this, S);
        }

        /// <summary>
        /// 弹出窗口的外观：Win11 交给 DWM 画圆角和极浅描边，旧系统用 Region 兜底圆角。
        /// 主菜单（IosMenu）和「辅助功能」的二级菜单（普通 ToolStripDropDownMenu）共用这一套。
        /// </summary>
        internal static void StylePopup(ToolStripDropDown w, float scale)
        {
            bool dwmOk = false;
            try
            {
                int pref = 2;   // DWMWCP_ROUND
                dwmOk = DwmSetWindowAttribute(w.Handle, 33 /*DWMWA_WINDOW_CORNER_PREFERENCE*/, ref pref, sizeof(int)) == 0;
                // 极浅的描边，让菜单在深色壁纸上也有边界感（同色 = 不画）
                int color = ColorTranslator.ToWin32(Ios.Border) | unchecked((int)0xFF000000);
                DwmSetWindowAttribute(w.Handle, 35 /*DWMWA_BORDER_COLOR*/, ref color, sizeof(int));
            }
            catch { }

            if (!dwmOk || Environment.OSVersion.Version.Build < 22000)
            {
                try
                {
                    IosMenu im = w as IosMenu;
                    int radius = im != null ? im.Radius : (int)Math.Round(10 * (scale <= 0f ? 1f : scale));
                    using (GraphicsPath p = IosRenderer.Round(new Rectangle(0, 0, w.Width, w.Height), radius))
                    {
                        Region old = w.Region;
                        w.Region = new Region(p);
                        if (old != null) old.Dispose();
                    }
                }
                catch { }
            }
        }
    }

    /// <summary>菜单实例 + 用于状态刷新的条目引用。</summary>
    internal sealed class MenuSpec
    {
        public IosMenu Menu;
        public ToolStripMenuItem Status, Open, Restart, Stop, Auto, Log, Quit, QuitAll;
        /// <summary>顶级项「辅助功能」，挂在「开机自动启动」下面，带二级菜单。</summary>
        public ToolStripMenuItem Aux;
        /// <summary>「辅助功能」二级菜单本体：后续升级类功能统一往这里加（见 AddAuxItem）。</summary>
        public ToolStripDropDownMenu AuxMenu;
        /// <summary>模块目录：丢进这里的脚本会自动出现在菜单里（每次弹出菜单重新扫描）。</summary>
        public string AuxDir;
        /// <summary>执行一个模块脚本（由启动器提供：隐藏窗口跑，工作目录 = 脚本所在目录）。</summary>
        public Action<string> AuxRun;
        /// <summary>打开模块目录（由启动器提供）。</summary>
        public Action AuxOpenDir;
        /// <summary>
        /// 图标目录：按「菜单文字.png」放图，下次弹菜单就出现在行首槽位里。
        /// 留空 = exe 同目录的 icons；没有这个目录也没关系，槽位空着。
        /// </summary>
        public string IconDir;
        /// <summary>已加载的图标（文件名 → 位图），避免每次弹菜单都重新读盘。</summary>
        private readonly System.Collections.Generic.Dictionary<string, Image> _iconCache =
            new System.Collections.Generic.Dictionary<string, Image>(StringComparer.OrdinalIgnoreCase);

        /// <summary>代码里给某一行指定图标（发布后给人换图标走图标目录，不用改代码）。</summary>
        public void SetIcon(ToolStripMenuItem item, Image icon)
        {
            if (item == null) return;
            IosRenderer r = RendererOf(item);
            if (r == null) return;
            r.Icons[item] = icon;
            if (item.Owner != null) item.Owner.Invalidate();
        }

        private static IosRenderer RendererOf(ToolStripItem item)
        {
            ToolStrip owner = item == null ? null : item.Owner;
            return owner == null ? null : owner.Renderer as IosRenderer;
        }

        /// <summary>按「菜单文字.png」给每一行配上图标；没有对应文件的就空着。</summary>
        public void ApplyIcons()
        {
            if (string.IsNullOrEmpty(IconDir) || !Directory.Exists(IconDir)) return;
            ApplyIcons(Menu.Items);
        }

        private void ApplyIcons(ToolStripItemCollection items)
        {
            foreach (ToolStripItem it in items)
            {
                ToolStripMenuItem mi = it as ToolStripMenuItem;
                if (mi == null) continue;
                if (!string.IsNullOrEmpty(mi.Text)) AssignIcon(mi);
                // 模块分组是二级下拉，里面的条目也要配图标
                ToolStripDropDownMenu sub = mi.DropDown as ToolStripDropDownMenu;
                if (sub != null && sub.Items.Count > 0) ApplyIcons(sub.Items);
            }
        }

        private void AssignIcon(ToolStripMenuItem mi)
        {
            IosRenderer r = RendererOf(mi);
            if (r == null || r.Icons.ContainsKey(mi)) return;
            Image img;
            if (_iconCache.TryGetValue(mi.Text, out img)) { r.Icons[mi] = img; return; }
            string file = Path.Combine(IconDir, mi.Text + ".png");
            try
            {
                if (!File.Exists(file)) return;
                using (Image src = Image.FromFile(file))
                    img = new Bitmap(src);      // 拷一份，别让 PNG 文件被进程一直占着
                _iconCache[mi.Text] = img;
                r.Icons[mi] = img;
            }
            catch (Exception ex) { DshMenu.Trace("图标加载失败 " + file + "：" + ex.Message); }
        }
        /// <summary>代码里追加的模块条目：不随目录扫描被清掉。</summary>
        private readonly System.Collections.Generic.List<ToolStripMenuItem> _auxCustom =
            new System.Collections.Generic.List<ToolStripMenuItem>();
        /// <summary>菜单上下两条透明占位条：ToolStripDropDownMenu 不吃 Padding，靠它们撑出边框留白。</summary>
        public ToolStripMenuItem SpacerTop, SpacerBottom;
        public float Scale = 1f;
        /// <summary>每次弹出前是否按鼠标所在显示器重新缩放（预览工具会关掉它）。</summary>
        public bool AutoScaleOnOpen = true;
        private Font _font;

        private static int Px(float v)
        {
            int i = (int)Math.Round(v);
            return i < 1 ? 1 : i;
        }

        /// <summary>更新状态行文字 + 左侧彩色球颜色（颜色与托盘图标一个语义）。</summary>
        public void SetStatus(string text, Color dot)
        {
            if (Status.Text != text) Status.Text = text;
            IosRenderer r = Menu.Renderer as IosRenderer;
            if (r != null && r.StatusColor != dot) { r.StatusColor = dot; Menu.Invalidate(); }
        }

        /// <summary>
        /// 代码里往「辅助功能」里加一条模块（编译期扩展用；发布后给人加功能走模块目录）。
        /// 加完立刻重建一次，菜单下次弹出就是正确尺寸。
        /// </summary>
        public ToolStripMenuItem AddAuxItem(string text, Action onClick, string tag = null)
        {
            ToolStripMenuItem it = new ToolStripMenuItem(text, null, delegate { if (onClick != null) onClick(); });
            if (tag != null) it.Tag = tag;
            _auxCustom.Add(it);
            _auxSig = null;          // 强制重建：目录指纹没变也要把新条目挂上去
            RefreshAux();
            return it;
        }

        private static readonly string[] AuxExts = new string[] { ".bat", ".cmd", ".exe", ".ps1" };
        /// <summary>上次扫描时的目录指纹；没变化就不重建条目。</summary>
        private string _auxSig;

        /// <summary>模块目录的内容指纹：目录/文件的最大修改时间 + 文件数。</summary>
        private string AuxSignature()
        {
            try
            {
                if (string.IsNullOrEmpty(AuxDir) || !Directory.Exists(AuxDir)) return "missing";
                long ticks = Directory.GetLastWriteTimeUtc(AuxDir).Ticks;
                int n = 0;
                foreach (string f in Directory.GetFiles(AuxDir))
                {
                    n++;
                    long t = File.GetLastWriteTimeUtc(f).Ticks;
                    if (t > ticks) ticks = t;
                }
                foreach (string d in Directory.GetDirectories(AuxDir))
                {
                    long t = Directory.GetLastWriteTimeUtc(d).Ticks;
                    if (t > ticks) ticks = t;
                    foreach (string f in Directory.GetFiles(d))
                    {
                        n++;
                        long ft = File.GetLastWriteTimeUtc(f).Ticks;
                        if (ft > ticks) ticks = ft;
                    }
                }
                return ticks.ToString() + ":" + n;
            }
            catch { return null; }
        }

        /// <summary>
        /// 重建「辅助功能」二级菜单：代码追加的模块 + 模块目录里的脚本（子文件夹 = 下一级菜单）。
        /// 每次弹菜单前都会调一次，所以丢文件进去不用重启启动器。
        /// </summary>
        public void RefreshAux()
        {
            if (AuxMenu == null) return;

            // 目录没变就别重建：条目重建 + 重新量尺寸在 20 个模块时实测要 130ms 以上，
            // 每次右键都做一遍会让菜单明显慢半拍。
            string sig = AuxSignature();
            if (sig != null && sig == _auxSig && AuxMenu.Items.Count > 0) return;
            _auxSig = sig;

            // 条目重建后旧对象会被丢弃，图标表里的键也要跟着清（否则每次右键都留一份）
            IosRenderer auxRenderer = AuxMenu.Renderer as IosRenderer;
            if (auxRenderer != null) auxRenderer.Icons.Clear();

            // 每次弹出都重建，旧条目要显式释放：Clear() 只摘链、不 Dispose，
            // 反复右键会白白攒下一堆 ToolStripItem 和它们的子菜单。
            for (int i = AuxMenu.Items.Count - 1; i >= 0; i--)
            {
                ToolStripItem old = AuxMenu.Items[i];
                AuxMenu.Items.RemoveAt(i);
                ToolStripMenuItem om = old as ToolStripMenuItem;
                if (om != null && _auxCustom.Contains(om)) continue;   // 代码加的条目要留着复用
                old.Dispose();
            }

            int count = 0;
            foreach (ToolStripMenuItem it in _auxCustom) { AuxMenu.Items.Add(it); count++; }

            try
            {
                if (!string.IsNullOrEmpty(AuxDir) && Directory.Exists(AuxDir))
                    count += AddAuxEntries(AuxMenu.Items, AuxDir, 1);
            }
            catch (Exception ex) { DshMenu.Trace("辅助功能：扫描模块目录失败 " + ex.Message); }

            if (count == 0)
            {
                ToolStripMenuItem empty = new ToolStripMenuItem("（还没有模块，见「打开模块文件夹」）");
                empty.Enabled = false;
                AuxMenu.Items.Add(empty);
            }

            AuxMenu.Items.Add(new ToolStripSeparator());
            ToolStripMenuItem openDir = new ToolStripMenuItem("打开模块文件夹", null,
                delegate { if (AuxOpenDir != null) AuxOpenDir(); });
            openDir.Tag = "accent";
            AuxMenu.Items.Add(openDir);
            // 尺寸不在这里算：调用方（m.Opening）随后会统一 ApplyScale，
            // 以前这里再算一遍等于每次弹菜单多跑一次全量排版。
        }

        /// <summary>把一个目录里的脚本和子目录变成菜单条目（子目录只展开一层，避免无底洞）。</summary>
        private int AddAuxEntries(ToolStripItemCollection items, string dir, int depth)
        {
            int count = 0;

            if (depth <= 1)
            {
                System.Collections.Generic.List<string> subs =
                    new System.Collections.Generic.List<string>(Directory.GetDirectories(dir));
                subs.Sort(StringComparer.CurrentCultureIgnoreCase);
                foreach (string sub in subs)
                {
                    ToolStripDropDownMenu dd = DshMenu.NewDropDown(this);
                    int kids = AddAuxEntries(dd.Items, sub, depth + 1);
                    if (kids == 0) { dd.Dispose(); continue; }
                    ToolStripMenuItem group = new ToolStripMenuItem(Path.GetFileName(sub));
                    group.DropDown = dd;
                    items.Add(group);
                    count++;
                }
            }

            System.Collections.Generic.List<string> files =
                new System.Collections.Generic.List<string>(Directory.GetFiles(dir));
            files.Sort(StringComparer.CurrentCultureIgnoreCase);
            foreach (string f in files)
            {
                if (Array.IndexOf(AuxExts, Path.GetExtension(f).ToLowerInvariant()) < 0) continue;
                string path = f;
                // 文件名就是菜单文字（去扩展名），不用改配置、不用重编译
                ToolStripMenuItem mi = new ToolStripMenuItem(Path.GetFileNameWithoutExtension(f), null,
                    delegate { if (AuxRun != null) AuxRun(path); });
                items.Add(mi);
                count++;
            }
            return count;
        }

        /// <summary>按 DPI 缩放重算字号、内边距、行高。</summary>
        public void ApplyScale(float s)
        {
            if (s <= 0f) s = 1f;
            Scale = s;
            Menu.S = s;
            IosRenderer r = Menu.Renderer as IosRenderer;
            if (r != null) r.S = s;
            Menu.Radius = Px(10 * s);

            Font f = new Font("Microsoft YaHei UI", Math.Max(11f, 13.5f * s), FontStyle.Regular, GraphicsUnit.Pixel);
            // 状态行和条目用同一号字：只靠颜色区分层级，尺寸不再缩水
            Font old = _font;
            _font = f;

            int padH = Px(16 * s);          // 左右留白（关掉图片栏后靠它撑出呼吸感）
            int padV = Px(7 * s);
            int dotRoom = Px(20 * s);        // 状态行：彩色球 + 间距
            int iconRoom = Px(21 * s);       // 每行行首的图标槽：图标本体 + 到文字的间距
            int checkRoom = Px(20 * s);
            int sepV = Px(3 * s);            // 胶囊↔分割线的距离；菜单上下边框也用同一个值，三处一致

            if (r != null) { r.PadH = padH; r.PadR = padH; r.DotRoom = dotRoom; r.IconRoom = iconRoom; }

            foreach (ToolStripItem it in Menu.Items)
            {
                if (it is ToolStripSeparator)
                {
                    it.Margin = new Padding(0, sepV, 0, sepV);
                    continue;
                }
                if ((it.Tag as string) == "spacer") { it.Margin = new Padding(0); continue; }
                it.Font = f;
                it.Margin = new Padding(0);
                it.Padding = new Padding(padH + iconRoom, padV, padH, padV);
                it.AutoSize = true;
            }
            Status.Font = f;
            // 状态行上下等距 → 文字与圆球在自己那一条里天然居中；
            // 再让「菜单上边框」与「胶囊到分割线」用同一个 sepV，三处留白就完全一致了。
            Status.Padding = new Padding(padH + dotRoom, Px(9 * s), padH, Px(9 * s));
            Auto.Padding = new Padding(padH + iconRoom, padV, padH + checkRoom, padV);
            // 「辅助功能」右侧要留出二级菜单箭头的位置（宽度里先算进去，免得箭头压到文字）
            if (Aux != null) Aux.Padding = new Padding(padH + iconRoom, padV, padH + checkRoom, padV);

            // 菜单上/下边框与胶囊的距离 == 胶囊与分割线的距离
            // 注意：ToolStripDropDownMenu 只把 Padding.Top 用于排版、下边压根不参与尺寸计算
            //（实测把 Padding 改到 16×缩放，菜单尺寸一点没变），所以上下留白一律交给占位条目。
            Menu.Padding = new Padding(0);
            if (SpacerTop != null)
            {
                // 竖线方向的距离靠这两条占位条目撑住（Padding 不管用）
                // 经验值：分割线自身占掉约 5px，所以占位条比 sepV 略高一点，两边看起来才一样高
                SpacerTop.AutoSize = false;
                SpacerBottom.AutoSize = false;
                SpacerTop.Enabled = false;
                SpacerBottom.Enabled = false;
                SpacerTop.Size = new Size(1, sepV + Px(2 * s));
                SpacerBottom.Size = new Size(1, sepV + Px(2 * s));
            }

            // ToolStrip 不会把条目拉成等宽：默认每条只占自己的 preferred 宽度，
            // 结果菜单右侧一片空白、悬浮胶囊长短不一。这里量出最宽条目后统一定宽。
            int maxW = 0;
            foreach (ToolStripItem it in Menu.Items)
            {
                if (it is ToolStripSeparator) continue;
                if ((it.Tag as string) == "spacer") continue;
                Size ps = it.GetPreferredSize(Size.Empty);
                if (ps.Width > maxW) maxW = ps.Width;
            }
            foreach (ToolStripItem it in Menu.Items)
            {
                if (it is ToolStripSeparator) continue;
                if ((it.Tag as string) == "spacer") { it.Size = new Size(maxW, it.Height); continue; }
                Size ps = it.GetPreferredSize(Size.Empty);
                it.AutoSize = false;
                it.Size = new Size(maxW, ps.Height);
            }
            Menu.MinimumSize = new Size(maxW + Px(2 * s), 0);

            // 「辅助功能」及其下的模块菜单是独立的 ToolStripDropDownMenu，字号/内边距/等宽
            // 都要单独算一遍（而且可能嵌套），否则会退回系统默认的灰底小字号。
            if (AuxMenu != null) StyleDropDown(AuxMenu, f, padH, padV, sepV, s, checkRoom, iconRoom);

            Menu.PerformLayout();
            Menu.Invalidate();

            if (old != null) { try { old.Dispose(); } catch { } }
        }

        /// <summary>把同一套 DPI 尺寸递归套到下拉菜单（含模块分组的子菜单）上。</summary>
        private void StyleDropDown(ToolStripDropDownMenu dd, Font f, int padH, int padV,
                                   int sepV, float s, int arrowRoom, int iconRoom)
        {
            IosRenderer r = dd.Renderer as IosRenderer;
            if (r != null) { r.S = s; r.PadH = padH; r.PadR = padH; r.IconRoom = iconRoom; }

            foreach (ToolStripItem it in dd.Items)
            {
                if (it is ToolStripSeparator)
                {
                    it.Margin = new Padding(0, sepV, 0, sepV);
                    it.AutoSize = true;
                    continue;
                }
                it.Font = f;
                it.Margin = new Padding(0);
                ToolStripMenuItem mi = it as ToolStripMenuItem;
                bool hasSub = mi != null && mi.HasDropDownItems;
                it.Padding = new Padding(padH + iconRoom, padV, padH + (hasSub ? arrowRoom : 0), padV);
                it.AutoSize = true;
            }

            int w = 0;
            foreach (ToolStripItem it in dd.Items)
            {
                if (it is ToolStripSeparator) continue;
                Size ps = it.GetPreferredSize(Size.Empty);
                if (ps.Width > w) w = ps.Width;
            }
            foreach (ToolStripItem it in dd.Items)
            {
                if (it is ToolStripSeparator) continue;
                Size ps = it.GetPreferredSize(Size.Empty);
                it.AutoSize = false;
                it.Size = new Size(w, ps.Height);
                ToolStripMenuItem mi = it as ToolStripMenuItem;
                ToolStripDropDownMenu sub = (mi == null ? null : mi.DropDown) as ToolStripDropDownMenu;
                if (sub != null && sub.Items.Count > 0) StyleDropDown(sub, f, padH, padV, sepV, s, arrowRoom, iconRoom);
            }
            dd.MinimumSize = new Size(w + Px(2 * s), 0);
        }
    }

    internal static class DshMenu
    {
        /// <summary>诊断用：菜单事件写进启动器日志（默认什么都不做）。</summary>
        public static Action<string> Trace = delegate { };
        /// <summary>开合菜单这类高频调试日志默认关，launcher.ini 里写 verbose=1 打开。</summary>
        public static bool Verbose;
        /// <summary>造一个和主菜单同一套视觉的下拉菜单（辅助功能 / 模块分组共用）。</summary>
        internal static ToolStripDropDownMenu NewDropDown(MenuSpec spec)
        {
            ToolStripDropDownMenu dd = new ToolStripDropDownMenu();
            dd.ShowImageMargin = false;   // 同主菜单：不要左侧那条 25px 白边
            dd.ShowCheckMargin = false;
            dd.ShowItemToolTips = false;
            dd.BackColor = Ios.Bg;
            dd.Padding = new Padding(0);
            dd.Renderer = new IosRenderer();   // 复用主菜单的自绘，下拉菜单才不会冒出系统灰框
            dd.DropShadowEnabled = Environment.OSVersion.Version.Build < 22000;
            // 下拉菜单是独立窗口，圆角/描边要自己再套一次
            dd.Opened += delegate { IosMenu.StylePopup(dd, spec.Scale); };
            return dd;
        }

        /// <summary>构建 iOS 风格托盘菜单。</summary>
        public static MenuSpec Create(Action onOpen, Action onRestart, Action onStop,
                                      Action onToggleAuto, Action onLog, Action onQuit,
                                      Action onQuitAll = null)
        {
            MenuSpec spec = new MenuSpec();
            IosMenu m = new IosMenu();
            spec.Menu = m;

            spec.Status = new ToolStripMenuItem("状态：检查中…");
            spec.Status.Enabled = false;
            spec.Status.Tag = "status";
            spec.Open = new ToolStripMenuItem("打开 DSH 界面", null, delegate { onOpen(); });
            spec.Open.Tag = "accent";
            spec.Restart = new ToolStripMenuItem("重启服务", null, delegate { onRestart(); });
            spec.Stop = new ToolStripMenuItem("停止服务", null, delegate { onStop(); });
            spec.Stop.Tag = "danger";
            spec.Auto = new ToolStripMenuItem("开机自动启动", null, delegate { onToggleAuto(); });
            spec.Log = new ToolStripMenuItem("打开日志", null, delegate { onLog(); });
            spec.Quit = new ToolStripMenuItem("退出启动器", null, delegate { onQuit(); });
            // 「停服务 + 退启动器」一条走完：给不想让 dsh web 继续占端口的人用
            spec.QuitAll = new ToolStripMenuItem("退出启动器并停止服务", null,
                delegate { if (onQuitAll != null) onQuitAll(); });
            spec.QuitAll.Tag = "danger";

            // ── 辅助功能（模块化二级菜单）──────────────────────────────────
            // 内容不写死在代码里：MenuSpec.RefreshAux 每次弹出前扫一遍模块目录，
            // 谁想加功能，丢个 .bat/.cmd/.exe/.ps1 进去就行，不用重新编译。
            spec.Aux = new ToolStripMenuItem("辅助功能");
            spec.AuxMenu = NewDropDown(spec);
            spec.Aux.DropDown = spec.AuxMenu;   // 挂上去之后 HasDropDownItems 才为真，箭头也才会画

            m.Items.Add(spec.Status);
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(spec.Open);
            m.Items.Add(spec.Restart);
            m.Items.Add(spec.Stop);
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(spec.Auto);
            m.Items.Add(spec.Aux);   // 按约定紧跟「开机自动启动」
            m.Items.Add(spec.Log);
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(spec.Quit);
            m.Items.Add(spec.QuitAll);

            // 顶部/底部透明占位：ToolStripDropDownMenu 会把 Padding 吃掉（实测加上去尺寸不变），
            // 只能靠一个空条目把「菜单边框到胶囊」的距离撑出来，否则最后一条贴着底边。
            spec.SpacerTop = new ToolStripMenuItem("");
            spec.SpacerTop.Tag = "spacer";
            spec.SpacerBottom = new ToolStripMenuItem("");
            spec.SpacerBottom.Tag = "spacer";
            m.Items.Insert(0, spec.SpacerTop);
            m.Items.Add(spec.SpacerBottom);

            // 每次弹出前按鼠标所在显示器重算尺寸：多屏、不同缩放比都跟着走
            m.Opening += delegate
            {
                try
                {
                    // 先按模块目录重建「辅助功能」，再统一缩放：
                    // 丢个脚本进目录，下次右键就能看到，不用重启启动器。
                    spec.RefreshAux();
                    spec.ApplyIcons();      // 图标目录里丢了新 PNG，下次右键就生效
                    if (spec.AutoScaleOnOpen) spec.ApplyScale(Dpi.CursorScale());
                }
                catch (Exception ex) { DshMenu.Trace("菜单缩放失败：" + ex.Message); }
            };
            spec.ApplyScale(1f);
            return spec;
        }
    }
}
