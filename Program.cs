using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Skail.Platform.Runtime;
using Skail.Platform.Runtime.Hosting;
using SkailSaga;
using SkailSaga.Demo;
using SkailSaga.Skail;

[assembly: VisibleToSkailPlatform]

// usage: dotnet run [-- confirmed | declined | invoice | stock | resume]
//   no argument: one order per path; a path: only that order; resume: no new orders, finishes the runs left waiting
var argument = args.FirstOrDefault(a => !a.StartsWith('-'))?.ToLowerInvariant();
var resume = argument == "resume";
IReadOnlyList<DemoOrder> orders = argument is null ? DemoOrders.All
    : resume ? []
    : DemoOrders.Find(argument) is { } one ? [one]
    : [];
if (!resume && orders.Count == 0)
{
    Console.Error.WriteLine(
        $"Unknown argument '{argument}'. Use {string.Join(", ", DemoOrders.All.Select(o => o.Path))} or resume.");
    return 1;
}

DotEnv.Load();
string[] required = ["SKAIL_WORKLOAD", "SKAIL_NAMESPACE", "SKAIL_KEY", "SKAIL_SIDECAR"];
var missing = required.Where(name => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name))).ToArray();
if (missing.Length > 0)
{
    Console.Error.WriteLine($"Missing {string.Join(", ", missing)}. Copy .env.example to .env and fill it in.");
    return 1;
}

// a new demo starts over with full stock; resume picks up the state the previous run of the app left
const string StateFile = ".saga-demo.json";
var services = resume ? SimulatedServices.Resume(StateFile) : SimulatedServices.Fresh(StateFile);

var builder = Host.CreateApplicationBuilder(args);

// keep the console for the saga: only warnings and errors from the host and the libraries
builder.Logging.SetMinimumLevel(LogLevel.Warning);

builder.Services.AddSingleton(services);
builder.Services.AddSingleton(SkailApiOptions.FromEnvironment());
builder.Services.AddHttpClient<SkailApiClient>(http => http.Timeout = TimeSpan.FromSeconds(10));
builder.Services.AddSingleton(orders);
builder.Services.AddHostedService<OrderPlacer>();

// hosts the worker that executes OrderFunctions; reads the SKAIL_* variables
builder.UseSkail();

await builder.Build().RunAsync();
return 0;
