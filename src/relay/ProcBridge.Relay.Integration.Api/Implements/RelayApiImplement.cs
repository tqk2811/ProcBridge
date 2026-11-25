using System.Text;

using Newtonsoft.Json;

using ProcBridge.Relay.Integration.Api.Interfaces;
using ProcBridge.Relay.Integration.Api.Models;
using ProcBridge.Relay.Shared.Common;
using ProcBridge.Relay.Shared.Json;

namespace ProcBridge.Relay.Integration.Api.Implements;

/// <summary>
/// <see cref="IRelayApi"/> over a plain <see cref="HttpClient"/>. The client is NOT owned (not disposed here). The relay
/// has no accounts: every call is anonymous.
/// </summary>
public sealed partial class RelayApiImplement : IRelayApi
{
    private readonly HttpClient _http;
    private readonly JsonSerializerSettings _json = RelayJsonSettings.Create();

    public RelayApiImplement(HttpClient http)
    {
        _http = http;
        Tunnel = new TunnelControllerImplement(this);
    }

    public Uri? BaseAddress => _http.BaseAddress;
    public IRelayTunnelController Tunnel { get; }

    private async Task<T> SendAsync<T>(HttpMethod method, string url, object? body, CancellationToken cancellationToken)
    {
        var text = await SendRawAsync(method, url, body, cancellationToken);
        var envelope = JsonConvert.DeserializeObject<ApiResponse<T>>(text, _json);
        if (envelope is null || envelope.Data is null)
            throw new RelayApiException(500, "EMPTY_RESPONSE", $"Relay trả về rỗng cho {method} {url}.");
        return envelope.Data;
    }

    private async Task<string> SendRawAsync(HttpMethod method, string url, object? body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, url);
        if (body is not null)
            request.Content = new StringContent(JsonConvert.SerializeObject(body, _json), Encoding.UTF8, "application/json");

        using var response = await _http.SendAsync(request, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (response.IsSuccessStatusCode)
            return text;

        ApiErrorResponse? error = null;
        try
        {
            error = JsonConvert.DeserializeObject<ApiErrorResponse>(text, _json);
        }
        catch (JsonException)
        {
        }
        throw new RelayApiException(
            (int)response.StatusCode,
            error?.Error?.Code ?? "HTTP_" + (int)response.StatusCode,
            error?.Error?.Message ?? $"{method} {url} -> {(int)response.StatusCode} {response.ReasonPhrase}");
    }
}
