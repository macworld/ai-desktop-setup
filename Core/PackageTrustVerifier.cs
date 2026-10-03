using System.Runtime.InteropServices;
using System.Security.Cryptography;
using AiDesktopSetup.Core.Recovery;
namespace AiDesktopSetup.Core;
public sealed class PackageTrustException : Exception
{
    public int NativeStatus { get; }
    public bool Retryable => NativeStatus is unchecked((int)0x80092013) or unchecked((int)0x800B010E);
    public PackageTrustException(int status) : base("Official package signature trust could not be established. Native status: 0x"+unchecked((uint)status).ToString("X8")) { NativeStatus=status; }
}
internal interface IPackageSignatureTrust { void Verify(string path,FileStream file); }
/// <summary>Owns pinned parent directories and a read-only file handle. Dispose only after the consumer finishes using these bytes.</summary>
public sealed class VerifiedPackage : IDisposable
{
    public string Path { get; } public string Sha256 { get; } public string IdentityName { get; } public Version Version { get; } public string Architecture { get; }
    internal FileStream File { get; }
    private readonly IDisposable directories;
    internal VerifiedPackage(string path,string hash,OfficialPackagePolicy policy,Version version,FileStream file,IDisposable directories)
    { Path=path;Sha256=hash;IdentityName=policy.IdentityName;Version=version;Architecture=policy.Architecture;File=file;this.directories=directories; }
    public void Dispose() { File.Dispose(); directories.Dispose(); }
}
public sealed class PackageTrustVerifier
{
    private readonly IPackageSignatureTrust signature;
    public PackageTrustVerifier() : this(new WindowsPackageSignatureTrust()) {}
    internal PackageTrustVerifier(IPackageSignatureTrust signature) { this.signature=signature; }
    /// <summary>Anonymous download only. On any mirror failure the caller must explicitly select Official(policy) and retry; no implicit fallback.</summary>
    public async Task<PreparedInstallation> PrepareAsync(AiDesktopSetup.Core.Protocol.ProtocolHttpClients clients,InstallationPlan plan,MachineState machine,string directory,IProgress<SetupProgress>? progress,CancellationToken token)
    {
        if (plan.Action!=InstallationAction.Install || plan.Package==null || plan.License==null) throw new SetupException("No complete package/license pair was selected.");
        var packagePath=System.IO.Path.Combine(directory,"package.msix");var licensePath=System.IO.Path.Combine(directory,"license.xml");
        VerifiedPackage? package=null;FileStream? license=null;
        try
        {
            await WindowsInstallerPolicy.DownloadAsync(clients,plan.Package,packagePath,plan.Policy.MaximumPackageBytes,progress,token).ConfigureAwait(false);
            await WindowsInstallerPolicy.DownloadAsync(clients,plan.License,licensePath,plan.Policy.MaximumLicenseBytes,progress,token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            package=Verify(packagePath,plan.Policy,machine);
            if (plan.Package.Version!=null && package.Version.ToString()!=plan.Package.Version) throw new SetupException("Package version differs from the selected mirror.");
            license=new FileStream(licensePath,FileMode.Open,FileAccess.Read,FileShare.Read);
            using var sha=SHA256.Create();var digest=RuntimeCompat.Hex(sha.ComputeHash(license));license.Position=0;
            return new(package,license,digest,machine.CurrentUserSid);
        }
        catch
        {
            package?.Dispose();license?.Dispose();
            foreach(var path in new[]{packagePath,licensePath}) { try { System.IO.File.Delete(path); } catch(IOException) {} }
            throw;
        }
    }
    internal async Task<PreparedInstallation> PrepareHelperCopyAsync(InstallHelperRequest request,MachineState machine,string privateDirectory,CancellationToken token)
    {
        request.Validate();
        var policy=CodexOfficialPolicy.ForArchitecture(machine.OsArchitecture); // Never a policy supplied over IPC.
        var packagePath=System.IO.Path.Combine(privateDirectory,"package.msix");var licensePath=System.IO.Path.Combine(privateDirectory,"license.xml");
        VerifiedPackage? package=null;FileStream? license=null;
        try
        {
            await CopyArtifactAsync(request.PackagePath,packagePath,policy.MaximumPackageBytes,token).ConfigureAwait(false);
            await CopyArtifactAsync(request.LicensePath!,licensePath,policy.MaximumLicenseBytes,token).ConfigureAwait(false);
            package=VerifyExpected(packagePath,policy,machine,request.PackageSha256);
            license=new FileStream(licensePath,FileMode.Open,FileAccess.Read,FileShare.Read);
            using var sha=SHA256.Create();var digest=RuntimeCompat.Hex(sha.ComputeHash(license));license.Position=0;
            if (digest!=request.LicenseSha256) throw new SetupException("The license changed after verification.");
            return new(package,license,digest,machine.CurrentUserSid);
        }
        catch { package?.Dispose();license?.Dispose();throw; }
    }
    private static async Task CopyArtifactAsync(string source,string destination,long limit,CancellationToken token)
    {
        using var parents=ResumeFileSecurity.PinDirectories(System.IO.Path.GetDirectoryName(source)!);
        if ((System.IO.File.GetAttributes(source)&FileAttributes.ReparsePoint)!=0) throw new SetupException("Artifact path is unsafe.");
        using var input=new FileStream(source,FileMode.Open,FileAccess.Read,FileShare.Read);
        if (input.Length<=0 || input.Length>limit) throw new SetupException("Artifact size is unsupported.");
        using var output=new FileStream(destination,FileMode.CreateNew,FileAccess.Write,FileShare.None);
        var buffer=new byte[131072];long received=0;int read;
        while ((read=await RuntimeCompat.ReadAsync(input,buffer,0,buffer.Length,token).ConfigureAwait(false))>0)
        { received+=read;if (received>limit) throw new SetupException("Artifact size is unsupported.");await output.WriteAsync(buffer,0,read,token).ConfigureAwait(false); }
        await output.FlushAsync(token).ConfigureAwait(false);
    }
    public VerifiedPackage Verify(string path,OfficialPackagePolicy policy,MachineState machine) => VerifyCore(path,policy,machine,null);
    public VerifiedPackage VerifyExpected(string path,OfficialPackagePolicy policy,MachineState machine,string expectedSha256) => VerifyCore(path,policy,machine,expectedSha256);
    private VerifiedPackage VerifyCore(string path,OfficialPackagePolicy policy,MachineState machine,string? expected)
    {
        var full=System.IO.Path.GetFullPath(path);
        var directories=ResumeFileSecurity.PinDirectories(System.IO.Path.GetDirectoryName(full)!);
        FileStream? file=null;
        try
        {
            if ((System.IO.File.GetAttributes(full)&FileAttributes.ReparsePoint)!=0) throw new SetupException("Package path is unsafe.");
            file=new FileStream(full,FileMode.Open,FileAccess.Read,FileShare.Read);
            if (file.Length<=0 || file.Length>policy.MaximumPackageBytes) throw new SetupException("Package size is unsupported.");
            // Package-aware native verification is mandatory. Complete-payload coverage remains a native acceptance gate; certificate/ZIP parsing is not evidence.
            signature.Verify(full,file);
            file.Position=0;
            WindowsPackageIdentity metadata;
            try { metadata=WindowsInstallerPolicy.ValidatePackage(file,policy.Architecture,machine.OsVersion,null); }
            catch (Exception error) when (error is InvalidDataException or System.Xml.XmlException or InvalidOperationException) { throw new SetupException("The signed package identity, architecture or OS requirement is unsupported."); }
            if(!CodexOfficialPolicy.TryVersion(metadata.Version,out var version)) throw new SetupException("The signed package version is outside the supported MSIX range.");
            if (version<policy.MinimumVersion || machine.OsArchitecture!=policy.Architecture || !CodexOfficialPolicy.IsSupported(machine))
                throw new SetupException("The signed package version or machine is unsupported.");
            if (CodexOfficialPolicy.Matches(machine.InstalledPackage,policy) && version<machine.InstalledPackage!.Version)
                throw new SetupException("An installed newer package cannot be downgraded.");
            file.Position=0;
            using var sha=SHA256.Create(); var digest=RuntimeCompat.Hex(sha.ComputeHash(file)); file.Position=0;
            if (expected!=null && !string.Equals(digest,expected,StringComparison.Ordinal)) throw new SetupException("The package changed after verification.");
            return new(full,digest,policy,version,file,directories);
        }
        catch { file?.Dispose();directories.Dispose();throw; }
    }
}

/// <summary>Keep alive through helper completion. License integrity is pinned locally; Windows servicing validates license/package authorization.</summary>
public sealed class PreparedInstallation : IDisposable
{
    public VerifiedPackage Package { get; }
    public string LicensePath => license.Name;
    public string LicenseSha256 { get; }
    public string OriginalUserSid { get; }
    private readonly FileStream license;
    internal PreparedInstallation(VerifiedPackage package,FileStream license,string digest,string originalUserSid) { Package=package;this.license=license;LicenseSha256=digest;OriginalUserSid=originalUserSid; }
    public InstallHelperRequest CreateHelperRequest()=>new(Package.Path,LicensePath,Package.Sha256,LicenseSha256,"codex-desktop",InstallationOperation.Provision);
    public void Dispose() { Package.Dispose();license.Dispose(); }
}

internal sealed class WindowsPackageSignatureTrust : IPackageSignatureTrust
{
    public void Verify(string path, FileStream file)
    {
        if (!RuntimeCompat.IsWindows) throw new PackageTrustException(unchecked((int)0x800B0001));
        // WINTRUST_ACTION_GENERIC_VERIFY_V2, app-package aware.
        var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
        var info = new TrustFile
        {
            Size = (uint)Marshal.SizeOf<TrustFile>(),
            Path = path,
            File = file.SafeFileHandle.DangerousGetHandle()
        };
        var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<TrustFile>());
        var data = new TrustData
        {
            Size = (uint)Marshal.SizeOf<TrustData>(),
            Ui = 2,
            Revocation = 1,
            Choice = 1,
            Info = pointer,
            StateAction = 1,
            // CHAIN_EXCLUDE_ROOT, disable MD2/MD4. Online retrieval allowed; no lifetime-signing flag.
            Flags = 0x80 | 0x2000
        };
        try
        {
            Marshal.StructureToPtr(info, pointer, false);
            var status = WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            if (status != 0) throw new PackageTrustException(status); // LONG: only zero succeeds.
        }
        finally
        {
            // CLOSE is mandatory even when VERIFY fails. Do not retry with weaker revocation flags.
            data.StateAction = 2;
            try { WinVerifyTrust(new IntPtr(-1), ref action, ref data); }
            finally
            {
                Marshal.DestroyStructure<TrustFile>(pointer);
                Marshal.FreeHGlobal(pointer);
                GC.KeepAlive(file);
            }
        }
    }
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] private struct TrustFile
    { internal uint Size; [MarshalAs(UnmanagedType.LPWStr)] internal string Path; internal IntPtr File,KnownSubject; }
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] private struct TrustData
    {
        internal uint Size; internal IntPtr PolicyCallback,SipClient; internal uint Ui,Revocation,Choice; internal IntPtr Info;
        internal uint StateAction; internal IntPtr State,Url; internal uint Flags,UiContext; internal IntPtr SignatureSettings;
    }
    [DllImport("wintrust.dll",ExactSpelling=true,CharSet=CharSet.Unicode)] private static extern int WinVerifyTrust(IntPtr window,ref Guid action,ref TrustData data);
}
