#if !NETFRAMEWORK
using System.ComponentModel;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace AiDesktopSetup.Tests.Protocol;

internal static class HttpsFixture
{
    internal static X509Certificate2 ImportServerCertificate(X509Certificate2 generated)
    {
        // Schannel cannot use CreateSelfSigned's ephemeral key. Import a temporary
        // user key container: without PersistKeySet, disposal deletes that key.
        // This does not install the certificate in any OS certificate trust store.
        var pfx = generated.Export(X509ContentType.Pkcs12);
        try { return X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.UserKeySet); }
        finally { CryptographicOperations.ZeroMemory(pfx); }
    }

    internal static string TransportFailure(string stage, Exception failure)
    {
        // Never include exception messages, requests, or generated credentials.
        var codes = new List<string>();
        for (Exception? error = failure; error != null && codes.Count < 4; error = error.InnerException)
            codes.Add(error.GetType().Name + " HRESULT=0x" + error.HResult.ToString("X8") +
                (error is Win32Exception native ? " NativeError=0x" + native.NativeErrorCode.ToString("X8") : ""));
        return "HTTPS fixture " + stage + ": " + string.Join(" -> ", codes);
    }
}
#endif
