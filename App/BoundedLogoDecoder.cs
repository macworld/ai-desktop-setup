using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AiDesktopSetup.Core.Protocol;
namespace AiDesktopSetup;
public static class BoundedLogoDecoder
{
    public static BitmapSource Decode(byte[] bytes)
    {
        if (!SetupSessionClient.HasBoundedLogoDimensions(bytes)) throw new InvalidDataException("Invalid logo dimensions.");
        using var input = new MemoryStream(bytes, false);
        BitmapDecoder decoder = bytes[0] == 137
            ? new PngBitmapDecoder(input, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad)
            : new JpegBitmapDecoder(input, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count != 1) throw new InvalidDataException("Invalid logo frame count.");
        var frame = decoder.Frames[0]; var width = frame.PixelWidth; var height = frame.PixelHeight;
        if (width < 1 || height < 1 || width > 1024 || height > 1024) throw new InvalidDataException("Invalid logo dimensions.");
        var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        var stride = checked(width * 4); var pixels = new byte[checked(stride * height)];
        converted.CopyPixels(pixels, stride, 0);
        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        image.Freeze(); return image;
    }
}
