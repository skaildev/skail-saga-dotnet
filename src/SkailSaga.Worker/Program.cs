using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Skail.Platform.Runtime;
using Skail.Platform.Runtime.Hosting;
using SkailSaga.Worker.Services;

[assembly: VisibleToSkailPlatform]

var builder = Host.CreateApplicationBuilder(args);

// HttpClient request logs would show up as extra lines under each command in the skail Monitor
builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);

var webBaseUrl = Environment.GetEnvironmentVariable("WEB_BASE_URL") ?? "http://localhost:8080";

// the simulated stock, gateway, issuer and customer messaging live in the web app
builder.Services.AddHttpClient<WebServices>(c =>
{
    c.BaseAddress = new Uri(webBaseUrl.EndsWith('/') ? webBaseUrl : webBaseUrl + "/");
    c.Timeout = TimeSpan.FromSeconds(10);
});
builder.Services.AddTransient<IStockService>(sp => sp.GetRequiredService<WebServices>());
builder.Services.AddTransient<IPaymentGateway>(sp => sp.GetRequiredService<WebServices>());
builder.Services.AddTransient<IInvoiceIssuer>(sp => sp.GetRequiredService<WebServices>());
builder.Services.AddTransient<IOrderDesk>(sp => sp.GetRequiredService<WebServices>());

// reads SKAIL_WORKLOAD, SKAIL_NAMESPACE, SKAIL_KEY and SKAIL_SIDECAR from the environment (.env)
builder.UseSkail();

await builder.Build().RunAsync();
