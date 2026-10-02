using System.Text;
using System.Text.Json;
using AiDesktopSetup.Core.Protocol;

namespace AiDesktopSetup.Core.Recovery;
/// <summary>Current-user encrypted records. A claim is returned only after its atomic durable commit.
/// Local records are recovery data, never authorization; every resumed stage requires server checks.</summary>
public sealed class DpapiResumeStore : IResumeStore
{
    private const int MaximumRecordBytes = 196608;
    private readonly string root; private readonly IClock clock; private readonly IRandomSource random; private readonly IResumeProtection protection;
    public DpapiResumeStore(string root, IClock clock, IRandomSource random) : this(root, clock, random, new DpapiProtection())
    { if (!RuntimeCompat.IsWindows) throw new PlatformNotSupportedException("Resume protection requires Windows."); }
    internal DpapiResumeStore(string root, IClock clock, IRandomSource random, IResumeProtection protection)
    { this.root = Path.GetFullPath(root); this.clock = clock ?? throw new ArgumentNullException(nameof(clock)); this.random = random ?? throw new ArgumentNullException(nameof(random)); this.protection = protection; }
    public ClaimRecord GetOrCreate(SetupCode code)
    {
        using var transaction = Enter();
        foreach (var path in Directory.GetFiles(root, "*.resume"))
        {
            if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "D", out var id)) throw new IOException("Resume record is invalid.");
            var record = LoadCore(new ResumeId(id));
            if (record != null && record.Code.OriginalCode == code.OriginalCode) return record.Claim;
        }
        var now = clock.UtcNow;
        var claim = new ClaimRecord(new ResumeId(NewUuid()), NewUuid(), new ResumeSecret(random.GetBytes(32)));
        Write(new ResumeRecord(claim, code, now, now.AddHours(2), LocalStage.Created), creating: true);
        return claim;
    }
    public ResumeRecord? Load(ResumeId id) { using var transaction = Enter(); return LoadCore(id); }
    public void Save(ResumeRecord record)
    {
        using var transaction = Enter(); Validate(record);
        var original = LoadCore(record.Claim.ResumeId) ?? throw new IOException("Resume record is unavailable.");
        if (record.Code.OriginalCode != original.Code.OriginalCode || record.CreatedAt != original.CreatedAt || record.Claim.ClaimId != original.Claim.ClaimId || !record.Claim.ResumeSecret.Bytes.SequenceEqual(original.Claim.ResumeSecret.Bytes) || record.LocalStage < original.LocalStage) throw new ProtocolException();
        if (original.Snapshot != null)
        {
            if (record.Snapshot == null || record.ExpiresAt != original.ExpiresAt || record.Snapshot.State < original.Snapshot.State || !SessionBindingValidator.SameConfiguration(record.Snapshot, original.Snapshot)) throw new ProtocolException();
        }
        else if (record.Snapshot == null && record.ExpiresAt != original.ExpiresAt) throw new ProtocolException();
        Write(record);
    }
    public void Delete(ResumeId id) { using var transaction = Enter(); DeleteCore(id); }
    private string RecordPath(ResumeId id) { if (id.Value == Guid.Empty) throw new ProtocolException(); return Path.Combine(root, id.Value.ToString("D") + ".resume"); }
    private ResumeRecord? LoadCore(ResumeId id)
    {
        var path = RecordPath(id); if (!RuntimeCompat.PathExists(path)) return null;
        byte[] encrypted;
        using (var stream = ResumeFileSecurity.OpenRead(path))
        {
            if (stream.Length > MaximumRecordBytes) throw new IOException("Resume record is invalid.");
            using var buffer = new MemoryStream(); stream.CopyTo(buffer); encrypted = buffer.ToArray();
        }
        byte[]? plain = null;
        try
        {
            plain = protection.Unprotect(encrypted); var json = StrictJson.Object(StrictJson.Parse(plain, MaximumRecordBytes)); StrictJson.Version(json);
            var code = SetupCodeParser.Parse(StrictJson.String(json, "code"));
            if (!Guid.TryParseExact(StrictJson.String(json, "claim_id"), "D", out var claimId)) throw new ProtocolException();
            if (StrictJson.String(json, "resume_id") != id.Value.ToString("D")) throw new ProtocolException();
            var claim = new ClaimRecord(id, claimId, ResumeSecret.Parse(StrictJson.String(json, "resume_secret")));
            if (!Enum.TryParse<LocalStage>(StrictJson.String(json, "local_stage"), out var stage) || !Enum.IsDefined(typeof(LocalStage), stage)) throw new ProtocolException();
            var snapshot = json.TryGetProperty("snapshot", out var saved) ? SessionSnapshot.Parse(Encoding.UTF8.GetBytes(saved.GetRawText())) : null;
            var record = new ResumeRecord(claim, code, StrictJson.UtcTime(StrictJson.String(json, "created_at")), StrictJson.UtcTime(StrictJson.String(json, "expires_at")), stage, snapshot);
            Validate(record, checkExpiry: false);
            if (clock.UtcNow >= record.ExpiresAt) { DeleteCore(id); return null; }
            return record;
        }
        finally { if (plain != null) Array.Clear(plain, 0, plain.Length); }
    }
    private void Validate(ResumeRecord record, bool checkExpiry = true)
    {
        if (!Enum.IsDefined(typeof(LocalStage), record.LocalStage) || record.CreatedAt > clock.UtcNow || record.ExpiresAt <= record.CreatedAt || (checkExpiry && clock.UtcNow >= record.ExpiresAt)) throw new ProtocolException();
        if (record.Snapshot is null)
        { if (record.LocalStage != LocalStage.Created || record.ExpiresAt > record.CreatedAt.AddHours(2)) throw new ProtocolException(); }
        else
        {
            if (record.LocalStage < LocalStage.Authenticated || record.ExpiresAt != record.Snapshot.ExpiresAt || record.Snapshot.SetupBaseUrl != record.Code.SetupBaseUrl || record.Snapshot.ApiBaseUrl != record.Code.ApiBaseUrl || record.Snapshot.AppId != record.Code.AppId || (checkExpiry && record.ExpiresAt > clock.UtcNow.AddHours(2))) throw new ProtocolException();
        }
    }
    private void DeleteCore(ResumeId id)
    { var path = RecordPath(id); if (RuntimeCompat.PathExists(path)) { ResumeFileSecurity.ValidatePrivate(path, false); File.Delete(path); } }
    private void Write(ResumeRecord record, bool creating = false)
    {
        Validate(record);
        if (creating && RuntimeCompat.PathExists(RecordPath(record.Claim.ResumeId))) throw new IOException("Resume identifier collision.");
        var data = new Dictionary<string, object> { ["version"] = 1, ["resume_id"] = record.Claim.ResumeId.Value.ToString("D"), ["claim_id"] = record.Claim.ClaimId.ToString("D"), ["resume_secret"] = record.Claim.ResumeSecret.ToBearer(), ["code"] = record.Code.OriginalCode, ["created_at"] = record.CreatedAt.UtcDateTime.ToString("O"), ["expires_at"] = record.ExpiresAt.UtcDateTime.ToString("O"), ["local_stage"] = record.LocalStage.ToString() };
        if (record.Snapshot != null) data["snapshot"] = record.Snapshot.ToWire();
        var plain = JsonSerializer.SerializeToUtf8Bytes(data); byte[] encrypted;
        try { encrypted = protection.Protect(plain); } finally { Array.Clear(plain, 0, plain.Length); }
        if (encrypted.Length > MaximumRecordBytes) throw new IOException("Resume record is too large.");
        var temp = Path.Combine(root, Guid.NewGuid().ToString("N") + ".tmp");
        try { ResumeFileSecurity.WriteNew(temp, encrypted); ResumeFileSecurity.AtomicReplace(temp, RecordPath(record.Claim.ResumeId)); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private Guid NewUuid()
    { var bytes = random.GetBytes(16); if (bytes.Length != 16) throw new ProtocolException(); bytes[7] = (byte)((bytes[7] & 15) | 64); bytes[8] = (byte)((bytes[8] & 63) | 128); return new Guid(bytes); }
    private IDisposable Enter()
    {
        ResumeFileSecurity.EnsureDirectory(root); var pinned = ResumeFileSecurity.PinDirectories(root);
        try
        {
            var path = Path.Combine(root, "store.lock");
            for (var attempt = 0; ; attempt++)
            {
                try { return new Transaction(pinned, ResumeFileSecurity.OpenLock(path)); }
                catch (IOException) when (attempt < 100) { Thread.Sleep(10); }
            }
        }
        catch { pinned.Dispose(); throw; }
    }
    private sealed class Transaction : IDisposable
    { private readonly IDisposable directories, file; internal Transaction(IDisposable directories, IDisposable file) { this.directories = directories; this.file = file; } public void Dispose() { file.Dispose(); directories.Dispose(); } }
}
