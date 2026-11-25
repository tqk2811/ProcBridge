using ProcBridge.Relay.Api.Endpoints;
using ProcBridge.Relay.Api.Extensions;
using ProcBridge.Relay.Api.Infrastructure;
using ProcBridge.Relay.Api.Middleware;

// "account create <user>": operator command on the relay database instead of serving.
var (operatorCommand, hostArgs) = OperatorCommands.IsOperatorCommand(args) ? OperatorCommands.Split(args) : ([], args);

var builder = WebApplication.CreateBuilder(hostArgs);
builder.Services.AddRelayHost(builder.Configuration);

var app = builder.Build();
app.UseMiddleware<RequestIdMiddleware>();
app.UseMiddleware<ExceptionWrapperMiddleware>();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
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

if (operatorCommand.Length > 0)
    return await OperatorCommands.RunAsync(app.Services, operatorCommand, Console.Out, Console.Error);

await app.RunAsync();
return 0;

/// <summary>Entry point type, public for <c>WebApplicationFactory</c> in tests.</summary>
public partial class Program;
