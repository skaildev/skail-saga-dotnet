namespace SkailSaga.Demo;

/// <summary>One order per path of the saga, set up for the rules of the simulated services.</summary>
public sealed record DemoOrder(string Path, string Product, int Quantity, decimal Amount, string Card, string Expected);

public static class DemoOrders
{
    public static readonly IReadOnlyList<DemoOrder> All =
    [
        new("confirmed", "Monitor", 1, 1200m, "4111111111111111", SagaResults.Confirmed),
        new("declined", "Monitor", 1, 1200m, "4000000000000002", SagaResults.PaymentDeclined),
        new("invoice", "Cabo HDMI", 2, 80m, "4111111111111111", SagaResults.CancelledWithRefund),
        new("stock", "Monitor", 6, 7200m, "4111111111111111", SagaResults.OutOfStock),
    ];

    public static DemoOrder? Find(string path) =>
        All.FirstOrDefault(o => string.Equals(o.Path, path, StringComparison.OrdinalIgnoreCase));
}
