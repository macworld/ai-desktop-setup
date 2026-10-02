namespace AiDesktopSetup.Core.Protocol;

/// <summary>Independent pools: setup authorization, protected artwork, and anonymous packages.</summary>
public sealed class ProtocolHttpClients : IDisposable
{
    internal HttpClient Authenticated { get; }
    internal HttpClient Logo { get; }
    private readonly HttpClient package;
    internal IReadOnlyList<HttpClientHandler> Handlers { get; } = [];
    public ProtocolHttpClients()
    {
        var handlers = new[] { CreateHandler(), CreateHandler(), CreateHandler() }; Handlers = Array.AsReadOnly(handlers);
        Authenticated = Client(handlers[0]); Logo = Client(handlers[1]); package = Client(handlers[2]);
    }
    // Only Tests can substitute transport; production handlers never inherit browser/user credentials.
    internal ProtocolHttpClients(HttpMessageHandler authenticated, HttpMessageHandler logo, HttpMessageHandler package)
    { Authenticated = Client(authenticated); Logo = Client(logo); this.package = Client(package); }
    internal static HttpClientHandler CreateHandler() => new() { AllowAutoRedirect = false, UseCookies = false, UseDefaultCredentials = false, Credentials = null, AutomaticDecompression = System.Net.DecompressionMethods.None };
    private static HttpClient Client(HttpMessageHandler handler) => new(handler, true) { Timeout = Timeout.InfiniteTimeSpan };
    /// <summary>One anonymous hop; the adapter owns any explicitly approved redirect and package limits.</summary>
    public async Task<HttpResponseMessage> GetPackageAsync(string url, CancellationToken ct)
    {
        var validated = SetupUrlValidator.Asset(url);
        using var request = new HttpRequestMessage(HttpMethod.Get, validated);
        return await package.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
    }
    public void Dispose() { Authenticated.Dispose(); Logo.Dispose(); package.Dispose(); }
}
