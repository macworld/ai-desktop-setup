using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace AiDesktopSetup.Core;

/// <summary>Small runtime API bridge; no change to installer policy or user workflow.</summary>
public static class RuntimeCompat
{
    internal static bool PathExists(string path)
    {
        try { File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    [SupportedOSPlatformGuard("windows")]
    public static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    public static bool IsWindowsVersionAtLeast(int major, int minor = 0, int build = 0) =>
        IsWindows && Environment.OSVersion.Version >= new Version(major, minor, build);
    public static string? ProcessPath
    {
        get
        {
#if NETFRAMEWORK
            using var current = Process.GetCurrentProcess();
            return current.MainModule?.FileName;
#else
            return Environment.ProcessPath;
#endif
        }
    }

    public static Architecture OsArchitecture
    {
        get
        {
            if (!IsWindows) return RuntimeInformation.OSArchitecture;
            using var process = Process.GetCurrentProcess();
            // NativeMachine remains the real OS architecture when this helper is emulated.
            if (!IsWow64Process2(process.Handle, out _, out var machine))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            return machine switch { 0x8664 => Architecture.X64, 0xaa64 => Architecture.Arm64, 0x014c => Architecture.X86, 0x01c4 => Architecture.Arm, _ => throw new PlatformNotSupportedException() };
        }
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWow64Process2(IntPtr process, out ushort processMachine, out ushort nativeMachine);

    public static void AddArgument(ProcessStartInfo start, string argument)
    {
#if NETFRAMEWORK
        start.Arguments += (start.Arguments.Length == 0 ? "" : " ") + QuoteArgument(argument);
#else
        start.ArgumentList.Add(argument);
#endif
    }

    internal static string QuoteArgument(string value)
    {
        // Windows command line quoting: escape quotes and preceding/trailing backslashes.
        if (value.IndexOf('\0') >= 0) throw new ArgumentException("NUL in argument");
        // Match ArgumentList: plain switches and identifiers do not need quotes.
        // Some Windows tools parse those switches before normal argv decoding.
        if (value.Length > 0 && !value.Any(ch => char.IsWhiteSpace(ch) || ch == '"')) return value;
        var result = new StringBuilder("\""); var slashes = 0;
        foreach (var ch in value)
        {
            if (ch == '\\') { slashes++; continue; }
            result.Append('\\', ch == '"' ? slashes * 2 + 1 : slashes);
            result.Append(ch); slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }

    public static async Task WaitForExitAsync(Process process, CancellationToken token = default)
    {
#if NETFRAMEWORK
        token.ThrowIfCancellationRequested();
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler exited = (_, _) => completion.TrySetResult(true);
        process.Exited += exited;
        try
        {
            process.EnableRaisingEvents = true;
            if (process.HasExited) return;
            using (token.Register(() => completion.TrySetCanceled()))
                await completion.Task.ConfigureAwait(false);
        }
        finally { process.Exited -= exited; }
#else
        await process.WaitForExitAsync(token).ConfigureAwait(false);
#endif
    }

    public static async Task<Stream> ReadAsStreamAsync(HttpContent content, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
#if NETFRAMEWORK
        using (token.Register(() => { try { content.Dispose(); } catch { } }))
        {
            try { var stream = await content.ReadAsStreamAsync().ConfigureAwait(false); token.ThrowIfCancellationRequested(); return stream; }
            catch (Exception) when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
        }
#else
        return await content.ReadAsStreamAsync(token).ConfigureAwait(false);
#endif
    }

    public static async Task<int> ReadAsync(Stream stream, byte[] buffer, int offset, int count, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
#if NETFRAMEWORK
        // Framework HTTP streams may ignore CancellationToken during a pending read.
        using (token.Register(() => { try { stream.Dispose(); } catch { } }))
        {
            try { var read = await stream.ReadAsync(buffer, offset, count, token).ConfigureAwait(false); token.ThrowIfCancellationRequested(); return read; }
            catch (Exception) when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
        }
#else
        return await stream.ReadAsync(buffer, offset, count, token).ConfigureAwait(false);
#endif
    }

    internal static string Hex(byte[] bytes) => BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
    internal static async Task<byte[]> HashAsync(Stream stream, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[131072]; int read;
        while ((read = await ReadAsync(stream, buffer, 0, buffer.Length, token).ConfigureAwait(false)) > 0) hash.AppendData(buffer, 0, read);
        return hash.GetHashAndReset();
    }
}
