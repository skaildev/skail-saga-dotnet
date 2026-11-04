using System.Text.Json;
using System.Text.Json.Serialization;
using SkailSaga.Contracts;
using SkailSaga.Web.Skail;
using SkailSaga.Web.Store;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddSingleton<SagaStore>();
builder.Services.AddSingleton(SkailOptions.FromEnvironment());
builder.Services.AddHttpClient<ISkailGateway, SkailApiGateway>(c => c.Timeout = TimeSpan.FromSeconds(10));

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions { OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache" });

app.MapGet("/healthz", () => Results.Ok("ok"));

// Places an order: the page (or a curl) calls this, and the web app starts CloseOrder in skail. The card stays here.
app.MapPost("/api/orders", async (NewOrder? request, SagaStore store, ISkailGateway skail, ILogger<Program> log, CancellationToken ct) =>
{
    if (request is null || string.IsNullOrWhiteSpace(request.Product) || request.Quantity < 1 || request.Amount <= 0 ||
        string.IsNullOrWhiteSpace(request.Card))
        return Results.BadRequest(new { error = "product, quantity (at least 1), amount (above 0) and card are required" });

    var taskId = Guid.NewGuid();   // each order is its own execution
    var order = store.CreateOrder(request, taskId);
    try
    {
        await skail.TriggerAsync(taskId, order, ct);
    }
    catch (SkailGatewayException ex)
    {
        log.LogWarning(ex, "skail did not accept the trigger for order {OrderId}", order.Id);
        store.MarkNotAccepted(order.Id);
        return Results.Json(new { orderId = order.Id, taskId, error = $"skail unavailable ({ex.Message})" },
            statusCode: StatusCodes.Status502BadGateway);
    }

    store.MarkAccepted(order.Id);
    return Results.Ok(new { orderId = order.Id, taskId });
});

app.MapGet("/api/state", (SagaStore store) => Results.Ok(store.Snapshot()));

app.MapPost("/api/reset", (SagaStore store) =>
{
    store.Reset();
    return Results.NoContent();
});

app.MapGet("/api/stream", async (HttpContext http, SagaStore store, CancellationToken ct) =>
{
    using var subscription = store.Subscribe();   // subscribe before reading the snapshot: no update is lost

    http.Response.ContentType = "text/event-stream";
    http.Response.Headers.CacheControl = "no-cache";

    async Task Send(StateView state)
    {
        var json = JsonSerializer.Serialize(state, Json.Options);
        await http.Response.WriteAsync($"data: {json}\n\n", ct);
        await http.Response.Body.FlushAsync(ct);
    }

    try
    {
        await Send(store.Snapshot());
        await foreach (var state in subscription.Reader.ReadAllAsync(ct)) await Send(state);
    }
    catch (OperationCanceledException)
    {
        // client disconnected
    }
    return Results.Empty;
});

// ---- called by the worker: the simulated stock, payment gateway, invoice issuer and customer messaging ------------

app.MapPost("/internal/stock/reservations", (ReserveRequest request, SagaStore store) => Results.Ok(store.Reserve(request)));

app.MapPost("/internal/stock/reservations/{reservationId}/release", (string reservationId, SagaStore store) =>
    store.ReleaseStock(reservationId) ? Results.NoContent() : Results.NotFound());

// The first attempt of each order answers 504, like a gateway that did not respond in time.
app.MapPost("/internal/payments/charges", (ChargeRequest request, SagaStore store) =>
{
    var outcome = store.ChargeCard(request);
    return outcome.TimedOut
        ? Results.Json(new { error = "the gateway did not answer in time" }, statusCode: StatusCodes.Status504GatewayTimeout)
        : Results.Ok(outcome.Charge);
});

app.MapPost("/internal/payments/charges/{chargeId}/refund", (string chargeId, SagaStore store) =>
    store.Refund(chargeId) ? Results.NoContent() : Results.NotFound());

app.MapPost("/internal/invoices", (InvoiceRequest request, SagaStore store) => Results.Ok(store.IssueInvoice(request)));

app.MapPost("/internal/orders/{orderId}/customer-messages", (string orderId, NotifyCustomerRequest request, SagaStore store) =>
{
    store.NotifyCustomer(orderId, request.Message);
    return Results.NoContent();
});

app.MapPost("/internal/orders/{orderId}/result", (string orderId, RecordResultRequest request, SagaStore store) =>
{
    store.RecordResult(orderId, request.Result);
    return Results.NoContent();
});

app.Run();

public partial class Program;
