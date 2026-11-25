using System.Net;
using System.Threading.RateLimiting;

using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

using ProcBridge.Relay.Api.Filters;
using ProcBridge.Relay.Api.Infrastructure;
using ProcBridge.Relay.Services;
using ProcBridge.Relay.Services.Common.Common.Errors;
using ProcBridge.Relay.Services.Tunnel.Models;
using ProcBridge.Relay.Shared.Common;
using ProcBridge.Relay.Shared.Json;

namespace ProcBridge.Relay.Api.Extensions;

public static class RelayHostServiceCollectionExtensions
{
    /// <summary>
    /// Everything the relay host needs: the tunnel services, rate limits, forwarded headers, MVC. No accounts, no
    /// authentication and no storage: the relay only pairs lanes in memory (plan 8.2).
    /// </summary>
    public static IServiceCollection AddRelayHost(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(TimeProvider.System);
        services.Configure<TunnelOptions>(configuration.GetSection(TunnelOptions.SectionName));
        services.AddApplicationServices();

        AddRateLimiter(services, configuration);
        AddForwardedHeaders(services, configuration);

        services.AddControllers(o => o.Filters.Add<ApiEnvelopeResultFilter>())
            .AddNewtonsoftJson(o => RelayJsonSettings.Apply(o.SerializerSettings))
            .ConfigureApiBehaviorOptions(o => o.InvalidModelStateResponseFactory = context =>
            {
                var message = string.Join(" ", context.ModelState
                    .Where(x => x.Value?.Errors.Count > 0)
                    .Select(x => $"{x.Key}: {string.Join(", ", x.Value!.Errors.Select(e => e.ErrorMessage))}"));
                return new BadRequestObjectResult(new ApiErrorResponse
                {
                    RequestId = context.HttpContext.Items[RequestIdKeys.ItemKey]?.ToString() ?? string.Empty,
                    Error = new ApiError { Code = RelayErrors.ValidationFailed, Message = message },
                });
            });
        return services;
    }

    /// <summary>Fixed windows per client address on sessions, control channels and lanes (<see cref="RelayRateLimitOptions"/>).</summary>
    private static void AddRateLimiter(IServiceCollection services, IConfiguration configuration)
    {
        var limits = configuration.GetSection(RelayRateLimitOptions.SectionName).Get<RelayRateLimitOptions>() ?? new RelayRateLimitOptions();
        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            AddPerAddressPolicy(o, RelayRateLimitOptions.TunnelSessionPolicyName, limits.TunnelSessionPermitLimit, limits.Window);
            AddPerAddressPolicy(o, RelayRateLimitOptions.ControlPolicyName, limits.ControlPermitLimit, limits.Window);
            AddPerAddressPolicy(o, RelayRateLimitOptions.LanePolicyName, limits.LanePermitLimit, limits.Window);
            o.OnRejected = async (context, cancellationToken) =>
            {
                var response = context.HttpContext.Response;
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                    response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
                response.ContentType = "application/json; charset=utf-8";
                var body = new ApiErrorResponse
                {
                    RequestId = context.HttpContext.Items[RequestIdKeys.ItemKey]?.ToString() ?? string.Empty,
                    Error = new ApiError { Code = RelayErrors.TooManyRequests, Message = "Thử quá nhiều lần, hãy đợi rồi thử lại." },
                };
                await response.WriteAsync(Newtonsoft.Json.JsonConvert.SerializeObject(body, RelayJsonSettings.Create()), cancellationToken);
            };
        });
    }

    private static void AddPerAddressPolicy(RateLimiterOptions options, string name, int permits, TimeSpan window)
        => options.AddPolicy(name, context => RateLimitPartition.GetFixedWindowLimiter(
            context.ClientAddress(),
            _ => new FixedWindowRateLimiterOptions { PermitLimit = permits, Window = window, QueueLimit = 0 }));

    /// <summary>Trusts <c>X-Forwarded-*</c> only from the proxies listed in <c>Relay:ForwardedHeaders</c>; none listed means none trusted.</summary>
    private static void AddForwardedHeaders(IServiceCollection services, IConfiguration configuration)
    {
        var settings = configuration.GetSection(ForwardedHeadersSettings.SectionName).Get<ForwardedHeadersSettings>() ?? new ForwardedHeadersSettings();
        services.AddSingleton(settings);
        services.Configure<ForwardedHeadersOptions>(o =>
        {
            o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            // The framework defaults trust loopback; the relay trusts exactly what the operator lists.
            o.KnownProxies.Clear();
            o.KnownIPNetworks.Clear();
            foreach (var proxy in settings.KnownProxies)
                o.KnownProxies.Add(IPAddress.Parse(proxy));
            foreach (var network in settings.KnownNetworks)
                o.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
        });
    }

    /// <summary>
    /// Applies <c>X-Forwarded-For/Proto</c> from the configured proxies. Put it before everything that reads the client
    /// address (rate limiter, tunnel limits). Without configured proxies it adds nothing: the headers are ignored.
    /// </summary>
    public static WebApplication UseRelayForwardedHeaders(this WebApplication app)
    {
        if (!app.Services.GetRequiredService<ForwardedHeadersSettings>().IsEmpty)
            app.UseForwardedHeaders();
        return app;
    }
}
