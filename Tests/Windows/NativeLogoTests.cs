using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AiDesktopSetup.Core.Protocol;
using AiDesktopSetup.Tests.Protocol;
namespace AiDesktopSetup.Tests.Windows;
public sealed class NativeLogoTests
{
    [Theory] [InlineData(true)] [InlineData(false)]
    public async Task ProductionCallbackFullyDecodesOnBackgroundContinuation(bool png)
    {
        var bytes = Encode(png, 2, 3);
        using var clients = new ProtocolHttpClients(new Handler(bytes, png), new Handler(bytes, png), new Handler(bytes, png));
        var clock = new FixedClock(); BitmapSource? decoded = null;
        var client = new SetupSessionClient(clients, b => decoded = BoundedLogoDecoder.Decode(b), clock);
        var session = ProtocolFixtures.Validator().Validate(ProtocolFixtures.Code(), ProtocolFixtures.Session());
        var access = new SessionAccess(session, new(new byte[32]));
        var result = await Task.Run(() => client.GetLogoAsync(access, default));
        Assert.NotNull(result); Assert.NotNull(decoded); Assert.True(decoded!.IsFrozen); Assert.Equal(2, decoded.PixelWidth); Assert.Equal(3, decoded.PixelHeight);
        var pixels = new byte[24]; decoded.CopyPixels(pixels, 8, 0);
    }
    [Theory] [InlineData(true)] [InlineData(false)]
    public void RejectsOversizeAndTruncatedPixels(bool png)
    {
        Assert.ThrowsAny<Exception>(() => BoundedLogoDecoder.Decode(Encode(png, 1025, 1)));
        var bytes = Encode(png, 32, 32);
        Assert.ThrowsAny<Exception>(() => BoundedLogoDecoder.Decode(bytes.Take(png ? 33 : 20).ToArray()));
        Assert.ThrowsAny<Exception>(() => BoundedLogoDecoder.Decode(new byte[524289]));
    }
    private static byte[] Encode(bool png, int width, int height)
    {
        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, new byte[width * height * 4], width * 4);
        BitmapEncoder encoder = png ? new PngBitmapEncoder() : new JpegBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream(); encoder.Save(stream); return stream.ToArray();
    }
    private sealed class Handler(byte[] bytes, bool png) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { await Task.Yield(); var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }; response.Headers.CacheControl = new() { NoStore = true }; response.Content.Headers.ContentType = new(png ? "image/png" : "image/jpeg"); return response; }
    }
}
