using System.Net;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace ProcBridge.Relay.Testing.Infrastructure;

/// <summary>
/// The test server has no peer address. This sets <c>Connection.RemoteIpAddress</c> from the <see cref="HeaderName"/>
/// request header (when present), ahead of the host's own pipeline, so tests can play many clients or a proxy.
/// </summary>
public sealed class TestRemoteAddressStartupFilter : IStartupFilter
{
    public const string HeaderName = "X-Test-Remote-Ip";

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use((context, nextMiddleware) =>
        {
            if (context.Request.Headers.TryGetValue(HeaderName, out var value) && IPAddress.TryParse(value.ToString(), out var address))
                context.Connection.RemoteIpAddress = address;
            return nextMiddleware(context);
        });
        next(app);
    };
}
