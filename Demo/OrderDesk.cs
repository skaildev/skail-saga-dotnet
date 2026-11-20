using SkailSaga.Skail;

namespace SkailSaga.Demo;

/// <summary>Places the demo orders for the page and starts one CloseOrder run for each.</summary>
public sealed class OrderDesk(SimulatedServices services, SkailApiClient skail, Timeline timeline)
{
    public async Task<bool> PlaceAsync(string path, CancellationToken ct)
    {
        var demos = path == "all" ? DemoOrders.All : DemoOrders.Find(path) is { } one ? [one] : [];
        foreach (var demo in demos)
        {
            var order = services.PlaceOrder(demo.Product, demo.Quantity, demo.Amount, demo.Card, demo.Expected);
            await skail.TriggerAsync(Guid.NewGuid().ToString(), order, ct);
            timeline.Add(order.Id, $"order placed: {order.Quantity} × {order.Product}, {SimulatedServices.Money(order.Amount)}, " +
                $"card …{demo.Card[^4..]}", "placed");
        }
        return demos.Count > 0;
    }

    public void Reset() => services.Reset();

    /// <summary>What the page shows: the stock, the orders with their result, and the timeline.</summary>
    public object View()
    {
        var (stock, orders) = services.Snapshot();
        var entries = timeline.All();
        return new
        {
            stock = stock.Select(s => new { product = s.Key, available = s.Value }),
            orders = orders.Select(o => new
            {
                o.Id, o.Product, o.Quantity, o.Amount, o.CardLast4, o.Expected,
                result = entries.LastOrDefault(e => e.RunId == o.Id && e.Step?.StartsWith("end:") == true)?.Step?[4..],
            }).Reverse(),
            timeline = entries.Select(e => new { at = e.At, order = e.RunId, text = e.Text }),
        };
    }
}
