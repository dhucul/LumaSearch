$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Collections.Generic;
public static class LumaIcon
{
    public static void Create(string path)
    {
        int[] sizes = {16, 24, 32, 48, 64, 128, 256};
        var images = new List<byte[]>();
        foreach (int size in sizes)
        {
            using (var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb))
            using (var g = Graphics.FromImage(bitmap))
            using (var outline = new GraphicsPath())
            using (var folder = new GraphicsPath())
            using (var bg = new LinearGradientBrush(new Rectangle(0,0,256,256), Color.FromArgb(99,91,255), Color.FromArgb(48,37,123), 45f))
            using (var pale = new SolidBrush(Color.FromArgb(169,223,255)))
            using (var paper = new SolidBrush(Color.FromArgb(238,248,255)))
            using (var violet = new SolidBrush(Color.FromArgb(99,91,255)))
            using (var rim = new Pen(Color.White,10))
            using (var handle = new Pen(Color.FromArgb(48,37,123),19))
            using (var plus = new Pen(Color.White,7))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.ScaleTransform(size / 256f, size / 256f);
                outline.AddArc(8,8,108,108,180,90); outline.AddArc(140,8,108,108,270,90);
                outline.AddArc(140,140,108,108,0,90); outline.AddArc(8,140,108,108,90,90); outline.CloseFigure();
                g.FillPath(bg,outline);
                folder.AddPolygon(new [] {new Point(47,64),new Point(105,64),new Point(121,83),new Point(209,83),new Point(209,190),new Point(47,190)});
                g.FillPath(pale,folder); g.FillRectangle(paper,47,100,162,90);
                g.FillEllipse(violet,89,95,74,74); g.DrawEllipse(rim,89,95,74,74);
                handle.StartCap=LineCap.Round; handle.EndCap=LineCap.Round;
                g.DrawLine(handle,152,160,190,199);
                plus.StartCap=LineCap.Round; plus.EndCap=LineCap.Round;
                g.DrawLine(plus,111,132,141,132); g.DrawLine(plus,126,117,126,147);
                using (var data = new MemoryStream()) { bitmap.Save(data,ImageFormat.Png); images.Add(data.ToArray()); }
                if (size == 256) bitmap.Save(Path.ChangeExtension(path,".png"),ImageFormat.Png);
            }
        }
        using (var output = new BinaryWriter(File.Create(path)))
        {
            output.Write((ushort)0); output.Write((ushort)1); output.Write((ushort)sizes.Length);
            int offset=6 + sizes.Length*16;
            for(int i=0;i<sizes.Length;i++)
            {
                output.Write((byte)(sizes[i]==256?0:sizes[i])); output.Write((byte)(sizes[i]==256?0:sizes[i]));
                output.Write((byte)0); output.Write((byte)0); output.Write((ushort)1); output.Write((ushort)32);
                output.Write(images[i].Length); output.Write(offset); offset += images[i].Length;
            }
            foreach(var data in images) output.Write(data);
        }
    }
}
'@
[LumaIcon]::Create((Join-Path $PSScriptRoot 'LumaSearch.ico'))
