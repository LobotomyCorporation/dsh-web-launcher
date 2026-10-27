// 把一张 PNG 打包成多尺寸 ICO。
// 小尺寸用 DIB（未压缩）条目——System.Drawing.Icon / 老工具都认；
// 128、256 用 PNG 条目——只有它们需要 PNG 压缩，Explorer 也支持。
// 用法: MakeIco.exe <源PNG> <输出ICO>
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

internal static class MakeIco
{
    private static readonly int[] DibSizes = new int[] { 16, 20, 24, 32, 40, 48, 64 };
    private static readonly int[] PngSizes = new int[] { 128, 256 };

    private static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("用法: MakeIco <源PNG> <输出ICO>");
            return 2;
        }
        try
        {
            using (Bitmap src = new Bitmap(args[0]))
            {
                List<int> sizes = new List<int>();
                List<byte[]> blobs = new List<byte[]>();
                foreach (int s in DibSizes)
                {
                    sizes.Add(s);
                    blobs.Add(Dib(src, s));
                }
                foreach (int s in PngSizes)
                {
                    sizes.Add(s);
                    blobs.Add(Png(src, s));
                }

                using (FileStream fs = File.Create(args[1]))
                using (BinaryWriter w = new BinaryWriter(fs))
                {
                    w.Write((ushort)0);
                    w.Write((ushort)1);
                    w.Write((ushort)sizes.Count);
                    int offset = 6 + 16 * sizes.Count;
                    for (int i = 0; i < sizes.Count; i++)
                    {
                        int s = sizes[i];
                        w.Write((byte)(s >= 256 ? 0 : s));
                        w.Write((byte)(s >= 256 ? 0 : s));
                        w.Write((byte)0);
                        w.Write((byte)0);
                        w.Write((ushort)1);
                        w.Write((ushort)32);
                        w.Write((uint)blobs[i].Length);
                        w.Write((uint)offset);
                        offset += blobs[i].Length;
                    }
                    foreach (byte[] b in blobs) w.Write(b);
                }
            }
            Console.WriteLine("ok");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static Bitmap Scale(Bitmap src, int s)
    {
        Bitmap bmp = new Bitmap(s, s, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.Clear(Color.Transparent);
            g.DrawImage(src, new Rectangle(0, 0, s, s));
        }
        return bmp;
    }

    private static byte[] Png(Bitmap src, int s)
    {
        using (Bitmap bmp = Scale(src, s))
        using (MemoryStream ms = new MemoryStream())
        {
            bmp.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }
    }

    private static byte[] Dib(Bitmap src, int s)
    {
        using (Bitmap bmp = Scale(src, s))
        {
            BitmapData bd = bmp.LockBits(new Rectangle(0, 0, s, s), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            byte[] pixels = new byte[s * s * 4];
            for (int y = 0; y < s; y++)
            {
                IntPtr line = new IntPtr(bd.Scan0.ToInt64() + (long)(s - 1 - y) * bd.Stride);
                Marshal.Copy(line, pixels, y * s * 4, s * 4);
            }
            bmp.UnlockBits(bd);

            int maskRow = ((s + 31) / 32) * 4;
            byte[] mask = new byte[maskRow * s];

            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter w = new BinaryWriter(ms))
            {
                w.Write(40);          // biSize
                w.Write(s);           // biWidth
                w.Write(s * 2);       // biHeight（XOR + AND）
                w.Write((ushort)1);   // biPlanes
                w.Write((ushort)32);  // biBitCount
                w.Write(0);           // biCompression
                w.Write(pixels.Length + mask.Length);
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
}
