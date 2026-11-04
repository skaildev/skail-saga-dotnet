using Skail.Platform.Runtime;
using Skail.Platform.Runtime.Threading;
using SkailSaga.Contracts;
using SkailSaga.Worker.Services;

namespace SkailSaga.Worker;

/// <summary>
/// The I/O of the saga, one operation per command. skail never runs a finished command again, but a retry runs the whole
/// command again, so every call below is idempotent. The commands get ids and values only: what goes in and out of a
/// command shows up in the Monitor.
/// </summary>
public class OrderCommands(IStockService stock, IPaymentGateway gateway, IInvoiceIssuer issuer, IOrderDesk desk)
{
    [SkailCommand]
    public async SkailTask<Reservation> ReserveStock(string orderId, string product, int quantity)
    {
        return await stock.ReserveAsync(orderId, product, quantity);
    }

    // A timeout or a network error throws, and skail runs the command again (up to 3 times here). The order id is the
    // idempotency key, so a new attempt does not charge twice. A declined card comes back as a result, because another
    // attempt would not change the bank's answer.
    [SkailCommand(retryCount: 3)]
    public async SkailTask<Charge> ChargeCard(string orderId, decimal amount)
    {
        return await gateway.ChargeAsync(orderId, amount);
    }

    [SkailCommand]
    public async SkailTask<TaxInvoice> IssueInvoice(string orderId, string product, decimal amount)
    {
        return await issuer.IssueAsync(orderId, product, amount);
    }

    // Compensations

    [SkailCommand]
    public async SkailTask RefundCharge(string chargeId)
    {
        await gateway.RefundAsync(chargeId);
    }

    [SkailCommand]
    public async SkailTask ReleaseStock(string reservationId)
    {
        await stock.ReleaseAsync(reservationId);
    }

    [SkailCommand]
    public async SkailTask NotifyCustomer(string orderId, string message)
    {
        await desk.NotifyCustomerAsync(orderId, message);
    }

    /// <summary>Records how the order ended, for the page, and returns that result to the function.</summary>
    [SkailCommand]
    public async SkailTask<string> RecordResult(string orderId, string result)
    {
        await desk.RecordResultAsync(orderId, result);
        return result;
    }
}
