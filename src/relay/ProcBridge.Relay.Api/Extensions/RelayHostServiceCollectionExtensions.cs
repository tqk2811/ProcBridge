using System.Net;
using System.Threading.RateLimiting;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Mvc;

using ProcBridge.Relay.Api.Filters;
using ProcBridge.Relay.Api.Infrastructure;
using ProcBridge.Relay.Repositories;
using ProcBridge.Relay.Repositories.Abstractions.Interfaces;
using ProcBridge.Relay.Services;
using ProcBridge.Relay.Services.Accounts.Models;
using ProcBridge.Relay.Services.Common.Common.Errors;
using ProcBridge.Relay.Services.Tunnel.Models;
using ProcBridge.Relay.Shared.Auth;
using ProcBridge.Relay.Shared.Common;
using ProcBridge.Relay.Shared.Json;

namespace ProcBridge.Relay.Api.Extensions;

public static class RelayHostServiceCollectionExtensions
{
    /// <summary>Everything the relay host needs: in-memory store, repositories, services, auth, MVC.</summary>
    public static IServiceCollection AddRelayHost(this IServiceCollection services, IConfiguration configuration)
    {
        // All data lives in this process (see RelayStore): one store for the host, repositories are views of it.
        services.AddSingleton<RelayStore>();
        services.AddScoped<IAccountRepository, AccountRepository>();
        services.AddScoped<IAccountSessionRepository, AccountSessionRepository>();
        services.AddScoped<IDeviceRepository, DeviceRepository>();
        services.AddScoped<IShareRepository, ShareRepository>();

        services.AddSingleton(TimeProvider.System);
        services.Configure<AccountsOptions>(configuration.GetSection(AccountsOptions.SectionName));
        services.Configure<TunnelOptions>(configuration.GetSection(TunnelOptions.SectionName));
        services.AddApplicationServices();
        services.AddHostedService<AccountSeedHostedService>();

        AddAuthRateLimiter(services, configuration);
        AddForwardedHeaders(services, configuration);

        services.AddAuthentication(RelayAuth.Scheme)
            .AddScheme<AuthenticationSchemeOptions, RelayBearerAuthenticationHandler>(RelayAuth.Scheme, null);
        services.AddAuthorizationBuilder()
            .AddPolicy(RelayAuth.AccountPolicy, p => p.RequireClaim(RelayAuth.AccountIdClaim))
            .AddPolicy(RelayAuth.DevicePolicy, p => p.RequireClaim(RelayAuth.DeviceIdClaim));

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

    /// <summary>
    /// Fixed window per client address on login and register, and per device on opening tunnel sessions
    /// (<see cref="AuthRateLimitOptions"/>); plus a per user name limit on login (<see cref="LoginUserNameLimiter"/>).
    /// </summary>
    private static void AddAuthRateLimiter(IServiceCollection services, IConfiguration configuration)
    {
        var limits = configuration.GetSection(AuthRateLimitOptions.SectionName).Get<AuthRateLimitOptions>() ?? new AuthRateLimitOptions();
        services.AddSingleton(new LoginUserNameLimiter(limits));
        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.AddPolicy(AuthRateLimitOptions.TunnelSessionPolicyName, context => RateLimitPartition.GetFixedWindowLimiter(
                context.User.FindFirst(RelayAuth.DeviceIdClaim)?.Value ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = limits.TunnelSessionPermitLimit,
                    Window = limits.TunnelSessionWindow,
                    QueueLimit = 0,
                }));
            o.AddPolicy(AuthRateLimitOptions.PolicyName, context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = limits.AuthPermitLimit,
                    Window = limits.AuthWindow,
                    QueueLimit = 0,
                }));
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
    /// address (rate limiter). Without configured proxies it adds nothing: the headers are ignored.
    /// </summary>
    public static WebApplication UseRelayForwardedHeaders(this WebApplication app)
    {
        if (!app.Services.GetRequiredService<ForwardedHeadersSettings>().IsEmpty)
            app.UseForwardedHeaders();
        return app;
    }
}
