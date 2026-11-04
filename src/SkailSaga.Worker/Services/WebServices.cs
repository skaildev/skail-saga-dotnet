using System.Net.Http.Json;
using SkailSaga.Contracts;

namespace SkailSaga.Worker.Services;

/// <summary>The four services over HTTP, on the internal routes of the web app.</summary>
public sealed class WebServices(HttpClient http) : IStockService, IPaymentGateway, IInvoiceIssuer, IOrderDesk
{
    public Task<Reservation> ReserveAsync(string orderId, string product, int quantity, CancellationToken ct = default) =>
        PostAsync<Reservation>("internal/stock/reservations", new ReserveRequest(orderId, product, quantity), "stock reservation", ct);

    public Task ReleaseAsync(string reservationId, CancellationToken ct = default) =>
        PostAsync($"internal/stock/reservations/{Uri.EscapeDataString(reservationId)}/release", null, "stock release", ct);

    // A 504 (the simulated gateway did not answer in time) throws here, like a real timeout, and skail retries the command.
    public Task<Charge> ChargeAsync(string orderId, decimal amount, CancellationToken ct = default) =>
        PostAsync<Charge>("internal/payments/charges", new ChargeRequest(orderId, amount), "charge", ct);

    public Task RefundAsync(string chargeId, CancellationToken ct = default) =>
        PostAsync($"internal/payments/charges/{Uri.EscapeDataString(chargeId)}/refund", null, "refund", ct);

    public Task<TaxInvoice> IssueAsync(string orderId, string product, decimal amount, CancellationToken ct = default) =>
        PostAsync<TaxInvoice>("internal/invoices", new InvoiceRequest(orderId, product, amount), "invoice", ct);

    public Task NotifyCustomerAsync(string orderId, string message, CancellationToken ct = default) =>
        PostAsync($"internal/orders/{Uri.EscapeDataString(orderId)}/customer-messages", new NotifyCustomerRequest(message),
            "customer message", ct);

    public Task RecordResultAsync(string orderId, string result, CancellationToken ct = default) =>
        PostAsync($"internal/orders/{Uri.EscapeDataString(orderId)}/result", new RecordResultRequest(result), "result", ct);

    // The release and the refund have no body, so they go without content.
    private async Task PostAsync(string route, object? body, string what, CancellationToken ct)
    {
        using var response = body is null
            ? await http.PostAsync(route, null, ct)
            : await http.PostAsJsonAsync(route, body, Json.Options, ct);
        EnsureSuccess(response, what);
    }

    private async Task<T> PostAsync<T>(string route, object body, string what, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync(route, body, Json.Options, ct);
        EnsureSuccess(response, what);
        return (await response.Content.ReadFromJsonAsync<T>(Json.Options, ct))!;
    }

    // When a command fails, this message is what the skail Monitor shows.
    private static void EnsureSuccess(HttpResponseMessage response, string what)
    {
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Web app answered {(int)response.StatusCode} to the {what}", null, response.StatusCode);
    }
}
