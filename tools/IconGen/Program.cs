using SkiaSharp;

// Draws the JSON Converter Pro icon (nested JSON boxes flattening into table rows) and writes
// PNGs, a Windows .ico and a macOS .iconset. Usage: IconGen <outDir>
string outDir = args.Length > 0 ? args[0] : ".";
Directory.CreateDirectory(outDir);

byte[] Render(int size, double inset)
{
    using var bmp = new SKBitmap(size, size);
    using var c = new SKCanvas(bmp);
    c.Clear(SKColors.Transparent);
    float pad = (float)(size * inset);
    float s = (size - 2 * pad) / 100f;
    c.Translate(pad, pad);
    c.Scale(s);

    var bg = new SKRect(0, 0, 100, 100);
    using (var p = new SKPaint { IsAntialias = true })
    {
        p.Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(100, 100),
            new[] { SKColor.Parse("#8B6CFF"), SKColor.Parse("#4126C9") }, new[] { 0f, 1f }, SKShaderTileMode.Clamp);
        c.DrawRoundRect(bg, 22.5f, 22.5f, p);
        p.Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(0, 50),
            new[] { SKColors.White.WithAlpha(56), SKColors.White.WithAlpha(0) }, null, SKShaderTileMode.Clamp);
        c.Save(); c.ClipRoundRect(new SKRoundRect(bg, 22.5f, 22.5f)); c.DrawRect(new SKRect(0, 0, 100, 50), p); c.Restore();
    }
    SKPaint Stroke(float w, SKColor col) => new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = w, Color = col, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round };
    var white = SKColors.White; var lilac = SKColor.Parse("#C9BEFF");
    using (var st = Stroke(6, white)) c.DrawCircle(46, 46, 23.6f, st);
    using (var st = Stroke(6.8f, white)) c.DrawLine(63.5f, 63.5f, 77.5f, 77.5f, st);
    using (var st = Stroke(3.6f, white)) { var p = new SKPath(); p.MoveTo(34f, 39f); p.LineTo(54f, 39f); p.MoveTo(48.6f, 33.8f); p.LineTo(54.4f, 39f); p.LineTo(48.6f, 44.2f); c.DrawPath(p, st); }
    using (var st = Stroke(3.6f, lilac)) { var p = new SKPath(); p.MoveTo(58f, 54f); p.LineTo(38f, 54f); p.MoveTo(43.4f, 48.8f); p.LineTo(37.6f, 54f); p.LineTo(43.4f, 59.2f); c.DrawPath(p, st); }

    using var img = SKImage.FromBitmap(bmp);
    return img.Encode(SKEncodedImageFormat.Png, 100).ToArray();
}

// Full-bleed PNG for in-app branding
File.WriteAllBytes(Path.Combine(outDir, "AppIcon.png"), Render(512, 0));

// Windows .ico with PNG-compressed entries
int[] icoSizes = { 16, 24, 32, 48, 64, 128, 256 };
var images = icoSizes.Select(sz => Render(sz, 0)).ToList();
using (var fs = File.Create(Path.Combine(outDir, "AppIcon.ico")))
using (var w = new BinaryWriter(fs))
{
    w.Write((short)0); w.Write((short)1); w.Write((short)icoSizes.Length);
    int offset = 6 + 16 * icoSizes.Length;
    for (int i = 0; i < icoSizes.Length; i++)
    {
        int sz = icoSizes[i];
        w.Write((byte)(sz >= 256 ? 0 : sz)); w.Write((byte)(sz >= 256 ? 0 : sz));
        w.Write((byte)0); w.Write((byte)0); w.Write((short)1); w.Write((short)32);
        w.Write(images[i].Length); w.Write(offset);
        offset += images[i].Length;
    }
    foreach (var img in images) w.Write(img);
}

// macOS iconset: Apple's icon grid leaves ~10% transparent margin
string iconset = Path.Combine(outDir, "AppIcon.iconset");
Directory.CreateDirectory(iconset);
foreach (var (name, px) in new[] { ("icon_16x16", 16), ("icon_16x16@2x", 32), ("icon_32x32", 32), ("icon_32x32@2x", 64),
    ("icon_128x128", 128), ("icon_128x128@2x", 256), ("icon_256x256", 256), ("icon_256x256@2x", 512),
    ("icon_512x512", 512), ("icon_512x512@2x", 1024) })
    File.WriteAllBytes(Path.Combine(iconset, name + ".png"), Render(px, 0.098));
Console.WriteLine("Icons written to " + outDir);
