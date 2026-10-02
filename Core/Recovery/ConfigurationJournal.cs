using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using AiDesktopSetup.Core.Protocol;

namespace AiDesktopSetup.Core.Recovery;
public enum ConfigurationRecoveryState { None, Prepared, Committed, Conflict }
public sealed record ConfigurationFileIntent(string TargetPath, string? OriginalSha256, string NewSha256, string BackupPath, string StagedPath);
public sealed record ConfigurationIntent(string BackupDirectory, IReadOnlyList<ConfigurationFileIntent> Files);

/// <summary>Private, secret-free intent; the durable stage files are private and reclaimed on commit/abandon.</summary>
public sealed class ConfigurationJournal
{
    private readonly string root;
    private readonly ResumeId resumeId;
    public string StagingDirectory { get; }
    private string IntentPath => Path.Combine(StagingDirectory,"intent.json");
    public ConfigurationJournal(string root, ResumeId resumeId)
    { this.root = Path.GetFullPath(root); this.resumeId = resumeId; StagingDirectory = Path.Combine(this.root,resumeId.Value.ToString("N")); }
    /// <summary>Coordinator serializes per-ID work. Deleted/expired IDs must never be resurrected.
    /// Check C2 liveness outside journal locks; a failed check preserves all data.</summary>
    public static void CleanupOrphans(string root, Func<ResumeId,bool> hasLiveRecord)
    {
        if (hasLiveRecord == null) throw new ArgumentNullException(nameof(hasLiveRecord));
        root = Path.GetFullPath(root); ResumeFileSecurity.EnsureDirectory(root);
        List<ResumeId> ids = new();
        using (ResumeFileSecurity.PinDirectories(root))
        using (ResumeFileSecurity.OpenLock(Path.Combine(root,"configuration.lock")))
        {
            foreach(var directory in Directory.EnumerateDirectories(root))
            {
                var name = Path.GetFileName(directory);
                if (!Regex.IsMatch(name,@"\A[a-f0-9]{32}\z") || !Guid.TryParseExact(name,"N",out var id) || id == Guid.Empty) continue;
                ResumeFileSecurity.ValidatePrivate(directory,true); ids.Add(new(id));
            }
        }
        // Resolve the complete snapshot first: any lookup failure means no deletion.
        var dead = ids.Where(id=>!hasLiveRecord(id)).ToArray();
        foreach(var id in dead) new ConfigurationJournal(root,id).Abandon();
    }
    public void Prepare(ConfigurationIntent intent) => Execute(() => { PrepareCore(intent); return 0; }, reclaim: false);
    public ConfigurationRecoveryState Inspect() => Execute(InspectCore);
    public void MarkCommitted() => Execute(() => { MarkCommittedCore(); return 0; });
    public ConfigurationResult? Recover() => Execute(RecoverCore);
    /// <summary>Use on explicit end/cancel, acknowledged completion, and expired-record cleanup. Never deletes user targets or backups.</summary>
    public void Abandon() => Execute(() => { CleanupOwned(null); if (File.Exists(IntentPath)) { ResumeFileSecurity.ValidatePrivate(IntentPath,false); File.Delete(IntentPath); } return 0; }, reclaim: false);
    internal T Execute<T>(Func<T> action, bool reclaim = true)
    {
        ResumeFileSecurity.EnsureDirectory(root);
        using var pins = ResumeFileSecurity.PinDirectories(root);
        using var gate = ResumeFileSecurity.OpenLock(Path.Combine(root,"configuration.lock"));
        ResumeFileSecurity.EnsureDirectory(StagingDirectory);
        using var stagePins = ResumeFileSecurity.PinDirectories(StagingDirectory);
        if (reclaim) CleanupOwned(Load()?.Intent);
        return action();
    }
    internal void PrepareCore(ConfigurationIntent intent)
    {
        if (Load() != null) throw new SetupException("A configuration intent already exists.");
        Validate(intent);
        using var pins = PinIntent(intent);
        foreach (var file in intent.Files)
        {
            if (Digest(file.TargetPath) != file.OriginalSha256 || PrivateDigest(file.StagedPath) != file.NewSha256 || PrivateDigest(file.BackupPath) != (file.OriginalSha256 ?? Hash([]))) throw new SetupException("Configuration changed while preparing.");
        }
        Save(new(1,resumeId.ToString(),false,intent)); CleanupOwned(intent);
    }
    internal ConfigurationRecoveryState InspectCore()
    {
        var entry = Load(); if (entry == null) return ConfigurationRecoveryState.None;
        Validate(entry.Intent);
        using var pins = PinIntent(entry.Intent);
        var unfinished = false;
        foreach(var file in entry.Intent.Files)
        {
            var digest = Digest(file.TargetPath);
            if (digest == file.NewSha256) continue;
            if (digest != file.OriginalSha256 || !File.Exists(file.StagedPath) || PrivateDigest(file.StagedPath) != file.NewSha256) return ConfigurationRecoveryState.Conflict;
            unfinished = true;
        }
        return unfinished ? ConfigurationRecoveryState.Prepared : ConfigurationRecoveryState.Committed;
    }
    internal ConfigurationResult? RecoverCore()
    {
        var entry = Load(); if (entry == null) return null;
        if (InspectCore() == ConfigurationRecoveryState.Conflict) throw Conflict(entry.Intent);
        using var pins = PinIntent(entry.Intent);
        foreach(var file in entry.Intent.Files)
        {
            if (Digest(file.TargetPath) == file.NewSha256) continue;
            var replacement = Path.Combine(StagingDirectory,Guid.NewGuid().ToString("N")+".replace");
            var bytes = ReadPrivate(file.StagedPath);
            try
            {
                if (Hash(bytes) != file.NewSha256) throw Conflict(entry.Intent);
                ResumeFileSecurity.WriteNew(replacement,bytes);
                if (Digest(file.TargetPath) != file.OriginalSha256) throw Conflict(entry.Intent);
                ConfigurationService.MovePrivateFile(replacement,file.TargetPath);
            }
            finally { Array.Clear(bytes,0,bytes.Length); if (File.Exists(replacement)) File.Delete(replacement); }
        }
        MarkCommittedCore(); return new(entry.Intent.BackupDirectory,true);
    }
    internal void MarkCommittedCore()
    {
        var entry = Load() ?? throw new SetupException("No configuration intent exists.");
        if (InspectCore() != ConfigurationRecoveryState.Committed) throw Conflict(entry.Intent);
        Save(entry with { Committed = true }); CleanupOwned(null);
    }
    internal string Stage(byte[] bytes)
    {
        var path = Path.Combine(StagingDirectory,Guid.NewGuid().ToString("N")+".stage"); ResumeFileSecurity.WriteNew(path,bytes); return path;
    }
    internal void ClearCore() { CleanupOwned(null); if (File.Exists(IntentPath)) File.Delete(IntentPath); }
    private static SetupException Conflict(ConfigurationIntent intent) => new("Configuration recovery conflicts with current files. Restore manually from the private backup: " + intent.BackupDirectory);
    private JournalEntry? Load()
    {
        if (!RuntimeCompat.PathExists(IntentPath)) return null;
        var bytes = ReadPrivate(IntentPath,1048576);
        try
        {
            _ = StrictJson.Parse(bytes,1048576);
            var entry = JsonSerializer.Deserialize<JournalEntry>(bytes);
            if (entry == null || entry.Version != 1 || entry.ResumeId != resumeId.ToString() || entry.Intent == null) throw new IOException("Configuration journal is invalid.");
            Validate(entry.Intent); return entry;
        }
        catch (JsonException) { throw new IOException("Configuration journal is invalid."); }
    }
    private void Save(JournalEntry entry)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(entry);
        if (bytes.Length > 1048576) throw new IOException("Configuration intent is too large.");
        var temporary = Path.Combine(StagingDirectory,Guid.NewGuid().ToString("N")+".tmp");
        try { ResumeFileSecurity.WriteNew(temporary,bytes); ResumeFileSecurity.AtomicReplace(temporary,IntentPath); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private void Validate(ConfigurationIntent intent)
    {
        if (intent.Files == null || intent.Files.Count != 2) throw new IOException("Configuration intent is invalid.");
        var home = Path.GetDirectoryName(intent.Files[0].TargetPath);
        if (home == null || Path.GetFileName(intent.Files[0].TargetPath) != "config.toml" || Path.GetFileName(intent.Files[1].TargetPath) != "auth.json" || Path.GetDirectoryName(intent.Files[1].TargetPath) != home || Path.GetDirectoryName(intent.BackupDirectory) != home || !Regex.IsMatch(Path.GetFileName(intent.BackupDirectory),@"\Aai-desktop-setup-backup\.[a-f0-9]{32}\z")) throw new IOException("Configuration intent is invalid.");
        foreach(var file in intent.Files)
        {
            if (string.IsNullOrEmpty(file.TargetPath) || string.IsNullOrEmpty(file.StagedPath) || string.IsNullOrEmpty(file.NewSha256) || file.TargetPath != Path.GetFullPath(file.TargetPath) || Path.GetDirectoryName(file.StagedPath) != StagingDirectory || !Regex.IsMatch(Path.GetFileName(file.StagedPath),@"\A[a-f0-9]{32}\.stage\z") || file.BackupPath != Path.Combine(intent.BackupDirectory,Path.GetFileName(file.TargetPath)+(file.OriginalSha256 == null ? ".absent" : "")) || !Regex.IsMatch(file.NewSha256,@"\A[a-f0-9]{64}\z") || (file.OriginalSha256 != null && !Regex.IsMatch(file.OriginalSha256,@"\A[a-f0-9]{64}\z"))) throw new IOException("Configuration intent is invalid.");
        }
    }
    private static IDisposable PinIntent(ConfigurationIntent intent)
    {
        ResumeFileSecurity.ValidatePrivate(intent.BackupDirectory,true);
        var targetPins = ResumeFileSecurity.PinDirectories(Path.GetDirectoryName(intent.Files[0].TargetPath)!);
        try { return new PairedPins(targetPins,ResumeFileSecurity.PinDirectories(intent.BackupDirectory)); }
        catch { targetPins.Dispose(); throw; }
    }
    private void CleanupOwned(ConfigurationIntent? retain)
    {
        var kept = retain == null ? new HashSet<string>(StringComparer.Ordinal) : new HashSet<string>(retain.Files.Select(f=>f.StagedPath),StringComparer.Ordinal);
        foreach(var path in Directory.EnumerateFiles(StagingDirectory))
        {
            var extension = Path.GetExtension(path);
            if (extension != ".stage" && extension != ".tmp" && extension != ".replace") continue;
            ResumeFileSecurity.ValidatePrivate(path,false); if (!kept.Contains(path)) File.Delete(path);
        }
    }
    private static byte[] ReadPrivate(string path, int limit = 4194304)
    { using var stream = ResumeFileSecurity.OpenRead(path); if (stream.Length > limit) throw new IOException("Configuration file is too large."); using var output = new MemoryStream(); stream.CopyTo(output); if (output.Length > limit) throw new IOException("Configuration file is too large."); return output.ToArray(); }
    private static string PrivateDigest(string path) { var bytes = ReadPrivate(path); try { return Hash(bytes); } finally { Array.Clear(bytes,0,bytes.Length); } }
    internal static string? Digest(string path) { var bytes = ConfigurationService.ReadOptional(path); if (bytes == null) return null; try { return Hash(bytes); } finally { Array.Clear(bytes,0,bytes.Length); } }
    internal static string Hash(byte[] bytes) { using var hash = SHA256.Create(); return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-","").ToLowerInvariant(); }
    private sealed record JournalEntry(int Version,string ResumeId,bool Committed,ConfigurationIntent Intent);
    private sealed class PairedPins(IDisposable first, IDisposable second) : IDisposable { public void Dispose() { second.Dispose(); first.Dispose(); } }
}
