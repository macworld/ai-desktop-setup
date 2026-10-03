using System.Diagnostics;
using System.Security.Cryptography;
#if NETFRAMEWORK
using System.ComponentModel;
using System.Runtime.InteropServices;
#endif

namespace AiDesktopSetup.Tests;

// Keep the same fixtures and assertions on the Windows Framework and Mac test runtimes.
internal static class TestCompat
{
    internal static Task WriteAllTextAsync(string path, string text)
    {
#if NETFRAMEWORK
        return WriteTextAsync();
        async Task WriteTextAsync()
        {
            using var writer = new StreamWriter(path);
            await writer.WriteAsync(text);
        }
#else
        return File.WriteAllTextAsync(path, text);
#endif
    }

    internal static Task<string> ReadAllTextAsync(string path)
    {
#if NETFRAMEWORK
        return ReadTextAsync();
        async Task<string> ReadTextAsync()
        {
            using var reader = new StreamReader(path);
            return await reader.ReadToEndAsync();
        }
#else
        return File.ReadAllTextAsync(path);
#endif
    }

    internal static Task<byte[]> ReadAllBytesAsync(string path)
    {
#if NETFRAMEWORK
        return ReadBytesAsync();
        async Task<byte[]> ReadBytesAsync()
        {
            using var source = File.OpenRead(path);
            using var destination = new MemoryStream();
            await source.CopyToAsync(destination);
            return destination.ToArray();
        }
#else
        return File.ReadAllBytesAsync(path);
#endif
    }

    internal static string Sha256(byte[] bytes)
    {
        using var algorithm = SHA256.Create();
        return BitConverter.ToString(algorithm.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
    }

    internal static async Task WithTimeout(Task task, TimeSpan timeout)
    {
        using var timer = new CancellationTokenSource();
        var delay = Task.Delay(timeout, timer.Token);
        if (await Task.WhenAny(task, delay) != task) throw new TimeoutException("Test operation did not complete in time.");
        timer.Cancel();
        await task;
    }

    internal static void CreateFileSymbolicLink(string linkPath, string targetPath)
    {
#if NETFRAMEWORK
        // Windows 10 permits non-admin links with Developer Mode. Older systems require elevation.
        if (CreateSymbolicLink(linkPath, targetPath, 2)) return;
        var error = Marshal.GetLastWin32Error();
        if (error == 87)
        {
            if (CreateSymbolicLink(linkPath, targetPath, 0)) return;
            error = Marshal.GetLastWin32Error();
        }
        throw new Win32Exception(error, "Cannot create the symbolic-link test fixture; run elevated or enable Developer Mode.");
#else
        File.CreateSymbolicLink(linkPath, targetPath);
#endif
    }

    internal static string[] GetArguments(ProcessStartInfo start)
    {
#if NETFRAMEWORK
        // Parse with Windows itself so escaping tests do not repeat the production quoting code.
        var parsed = CommandLineToArgv("helper.exe " + start.Arguments, out var count);
        if (parsed == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            return Enumerable.Range(1, count - 1)
                .Select(index => Marshal.PtrToStringUni(Marshal.ReadIntPtr(parsed, index * IntPtr.Size))!)
                .ToArray();
        }
        finally { LocalFree(parsed); }
#else
        return start.ArgumentList.ToArray();
#endif
    }

#if NETFRAMEWORK
    [DllImport("kernel32.dll", EntryPoint = "CreateSymbolicLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool CreateSymbolicLink(string symbolicLink, string target, int flags);

    [DllImport("shell32.dll", EntryPoint = "CommandLineToArgvW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgv(string commandLine, out int argumentCount);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
#endif
}

#if !NETFRAMEWORK
// An actual child process terminates without unwinding; recovery tests read durable files after restart.
internal static class ConfigurationCrashProcess
{
    public static void Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "agsp-interoperability") { Environment.Exit(AiDesktopSetup.Tests.Protocol.InteropLiveProcess.Run()); return; }
        if (args.Length != 4 || args[0] != "configuration-crash") Environment.Exit(74);
        var journal = new AiDesktopSetup.Core.Recovery.ConfigurationJournal(Path.Combine(args[1],"journal"),new AiDesktopSetup.Core.Protocol.ResumeId(Guid.Parse(args[2])));
        var service = new AiDesktopSetup.Core.ConfigurationService(journal);
        service.ConfigureAsync(new("https://gateway.example/Tenant/v1/","example-model",null),new AiDesktopSetup.Core.Protocol.ApiCredential("FAKE_worker_key~+/=="),Path.Combine(args[1],"home"),new TerminateAt(args[3])).GetAwaiter().GetResult();
        Environment.Exit(75);
    }
    private sealed class TerminateAt(string stage) : IProgress<AiDesktopSetup.Core.SetupProgress>
    { public void Report(AiDesktopSetup.Core.SetupProgress progress) { if (progress.Stage == stage) Environment.Exit(73); } }
}
#endif
