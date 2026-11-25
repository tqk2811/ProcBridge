namespace ProcBridge.Transport.Relay.Models;

/// <summary>A relay and the device token this machine uses on it (from <c>procbridge-relay device register</c>).</summary>
/// <param name="BaseUrl">Relay root, <c>https://relay.example/</c> (<c>http://</c> only for tests and local runs).</param>
/// <param name="DeviceToken">Device bearer token (<c>pbd_...</c>); a secret, never logged.</param>
public sealed record RelayEndpoint(Uri BaseUrl, string DeviceToken)
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

    /// <summary>Does not print the token.</summary>
    public override string ToString() => BaseUrl.ToString();
}
