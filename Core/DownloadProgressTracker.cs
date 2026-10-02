namespace AiDesktopSetup.Core;

internal sealed class DownloadProgressTracker(Uri source, long? total)
{
    private readonly Queue<(double Seconds, long Bytes)> samples = new([(0, 0)]);
    private long previousBytes;
    private double lastDataAt;

    internal static string SourceLabel(Uri source) => source.Host == "persistent.oaistatic.com"
        ? "官方源 · persistent.oaistatic.com" : "下载源 · " + source.Host;

    internal SetupProgress Sample(long received, TimeSpan elapsed, long? transferredBytes = null)
    {
        var seconds = elapsed.TotalSeconds;
        var transferred = transferredBytes ?? received;
        if (transferred > previousBytes) lastDataAt = seconds;
        previousBytes = transferred;
        samples.Enqueue((seconds, transferred));
        while (samples.Count > 1 && samples.Peek().Seconds < seconds - 5) samples.Dequeue();
        var first = samples.Peek();
        var interval = seconds - first.Seconds;
        double? speed = seconds >= 1 && interval > 0 ? (transferred - first.Bytes) / interval : null;
        if (seconds - lastDataAt >= 3) speed = 0;
        double? remaining = total.HasValue && speed is > 0 ? Math.Max(0, (total.Value - received) / speed.Value) : null;
        var title = source.AbsolutePath.EndsWith(".xml", StringComparison.Ordinal) ? "正在下载许可证" : "正在下载";
        return new("download", $"{title}：{received / 1048576d:F1} MB" + (total.HasValue ? $" / {total.Value / 1048576d:F1} MB" : ""),
            total.HasValue ? received * 100d / total.Value : null, SourceLabel(source), speed, remaining);
    }
}
