// 图标预览：把 DshLauncher.cs 里的 MakeIcon 直接拿来渲染四种状态，
// 输出一张深色背景的对照图（托盘实际是 24px 左右，这里同时给 24 和 48）。
// 编译：csc /target:winexe /main:IconPreview.Program /out:tools\IconPreview.exe
//       /resource:logo256.png,DshLogo.png DshLauncher.cs DshMenu.cs tools\IconPreview.cs
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Reflection;
using System.Windows.Forms;

namespace IconPreview
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            string outPath = args.Length > 0 ? args[0] : "icon-states.png";
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            MethodInfo mi = Assembly.GetExecutingAssembly()
                .GetType("DshLauncher.Launcher")
                .GetMethod("MakeIcon", BindingFlags.NonPublic | BindingFlags.Static);

            Color[] colors = new Color[]
            {
                Color.FromArgb(41, 165, 96),    // 运行中
                Color.FromArgb(226, 148, 34),  // 启动中
                Color.FromArgb(210, 66, 66),   // 出错
                Color.FromArgb(130, 138, 152)  // 未运行
            };
            string[] names = new string[] { "运行中", "启动中", "出错", "未运行" };
            int[] sizes = new int[] { 16, 24, 32, 48 };

            using (Bitmap sheet = new Bitmap(420, 300))
            using (Graphics g = Graphics.FromImage(sheet))
            {
                g.Clear(Color.FromArgb(32, 34, 38));   // 深色任务栏底色
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                using (Font f = new Font("Microsoft YaHei", 9f))
                using (SolidBrush tb = new SolidBrush(Color.FromArgb(200, 200, 200)))
                {
                    for (int i = 0; i < colors.Length; i++)
                    {
                        using (Icon ico = (Icon)mi.Invoke(null, new object[] { colors[i] }))
                        {
                            int y = 12 + i * 62;
                            g.DrawString(names[i], f, tb, 8, y + 16);
                            int x = 90;
                            foreach (int s in sizes)
                            {
                                using (Icon small = new Icon(ico, s, s))
                                using (Bitmap bmp = small.ToBitmap())
                                    g.DrawImage(bmp, x, y, s, s);
                                x += s + 14;
                            }
                            using (Icon big = new Icon(ico, 48, 48))
                            using (Bitmap bb = big.ToBitmap())
                                g.DrawImage(bb, 320, y, 48, 48);
                        }
                    }
                }
                sheet.Save(outPath, ImageFormat.Png);
            }
            Console.WriteLine("ok");
        }
    }
}
