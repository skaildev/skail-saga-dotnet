using SkailSaga.Contracts;

namespace SkailSaga.Worker.Services;

// The systems the saga talks to. In this demo all four are simulated by the web app; in your system they would be the
// stock service, the payment gateway, the invoice issuer and the customer messaging.

public interface IStockService
{
    /// <summary>Idempotent by order id.</summary>
    Task<Reservation> ReserveAsync(string orderId, string product, int quantity, CancellationToken ct = default);

    /// <summary>Compensation. Idempotent by reservation id.</summary>
    Task ReleaseAsync(string reservationId, CancellationToken ct = default);
}

public interface IPaymentGateway
{
    /// <summary>
    /// Charges the card on file for the order. The order id is the idempotency key: a retry does not charge twice. A
    /// timeout or a network error throws, and skail retries; a declined card comes back as a result.
    /// </summary>
    Task<Charge> ChargeAsync(string orderId, decimal amount, CancellationToken ct = default);

    /// <summary>Compensation. Idempotent by charge id.</summary>
    Task RefundAsync(string chargeId, CancellationToken ct = default);
}

public interface IInvoiceIssuer
{
    /// <summary>Idempotent by order id. A rejection comes back as a result.</summary>
    Task<TaxInvoice> IssueAsync(string orderId, string product, decimal amount, CancellationToken ct = default);
}

public interface IOrderDesk
{
    /// <summary>The e-mail to the customer. Idempotent by order id and message.</summary>
    Task NotifyCustomerAsync(string orderId, string message, CancellationToken ct = default);

    /// <summary>Tells the page how the order ended. Idempotent.</summary>
    Task RecordResultAsync(string orderId, string result, CancellationToken ct = default);
}
