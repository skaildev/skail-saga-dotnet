using Skail.Platform.Runtime;
using Skail.Platform.Runtime.Hosting;
using SkailSaga;
using SkailSaga.Demo;
using SkailSaga.Skail;

[assembly: VisibleToSkailPlatform]

// usage: dotnet run [-- resume]
//   then open http://localhost:8080; resume keeps the state the previous run of the app left, to finish its orders
var argument = args.FirstOrDefault(a => !a.StartsWith('-'))?.ToLowerInvariant();
if (argument is not null && argument != "resume")
{
    Console.Error.WriteLine($"Unknown argument '{argument}'. Use resume, or nothing.");
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
var timeline = new Timeline();
var services = argument == "resume" ? SimulatedServices.Resume(StateFile, timeline) : SimulatedServices.Fresh(StateFile, timeline);

var builder = WebApplication.CreateBuilder(args);

// keep the console for the demo: only warnings and errors from the host and the libraries
builder.Logging.SetMinimumLevel(LogLevel.Warning);

// the page: localhost only, unless DEMO_LISTEN says otherwise (the container listens on all interfaces)
builder.WebHost.UseUrls(Environment.GetEnvironmentVariable("DEMO_LISTEN") ?? "http://localhost:8080");

builder.Services.AddSingleton(timeline);
builder.Services.AddSingleton(services);
builder.Services.AddSingleton<OrderDesk>();
builder.Services.AddSingleton(SkailApiOptions.FromEnvironment());
builder.Services.AddHttpClient<SkailApiClient>(http => http.Timeout = TimeSpan.FromSeconds(10));

// hosts the worker that executes OrderFunctions; reads the SKAIL_* variables
builder.UseSkail();

var app = builder.Build();
var desk = app.Services.GetRequiredService<OrderDesk>();

var page = Path.Combine(AppContext.BaseDirectory, "Page", "index.html");
app.MapGet("/", () => Results.File(page, "text/html; charset=utf-8"));
app.MapGet("/api/state", () => Results.Json(desk.View()));
app.MapPost("/api/orders/{path}", async (string path, CancellationToken ct) =>
    await desk.PlaceAsync(path, ct) ? Results.Ok() : Results.NotFound());
app.MapPost("/api/reset", () => { desk.Reset(); return Results.Ok(); });

app.Lifetime.ApplicationStarted.Register(() => Console.WriteLine(argument == "resume"
    ? "Resuming: skail hands this app the orders left in the middle. Open http://localhost:8080. Ctrl+C exits."
    : "Open http://localhost:8080 to place orders. Ctrl+C exits; scripts/run.sh resume finishes the orders left in the middle."));

await app.RunAsync();
return 0;
