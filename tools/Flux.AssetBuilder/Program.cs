using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

if (args.Length != 3)
{
    Console.Error.WriteLine("Usage: Flux.AssetBuilder <transparent.png> <black.png> <assets-directory>");
    return 1;
}

var transparentPath = Path.GetFullPath(args[0]);
var blackPath = Path.GetFullPath(args[1]);
var outputDirectory = Path.GetFullPath(args[2]);
Directory.CreateDirectory(outputDirectory);
Directory.CreateDirectory(Path.Combine(outputDirectory, "Source"));

var transparent = Load(transparentPath);
var black = Load(blackPath);
var transparentBounds = FindBounds(transparent, transparentMode: true);
var blackBounds = FindBounds(black, transparentMode: false);

var mark = RenderSquare(new CroppedBitmap(transparent, transparentBounds), 512, Colors.Transparent, 0.88);
SavePng(mark, Path.Combine(outputDirectory, "FluxMark.png"));

var iconSizes = new[] { 256, 128, 64, 48, 32, 24, 16 };
var blackCrop = new CroppedBitmap(black, blackBounds);
var iconPngs = iconSizes
    .Select(size => EncodePng(RenderSquare(blackCrop, size, Colors.Black, 0.84)))
    .ToArray();
WriteIco(Path.Combine(outputDirectory, "Flux.ico"), iconSizes, iconPngs);

File.Copy(transparentPath, Path.Combine(outputDirectory, "Source", "fluxv3T.png"), true);
File.Copy(blackPath, Path.Combine(outputDirectory, "Source", "fluxv3.png"), true);

Console.WriteLine($"Transparent content bounds: {transparentBounds}");
Console.WriteLine($"Black content bounds: {blackBounds}");
Console.WriteLine($"Wrote {Path.Combine(outputDirectory, "FluxMark.png")}");
Console.WriteLine($"Wrote {Path.Combine(outputDirectory, "Flux.ico")} with {iconSizes.Length} sizes");
return 0;

static BitmapSource Load(string path)
{
    using var stream = File.OpenRead(path);
    var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
    var converted = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgra32, null, 0);
    converted.Freeze();
    return converted;
}

static Int32Rect FindBounds(BitmapSource source, bool transparentMode)
{
    var stride = source.PixelWidth * 4;
    var pixels = new byte[stride * source.PixelHeight];
    source.CopyPixels(pixels, stride, 0);
    var minX = source.PixelWidth;
    var minY = source.PixelHeight;
    var maxX = -1;
    var maxY = -1;

    for (var y = 0; y < source.PixelHeight; y++)
    {
        var row = y * stride;
        for (var x = 0; x < source.PixelWidth; x++)
        {
            var offset = row + x * 4;
            var blue = pixels[offset];
            var green = pixels[offset + 1];
            var red = pixels[offset + 2];
            var alpha = pixels[offset + 3];
            var visible = transparentMode
                ? alpha > 20
                : alpha > 20 && red + green + blue > 90;
            if (!visible)
            {
                continue;
            }

            minX = Math.Min(minX, x);
            minY = Math.Min(minY, y);
            maxX = Math.Max(maxX, x);
            maxY = Math.Max(maxY, y);
        }
    }

    if (maxX < minX || maxY < minY)
    {
        throw new InvalidOperationException("The logo source contains no visible pixels.");
    }

    var width = maxX - minX + 1;
    var height = maxY - minY + 1;
    var padding = (int)Math.Ceiling(Math.Max(width, height) * 0.08);
    minX = Math.Max(0, minX - padding);
    minY = Math.Max(0, minY - padding);
    maxX = Math.Min(source.PixelWidth - 1, maxX + padding);
    maxY = Math.Min(source.PixelHeight - 1, maxY + padding);
    return new Int32Rect(minX, minY, maxX - minX + 1, maxY - minY + 1);
}

static BitmapSource RenderSquare(BitmapSource source, int size, Color background, double occupancy)
{
    var visual = new DrawingVisual();
    using (var drawing = visual.RenderOpen())
    {
        if (background.A > 0)
        {
            drawing.DrawRectangle(new SolidColorBrush(background), null, new Rect(0, 0, size, size));
        }

        var maximum = size * occupancy;
        var scale = Math.Min(maximum / source.PixelWidth, maximum / source.PixelHeight);
        var width = source.PixelWidth * scale;
        var height = source.PixelHeight * scale;
        drawing.DrawImage(source, new Rect((size - width) / 2, (size - height) / 2, width, height));
    }

    var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
    bitmap.Render(visual);
    bitmap.Freeze();
    return bitmap;
}

static void SavePng(BitmapSource source, string path) => File.WriteAllBytes(path, EncodePng(source));

static byte[] EncodePng(BitmapSource source)
{
    var encoder = new PngBitmapEncoder();
    encoder.Frames.Add(BitmapFrame.Create(source));
    using var stream = new MemoryStream();
    encoder.Save(stream);
    return stream.ToArray();
}

static void WriteIco(string path, IReadOnlyList<int> sizes, IReadOnlyList<byte[]> images)
{
    using var stream = File.Create(path);
    using var writer = new BinaryWriter(stream);
    writer.Write((ushort)0);
    writer.Write((ushort)1);
    writer.Write((ushort)sizes.Count);

    var offset = 6 + sizes.Count * 16;
    for (var index = 0; index < sizes.Count; index++)
    {
        writer.Write((byte)(sizes[index] == 256 ? 0 : sizes[index]));
        writer.Write((byte)(sizes[index] == 256 ? 0 : sizes[index]));
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write((ushort)1);
        writer.Write((ushort)32);
        writer.Write((uint)images[index].Length);
        writer.Write((uint)offset);
        offset += images[index].Length;
    }

    foreach (var image in images)
    {
        writer.Write(image);
    }
}
