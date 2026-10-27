using System.Text;

using SkiaSharp;

namespace WhalePod.Tests.Integration;

internal static class TestImages
{
    public static byte[] Png(int width = 16, int height = 8, SKColor? color = null)
    {
        return Encode(width, height, SKEncodedImageFormat.Png, color ?? SKColors.CornflowerBlue);
    }

    public static byte[] Jpeg(int width = 16, int height = 8)
    {
        return Encode(width, height, SKEncodedImageFormat.Jpeg, SKColors.OrangeRed);
    }

    public static byte[] Webp(int width = 16, int height = 8)
    {
        return Encode(width, height, SKEncodedImageFormat.Webp, SKColors.MediumSeaGreen);
    }

    public static byte[] JpegWithOrientation(int width, int height, SKEncodedOrigin orientation)
    {
        var jpeg = Jpeg(width, height);
        var payload = new List<byte>();
        payload.AddRange("Exif\0\0"u8.ToArray());
        payload.AddRange([0x49, 0x49, 0x2A, 0x00, 0x08, 0x00, 0x00, 0x00]);
        payload.AddRange([0x01, 0x00]);
        payload.AddRange([0x12, 0x01, 0x03, 0x00]);
        payload.AddRange([0x01, 0x00, 0x00, 0x00]);
        payload.AddRange([(byte)orientation, 0x00, 0x00, 0x00]);
        payload.AddRange([0x00, 0x00, 0x00, 0x00]);

        var segmentLength = payload.Count + 2;
        var segment = new byte[] { 0xFF, 0xE1, (byte)(segmentLength >> 8), (byte)segmentLength }
            .Concat(payload)
            .ToArray();
        var result = new byte[jpeg.Length + segment.Length];
        result[0] = jpeg[0];
        result[1] = jpeg[1];
        segment.CopyTo(result, 2);
        jpeg.AsSpan(2).CopyTo(result.AsSpan(2 + segment.Length));
        return result;
    }

    public static byte[] Svg()
    {
        return Encoding.UTF8.GetBytes(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"10\" height=\"10\"><script>alert(1)</script></svg>");
    }

    public static byte[] Executable()
    {
        return Encoding.ASCII.GetBytes("MZ\u0090\u0000\u0003\u0000\u0000\u0000");
    }

    public static byte[] TinyGif()
    {
        return Convert.FromBase64String("R0lGODlhAQABAIAAAAAAAP///ywAAAAAAQABAAACAUwAOw==");
    }

    private static byte[] Encode(int width, int height, SKEncodedImageFormat format, SKColor color)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(
            width,
            height,
            SKColorType.Bgra8888,
            SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(color);
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, 95);
        return data.ToArray();
    }
}