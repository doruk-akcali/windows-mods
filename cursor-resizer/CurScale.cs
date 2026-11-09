// Shrinks the artwork inside .cur/.ani files while keeping each frame's canvas size,
// so Windows picks the same frame but draws a smaller pointer. Scaling is around the hotspot.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Text;

public static class CurScale
{
    struct Frame { public int W, H, HotX, HotY; public Bitmap Image; }

    static List<Frame> ReadCur(byte[] b)
    {
        var frames = new List<Frame>();
        int n = BitConverter.ToUInt16(b, 4);
        for (int k = 0; k < n; k++) {
            int e = 6 + 16 * k;
            int size = BitConverter.ToInt32(b, e + 8), off = BitConverter.ToInt32(b, e + 12);
            var f = new Frame { W = b[e] == 0 ? 256 : b[e], H = b[e + 1] == 0 ? 256 : b[e + 1],
                                HotX = BitConverter.ToUInt16(b, e + 4), HotY = BitConverter.ToUInt16(b, e + 6) };
            if (b[off] != 0x89) throw new Exception("frame is not PNG");
            using (var ms = new MemoryStream(b, off, size)) f.Image = new Bitmap(new Bitmap(ms));
            frames.Add(f);
        }
        return frames;
    }

    public static byte[] ScaleCur(byte[] input, double factor)
    {
        var frames = ReadCur(input);
        Frame src = frames[0];
        foreach (var f in frames) if (f.W > src.W) src = f; // Sharpest source
        var pngs = new List<byte[]>();
        foreach (var f in frames) {
            double k = factor * f.W / src.W;
            // The directory size is the artwork's nominal size; the PNG canvas can be larger
            using (var canvas = new Bitmap(f.Image.Width, f.Image.Height, PixelFormat.Format32bppArgb))
            using (var g = Graphics.FromImage(canvas)) {
                g.Clear(Color.Transparent);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.SmoothingMode = SmoothingMode.HighQuality;
                g.CompositingQuality = CompositingQuality.HighQuality;
                // Keep this frame's hotspot fixed; shrink the art toward it
                double hx = (src.HotX + 0.5) * k, hy = (src.HotY + 0.5) * k;
                float x = (float)(f.HotX + 0.5 - hx), y = (float)(f.HotY + 0.5 - hy);
                using (var attr = new ImageAttributes()) {
                    attr.SetWrapMode(WrapMode.TileFlipXY); // No dark fringe at the edges
                    g.DrawImage(src.Image, new RectangleF(x, y, (float)(src.Image.Width * k), (float)(src.Image.Height * k)).ToRect(),
                                0, 0, src.Image.Width, src.Image.Height, GraphicsUnit.Pixel, attr);
                }
                using (var ms = new MemoryStream()) { canvas.Save(ms, ImageFormat.Png); pngs.Add(ms.ToArray()); }
            }
        }
        using (var o = new MemoryStream())
        using (var w = new BinaryWriter(o)) {
            w.Write((ushort)0); w.Write((ushort)2); w.Write((ushort)frames.Count);
            int offset = 6 + 16 * frames.Count;
            for (int i = 0; i < frames.Count; i++) {
                var f = frames[i];
                w.Write((byte)(f.W >= 256 ? 0 : f.W)); w.Write((byte)(f.H >= 256 ? 0 : f.H)); w.Write((byte)0); w.Write((byte)0);
                w.Write((ushort)f.HotX); w.Write((ushort)f.HotY);
                w.Write(pngs[i].Length); w.Write(offset);
                offset += pngs[i].Length;
            }
            foreach (var p in pngs) w.Write(p);
            foreach (var f in frames) f.Image.Dispose();
            return o.ToArray();
        }
    }

    static Rectangle ToRect(this RectangleF r) { return Rectangle.Round(r); }

    // .ani: RIFF 'ACON' with a LIST 'fram' of 'icon' chunks (each a .cur file)
    public static byte[] ScaleAni(byte[] b, double factor)
    {
        using (var o = new MemoryStream()) {
            o.Write(b, 0, 12); // RIFF, size (fixed below), ACON
            int p = 12;
            while (p + 8 <= b.Length) {
                string id = Encoding.ASCII.GetString(b, p, 4);
                int size = BitConverter.ToInt32(b, p + 4);
                if (id == "LIST" && Encoding.ASCII.GetString(b, p + 8, 4) == "fram") {
                    using (var list = new MemoryStream()) {
                        list.Write(b, p + 8, 4);
                        int q = p + 12, end = p + 8 + size;
                        while (q + 8 <= end) {
                            string cid = Encoding.ASCII.GetString(b, q, 4);
                            int csize = BitConverter.ToInt32(b, q + 4);
                            byte[] data = new byte[csize];
                            Array.Copy(b, q + 8, data, 0, csize);
                            if (cid == "icon") data = ScaleCur(data, factor);
                            list.Write(Encoding.ASCII.GetBytes(cid), 0, 4);
                            list.Write(BitConverter.GetBytes(data.Length), 0, 4);
                            list.Write(data, 0, data.Length);
                            if ((data.Length & 1) == 1) list.WriteByte(0);
                            q += 8 + csize + (csize & 1);
                        }
                        byte[] l = list.ToArray();
                        o.Write(Encoding.ASCII.GetBytes("LIST"), 0, 4);
                        o.Write(BitConverter.GetBytes(l.Length), 0, 4);
                        o.Write(l, 0, l.Length);
                    }
                } else {
                    o.Write(b, p, 8 + size + (size & 1));
                }
                p += 8 + size + (size & 1);
            }
            byte[] r = o.ToArray();
            Array.Copy(BitConverter.GetBytes(r.Length - 8), 0, r, 4, 4);
            return r;
        }
    }
}
