using Microsoft.Extensions.Hosting;
using SkailSaga.Skail;

namespace SkailSaga.Demo;

/// <summary>Places the demo orders and starts one CloseOrder run for each. The worker in this same process runs them.</summary>
public sealed class OrderPlacer(IReadOnlyList<DemoOrder> orders, SimulatedServices services, SkailApiClient skail)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (orders.Count == 0)
            Console.WriteLine("Resuming: no new orders. skail hands this app the runs that were waiting.");

        foreach (var demo in orders)
        {
            var order = services.PlaceOrder(demo.Product, demo.Quantity, demo.Amount, demo.Card);
            await skail.TriggerAsync(Guid.NewGuid().ToString(), order, stoppingToken);
            Console.WriteLine(
                $"[{order.Id}] order placed: {order.Quantity} × {order.Product}, {SimulatedServices.Money(order.Amount)}, " +
                $"card …{demo.Card[^4..]} (expected: {demo.Expected})");
        }

        Console.WriteLine("Press Ctrl+C to exit. A run left in the middle goes on when you start the app again with the argument resume.");
    }
}
