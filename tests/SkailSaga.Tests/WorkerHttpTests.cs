using System.Net;
using System.Text.Json;
using SkailSaga.Contracts;
using SkailSaga.Worker.Services;

namespace SkailSaga.Tests;

/// <summary>The worker's calls to the simulated services (they live in the web app).</summary>
public class WorkerHttpTests
{
    private static WebServices Services(StubHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://web:8080/") });

    private static JsonElement Body(StubHandler handler) => JsonDocument.Parse(Assert.Single(handler.Calls).Body).RootElement;

    private static string Url(StubHandler handler) => Assert.Single(handler.Calls).Request.RequestUri!.ToString();

    [Fact]
    public async Task Reserve_posts_the_order_id_product_and_quantity_and_reads_the_reservation()
    {
        var handler = StubHandler.Json("""{"confirmed":true,"id":"RES-P-10"}""");

        var reservation = await Services(handler).ReserveAsync("P-10", "Monitor", 1);

        Assert.Equal(new Reservation(true, "RES-P-10"), reservation);
        Assert.Equal("http://web:8080/internal/stock/reservations", Url(handler));
        Assert.Equal("P-10", Body(handler).GetProperty("orderId").GetString());
        Assert.Equal("Monitor", Body(handler).GetProperty("product").GetString());
        Assert.Equal(1, Body(handler).GetProperty("quantity").GetInt32());
    }

    [Fact]
    public async Task Release_posts_to_the_reservation_route()
    {
        var handler = StubHandler.Status(HttpStatusCode.NoContent);

        await Services(handler).ReleaseAsync("RES-P-10");

        Assert.Equal("http://web:8080/internal/stock/reservations/RES-P-10/release", Url(handler));
    }

    [Fact]
    public async Task Release_and_refund_go_with_no_body()
    {
        var handler = StubHandler.Status(HttpStatusCode.NoContent);
        var services = Services(handler);

        await services.ReleaseAsync("RES-P-10");
        await services.RefundAsync("COB-P-10");

        Assert.All(handler.Calls, call => Assert.Null(call.Request.Content));
    }

    [Fact]
    public async Task Charge_sends_the_card_on_file_with_the_order_id_as_the_idempotency_key()
    {
        var handler = StubHandler.Json("""{"approved":true,"id":"COB-P-10","reason":null}""");

        var charge = await Services(handler).ChargeAsync("P-10", 1200m);

        Assert.Equal(new Charge(true, "COB-P-10", null), charge);
        Assert.Equal("http://web:8080/internal/payments/charges", Url(handler));
        Assert.Equal("""{"orderId":"P-10","amount":1200}""", Assert.Single(handler.Calls).Body);
    }

    [Fact]
    public async Task A_declined_card_comes_back_as_a_result()
    {
        var handler = StubHandler.Json("""{"approved":false,"id":null,"reason":"insufficient funds"}""");

        var charge = await Services(handler).ChargeAsync("P-10", 1200m);

        Assert.False(charge.Approved);
        Assert.Equal("insufficient funds", charge.Reason);
    }

    [Theory]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task A_gateway_timeout_throws_so_skail_retries_the_command(HttpStatusCode status)
    {
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => Services(StubHandler.Status(status)).ChargeAsync("P-10", 1200m));

        Assert.Equal($"Web app answered {(int)status} to the charge", error.Message);   // what the Monitor shows
        Assert.Equal(status, error.StatusCode);
    }

    [Fact]
    public async Task Refund_posts_to_the_charge_route()
    {
        var handler = StubHandler.Status(HttpStatusCode.NoContent);

        await Services(handler).RefundAsync("COB-P-10");

        Assert.Equal("http://web:8080/internal/payments/charges/COB-P-10/refund", Url(handler));
    }

    [Fact]
    public async Task Issue_posts_the_order_id_product_and_amount_and_reads_the_invoice()
    {
        var handler = StubHandler.Json("""{"authorized":false,"number":null,"reason":"Cabo HDMI has no NCM registered"}""");

        var invoice = await Services(handler).IssueAsync("P-10", "Cabo HDMI", 60m);

        Assert.False(invoice.Authorized);
        Assert.Equal("Cabo HDMI has no NCM registered", invoice.Reason);
        Assert.Equal("http://web:8080/internal/invoices", Url(handler));
        Assert.Equal("Cabo HDMI", Body(handler).GetProperty("product").GetString());
    }

    [Fact]
    public async Task Notify_the_customer_posts_the_message()
    {
        var handler = StubHandler.Status(HttpStatusCode.NoContent);

        await Services(handler).NotifyCustomerAsync("P-10", "Order confirmed!");

        Assert.Equal("http://web:8080/internal/orders/P-10/customer-messages", Url(handler));
        Assert.Equal("Order confirmed!", Body(handler).GetProperty("message").GetString());
    }

    [Fact]
    public async Task Record_result_posts_the_result()
    {
        var handler = StubHandler.Status(HttpStatusCode.NoContent);

        await Services(handler).RecordResultAsync("P-10", SagaResults.Confirmed);

        Assert.Equal("http://web:8080/internal/orders/P-10/result", Url(handler));
        Assert.Equal("confirmed", Body(handler).GetProperty("result").GetString());
    }

    [Fact]
    public async Task Any_error_from_the_web_app_throws()
    {
        var services = Services(StubHandler.Status(HttpStatusCode.InternalServerError));

        await Assert.ThrowsAsync<HttpRequestException>(() => services.ReserveAsync("P-10", "Monitor", 1));
        await Assert.ThrowsAsync<HttpRequestException>(() => services.ReleaseAsync("RES-P-10"));
        await Assert.ThrowsAsync<HttpRequestException>(() => services.NotifyCustomerAsync("P-10", "x"));
        await Assert.ThrowsAsync<HttpRequestException>(() => services.RecordResultAsync("P-10", "x"));
    }
}
