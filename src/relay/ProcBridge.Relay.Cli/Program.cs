using ProcBridge.Relay.Cli;

var config = new CliConfigStore(CliConfigStore.DefaultPath());
using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
var app = new CliApp(Console.Out, Console.Error, config, new ConsolePasswordReader(), baseAddress =>
{
    http.BaseAddress ??= baseAddress;
    return new ProcBridge.Relay.Integration.Api.Implements.RelayApiImplement(http);
});
return await app.RunAsync(args);
