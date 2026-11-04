using Skail.Platform.Runtime;
using Skail.Platform.Runtime.Threading;
using SkailSaga.Contracts;

namespace SkailSaga.Worker;

public class OrderFunctions(OrderCommands commands)
{
    /// <summary>
    /// Reserves the stock, charges the card and issues the invoice. When a step does not work out, it undoes the previous
    /// ones in reverse order: an if for what the business refuses, a try/catch for a service that stayed down. The catch
    /// leaves out SkailNonDeterministicException, because it means the code and the run history diverged.
    /// </summary>
    [SkailFunction]
    public async SkailTask<string> CloseOrder(Order order)
    {
        var reservation = await commands.ReserveStock(order.Id, order.Product, order.Quantity);
        if (!reservation.Confirmed)
        {
            await commands.NotifyCustomer(order.Id, "Out of stock. Nothing was charged.");
            return await commands.RecordResult(order.Id, SagaResults.OutOfStock);
        }

        Charge charge;
        try
        {
            charge = await commands.ChargeCard(order.Id, order.Amount);
        }
        catch (Exception ex) when (ex is not SkailNonDeterministicException)
        {
            charge = Charge.Declined("payment gateway down");   // every attempt failed
        }

        if (!charge.Approved)
        {
            await commands.ReleaseStock(reservation.Id!);
            await commands.NotifyCustomer(order.Id, $"Payment not approved ({charge.Reason}). The order was cancelled.");
            return await commands.RecordResult(order.Id, SagaResults.PaymentDeclined);
        }

        TaxInvoice invoice;
        try
        {
            invoice = await commands.IssueInvoice(order.Id, order.Product, order.Amount);
        }
        catch (Exception ex) when (ex is not SkailNonDeterministicException)
        {
            invoice = TaxInvoice.Rejected("invoice issuer down");
        }

        if (!invoice.Authorized)
        {
            await commands.RefundCharge(charge.Id!);         // reverse order: the refund first,
            await commands.ReleaseStock(reservation.Id!);    // then the stock
            await commands.NotifyCustomer(order.Id,
                $"We could not issue the invoice ({invoice.Reason}). The amount was refunded and the order cancelled.");
            return await commands.RecordResult(order.Id, SagaResults.CancelledWithRefund);
        }

        await commands.NotifyCustomer(order.Id, $"Order confirmed! Invoice {invoice.Number}.");
        return await commands.RecordResult(order.Id, SagaResults.Confirmed);
    }
}
