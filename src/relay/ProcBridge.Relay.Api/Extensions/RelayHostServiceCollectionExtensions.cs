using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;

using ProcBridge.Relay.Api.Filters;
using ProcBridge.Relay.Api.Infrastructure;
using ProcBridge.Relay.Database.PostgreSql.EF;
using ProcBridge.Relay.Repositories;
using ProcBridge.Relay.Repositories.Abstractions.Interfaces;
using ProcBridge.Relay.Services;
using ProcBridge.Relay.Services.Accounts.Models;
using ProcBridge.Relay.Services.Common.Common.Errors;
using ProcBridge.Relay.Services.Tunnel.Models;
using ProcBridge.Relay.Shared.Auth;
using ProcBridge.Relay.Shared.Common;
using ProcBridge.Relay.Shared.Json;

using Microsoft.EntityFrameworkCore;

namespace ProcBridge.Relay.Api.Extensions;

public static class RelayHostServiceCollectionExtensions
{
    /// <summary>Everything the relay host needs: database, repositories, services, auth, MVC.</summary>
    public static IServiceCollection AddRelayHost(this IServiceCollection services, IConfiguration configuration)
    {
        // Required, no localhost fallback: a relay silently pointing at the wrong database is worse than not starting.
        var connectionString = configuration.GetConnectionString("Relay");
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("Thiếu cấu hình bắt buộc ConnectionStrings:Relay.");

        services.AddDbContext<RelayDbContext>(o => o.UseNpgsql(connectionString));
        services.AddScoped<IAccountRepository, AccountRepository>();
        services.AddScoped<IAccountSessionRepository, AccountSessionRepository>();
        services.AddScoped<IDeviceRepository, DeviceRepository>();
        services.AddScoped<IShareRepository, ShareRepository>();

        services.AddSingleton(TimeProvider.System);
        services.Configure<AccountsOptions>(configuration.GetSection(AccountsOptions.SectionName));
        services.Configure<TunnelOptions>(configuration.GetSection(TunnelOptions.SectionName));
        services.AddApplicationServices();

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
    /// The relay Api is the ONLY host that migrates this database (see <c>PostgreSqlSchemaInitializer</c>).
    /// </summary>
    /// <remarks><c>Relay:Database:MigrateOnStartup=false</c> skips it (schema applied out of band, or tests on fake repositories).</remarks>
    public static async Task InitializeDatabaseAsync(this WebApplication app)
    {
        if (!app.Configuration.GetValue("Relay:Database:MigrateOnStartup", true))
        {
            app.Logger.LogWarning("Bỏ qua migrate database lúc khởi động (Relay:Database:MigrateOnStartup=false).");
            return;
        }
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RelayDbContext>();
        await PostgreSqlSchemaInitializer.InitializeAsync(db);
    }
}
