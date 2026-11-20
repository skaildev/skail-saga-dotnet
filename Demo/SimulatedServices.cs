using System.Globalization;
using System.Text.Json;

namespace SkailSaga.Demo;

/// <summary>
/// The outside world of the saga, simulated: the stock, the payment gateway and the invoice issuer. In your system these
/// would be other services; here they print each effect to the console. Every call is idempotent by order id (or by
/// reservation and charge id), because a retried command runs again.
/// The state is saved to .saga-demo.json, so it survives an app restart in the middle of a run.
/// </summary>
public sealed class SimulatedServices
{
    /// <summary>Products with a tax registration: any other product gets its invoice rejected.</summary>
    private static readonly HashSet<string> WithTaxRegistration = ["Monitor", "Teclado"];

    private readonly object gate = new();
    private readonly string path;
    private readonly State state;

    private SimulatedServices(string path, State state)
    {
        this.path = path;
        this.state = state;
    }

    /// <summary>Starts over: full stock, no orders.</summary>
    public static SimulatedServices Fresh(string path)
    {
        var services = new SimulatedServices(path, new State());
        services.Save();
        return services;
    }

    /// <summary>Picks up the state saved by a previous run of the app.</summary>
    public static SimulatedServices Resume(string path) =>
        new(path, File.Exists(path) ? JsonSerializer.Deserialize<State>(File.ReadAllText(path))! : new State());

    /// <summary>Creates an order and keeps its card on file. The order that goes to skail has no card.</summary>
    public Order PlaceOrder(string product, int quantity, decimal amount, string card)
    {
        lock (gate)
        {
            var order = new Order($"P-{state.NextOrder++}", product, quantity, amount);
            state.Cards[order.Id] = card;
            Save();
            return order;
        }
    }

    // --- stock ----------------------------------------------------------------------------------------------------

    public Reservation Reserve(string orderId, string product, int quantity)
    {
        lock (gate)
        {
            if (state.Reservations.TryGetValue(orderId, out var known)) return known;

            var available = state.Stock.GetValueOrDefault(product);
            Reservation reservation;
            if (available >= quantity)
            {
                state.Stock[product] = available - quantity;
                reservation = new Reservation(true, $"RES-{orderId}");
                state.Holds[reservation.Id!] = new Hold(orderId, product, quantity);
                Print(orderId, $"stock: {quantity} × {product} reserved");
            }
            else
            {
                reservation = new Reservation(false, null);
                Print(orderId, $"stock: {quantity} × {product} requested, {available} available");
            }

            state.Reservations[orderId] = reservation;
            Save();
            return reservation;
        }
    }

    /// <summary>Compensation.</summary>
    public void Release(string reservationId)
    {
        lock (gate)
        {
            if (!state.Holds.TryGetValue(reservationId, out var hold) || hold.Released) return;

            hold.Released = true;
            state.Stock[hold.Product] = state.Stock.GetValueOrDefault(hold.Product) + hold.Quantity;
            Print(hold.OrderId, $"↩ stock: {hold.Quantity} × {hold.Product} back to stock");
            Save();
        }
    }

    // --- payment gateway ------------------------------------------------------------------------------------------

    /// <summary>
    /// Charges the card on file for the order. The first attempt of every order times out, so the skail retry shows up;
    /// a card ending in 0002 is declined; any other card is approved.
    /// </summary>
    public Charge Charge(string orderId, decimal amount)
    {
        lock (gate)
        {
            if (state.Charges.TryGetValue(orderId, out var known)) return known.Charge;

            var attempt = state.Attempts[orderId] = state.Attempts.GetValueOrDefault(orderId) + 1;
            if (attempt == 1)
            {
                Print(orderId, "gateway: no answer in time (attempt 1), skail will try again");
                Save();
                throw new TimeoutException("The payment gateway did not answer in time");
            }

            var card = state.Cards.GetValueOrDefault(orderId) ?? "";
            Charge charge;
            if (card.EndsWith("0002", StringComparison.Ordinal))
            {
                charge = new Charge(false, null, "insufficient funds");
                Print(orderId, $"gateway: card …{card[^4..]} declined, {charge.Reason}");
            }
            else
            {
                charge = new Charge(true, $"CH-{orderId}", null);
                Print(orderId, $"gateway: {Money(amount)} charged to card …{card[^4..]} (attempt {attempt})");
            }

            state.Charges[orderId] = new ChargeRow(orderId, charge, amount);
            Save();
            return charge;
        }
    }

    /// <summary>Compensation.</summary>
    public void Refund(string chargeId)
    {
        lock (gate)
        {
            var row = state.Charges.Values.FirstOrDefault(c => c.Charge.Id == chargeId);
            if (row is null || row.Refunded) return;

            row.Refunded = true;
            Print(row.OrderId, $"↩ gateway: {Money(row.Amount)} refunded");
            Save();
        }
    }

    // --- invoice issuer -------------------------------------------------------------------------------------------

    public TaxInvoice IssueInvoice(string orderId, string product, decimal amount)
    {
        lock (gate)
        {
            if (state.Invoices.TryGetValue(orderId, out var known)) return known;

            TaxInvoice invoice;
            if (WithTaxRegistration.Contains(product))
            {
                invoice = new TaxInvoice(true, $"NF-{state.NextInvoice++}", null);
                Print(orderId, $"invoice: {invoice.Number} issued, {Money(amount)}");
            }
            else
            {
                invoice = new TaxInvoice(false, null, $"{product} has no tax registration");
                Print(orderId, $"invoice: rejected, {invoice.Reason}");
            }

            state.Invoices[orderId] = invoice;
            Save();
            return invoice;
        }
    }

    public static string Money(decimal value) => "R$ " + value.ToString("N2", CultureInfo.InvariantCulture);

    private static void Print(string orderId, string message) => Console.WriteLine($"[{orderId}] {message}");

    private void Save() => File.WriteAllText(path, JsonSerializer.Serialize(state));

    public sealed class State
    {
        public Dictionary<string, int> Stock { get; set; } = new() { ["Monitor"] = 5, ["Teclado"] = 20, ["Cabo HDMI"] = 100 };
        public Dictionary<string, string> Cards { get; set; } = [];
        public Dictionary<string, Reservation> Reservations { get; set; } = [];
        public Dictionary<string, Hold> Holds { get; set; } = [];
        public Dictionary<string, int> Attempts { get; set; } = [];
        public Dictionary<string, ChargeRow> Charges { get; set; } = [];
        public Dictionary<string, TaxInvoice> Invoices { get; set; } = [];
        public int NextOrder { get; set; } = 10;
        public int NextInvoice { get; set; } = 10001;
    }

    public sealed record Hold(string OrderId, string Product, int Quantity)
    {
        public bool Released { get; set; }
    }

    public sealed record ChargeRow(string OrderId, Charge Charge, decimal Amount)
    {
        public bool Refunded { get; set; }
    }
}
