using System.Text;

namespace AiDesktopSetup.Core;

/// <summary>Owns the helper's duplex stream until its cancellation reader has stopped.</summary>
public sealed class InstallChannel(Stream pipe, Action cancel) : IAsyncDisposable
{
    public StreamWriter Writer { get; } = new(pipe, new UTF8Encoding(false), 1024, true) { AutoFlush = true, NewLine = "\n" };
    private readonly Task listener = ListenAsync(pipe, cancel);
    private int disposed;

    private static async Task ListenAsync(Stream pipe, Action cancel)
    {
        var expected = Encoding.ASCII.GetBytes("cancel\n");
        var received = new byte[expected.Length];
        var read = 0;
        try
        {
            while (read < received.Length)
            {
                var count = await pipe.ReadAsync(received, read, received.Length - read).ConfigureAwait(false);
                if (count == 0) { cancel(); return; }
                read += count;
            }
            if (received.AsSpan().SequenceEqual(expected)) cancel();
        }
        catch (IOException) { cancel(); }
        catch (ObjectDisposedException) { }
        catch (OperationCanceledException) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        // A leaveOpen StreamWriter may flush again on a second Dispose. Close it exactly
        // once while the pipe is still open, or cleanup can replace a successful result.
        try { Writer.Dispose(); } catch (IOException) { } catch (ObjectDisposedException) { }
        pipe.Dispose();
        await listener.ConfigureAwait(false);
    }
}
