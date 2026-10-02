using AiDesktopSetup.Core.Protocol;
namespace AiDesktopSetup.Core.Recovery;
public interface IClock { DateTimeOffset UtcNow { get; } }
public interface IRandomSource { byte[] GetBytes(int length); }
public interface IResumeStore
{
    ClaimRecord GetOrCreate(SetupCode code); ResumeRecord? Load(ResumeId id); void Save(ResumeRecord record); void Delete(ResumeId id);
}
internal interface IResumeProtection { byte[] Protect(byte[] value); byte[] Unprotect(byte[] value); }
public sealed class CryptoRandomSource : IRandomSource { public byte[] GetBytes(int length) { var bytes = new byte[length]; using var rng = System.Security.Cryptography.RandomNumberGenerator.Create(); rng.GetBytes(bytes); return bytes; } }
public sealed class SystemClock : IClock { public DateTimeOffset UtcNow => DateTimeOffset.UtcNow; }
