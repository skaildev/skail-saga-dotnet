namespace SkailSaga.Contracts;

/// <summary>
/// The argument of the CloseOrder function. It has no card: the card stays in the web app, which plays the store, so it
/// never goes into the run history or the Monitor.
/// </summary>
public sealed record Order(string Id, string Product, int Quantity, decimal Amount);

/// <summary>Result of reserving stock. Not having the stock is a result, not an error.</summary>
public sealed record Reservation(bool Confirmed, string? Id);

/// <summary>Result of charging the card. A declined card is a result, not an error: trying again would not change it.</summary>
public sealed record Charge(bool Approved, string? Id, string? Reason)
{
    public static Charge Declined(string reason) => new(false, null, reason);
}

/// <summary>Result of issuing the tax invoice.</summary>
public sealed record TaxInvoice(bool Authorized, string? Number, string? Reason)
{
    public static TaxInvoice Rejected(string reason) => new(false, null, reason);
}

// ---- calls from the worker to the simulated services (they live in the web app) ---------------------------------------

public sealed record ReserveRequest(string OrderId, string Product, int Quantity);

/// <summary>The gateway charges the card on file for the order. The order id is the idempotency key.</summary>
public sealed record ChargeRequest(string OrderId, decimal Amount);

public sealed record InvoiceRequest(string OrderId, string Product, decimal Amount);

public sealed record NotifyCustomerRequest(string Message);

public sealed record RecordResultRequest(string Result);

/// <summary>What the page (or a curl) sends to place an order. The web app picks the order id and keeps the card.</summary>
public sealed record NewOrder(string Product, int Quantity, decimal Amount, string Card);

public static class SkailFunctions
{
    /// <summary>Name of the function, as it appears in the trigger route.</summary>
    public const string CloseOrder = "CloseOrder";
}

/// <summary>The four ways CloseOrder can end: one per path of the saga.</summary>
public static class SagaResults
{
    public const string Confirmed = "confirmed";
    public const string OutOfStock = "out of stock";
    public const string PaymentDeclined = "payment declined";
    public const string CancelledWithRefund = "cancelled with refund";
}

public enum OrderState
{
    Received,
    NotAccepted,
    Running,
    Confirmed,
    OutOfStock,
    Declined,
    Refunded,
}

public enum StepKind
{
    StockReserved,
    OutOfStock,
    ChargeTimeout,
    ChargeApproved,
    ChargeDeclined,
    InvoiceAuthorized,
    InvoiceRejected,
    ChargeRefunded,
    StockReleased,
    CustomerNotified,
}

/// <summary>One effect on the outside world, in the order it happened. A compensation undoes a step that came before it.</summary>
public sealed record StepView(StepKind Kind, string Detail, bool Compensation);

public sealed record StockView(string Product, int Available, int Reserved);

public sealed record OrderView(
    string OrderId, string Product, int Quantity, decimal Amount, string Card, Guid TaskId,
    OrderState State, string? Result, IReadOnlyList<StepView> Steps);

/// <summary>What <c>GET /api/state</c> and the SSE stream return.</summary>
public sealed record StateView(IReadOnlyList<StockView> Stock, IReadOnlyList<OrderView> Orders);

public static class OrderStates
{
    /// <summary>The state an order reaches when the worker reports its result.</summary>
    public static OrderState FromResult(string? result) => result switch
    {
        SagaResults.Confirmed => OrderState.Confirmed,
        SagaResults.OutOfStock => OrderState.OutOfStock,
        SagaResults.PaymentDeclined => OrderState.Declined,
        SagaResults.CancelledWithRefund => OrderState.Refunded,
        _ => OrderState.Running,
    };

    public static bool IsTerminal(OrderState state) =>
        state is OrderState.Confirmed or OrderState.OutOfStock or OrderState.Declined or OrderState.Refunded;
}
