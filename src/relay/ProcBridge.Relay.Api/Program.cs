using ProcBridge.Relay.Api.Endpoints;
using ProcBridge.Relay.Api.Extensions;
using ProcBridge.Relay.Api.Middleware;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRelayHost(builder.Configuration);

var app = builder.Build();
app.UseRelayForwardedHeaders();
app.UseMiddleware<RequestIdMiddleware>();
app.UseMiddleware<ExceptionWrapperMiddleware>();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });
app.UseRateLimiter();
app.MapControllers();
app.MapTunnelEndpoints();
app.MapGet("/health/live", () => Results.Text("ok"));

await app.RunAsync();

/// <summary>Entry point type, public for <c>WebApplicationFactory</c> in tests.</summary>
public partial class Program;
