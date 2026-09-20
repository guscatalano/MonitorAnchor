#:property TargetFramework=net10.0-windows
#:property UseWindowsForms=true
#:property Nullable=enable
#:property PublishAot=false
#:property PublishTrimmed=false
// Generates assets/icon.ico (16..256, PNG-compressed) and assets/icon.png (256) for Monitor Anchor:
// a blue monitor with a white anchor on the screen. Run from the repo root: dotnet run tools/make-icon.cs
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

string root = args.Length > 0 ? args[0] : Directory.GetCurrentDirectory();
string assets = Path.Combine(root, "assets");
Directory.CreateDirectory(assets);

int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 128, 256 };
var pngs = new List<(int Size, byte[] Data)>();
foreach (int size in sizes)
{
    using var bmp = Render(size);
    using var ms = new MemoryStream();
    bmp.Save(ms, ImageFormat.Png);
    pngs.Add((size, ms.ToArray()));
    if (size == 256) File.WriteAllBytes(Path.Combine(assets, "icon.png"), ms.ToArray());
}
WriteIco(Path.Combine(assets, "icon.ico"), pngs);
Console.WriteLine($"wrote {Path.Combine(assets, "icon.ico")} and icon.png");

static Bitmap Render(int s)
{
    var bmp = new Bitmap(s, s, PixelFormat.Format32bppArgb);
    using var g = Graphics.FromImage(bmp);
    g.SmoothingMode = SmoothingMode.AntiAlias;
    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
    g.Clear(Color.Transparent);

    float u = s / 32f; // design units: the icon is laid out on a 32x32 grid
    var screenFill = new SolidBrush(Color.FromArgb(0x1E, 0x6F, 0xD9));
    var screenFillDark = new SolidBrush(Color.FromArgb(0x15, 0x4F, 0xA3));
    var bezel = new SolidBrush(Color.FromArgb(0x22, 0x2A, 0x36));
    var white = Color.White;

    // Bezel (rounded) and screen.
    var bezelRect = new RectangleF(1 * u, 2 * u, 30 * u, 21 * u);
    FillRounded(g, bezel, bezelRect, 2.5f * u);
    var screen = new RectangleF(2.5f * u, 3.5f * u, 27 * u, 18 * u);
    using (var grad = new LinearGradientBrush(screen, screenFill.Color, screenFillDark.Color, 90f))
        FillRounded(g, grad, screen, 1.5f * u);

    // Stand: neck + base.
    g.FillRectangle(bezel, 14 * u, 23 * u, 4 * u, 3 * u);
    FillRounded(g, bezel, new RectangleF(9 * u, 26 * u, 14 * u, 3 * u), 1.2f * u);

    // Anchor, centred on the screen. Stroke width scales but never drops below 1px.
    float stroke = Math.Max(1f, 2.2f * u);
    using var pen = new Pen(white, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
    float cx = 16 * u;

    // Ring at the top (radius big enough that the stroke leaves a visible hole).
    float ringR = 2.1f * u, ringTop = 5.0f * u;
    using (var ringPen = new Pen(white, Math.Max(1f, 1.8f * u)))
        g.DrawEllipse(ringPen, cx - ringR, ringTop, ringR * 2, ringR * 2);
    // Shaft.
    float shaftTop = ringTop + ringR * 2, shaftBottom = 19.3f * u;
    g.DrawLine(pen, cx, shaftTop, cx, shaftBottom);
    // Crossbar (stock).
    float stockY = 11.4f * u;
    g.DrawLine(pen, cx - 4.8f * u, stockY, cx + 4.8f * u, stockY);
    // Arms: the lower part of an ellipse whose bottom touches the shaft end.
    float ax = 6.6f * u, ay = 5.6f * u, acy = shaftBottom - ay;
    g.DrawArc(pen, cx - ax, acy - ay, ax * 2, ay * 2, 25f, 130f);
    // Flukes: arrowheads at the arm tips pointing up and outward.
    if (s >= 24)
    {
        using var fill = new SolidBrush(white);
        double a = 25 * Math.PI / 180;
        float tx = ax * (float)Math.Cos(a), ty = acy + ay * (float)Math.Sin(a);
        float f = 2.6f * u;
        foreach (int dir in new[] { 1, -1 })
        {
            float x = cx + dir * tx;
            g.FillPolygon(fill, new[]
            {
                new PointF(x + dir * f * 0.9f, ty - f * 1.0f),   // apex, up and outward
                new PointF(x - dir * f * 0.7f, ty - f * 0.1f),   // inner base
                new PointF(x + dir * f * 0.2f, ty + f * 0.8f),   // outer base
            });
        }
    }
    return bmp;
}

static void FillRounded(Graphics g, Brush b, RectangleF r, float radius)
{
    using var path = new GraphicsPath();
    float d = radius * 2;
    path.AddArc(r.X, r.Y, d, d, 180, 90);
    path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
    path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
    path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
    path.CloseFigure();
    g.FillPath(b, path);
}

static void WriteIco(string path, List<(int Size, byte[] Data)> images)
{
    using var fs = File.Create(path);
    using var w = new BinaryWriter(fs);
    w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)images.Count);
    int offset = 6 + 16 * images.Count;
    foreach (var (size, data) in images)
    {
        w.Write((byte)(size >= 256 ? 0 : size));
        w.Write((byte)(size >= 256 ? 0 : size));
        w.Write((byte)0); w.Write((byte)0);
        w.Write((ushort)1); w.Write((ushort)32);
        w.Write(data.Length); w.Write(offset);
        offset += data.Length;
    }
    foreach (var (_, data) in images) w.Write(data);
}
