using System.Diagnostics;
using AiDesktopSetup.Core;

namespace AiDesktopSetup.Tests;

public partial class RuntimeCompatibilityTests
{
    [Fact]
    public void WindowsArchitectureUsesTheHostRatherThanAnEmulatedProcess()
    {
        if (!RuntimeCompat.IsWindows) return;
        var expected = Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE", EnvironmentVariableTarget.Machine);
        Assert.False(string.IsNullOrEmpty(expected));
        var actual = RuntimeCompat.OsArchitecture.ToString();
        Assert.Equal(expected == "AMD64" ? "X64" : expected!.ToUpperInvariant(), actual.ToUpperInvariant());
    }

    [Fact]
    public async Task WaitingForProcessExitPreservesItsResult()
    {
        var start = RuntimeCompat.IsWindows
            ? new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"))
            : new ProcessStartInfo("/bin/sh");
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        foreach (var argument in RuntimeCompat.IsWindows ? new[] { "/d", "/c", "exit 7" } : new[] { "-c", "exit 7" })
            RuntimeCompat.AddArgument(start, argument);
        using var process = Process.Start(start)!;
        await TestCompat.WithTimeout(RuntimeCompat.WaitForExitAsync(process), TimeSpan.FromSeconds(5));
        Assert.Equal(7, process.ExitCode);
    }

    [Fact]
    public async Task CancellingAProcessWaitReturnsPromptlyWithoutTerminatingTheProcess()
    {
        var start = RuntimeCompat.IsWindows
            ? new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "ping.exe"))
            : new ProcessStartInfo("/bin/sleep");
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        start.RedirectStandardOutput = true;
        foreach (var argument in RuntimeCompat.IsWindows ? new[] { "-n", "30", "127.0.0.1" } : new[] { "30" })
            RuntimeCompat.AddArgument(start, argument);
        using var process = Process.Start(start)!;
        try
        {
            using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await TestCompat.WithTimeout(
                Assert.ThrowsAnyAsync<OperationCanceledException>(() => RuntimeCompat.WaitForExitAsync(process, cancel.Token)),
                TimeSpan.FromSeconds(5));
            Assert.False(process.HasExited);
        }
        finally
        {
            if (!process.HasExited) process.Kill();
            process.WaitForExit();
        }
    }

#if NETFRAMEWORK
    [Fact]
    public async Task FrameworkReadCancelsEvenWhenTheTransportIgnoresItsToken()
    {
        using var stream = new DisposalOnlyStream();
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await TestCompat.WithTimeout(
            Assert.ThrowsAnyAsync<OperationCanceledException>(() => RuntimeCompat.ReadAsync(stream, new byte[4], 0, 4, cancel.Token)),
            TimeSpan.FromSeconds(5));
        Assert.True(stream.WasDisposed);
    }

    [Fact]
    public async Task FrameworkContentCreationCancelsAndDisposesAnUnresponsiveResponse()
    {
        using var content = new DisposalOnlyContent();
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await TestCompat.WithTimeout(
            Assert.ThrowsAnyAsync<OperationCanceledException>(() => RuntimeCompat.ReadAsStreamAsync(content, cancel.Token)),
            TimeSpan.FromSeconds(5));
        Assert.True(content.WasDisposed);
    }

    private sealed class DisposalOnlyContent : HttpContent
    {
        private readonly TaskCompletionSource<Stream> pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool WasDisposed { get; private set; }
        protected override Task<Stream> CreateContentReadStreamAsync() => pending.Task;
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext context) => throw new NotSupportedException();
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override void Dispose(bool disposing)
        {
            WasDisposed = true;
            pending.TrySetException(new ObjectDisposedException(nameof(DisposalOnlyContent)));
            base.Dispose(disposing);
        }
    }

    private sealed class DisposalOnlyStream : Stream
    {
        private readonly TaskCompletionSource<int> pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool WasDisposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => pending.Task;
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            WasDisposed = true;
            pending.TrySetException(new ObjectDisposedException(nameof(DisposalOnlyStream)));
            base.Dispose(disposing);
        }
    }
#endif
}
