// MenuPreview.cs — 只用来「看菜单长什么样」的小工具，不属于启动器本体。
// 编译（和主程序共用 DshMenu.cs，保证看到的就是真菜单）：
//   csc /nologo /codepage:65001 /target:winexe /platform:anycpu ^
//       /win32manifest:app.manifest /out:tools\MenuPreview.exe ^
//       /r:System.Windows.Forms.dll /r:System.Drawing.dll ^
//       DshMenu.cs tools\MenuPreview.cs
// 用法：MenuPreview.exe [缩放倍数] [run|start|stop|err] [hover] [输出 png 路径]
//   · 不带缩放倍数就用当前显示器 DPI
//   · 加 hover 会把最后一条置为选中，方便看胶囊和边框的距离
//   · 给了 png 路径就自己截一张图再退出（背景是一块纯色渐变，不会拍到桌面）
//   · 不给路径就只把菜单显示 12 秒，人眼核对
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using DshLauncher;

internal static class PreviewProgram
{
    [STAThread]
    private static void Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        string state = "run";
        bool hover = false;
        string outPath = null;
        float scale = -1f;
        foreach (string a in args)
        {
            string t = (a ?? "").Trim();
            if (t.Length == 0) continue;
            if (t == "hover") { hover = true; continue; }
            if (t == "run" || t == "start" || t == "stop" || t == "err") { state = t; continue; }
            if (t.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) { outPath = t; continue; }
            float v;
            if (float.TryParse(t, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out v) && v > 0.2f && v < 5f)
                scale = v;
        }

        MenuSpec spec = DshMenu.Create(
            delegate { }, delegate { }, delegate { }, delegate { }, delegate { }, delegate { });
        // 状态行文字 + 左边彩色球（真实程序里由启动器按运行状态更新）
        switch (state)
        {
            case "start": spec.SetStatus("状态：启动中", Ios.Orange); break;
            case "stop": spec.SetStatus("状态：已停止", Ios.Gray); break;
            case "err": spec.SetStatus("状态：出错", Ios.Red); break;
            default: spec.SetStatus("状态：运行中", Ios.Green); break;
        }
        spec.Auto.Checked = !(state == "start" || state == "err");

        float s = scale > 0f ? scale : Dpi.CursorScale();
        spec.ApplyScale(s);
        // 显式指定缩放时，别再被「鼠标所在显示器」覆盖（否则预览永远等于 100%，白折腾）
        if (scale > 0f) spec.AutoScaleOnOpen = false;
        Console.WriteLine("scale=" + s.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)
            + " menu=" + spec.Menu.Size.Width + "x" + spec.Menu.Size.Height);
        Console.Out.Flush();

        // 背景：一块纯色渐变的窗体，保证截图里不会出现桌面上别的东西
        Form back = new Form();
        back.FormBorderStyle = FormBorderStyle.None;
        back.StartPosition = FormStartPosition.Manual;
        back.Bounds = new Rectangle(120, 120, 520, 680);
        back.BackColor = Color.FromArgb(0x4D, 0x6B, 0xFE);
        back.Paint += delegate (object o, PaintEventArgs e)
        {
            Rectangle r = back.ClientRectangle;
            if (r.Width <= 0 || r.Height <= 0) return;
            using (LinearGradientBrush lg = new LinearGradientBrush(
                new Rectangle(0, 0, r.Width + 1, r.Height + 1),
                Color.FromArgb(0x6E, 0x86, 0xFF), Color.FromArgb(0x33, 0x49, 0xCF), 60f))
                e.Graphics.FillRectangle(lg, r);
            // 右上角一团柔光，看起来不呆板
            using (GraphicsPath gp = new GraphicsPath())
            {
                gp.AddEllipse(r.Width - r.Width / 2, -r.Height / 3, r.Width, r.Height);
                using (PathGradientBrush pg = new PathGradientBrush(gp))
                {
                    pg.CenterColor = Color.FromArgb(70, 255, 255, 255);
                    pg.SurroundColors = new Color[] { Color.FromArgb(0, 255, 255, 255) };
                    e.Graphics.FillPath(pg, gp);
                }
            }
        };
        back.Show();
        Application.DoEvents();

        Point at = new Point(back.Left + 60, back.Top + 60);
        spec.Menu.Show(at);

        Timer hv = new Timer();
        hv.Interval = 400;
        hv.Tick += delegate { hv.Stop(); if (hover) { try { spec.Quit.Select(); } catch { } } };
        hv.Start();

        if (outPath == null)
        {
            // 只显示 12 秒，方便截图后自己关掉
            Timer t = new Timer();
            t.Interval = 12000;
            t.Tick += delegate { t.Stop(); Application.Exit(); };
            t.Start();
            Application.Run();
            return;
        }

        // 自截：等菜单画完，把背景窗体那块区域抓下来存盘，然后退出
        Timer shot = new Timer();
        shot.Interval = 700;
        shot.Tick += delegate
        {
            shot.Stop();
            try
            {
                Rectangle b = back.Bounds;
                using (Bitmap bmp = new Bitmap(b.Width, b.Height))
                {
                    using (Graphics g = Graphics.FromImage(bmp))
                        g.CopyFromScreen(b.Left, b.Top, 0, 0, b.Size);
                    bmp.Save(outPath, System.Drawing.Imaging.ImageFormat.Png);
                }
                Console.WriteLine("saved=" + outPath);
                Console.Out.Flush();
            }
            catch (Exception ex)
            {
                Console.WriteLine("capture failed: " + ex.Message);
            }
            Application.Exit();
        };
        shot.Start();
        Application.Run();
    }
}
