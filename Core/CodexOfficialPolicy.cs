using AiDesktopSetup.Core.Protocol;
using AiDesktopSetup.Core.Workflow;
namespace AiDesktopSetup.Core;
public enum InstallationAction { Skip, Install, Register, Unsupported }
public sealed record InstalledPackage(string IdentityName,string Publisher,string FamilyName,Version Version,string Architecture,bool RegisteredForCurrentUser);
public sealed record MachineState(bool IsWindows,Version OsVersion,string OsArchitecture,string ProcessArchitecture,string CurrentUserSid,InstalledPackage? InstalledPackage=null);
public sealed class OfficialPackagePolicy
{
    public string IdentityName => "OpenAI.Codex";
    public string Publisher => "CN=50BDFD77-8903-4850-9FFE-6E8522F64D5B";
    public string FamilyName => "OpenAI.Codex_2p2nqsd0c76g0";
    public string Architecture { get; }
    public Version MinimumVersion => new(26,928,3736,0);
    public long MaximumPackageBytes => 2L*1024*1024*1024;
    public long MaximumLicenseBytes => 1024*1024;
    public bool RequiresLicense => true;
    internal OfficialPackagePolicy(string architecture) { Architecture=architecture; }
}
public sealed record PackageArtifact(string Url,long? Bytes=null,string? Sha256=null,string? Version=null);
public sealed record InstallationPlan(InstallationAction Action,OfficialPackagePolicy Policy,PackageArtifact? Package,PackageArtifact? License,bool IsMirror);
public static class CodexOfficialPolicy
{
    public static SessionAdapterPolicy SessionCapabilities { get; }=new(new[]{"none","minimal","low","medium","high","xhigh"},true);
    public static OfficialPackagePolicy ForArchitecture(string architecture) => architecture is "x64" or "arm64" ? new(architecture) : throw new SetupException("Unsupported Windows architecture.");
    public static InstallationPlan Resolve(ValidatedSession session, MachineState machine, PackageSource source = PackageSource.Session)
    {
        if (session is null || machine is null) throw new ArgumentNullException();
        var architecture = machine.OsArchitecture;
        var supported = IsSupported(machine);
        var policy = ForArchitecture(architecture is "x64" or "arm64" ? architecture : "x64");
        if (!supported) return new(InstallationAction.Unsupported, policy, null, null, false);

        if (session.Snapshot.Client.ReasoningEffort is { } effort
            && !SessionCapabilities.SupportedReasoningEfforts.Contains(effort, StringComparer.Ordinal))
            throw new SetupException("The client reasoning setting is unsupported by this adapter.");

        // Recheck the local adapter boundary even if a consumer supplied a different parser policy.
        foreach (var mirror in session.Snapshot.Mirrors)
        {
            if (mirror.AppId != "codex-desktop" || !TryVersion(mirror.Version, out var version)
                || version < policy.MinimumVersion || mirror.License == null
                || mirror.Package.Bytes > policy.MaximumPackageBytes || mirror.License.Bytes > policy.MaximumLicenseBytes)
                throw new SetupException("The mirror package/license pair is unsupported.");
        }

        if (source is not (PackageSource.Session or PackageSource.Official))
            throw new ArgumentOutOfRangeException(nameof(source));
        var selected = source == PackageSource.Official
            ? null : session.Snapshot.Mirrors.SingleOrDefault(m => m.Architecture == architecture);
        var official = Official(policy);
        var package = selected == null ? official.Package
            : new PackageArtifact(selected.Package.Url, selected.Package.Bytes, selected.Package.Sha256, selected.Version);
        var license = selected == null ? official.License
            : new PackageArtifact(selected.License!.Url, selected.License.Bytes, selected.License.Sha256);
        var required = selected == null ? policy.MinimumVersion : Version.Parse(selected.Version);
        var installed = machine.InstalledPackage;
        if (Matches(installed, policy) && installed!.Version >= required)
            return new(installed.RegisteredForCurrentUser ? InstallationAction.Skip : InstallationAction.Register,
                policy, null, null, false);
        return new(InstallationAction.Install, policy, package, license, selected != null);
    }
    public static InstallationPlan Official(OfficialPackagePolicy policy) => new(InstallationAction.Install,policy,
        new($"https://persistent.oaistatic.com/codex-app-prod/ChatGPT-{policy.Architecture}.msix"),
        new("https://persistent.oaistatic.com/codex-app-prod/ChatGPT-License.xml"),false);
    internal static bool IsSupported(MachineState machine) => machine.IsWindows && machine.OsArchitecture is "x64" or "arm64"
        && machine.OsVersion >= (machine.OsArchitecture == "arm64" ? new Version(10,0,22621,0) : new Version(10,0,19041,0));
    internal static bool Matches(InstalledPackage? installed,OfficialPackagePolicy policy) => installed != null
        && installed.IdentityName == policy.IdentityName && installed.Publisher == policy.Publisher
        && installed.FamilyName == policy.FamilyName && installed.Architecture == policy.Architecture;
    internal static bool TryVersion(string value,out Version version)
    {
        version=null!;
        return System.Text.RegularExpressions.Regex.IsMatch(value,@"\A[0-9]+(\.[0-9]+){3}\z") && Version.TryParse(value,out version!)
            && version.Major <= 65535 && version.Minor <= 65535 && version.Build <= 65535 && version.Revision <= 65535;
    }
}
