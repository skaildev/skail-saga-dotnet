using Skail.Platform.Runtime;
using Skail.Platform.Runtime.Threading;
using SkailSaga.Demo;

namespace SkailSaga;

public class OrderFunctions(SimulatedServices services)
{
    /// <summary>
    /// Reserves the stock, charges the card and issues the invoice. When a step does not work out, it undoes the
    /// previous ones in reverse order. The name and the parameter are the trigger contract: [{"Id", "Product",
    /// "Quantity", "Amount"}].
    /// </summary>
    [SkailFunction]
    public async SkailTask<string> CloseOrder(Order order)
    {
        var reservation = await ReserveStock(order.Id, order.Product, order.Quantity);
        if (!reservation.Confirmed)
            return await ReportResult(order.Id, SagaResults.OutOfStock);

        var charge = await ChargeCard(order.Id, order.Amount);
        if (!charge.Approved)
        {
            await ReleaseStock(reservation.Id!);
            return await ReportResult(order.Id, SagaResults.PaymentDeclined);
        }

        var invoice = await IssueInvoice(order.Id, order.Product, order.Amount);
        if (!invoice.Authorized)
        {
            await RefundCharge(charge.Id!);          // reverse order: the refund first,
            await ReleaseStock(reservation.Id!);     // then the stock
            return await ReportResult(order.Id, SagaResults.CancelledWithRefund);
        }

        return await ReportResult(order.Id, SagaResults.Confirmed);
    }

    // The commands are the I/O, one operation each. skail never runs a completed command again, but a retry runs the
    // whole command again, so every call is idempotent: the order id is the idempotency key.

    [SkailCommand]
    public async SkailTask<Reservation> ReserveStock(string orderId, string product, int quantity) =>
        await Task.FromResult(services.Reserve(orderId, product, quantity));

    /// <summary>
    /// A gateway timeout throws, and skail runs the command again (up to 3 times); the order id keeps the card from
    /// being charged twice. A declined card comes back as a result, because another attempt would not change it.
    /// </summary>
    [SkailCommand(retryCount: 3)]
    public async SkailTask<Charge> ChargeCard(string orderId, decimal amount) =>
        await Task.FromResult(services.Charge(orderId, amount));

    [SkailCommand]
    public async SkailTask<TaxInvoice> IssueInvoice(string orderId, string product, decimal amount) =>
        await Task.FromResult(services.IssueInvoice(orderId, product, amount));

    // Compensations

    [SkailCommand]
    public async SkailTask RefundCharge(string chargeId)
    {
        services.Refund(chargeId);
        await Task.CompletedTask;
    }

    [SkailCommand]
    public async SkailTask ReleaseStock(string reservationId)
    {
        services.Release(reservationId);
        await Task.CompletedTask;
    }

    /// <summary>Prints how the order ended and returns that result to the function.</summary>
    [SkailCommand]
    public async SkailTask<string> ReportResult(string orderId, string result)
    {
        await Console.Out.WriteLineAsync($"[{orderId}] result: {result}");
        return result;
    }
}
