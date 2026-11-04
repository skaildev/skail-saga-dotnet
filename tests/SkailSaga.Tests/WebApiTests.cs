using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SkailSaga.Contracts;
using SkailSaga.Web.Skail;
using SkailSaga.Web.Store;

namespace SkailSaga.Tests;

public class WebApiTests : IDisposable
{
    private readonly FakeSkailGateway _skail = new();
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public WebApiTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.ConfigureServices(services =>
        {
            services.RemoveAll<ISkailGateway>();
            services.AddSingleton<ISkailGateway>(_skail);
        }));
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private SagaStore Store => _factory.Services.GetRequiredService<SagaStore>();

    private static object NewOrder(string product = "Monitor", int quantity = 1, decimal amount = 1200m, string card = "4111111111111111") =>
        new { product, quantity, amount, card };

    private async Task<StateView> State() => (await _client.GetFromJsonAsync<StateView>("/api/state", Json.Options))!;

    private Task<HttpResponseMessage> Post(string url, object? body = null) => _client.PostAsJsonAsync(url, body, Json.Options);

    // ---- placing orders ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Placing_an_order_starts_CloseOrder_with_a_new_execution_without_the_card_and_answers_the_ids()
    {
        var response = await Post("/api/orders", NewOrder());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>();
        var (taskId, order) = Assert.Single(_skail.Triggers);
        Assert.Equal("P-10", order.Id);
        Assert.Equal(new Order("P-10", "Monitor", 1, 1200m), order);
        Assert.Equal("P-10", body!["orderId"]);
        Assert.Equal(taskId.ToString(), body["taskId"]);
    }

    [Fact]
    public async Task Each_order_is_its_own_execution()
    {
        await Post("/api/orders", NewOrder());
        await Post("/api/orders", NewOrder());

        Assert.Equal(2, _skail.Triggers.Select(t => t.TaskId).Distinct().Count());
        Assert.Equal(["P-10", "P-11"], _skail.Triggers.Select(t => t.Order.Id));
    }

    [Fact]
    public async Task The_new_order_shows_up_in_the_state_with_the_card_masked()
    {
        await Post("/api/orders", NewOrder());

        var order = Assert.Single((await State()).Orders);
        Assert.Equal(("P-10", OrderState.Received, "•••• 1111"), (order.OrderId, order.State, order.Card));
    }

    [Theory]
    [InlineData("", 1, 10, "4111")]
    [InlineData("Monitor", 0, 10, "4111")]
    [InlineData("Monitor", 1, 0, "4111")]
    [InlineData("Monitor", 1, 10, "")]
    public async Task An_invalid_order_is_refused_and_nothing_starts(string product, int quantity, decimal amount, string card)
    {
        var response = await Post("/api/orders", NewOrder(product, quantity, amount, card));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_skail.Triggers);
        Assert.Empty((await State()).Orders);
    }

    [Fact]
    public async Task An_empty_body_is_refused()
    {
        var response = await _client.PostAsync("/api/orders", new StringContent("", System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task When_skail_does_not_accept_it_answers_502_with_the_reason_and_marks_the_order()
    {
        _skail.Fail = true;

        var response = await Post("/api/orders", NewOrder());

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Contains("skail answered 503", await response.Content.ReadAsStringAsync());
        Assert.Equal(OrderState.NotAccepted, Assert.Single((await State()).Orders).State);
    }

    [Fact]
    public async Task Reset_goes_back_to_the_seed()
    {
        await Post("/api/orders", NewOrder());

        Assert.Equal(HttpStatusCode.NoContent, (await Post("/api/reset")).StatusCode);

        var state = await State();
        Assert.Empty(state.Orders);
        Assert.Equal(new StockView("Monitor", 5, 0), state.Stock.Single(s => s.Product == "Monitor"));
    }

    // ---- the simulated services (called by the worker) --------------------------------------------------------------

    [Fact]
    public async Task Reserving_answers_the_reservation_and_takes_the_stock()
    {
        await Post("/api/orders", NewOrder());

        var response = await Post("/internal/stock/reservations", new { orderId = "P-10", product = "Monitor", quantity = 2 });

        Assert.Equal(new Reservation(true, "RES-P-10"), await response.Content.ReadFromJsonAsync<Reservation>(Json.Options));
        Assert.Equal(new StockView("Monitor", 3, 2), (await State()).Stock.Single(s => s.Product == "Monitor"));
    }

    [Fact]
    public async Task Releasing_answers_204_or_404()
    {
        await Post("/api/orders", NewOrder());
        await Post("/internal/stock/reservations", new { orderId = "P-10", product = "Monitor", quantity = 1 });

        Assert.Equal(HttpStatusCode.NoContent, (await Post("/internal/stock/reservations/RES-P-10/release")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Post("/internal/stock/reservations/RES-nope/release")).StatusCode);
    }

    [Fact]
    public async Task The_first_charge_attempt_answers_504_and_the_second_goes_through()
    {
        await Post("/api/orders", NewOrder());
        var charge = new { orderId = "P-10", amount = 1200m };

        Assert.Equal(HttpStatusCode.GatewayTimeout, (await Post("/internal/payments/charges", charge)).StatusCode);

        var second = await Post("/internal/payments/charges", charge);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(new Charge(true, "COB-P-10", null), await second.Content.ReadFromJsonAsync<Charge>(Json.Options));
    }

    [Fact]
    public async Task Refunding_answers_204_or_404()
    {
        await Post("/api/orders", NewOrder());
        var charge = new { orderId = "P-10", amount = 1200m };
        await Post("/internal/payments/charges", charge);
        await Post("/internal/payments/charges", charge);

        Assert.Equal(HttpStatusCode.NoContent, (await Post("/internal/payments/charges/COB-P-10/refund")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Post("/internal/payments/charges/COB-nope/refund")).StatusCode);
    }

    [Fact]
    public async Task Issuing_an_invoice_answers_the_result()
    {
        await Post("/api/orders", NewOrder("Cabo HDMI", 2, 60m));

        var response = await Post("/internal/invoices", new { orderId = "P-10", product = "Cabo HDMI", amount = 60m });

        var invoice = await response.Content.ReadFromJsonAsync<TaxInvoice>(Json.Options);
        Assert.False(invoice!.Authorized);
        Assert.Equal("Cabo HDMI has no NCM registered", invoice.Reason);
    }

    [Fact]
    public async Task The_customer_message_and_the_result_show_up_on_the_order()
    {
        await Post("/api/orders", NewOrder());

        Assert.Equal(HttpStatusCode.NoContent, (await Post("/internal/orders/P-10/customer-messages", new { message = "Order confirmed!" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Post("/internal/orders/P-10/result", new { result = "confirmed" })).StatusCode);

        var order = Assert.Single((await State()).Orders);
        Assert.Equal((OrderState.Confirmed, "confirmed"), (order.State, order.Result));
        Assert.Equal(new StepView(StepKind.CustomerNotified, "Order confirmed!", false), Assert.Single(order.Steps));
    }

    // ---- stream and page --------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_stream_sends_the_current_state_first_and_then_each_change()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var response = await _client.GetAsync("/api/stream", HttpCompletionOption.ResponseHeadersRead, cts.Token);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cts.Token));

        var first = await ReadEvent(reader);
        Assert.Empty(first.Orders);

        await Post("/api/orders", NewOrder());
        var next = await ReadEvent(reader);
        Assert.Equal("P-10", Assert.Single(next.Orders).OrderId);
    }

    [Fact]
    public async Task The_page_has_one_button_per_path()
    {
        var html = await _client.GetStringAsync("/");

        Assert.Contains("Confirmed", html);
        Assert.Contains("Card declined", html);
        Assert.Contains("Invoice rejected", html);
        Assert.Contains("Out of stock", html);
        Assert.Contains("Reset demo", html);
    }

    [Fact]
    public async Task Healthz_answers_ok()
    {
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/healthz")).StatusCode);
    }

    private static async Task<StateView> ReadEvent(StreamReader reader)
    {
        while (await reader.ReadLineAsync() is { } line)
            if (line.StartsWith("data: ", StringComparison.Ordinal))
                return System.Text.Json.JsonSerializer.Deserialize<StateView>(line["data: ".Length..], Json.Options)!;
        throw new InvalidOperationException("the stream ended");
    }
}
