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
            Color c;
            if (!e.Item.Enabled) c = Ios.Disabled;
            else if ((e.Item.Tag as string) == "danger") c = Ios.Danger;
            else if ((e.Item.Tag as string) == "accent") c = Ios.Accent;
            else c = Ios.Text;

            int left = PadH + (isStatus ? DotRoom : 0);
            int right = PadR + (e.Item is ToolStripMenuItem && ((ToolStripMenuItem)e.Item).Checked ? Px(18f, S) : 0);
            int width = Math.Max(8, e.Item.Width - left - right);
            Rectangle box = new Rectangle(left, 0, width, e.Item.Height);

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
        }

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
            DshMenu.Trace("菜单已打开（缩放 " + S.ToString("0.00") + "，尺寸 " + Width + "x" + Height + "）");
            bool dwmOk = false;
            try
            {
                int pref = 2;   // DWMWCP_ROUND
                dwmOk = DwmSetWindowAttribute(Handle, 33 /*DWMWA_WINDOW_CORNER_PREFERENCE*/, ref pref, sizeof(int)) == 0;
                // 极浅的描边，让菜单在深色壁纸上也有边界感（同色 = 不画）
                int color = ColorTranslator.ToWin32(Ios.Border) | unchecked((int)0xFF000000);
                DwmSetWindowAttribute(Handle, 35 /*DWMWA_BORDER_COLOR*/, ref color, sizeof(int));
            }
            catch { }

            if (!dwmOk || Environment.OSVersion.Version.Build < 22000)
            {
                try
                {
                    using (GraphicsPath p = IosRenderer.Round(new Rectangle(0, 0, Width, Height), Radius))
                    {
                        Region old = Region;
                        Region = new Region(p);
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
        public ToolStripMenuItem Status, Open, Restart, Stop, Auto, Log, Quit;
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
            int checkRoom = Px(20 * s);
            int sepV = Px(3 * s);            // 胶囊↔分割线的距离；菜单上下边框也用同一个值，三处一致

            if (r != null) { r.PadH = padH; r.PadR = padH; r.DotRoom = dotRoom; }

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
                it.Padding = new Padding(padH, padV, padH, padV);
                it.AutoSize = true;
            }
            Status.Font = f;
            // 状态行上下等距 → 文字与圆球在自己那一条里天然居中；
            // 再让「菜单上边框」与「胶囊到分割线」用同一个 sepV，三处留白就完全一致了。
            Status.Padding = new Padding(padH + dotRoom, Px(9 * s), padH, Px(9 * s));
            Auto.Padding = new Padding(padH, padV, padH + checkRoom, padV);

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

            Menu.PerformLayout();
            Menu.Invalidate();

            if (old != null) { try { old.Dispose(); } catch { } }
        }
    }

    internal static class DshMenu
    {
        /// <summary>诊断用：菜单事件写进启动器日志（默认什么都不做）。</summary>
        public static Action<string> Trace = delegate { };
        /// <summary>构建 iOS 风格托盘菜单。</summary>
        public static MenuSpec Create(Action onOpen, Action onRestart, Action onStop,
                                      Action onToggleAuto, Action onLog, Action onQuit)
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

            m.Items.Add(spec.Status);
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(spec.Open);
            m.Items.Add(spec.Restart);
            m.Items.Add(spec.Stop);
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(spec.Auto);
            m.Items.Add(spec.Log);
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(spec.Quit);

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
                try { if (spec.AutoScaleOnOpen) spec.ApplyScale(Dpi.CursorScale()); }
                catch (Exception ex) { DshMenu.Trace("菜单缩放失败：" + ex.Message); }
            };
            spec.ApplyScale(1f);
            return spec;
        }
    }
}
