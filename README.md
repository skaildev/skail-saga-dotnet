# skail saga (.NET 8)

[Português (Brasil)](README.pt-BR.md)

A console app with a skail function that closes an order: it reserves the stock, charges the card and issues the invoice. When a step does not work out, it undoes the previous ones in reverse order.

It shows three skail features:

- the **saga** pattern: each step and each compensation is a command, and the compensations run in reverse order;
- **automatic retries**: the first charge attempt of every order times out, and skail tries again by itself;
- **durability**: close the app in the middle of an order, start it again and the order goes on from where it was, without reserving the stock or charging the card twice.

## Requirements

- One of: [Docker](https://docs.docker.com/get-docker/), [Podman](https://podman.io/docs/installation) or the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (or newer). The run script uses the first one it finds.
- A skail environment with a workload registered for the `SKAIL_WORKLOAD` in `.env` (`demosaga:v1.0.0` by default). The [Quickstart](https://docs.skail.dev/comece-aqui/quickstart) shows how to create the account, the environment and the workload.

The project uses `Skail.Platform.Runtime` **2.3.0-rc.4**.

## Run

```bash
cp .env.example .env      # fill in SKAIL_SIDECAR, SKAIL_NAMESPACE and SKAIL_KEY (portal > Environments & keys > Connect)
scripts/run.sh            # one order per path
scripts/run.sh confirmed  # or declined, invoice, stock: only that order
scripts/run.sh resume     # no new orders: finishes the ones left in the middle
```

On Windows, in PowerShell: `copy .env.example .env`, then `powershell -ExecutionPolicy Bypass -File scripts\run.ps1` with the same arguments. The script works on the Windows PowerShell built into Windows; PowerShell 7 is not needed.

The script runs the demo with what the machine has: Docker, then Podman, then the .NET SDK. If it finds none of them, it says what to install. Without the script, the commands are `dotnet run` (with the arguments after `--`, as in `dotnet run -- <argument>`).

```text
[P-10] order placed: 1 × Monitor, R$ 1,200.00, card …1111 (expected: confirmed)
[P-11] order placed: 1 × Monitor, R$ 1,200.00, card …0002 (expected: payment declined)
[P-12] order placed: 2 × Cabo HDMI, R$ 80.00, card …1111 (expected: cancelled with refund)
[P-13] order placed: 6 × Monitor, R$ 7,200.00, card …1111 (expected: out of stock)
Press Ctrl+C to exit. A run left in the middle goes on when you start the app again with the argument resume.
[P-10] stock: 1 × Monitor reserved
[P-10] gateway: no answer in time (attempt 1), skail will try again
[P-11] stock: 1 × Monitor reserved
[P-11] gateway: no answer in time (attempt 1), skail will try again
[P-12] stock: 2 × Cabo HDMI reserved
[P-12] gateway: no answer in time (attempt 1), skail will try again
[P-13] stock: 6 × Monitor requested, 3 available
[P-13] result: out of stock
[P-10] gateway: R$ 1,200.00 charged to card …1111 (attempt 2)
[P-10] invoice: NF-10001 issued, R$ 1,200.00
[P-10] result: confirmed
[P-11] gateway: card …0002 declined, insufficient funds
[P-11] ↩ stock: 1 × Monitor back to stock
[P-11] result: payment declined
[P-12] gateway: R$ 80.00 charged to card …1111 (attempt 2)
[P-12] invoice: rejected, Cabo HDMI has no tax registration
[P-12] ↩ gateway: R$ 80.00 refunded
[P-12] ↩ stock: 2 × Cabo HDMI back to stock
[P-12] result: cancelled with refund
```

Lines with ↩ are compensations. The new charge attempt comes about 15 seconds after the timeout.

## The four paths

| Path | Order | What happens | Result |
|---|---|---|---|
| `confirmed` | 1 Monitor | reserved, timeout, charged, invoice issued | `confirmed` |
| `declined` | 1 Monitor, card `…0002` | reserved, timeout, declined, ↩ stock released | `payment declined` |
| `invoice` | 2 Cabo HDMI | reserved, timeout, charged, invoice rejected, ↩ refunded, ↩ stock released | `cancelled with refund` |
| `stock` | 6 Monitors (5 in stock) | out of stock | `out of stock` |

The simulated services are set up for these paths: the first charge attempt of every order times out, a card ending in `0002` is declined, and only `Monitor` and `Teclado` have a tax registration, so any other product gets its invoice rejected.

## How it works

The app does three jobs in one process: it runs the simulated services (stock, payment gateway and invoice issuer, which print each effect), it places the orders and starts one run per order through the skail HTTP API, and it hosts the worker that runs the function (`builder.UseSkail()`).

```http
POST {SKAIL_SIDECAR}/api/v1/trigger/demosaga/v1.0.0/CloseOrder/{runId}
[{"Id": "P-10", "Product": "Monitor", "Quantity": 1, "Amount": 1200}]
```

The function, in [`OrderFunctions.cs`](OrderFunctions.cs):

```csharp
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
```

- What the business refuses (no stock, a declined card, a rejected invoice) comes back as a result, and the function handles it with an `if`. Another attempt would not change the answer, so it is not an exception.
- A gateway timeout throws, and skail runs `ChargeCard` again (`retryCount: 3`). The order id is the idempotency key, so a new attempt does not charge twice.
- The compensations (`RefundCharge`, `ReleaseStock`) are commands too: if the app stops in the middle of them, the ones that already ran do not run again.
- The function receives the order without the card. The simulated store keeps the card on file and the gateway charges it by order id, so the card number never goes into the run history or the Monitor.

| File | What it has |
|---|---|
| `OrderFunctions.cs` | The function and its commands. |
| `Demo/SimulatedServices.cs` | The simulated stock, payment gateway and invoice issuer. |
| `Demo/DemoOrders.cs` | The order of each path. |
| `Demo/OrderPlacer.cs` | Places the orders and starts one run for each. |
| `Skail/SkailApiClient.cs` | The trigger through the skail HTTP API. |
| `Program.cs` | Reads `.env` and the argument, and wires everything up. |
| `scripts/run.sh`, `scripts/run.ps1`, `Dockerfile` | Run the demo with Docker, Podman or .NET, whatever the machine has. |

## See the durability

1. Run `scripts/run.sh confirmed` and wait for `gateway: no answer in time`.
2. Before the new attempt, about 15 seconds later, press **Ctrl+C**. The app closes; the run stays in skail.
3. Run `scripts/run.sh resume`. The order goes on from the charge: the stock is not reserved again, and the card is charged once.

The simulated services keep their state in `.saga-demo.json`, so it survives the restart. `scripts/run.sh` without `resume` starts over, with full stock.

In the portal, open the [Monitor](https://docs.skail.dev/referencia/portal/monitor) of your environment and click a run: on the `invoice` path, `RefundCharge` and `ReleaseStock` come after `IssueInvoice`, in reverse order, and the timeout shows up as a failed attempt of `ChargeCard`. The card number does not show up anywhere.

## Notes

- A saga is not a transaction: between the charge and the refund, the customer was charged. What the function guarantees is that the compensations run, in reverse order, even if the app stops in the middle.
- Here the simulated services always answer in the end. In your system, a service can stay down longer than the retries; see [handling errors and undoing steps](https://docs.skail.dev/construa/como-fazer/tratar-erros-e-desfazer-passos) for undoing the previous steps in that case.
- Apps running the same `SKAIL_WORKLOAD` on the same environment share the same runs: use one workload (or environment) per person.

## Disclaimer

**This is a demo, not production code.** It exists only to show how [skail](https://docs.skail.dev) works and is provided "as is", without warranty of any kind. The stock, the payment gateway and the invoice issuer are simulated, and the card numbers are test numbers: never use a real card in it.

## License

[Apache-2.0](LICENSE)
