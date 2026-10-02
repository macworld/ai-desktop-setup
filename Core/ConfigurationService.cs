using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AiDesktopSetup.Core;

public sealed class SetupException(string message) : Exception(message);

public sealed class ConfigurationService
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    public Task<ConfigurationResult> ConfigureAsync(CodexConfiguration configuration, string token, string codexHome, IProgress<SetupProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (configuration is null) throw new ArgumentNullException(nameof(configuration));
        if (string.IsNullOrEmpty(token) || !Regex.IsMatch(token, "\\A[A-Za-z0-9._-]+\\z")) throw new SetupException("Token 为空或格式不正确，请重新复制完整 Token。");
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(codexHome)) throw new SetupException("Codex 配置目录不能为空。");
        string home;
        try
        {
            home = Path.GetFullPath(codexHome);
#if NETFRAMEWORK
            // Framework and native file APIs must use the same extended path,
            // including when the machine-wide long-path policy is disabled.
            home = ExtendedConfigurationPath(home);
#endif
        }
        catch { throw new SetupException("Codex 配置目录无效。"); }
        var configPath = Path.Combine(home, "config.toml"); var authPath = Path.Combine(home, "auth.json");
        AssertRegular(home, directory: true); AssertRegular(configPath); AssertRegular(authPath);
        byte[]? originalConfig; byte[]? originalAuth; string config; string auth;
        try
        {
            originalConfig = ReadOptional(configPath); originalAuth = ReadOptional(authPath);
            config = ConservativeToml.Merge(originalConfig is null ? "" : Utf8.GetString(originalConfig), configuration);
            var authObject = originalAuth is null ? new JsonObject() : JsonNode.Parse(Utf8.GetString(originalAuth).TrimStart('\uFEFF')) as JsonObject;
            if (authObject is null) throw new SetupException("auth.json 不是有效对象，配置未修改。请保留原文件并联系支持。");
            authObject["OPENAI_API_KEY"] = token; authObject["auth_mode"] = "apikey";
            auth = authObject.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
        }
        catch (SetupException) { throw; }
        catch { throw new SetupException("现有 config.toml / auth.json 无法安全读取或合并。配置未修改，请保留原文件并联系支持。"); }
        cancellationToken.ThrowIfCancellationRequested();
        var backup = Path.Combine(home, "ai-desktop-setup-backup." + Guid.NewGuid().ToString("N"));
        var writtenConfig = Utf8.GetBytes(config); var writtenAuth = Utf8.GetBytes(auth);
        var configReplaced = false; var authReplaced = false;
        try
        {
            AssertRegular(home, directory: true);
            if (!Directory.Exists(home)) CreatePrivateDirectory(home);
            CreatePrivateDirectory(backup);
            SaveBackup(backup, "config.toml", originalConfig); SaveBackup(backup, "auth.json", originalAuth);
            WritePrivateFile(Path.Combine(backup, "config.toml.new"), writtenConfig);
            WritePrivateFile(Path.Combine(backup, "auth.json.new"), writtenAuth);
            cancellationToken.ThrowIfCancellationRequested();
            // Another Codex instance may write while configuration is prepared. Do not overwrite it.
            progress?.Report(new("writing-config", "正在保存 Codex 配置…"));
            AssertUnchanged(configPath, originalConfig); AssertUnchanged(authPath, originalAuth);
            MovePrivateFile(Path.Combine(backup, "config.toml.new"), configPath); configReplaced = true;
            progress?.Report(new("writing-auth", "正在保存当前用户的凭据…"));
            AssertUnchanged(authPath, originalAuth);
            MovePrivateFile(Path.Combine(backup, "auth.json.new"), authPath); authReplaced = true;
            // Consumers may cancel after both replacements; rollback remains a two-file transaction.
            progress?.Report(new("config-written", "配置文件已保存。"));
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new ConfigurationResult(backup));
        }
        catch (Exception error)
        {
            try
            {
                if (authReplaced) Restore(authPath, originalAuth, writtenAuth, backup);
                if (configReplaced) Restore(configPath, originalConfig, writtenConfig, backup);
            }
            catch { throw new SetupException($"配置保存失败，自动恢复未完成。请完全退出 Codex，并从此备份目录手动恢复：{backup}"); }
            if (error is OperationCanceledException) throw;
            throw new SetupException("配置保存失败，已保留或恢复原文件。请检查目录权限、磁盘空间，并完全退出 Codex 后重试。");
        }
        finally
        {
            foreach (var name in new[] { "config.toml.new", "auth.json.new" })
            { try { File.Delete(Path.Combine(backup, name)); } catch { /* private backup remains recoverable */ } }
        }
    }

    private static void AssertRegular(string path, bool directory = false)
    {
        try
        {
#if NETFRAMEWORK
            // File.Exists / Directory.Exists can hide dangling links. GetAttributes
            // inspects the Windows directory entry itself, including reparse points.
            FileAttributes attributes;
            try { attributes = File.GetAttributes(path); }
            catch (FileNotFoundException) { return; }
            catch (DirectoryNotFoundException) { return; }
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new SetupException("配置路径是符号链接或重解析点，请手动配置。原文件未修改。");
#else
            var item = directory ? (FileSystemInfo)new DirectoryInfo(path) : new FileInfo(path);
            if (item.LinkTarget is not null || (item.Exists && item.Attributes.HasFlag(FileAttributes.ReparsePoint))) throw new SetupException("配置路径是符号链接或重解析点，请手动配置。原文件未修改。");
#endif
            if (directory ? File.Exists(path) : Directory.Exists(path)) throw new SetupException("配置路径类型不正确，原文件未修改。");
        }
        catch (SetupException) { throw; }
        catch { throw new SetupException("无法检查配置路径，请检查目录权限后重试。"); }
    }
    private static byte[]? ReadOptional(string path)
    { AssertRegular(path); if (!File.Exists(path)) return null; if (new FileInfo(path).Length > 4194304) throw new SetupException("配置文件过大，无法安全合并。原文件未修改。"); return File.ReadAllBytes(path); }
    private static void AssertUnchanged(string path, byte[]? original)
    { var now = ReadOptional(path); if (original is null ? now is not null : now is null || !original.AsSpan().SequenceEqual(now)) throw new SetupException("配置已由其他进程更改，请完全退出 Codex 后重试。"); }
    private static void SaveBackup(string directory, string name, byte[]? content) => WritePrivateFile(Path.Combine(directory, name + (content is null ? ".absent" : "")), content ?? []);
    private static void Restore(string path, byte[]? content, byte[] written, string backup)
    {
        // Roll back only our own write; another process may have changed it since commit.
        if (content is null) { AssertUnchanged(path, written); File.Delete(path); }
        else
        {
            var staged = Path.Combine(backup, Path.GetFileName(path) + ".restore");
            WritePrivateFile(staged, content);
            AssertUnchanged(path, written);
            MovePrivateFile(staged, path);
        }
    }
    private static void MovePrivateFile(string source, string destination)
    {
#if NETFRAMEWORK
        // Match File.Move(overwrite: true): an atomic same-volume rename retains
        // the private staging file ACL. File.Replace would keep the destination
        // ACL and could expose credentials that started with broad permissions.
        if (!MoveFileEx(source, destination, 0x1))
            throw new IOException("Unable to replace the configuration file.", new Win32Exception(Marshal.GetLastWin32Error()));
#else
        File.Move(source, destination, true);
#endif
    }
