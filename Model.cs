namespace SkailSaga;

/// <summary>
/// The argument of CloseOrder. It has no card: the store keeps the card on file and the gateway charges it by order id,
/// so the card number never goes into the run history or the Monitor.
/// </summary>
public sealed record Order(string Id, string Product, int Quantity, decimal Amount);

/// <summary>Result of reserving stock. Not having the stock is a result, not an error.</summary>
public sealed record Reservation(bool Confirmed, string? Id);

/// <summary>Result of charging the card. A declined card is a result, not an error: trying again would not change it.</summary>
public sealed record Charge(bool Approved, string? Id, string? Reason);

/// <summary>Result of issuing the tax invoice. A rejection is a result, not an error.</summary>
public sealed record TaxInvoice(bool Authorized, string? Number, string? Reason);

/// <summary>The four ways CloseOrder can end.</summary>
public static class SagaResults
{
    public const string Confirmed = "confirmed";
    public const string OutOfStock = "out of stock";
    public const string PaymentDeclined = "payment declined";
    public const string CancelledWithRefund = "cancelled with refund";
}
