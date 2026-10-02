using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiDesktopSetup.Core.Protocol;
using AiDesktopSetup.Core.Recovery;

namespace AiDesktopSetup.Core;

public sealed class SetupException(string message) : Exception(message);

public sealed class ConfigurationService
{
    private readonly ConfigurationJournal journal;
    public ConfigurationService(ConfigurationJournal journal) => this.journal = journal ?? throw new ArgumentNullException(nameof(journal));
    private static readonly UTF8Encoding Utf8 = new(false, true);
    public Task<ConfigurationResult> ConfigureAsync(CodexConfiguration configuration, ApiCredential credential, string codexHome, IProgress<SetupProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (configuration is null) throw new ArgumentNullException(nameof(configuration));
        if (credential is null) throw new ArgumentNullException(nameof(credential));
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(codexHome)) throw new SetupException("Codex 配置目录不能为空。");
        string home;
        try
        {
            home = Path.GetFullPath(codexHome);
#if NETFRAMEWORK
            home = ExtendedConfigurationPath(home);
#endif
            ResumeFileSecurity.CheckAncestors(home);
        }
        catch { throw new SetupException("Codex 配置目录无效。"); }
        return Task.FromResult(journal.Execute(() =>
        {
            if (journal.InspectCore() != ConfigurationRecoveryState.None) return journal.RecoverCore()!;
            return Configure(configuration,credential,home,progress,cancellationToken);
        }));
    }
    private ConfigurationResult Configure(CodexConfiguration configuration, ApiCredential credential, string home, IProgress<SetupProgress>? progress, CancellationToken cancellationToken)
    {
        var configPath = Path.Combine(home,"config.toml"); var authPath = Path.Combine(home,"auth.json");
        AssertRegular(home,true); AssertRegular(configPath); AssertRegular(authPath);
        byte[]? originalConfig = null; byte[]? originalAuth = null; byte[]? writtenConfig = null; byte[]? writtenAuth = null;
        try
        {
            string config; string auth;
            try
            {
                originalConfig = ReadOptional(configPath); originalAuth = ReadOptional(authPath);
                config = ConservativeToml.Merge(originalConfig == null ? "" : Utf8.GetString(originalConfig),configuration);
                // Duplicate JSON keys are ambiguous, including nested objects; do not silently discard them.
                var authObject = originalAuth == null ? new JsonObject() : JsonNode.Parse(StrictJson.Parse(Utf8.GetBytes(Utf8.GetString(originalAuth).TrimStart('\uFEFF')),4194304).GetRawText()) as JsonObject;
                if (authObject == null) throw new SetupException("auth.json 不是有效对象，配置未修改。请保留原文件并联系支持。");
                authObject["OPENAI_API_KEY"] = credential.Value; authObject["auth_mode"] = "apikey";
                auth = authObject.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
            }
            catch (SetupException) { throw; }
            catch { throw new SetupException("现有 config.toml / auth.json 无法安全读取或合并。配置未修改，请保留原文件并联系支持。"); }
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(home)) CreatePrivateDirectory(home);
            using var pins = ResumeFileSecurity.PinDirectories(home);
            var backup = Path.Combine(home,"ai-desktop-setup-backup."+Guid.NewGuid().ToString("N")); CreatePrivateDirectory(backup);
            SaveBackup(backup,"config.toml",originalConfig); SaveBackup(backup,"auth.json",originalAuth);
            writtenConfig = Utf8.GetBytes(config); writtenAuth = Utf8.GetBytes(auth);
            var configReplaced = false; var authReplaced = false;
            try
            {
                var configStage = journal.Stage(writtenConfig); var authStage = journal.Stage(writtenAuth);
                var intent = new ConfigurationIntent(backup,new[] {
                    new ConfigurationFileIntent(configPath,originalConfig == null ? null : ConfigurationJournal.Hash(originalConfig),ConfigurationJournal.Hash(writtenConfig),Path.Combine(backup,"config.toml"+(originalConfig == null ? ".absent" : "")),configStage),
                    new ConfigurationFileIntent(authPath,originalAuth == null ? null : ConfigurationJournal.Hash(originalAuth),ConfigurationJournal.Hash(writtenAuth),Path.Combine(backup,"auth.json"+(originalAuth == null ? ".absent" : "")),authStage) });
                progress?.Report(new("config-before-prepare","Preparing configuration recovery."));
                cancellationToken.ThrowIfCancellationRequested(); journal.PrepareCore(intent);
                progress?.Report(new("config-prepared","Configuration recovery saved."));
                progress?.Report(new("writing-config","正在保存 Codex 配置…"));
                AssertUnchanged(configPath,originalConfig); AssertUnchanged(authPath,originalAuth);
                ReplaceFromStage(configStage,configPath,writtenConfig); configReplaced = true;
                progress?.Report(new("config-replaced","Configuration saved."));
                progress?.Report(new("writing-auth","正在保存当前用户的凭据…"));
                AssertUnchanged(authPath,originalAuth); ReplaceFromStage(authStage,authPath,writtenAuth); authReplaced = true;
                progress?.Report(new("auth-replaced","Credentials saved."));
                progress?.Report(new("config-written","配置文件已保存。"));
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new("config-before-commit","Confirming configuration recovery."));
                journal.MarkCommittedCore();
                progress?.Report(new("config-committed","Configuration committed."));
                return new(backup,true);
            }
            catch (Exception error)
            {
                try
                {
                    if (authReplaced) Restore(authPath,originalAuth,writtenAuth,backup);
                    if (configReplaced) Restore(configPath,originalConfig,writtenConfig,backup);
                    // A failed concurrent-edit check must remain detectable as Conflict.
                    if (journal.InspectCore() != ConfigurationRecoveryState.Conflict) journal.ClearCore();
                }
                catch { throw new SetupException($"配置保存失败，自动恢复未完成。请完全退出 Codex，并从此备份目录手动恢复：{backup}"); }
                if (error is OperationCanceledException) throw;
                throw new SetupException("配置保存失败，已保留或恢复原文件。请检查目录权限、磁盘空间，并完全退出 Codex 后重试。");
            }
        }
        finally
        {
            foreach(var bytes in new[] {originalConfig,originalAuth,writtenConfig,writtenAuth}) if (bytes != null) Array.Clear(bytes,0,bytes.Length);
        }
    }
    private void ReplaceFromStage(string stage,string target,byte[] bytes)
    {
        ResumeFileSecurity.ValidatePrivate(stage,false);
        if (ConfigurationJournal.Digest(stage) != ConfigurationJournal.Hash(bytes)) throw new SetupException("Configuration staging changed.");
        var replacement = Path.Combine(journal.StagingDirectory,Guid.NewGuid().ToString("N")+".replace");
        try { WritePrivateFile(replacement,bytes); MovePrivateFile(replacement,target); }
        finally { if (File.Exists(replacement)) File.Delete(replacement); }
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
    internal static byte[]? ReadOptional(string path)
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
    internal static void MovePrivateFile(string source, string destination)
    {
#if NETFRAMEWORK
        // Match File.Move(overwrite: true): an atomic same-volume rename retains
        // the private staging file ACL. File.Replace would keep the destination
        // ACL and could expose credentials that started with broad permissions.
        if (!MoveFileEx(source, destination, 0x1 | 0x8))
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
