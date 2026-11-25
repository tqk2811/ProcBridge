using ProcBridge.Relay.Api.Endpoints;
using ProcBridge.Relay.Api.Extensions;
using ProcBridge.Relay.Api.Infrastructure;
using ProcBridge.Relay.Api.Middleware;

// "account hash": operator command (prints a password hash for Relay:Accounts:Seed) instead of serving.
var (operatorCommand, hostArgs) = OperatorCommands.IsOperatorCommand(args) ? OperatorCommands.Split(args) : ([], args);
if (operatorCommand.Length > 0)
    return await OperatorCommands.RunAsync(operatorCommand, Console.Out, Console.Error);

var builder = WebApplication.CreateBuilder(hostArgs);
builder.Services.AddRelayHost(builder.Configuration);

var app = builder.Build();
app.UseRelayForwardedHeaders();
app.UseMiddleware<RequestIdMiddleware>();
app.UseMiddleware<ExceptionWrapperMiddleware>();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.MapControllers();
app.MapTunnelEndpoints();
app.MapGet("/health/live", () => Results.Text("ok"));

await app.RunAsync();
return 0;

/// <summary>Entry point type, public for <c>WebApplicationFactory</c> in tests.</summary>
public partial class Program;