#if NETFRAMEWORK
    private static string ExtendedConfigurationPath(string fullyQualifiedPath)
    {
        if (fullyQualifiedPath.StartsWith(@"\\?\", StringComparison.Ordinal)
            || fullyQualifiedPath.StartsWith(@"\\.\", StringComparison.Ordinal)) return fullyQualifiedPath;
        return fullyQualifiedPath.StartsWith(@"\\", StringComparison.Ordinal)
            ? @"\\?\UNC\" + fullyQualifiedPath.Substring(2)
            : @"\\?\" + fullyQualifiedPath;
    }

    [DllImport("kernel32.dll", EntryPoint = "MoveFileExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(string existingFileName, string newFileName, uint flags);
#endif
    private static void CreatePrivateDirectory(string path)
    {
        if (RuntimeCompat.IsWindows)
        {
            var security = new DirectorySecurity(); security.SetAccessRuleProtection(true, false);
            var sid = WindowsIdentity.GetCurrent().User ?? throw new SetupException("无法识别当前 Windows 用户。");
            security.SetOwner(sid);
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(path).Create(security);
        }
#if !NETFRAMEWORK
        else Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
#else
        else throw new PlatformNotSupportedException("The .NET Framework installer requires Windows.");
#endif
    }
    private static void WritePrivateFile(string path, byte[] bytes)
    {
        // Windows files inherit the private staging directory ACL before any bytes are written.
#if NETFRAMEWORK
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
#else
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using var stream = new FileStream(path, options);
#endif
        stream.Write(bytes, 0, bytes.Length); stream.Flush(true);
    }
}
