#:property TargetFramework=net10.0-windows
#:property UseWPF=true
#:property OutputType=Exe
#:property PublishAot=false
#:property PublishTrimmed=false

// Erzeugt Gui/Assets/ConanModCheck.ico (16, 24, 32, 48, 64, 256 Pixel) aus der Logo-Zeichnung.
// Aufruf aus dem Projektordner:  dotnet run --file tools/MakeIcon.cs
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

var output = Path.GetFullPath(Path.Combine("Gui", "Assets", "ConanModCheck.ico"));
Directory.CreateDirectory(Path.GetDirectoryName(output)!);

var thread = new Thread(() =>
{
    int[] sizes = [16, 24, 32, 48, 64, 256];
    var images = sizes.Select(Render).ToList();
    using var fs = File.Create(output);
    using var w = new BinaryWriter(fs);
    // ICO-Kopf, dann je Bild ein Verzeichniseintrag, dann die PNG-Daten
    w.Write((short)0); w.Write((short)1); w.Write((short)images.Count);
    int offset = 6 + 16 * images.Count;
    for (int i = 0; i < images.Count; i++)
    {
        int s = sizes[i];
        w.Write((byte)(s >= 256 ? 0 : s)); w.Write((byte)(s >= 256 ? 0 : s));
        w.Write((byte)0); w.Write((byte)0);
        w.Write((short)1); w.Write((short)32);
        w.Write(images[i].Length); w.Write(offset);
        offset += images[i].Length;
    }
    foreach (var img in images) w.Write(img);
});
thread.SetApartmentState(ApartmentState.STA);
thread.Start();
thread.Join();
Console.WriteLine($"Icon geschrieben: {output}");

// Logo „Mod-Puzzleteil“ im 100er-Raster, auf die Zielgröße skaliert
static byte[] Render(int size)
{
    var visual = new DrawingVisual();
    using (var dc = visual.RenderOpen())
    {
        dc.PushTransform(new ScaleTransform(size / 100.0, size / 100.0));
        var dark = new SolidColorBrush(Color.FromRgb(0x3A, 0x2E, 0x28));
        var piece = new SolidColorBrush(Color.FromRgb(0xD9, 0xB2, 0x6F));
        dc.DrawRoundedRectangle(dark, null, new Rect(0, 0, 100, 100), 18, 18);
        dc.DrawRoundedRectangle(piece, null, new Rect(26, 32, 44, 44), 4, 4);
        dc.DrawEllipse(piece, null, new Point(48, 32), 10, 10);
        dc.DrawEllipse(piece, null, new Point(70, 54), 10, 10);
        var check = new StreamGeometry();
        using (var g = check.Open())
        {
            g.BeginFigure(new Point(35, 55), false, false);
            g.PolyLineTo([new Point(45, 65), new Point(61, 44)], true, true);
        }
        dc.DrawGeometry(null, new Pen(dark, 7) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }, check);
    }
    var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
    bmp.Render(visual);
    var enc = new PngBitmapEncoder();
    enc.Frames.Add(BitmapFrame.Create(bmp));
    using var ms = new MemoryStream();
    enc.Save(ms);
    return ms.ToArray();
}
