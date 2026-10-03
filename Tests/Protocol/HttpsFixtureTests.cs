#if !NETFRAMEWORK
using System.ComponentModel;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace AiDesktopSetup.Tests.Protocol;

public sealed class HttpsFixtureTests
{
    [Fact]
    public async Task FaultedGatewayDisposalReleasesCertificateAndPreservesFailure()
    {
        var gateway = new NeutralGateway("/setup", "Example", "model", false, _ => { });
        var faultEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gateway.Fault = _ => { faultEntered.SetResult(); throw new InvalidOperationException("fixture-reply-failure"); };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            using var peer = new TcpClient();
            var origin = new Uri(gateway.Origin);
            await peer.ConnectAsync(origin.Host, origin.Port, timeout.Token);
            using var tls = new SslStream(peer.GetStream(), false, (_, cert, _, _) => cert != null && cert.GetRawCertData().SequenceEqual(gateway.Certificate.RawData));
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "localhost" }, timeout.Token);
            await tls.WriteAsync(System.Text.Encoding.ASCII.GetBytes("GET /setup HTTP/1.1\r\nHost: localhost\r\n\r\n"), timeout.Token);
            await tls.FlushAsync(timeout.Token);
            await faultEntered.Task.WaitAsync(timeout.Token);
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => gateway.DisposeAsync().AsTask());
            Assert.Equal("fixture-reply-failure", failure.Message);
            Assert.Equal(IntPtr.Zero, gateway.Certificate.Handle);
            var repeated = await Assert.ThrowsAsync<InvalidOperationException>(() => gateway.DisposeAsync().AsTask());
            Assert.Same(failure, repeated);
        }
        finally
        {
            try { await gateway.DisposeAsync(); }
            catch (InvalidOperationException) { }
            finally { gateway.Certificate.Dispose(); }
        }
    }

    [Fact]
    public void ImportedServerCertificateRetainsIdentityAndKeyAfterIssuerIsDisposed()
    {
        X509Certificate2 imported;
        byte[] identity;
        using (var key = RSA.Create(2048))
        {
            var request = new CertificateRequest("CN=127.0.0.1", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(5));
            identity = generated.RawData;
            imported = HttpsFixture.ImportServerCertificate(generated);
        }
        using (imported)
        {
            Assert.Equal(identity, imported.RawData);
            using var privateKey = imported.GetRSAPrivateKey();
            using var publicKey = imported.GetRSAPublicKey();
            Assert.NotNull(privateKey);
            Assert.NotNull(publicKey);
            var challenge = RandomNumberGenerator.GetBytes(32);
            var signature = privateKey!.SignData(challenge, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            Assert.True(publicKey!.VerifyData(challenge, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
            if (OperatingSystem.IsWindows())
            {
                // The regression is platform-specific; real TLS handshakes are also
                // exercised on every platform by the interoperability suite.
                if (privateKey is RSACng cng) Assert.False(cng.Key.IsEphemeral);
                else Assert.True(Assert.IsType<RSACryptoServiceProvider>(privateKey).CspKeyContainerInfo.Accessible);
            }
        }
    }

    [Fact]
    public void TransportDiagnosticsContainOnlyStageTypesAndErrorCodes()
    {
        var error = new AuthenticationException("FAKE_certificate_secret", new Win32Exception(unchecked((int)0x8009030E), "FAKE_request_secret"));
        var diagnostic = HttpsFixture.TransportFailure("TLS handshake", error);
        Assert.DoesNotContain("FAKE_", diagnostic);
        Assert.Contains("TLS handshake", diagnostic);
        Assert.Contains("AuthenticationException HRESULT=0x", diagnostic);
        Assert.Contains("Win32Exception HRESULT=0x", diagnostic);
        Assert.Contains("NativeError=0x8009030E", diagnostic);
    }
}
#endif
