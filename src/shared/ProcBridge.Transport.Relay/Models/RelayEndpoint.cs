namespace ProcBridge.Transport.Relay.Models;

/// <summary>
/// A relay: only its URL. The relay has no accounts or tokens; machine B proves its identity key on the control channel
/// and machine A reaches it by HostId (the fingerprint of that key, known from pairing).
/// </summary>
/// <param name="BaseUrl">Relay root, <c>https://relay.example/</c> (<c>http://</c> only for tests and local runs).</param>
public sealed record RelayEndpoint(Uri BaseUrl)
{
    /// <summary>WebSocket URL of <paramref name="pathAndQuery"/> on this relay (<c>ws</c>/<c>wss</c> from the base scheme).</summary>
    public Uri WebSocketUri(string pathAndQuery)
    {
        var builder = new UriBuilder(BaseUrl)
        {
            Scheme = BaseUrl.Scheme == Uri.UriSchemeHttps ? "wss" : "ws",
        };
        string basePath = builder.Path.TrimEnd('/');
        int query = pathAndQuery.IndexOf('?');
        builder.Path = basePath + (query < 0 ? pathAndQuery : pathAndQuery[..query]);
        builder.Query = query < 0 ? string.Empty : pathAndQuery[(query + 1)..];
        return builder.Uri;
    }

    public override string ToString() => BaseUrl.ToString();
}
