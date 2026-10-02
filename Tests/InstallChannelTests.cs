using System.IO.Pipes;
using System.Text;
using AiDesktopSetup.Core;

namespace AiDesktopSetup.Tests;

public class InstallChannelTests
{
    [Fact]
    public async Task ExplicitCancellationStillReachesTheHelper()
    {
        var name = Guid.NewGuid().ToString("N");
        using var receiver = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var sender = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        var connection = receiver.WaitForConnectionAsync();
        await sender.ConnectAsync(); await connection;
        var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var channel = new InstallChannel(sender, () => cancelled.TrySetResult(true));
        var command = Encoding.ASCII.GetBytes("cancel\n");
        await receiver.WriteAsync(command, 0, command.Length);
        await TestCompat.WithTimeout(cancelled.Task, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task DisconnectedParentDoesNotMakeCleanupThrow()
    {
        var name = Guid.NewGuid().ToString("N");
        var receiver = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var sender = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        var connection = receiver.WaitForConnectionAsync();
        await sender.ConnectAsync(); await connection;
        var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var channel = new InstallChannel(sender, () => cancelled.TrySetResult(true));
        receiver.Dispose();
        await TestCompat.WithTimeout(cancelled.Task, TimeSpan.FromSeconds(5));
        await TestCompat.WithTimeout(channel.DisposeAsync().AsTask(), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task SuccessfulResultSurvivesCleanupAndRepeatedDisposal()
    {
        var name = Guid.NewGuid().ToString("N");
        using var receiver = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var sender = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        var connection = receiver.WaitForConnectionAsync();
        await sender.ConnectAsync(); await connection;
        var channel = new InstallChannel(sender, () => { });
        using var reader = new StreamReader(receiver, Encoding.UTF8, false, 1024, true);
        var reading = reader.ReadLineAsync();
        await channel.Writer.WriteLineAsync("success");
        Assert.Equal("success", await reading);
        await TestCompat.WithTimeout(channel.DisposeAsync().AsTask(), TimeSpan.FromSeconds(5));
        await channel.DisposeAsync();
    }
}
