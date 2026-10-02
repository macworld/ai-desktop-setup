using AiDesktopSetup.Core;
using Xunit;

namespace AiDesktopSetup.Tests;

public sealed class DownloadProgressTests
{
    private const long MB = 1024 * 1024;

    [Fact]
    public void SpeedAndEtaReflectRecentBytesRatherThanWholeDownloadAverage()
    {
        var tracker = new DownloadProgressTracker(new Uri("https://gateway.example/install/downloads/app.msix"), 100 * MB);
        tracker.Sample(2 * MB, TimeSpan.FromSeconds(1));
        tracker.Sample(4 * MB, TimeSpan.FromSeconds(2));
        tracker.Sample(6 * MB, TimeSpan.FromSeconds(3));
        tracker.Sample(8 * MB, TimeSpan.FromSeconds(4));
        tracker.Sample(10 * MB, TimeSpan.FromSeconds(5));
        // The next five seconds run at 1 MB/s, half the initial rate.
        SetupProgress progress = null!;
        for (var second = 6; second <= 10; second++)
            progress = tracker.Sample((second + 5) * MB, TimeSpan.FromSeconds(second));
        Assert.Equal((double)MB, progress.BytesPerSecond);
        Assert.Equal(85d, progress.RemainingSeconds);
        Assert.Equal(15d, progress.Percent);
        Assert.Contains("约 2 分钟", progress.GetTransferText());
    }

    [Fact]
    public void InitialSampleDoesNotInventAnInstantaneousSpeed()
    {
        var tracker = new DownloadProgressTracker(new Uri("https://gateway.example/install/downloads/app.msix"), 10 * MB);
        var progress = tracker.Sample(MB, TimeSpan.FromMilliseconds(100));
        Assert.Null(progress.BytesPerSecond);
        Assert.Null(progress.RemainingSeconds);
        Assert.Contains("计算", progress.GetTransferText());
    }

    [Fact]
    public void StalledDownloadClearsEtaAndRecoversWhenDataResumes()
    {
        var tracker = new DownloadProgressTracker(new Uri("https://gateway.example/install/downloads/app.msix"), 10 * MB);
        tracker.Sample(MB, TimeSpan.FromSeconds(1));
        var stopped = tracker.Sample(MB, TimeSpan.FromSeconds(4));
        Assert.Equal(0d, stopped.BytesPerSecond);
        Assert.Null(stopped.RemainingSeconds);
        Assert.Contains("等待", stopped.GetTransferText());
        var resumed = tracker.Sample(2 * MB, TimeSpan.FromSeconds(5));
        Assert.True(resumed.BytesPerSecond > 0);
        Assert.True(resumed.RemainingSeconds > 0);
    }

    [Fact]
    public void UnknownSizeReportsSpeedWithoutInventingAnEta()
    {
        var tracker = new DownloadProgressTracker(new Uri("https://persistent.oaistatic.com/codex-app-prod/ChatGPT-x64.msix"), null);
        var progress = tracker.Sample(MB, TimeSpan.FromSeconds(2));
        Assert.Equal(524288d, progress.BytesPerSecond);
        Assert.Null(progress.Percent);
        Assert.Null(progress.RemainingSeconds);
        Assert.Contains("persistent.oaistatic.com", progress.Source);
        Assert.Contains("未知", progress.GetTransferText());
    }

    [Fact]
    public void DownloadTelemetrySurvivesHelperSerialization()
    {
        var progress = new SetupProgress("download", "正在下载", 50, "下载源", MB, 12);
        var restored = System.Text.Json.JsonSerializer.Deserialize<SetupProgress>(System.Text.Json.JsonSerializer.Serialize(progress));
        Assert.Equal(progress, restored);
        Assert.Contains("12 秒", restored!.GetTransferText());
    }

    [Fact]
    public void RetriedBytesAffectSpeedWithoutInflatingCompletion()
    {
        var tracker = new DownloadProgressTracker(new Uri("https://gateway.example/install/downloads/app.msix"), 10 * MB);
        tracker.Sample(2 * MB, TimeSpan.FromSeconds(1), 2 * MB);
        var restarted = tracker.Sample(MB, TimeSpan.FromSeconds(2), 3 * MB);
        Assert.Equal(10d, restarted.Percent);
        Assert.Equal(1.5 * MB, restarted.BytesPerSecond);
        Assert.Equal(6d, restarted.RemainingSeconds);
    }

    [Theory]
    [InlineData(double.NaN, double.PositiveInfinity)]
    [InlineData(double.PositiveInfinity, -1)]
    public void InvalidMeasurementsDoNotProduceBrokenNumbers(double speed, double remaining)
    {
        var text = new SetupProgress("download", "", BytesPerSecond: speed, RemainingSeconds: remaining).GetTransferText();
        Assert.DoesNotContain("NaN", text);
        Assert.DoesNotContain("Infinity", text);
        Assert.DoesNotContain("-1", text);
    }
}
