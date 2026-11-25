using ProcBridge.Relay.Api.Endpoints;
using ProcBridge.Relay.Api.Extensions;
using ProcBridge.Relay.Api.Middleware;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRelayHost(builder.Configuration);

var app = builder.Build();
app.UseMiddleware<RequestIdMiddleware>();
app.UseMiddleware<ExceptionWrapperMiddleware>();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapTunnelEndpoints();
app.MapGet("/health/live", () => Results.Text("ok"));

try
{
    await app.InitializeDatabaseAsync();
}
catch (Exception ex)
{
    app.Logger.LogCritical(ex, "Không khởi tạo được database relay.");
    throw;
}

await app.RunAsync();

/// <summary>Entry point type, public for <c>WebApplicationFactory</c> in tests.</summary>
public partial class Program;
