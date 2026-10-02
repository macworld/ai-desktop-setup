namespace AiDesktopSetup.Core;

public sealed record SetupProgress(string Stage, string Message, double? Percent = null,
    string? Source = null, double? BytesPerSecond = null, double? RemainingSeconds = null)
{
    public string GetTransferText()
    {
        if (BytesPerSecond is not double speed || (double.IsNaN(speed) || double.IsInfinity(speed)) || speed < 0)
            return "速度和剩余时间正在计算…";
        if (speed == 0) return "0 KB/s · 等待网络响应…";
        var rate = speed >= 1048576 ? $"{speed / 1048576:0.0} MB/s" : $"{speed / 1024:0.0} KB/s";
        if (RemainingSeconds is not double remaining || (double.IsNaN(remaining) || double.IsInfinity(remaining)) || remaining < 0)
            return $"{rate} · 剩余时间未知";
        // Round up: an estimate should not promise less than the measured duration.
        var eta = remaining >= 3600 ? $"约 {Math.Ceiling(remaining / 3600):0} 小时"
            : remaining >= 60 ? $"约 {Math.Ceiling(remaining / 60):0} 分钟"
            : $"约 {Math.Max(1, Math.Ceiling(remaining)):0} 秒";
        return $"{rate} · 预计剩余 {eta}";
    }
}
public sealed record InstallState(bool Installed, bool NeedsSignOut = false, bool NeedsRestart = false);
public sealed record ConfigurationResult(string BackupDirectory);
