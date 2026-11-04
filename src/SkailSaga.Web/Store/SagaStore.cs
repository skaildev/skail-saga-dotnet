using System.Threading.Channels;
using SkailSaga.Contracts;

namespace SkailSaga.Web.Store;

/// <summary>Result of a charge attempt at the simulated gateway: it either did not answer in time, or answered.</summary>
public readonly record struct ChargeOutcome(bool TimedOut, Charge? Charge);

/// <summary>
/// The simulated outside world of the saga (stock, payment gateway, invoice issuer, customer messaging) and the view of
/// the orders, in memory. Every effect is recorded as a step of its order, in the order it happened, so the page shows
/// the compensations running backwards. Every write that comes from the worker is idempotent, because a command that is
/// retried runs again.
/// </summary>
public sealed class SagaStore
{
    private const int FirstOrderNumber = 10;

    private sealed class OrderRow(Order order, string card, Guid taskId)
    {
        public Order Order { get; } = order;
        /// <summary>The card on file: the gateway charges it by order id, so it never goes into the run.</summary>
        public string Card { get; } = card;
        public Guid TaskId { get; } = taskId;
        public bool Accepted { get; set; }
        public OrderState State { get; set; } = OrderState.Received;
        public string? Result { get; set; }
        public List<StepView> Steps { get; } = [];
    }

    private sealed class Hold(string orderId, string product, int quantity)
    {
        public string OrderId { get; } = orderId;
        public string Product { get; } = product;
        public int Quantity { get; } = quantity;
        public bool Released { get; set; }
    }

    private sealed class ChargeRow(string orderId, Charge charge, decimal amount)
    {
        public string OrderId { get; } = orderId;
        public Charge Charge { get; } = charge;
        public decimal Amount { get; } = amount;
        public bool Refunded { get; set; }
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, int> _stock = SeedStock();
    private readonly Dictionary<string, Reservation> _reservations = [];   // by order id
    private readonly Dictionary<string, Hold> _holds = [];                 // by reservation id
    private readonly Dictionary<string, int> _attempts = [];               // charge attempts by order id
    private readonly Dictionary<string, ChargeRow> _charges = [];          // by order id, the idempotency key
    private readonly Dictionary<string, TaxInvoice> _invoices = [];        // by order id
    private readonly HashSet<string> _messages = [];
    private readonly List<OrderRow> _orders = [];
    private readonly List<Channel<StateView>> _subscribers = [];
    private int _nextOrder = FirstOrderNumber;
    private int _nextInvoice = 10001;

    /// <summary>Products with a tax registration: any other product gets its invoice rejected.</summary>
    private static readonly HashSet<string> WithTaxRegistration = ["Monitor", "Teclado"];

    private static Dictionary<string, int> SeedStock() => new()
    {
        ["Monitor"] = 5,
        ["Teclado"] = 20,
        ["Cabo HDMI"] = 100,
    };

    // --- placing orders (the page, or a curl) -----------------------------------------------------------------------

    /// <summary>
    /// Creates the order the saga will close and keeps its card on file. The returned order, which goes to skail, has no
    /// card. The order id doubles as the payment idempotency key.
    /// </summary>
    public Order CreateOrder(NewOrder request, Guid taskId)
    {
        lock (_gate)
        {
            var order = new Order($"P-{_nextOrder++}", request.Product.Trim(), request.Quantity, request.Amount);
            _orders.Add(new OrderRow(order, request.Card.Trim(), taskId));
            Publish();
            return order;
        }
    }

    /// <summary>skail accepted the trigger.</summary>
    public void MarkAccepted(string orderId)
    {
        lock (_gate)
        {
            if (Find(orderId) is not { } row) return;
            row.Accepted = true;
            if (row.State == OrderState.NotAccepted) row.State = OrderState.Received;
            Publish();
        }
    }

    /// <summary>skail did not accept the trigger: nothing runs for this order.</summary>
    public void MarkNotAccepted(string orderId)
    {
        lock (_gate)
        {
            if (Find(orderId) is not { } row || row.Accepted || row.State != OrderState.Received) return;
            row.State = OrderState.NotAccepted;
            Publish();
        }
    }

    // --- stock ------------------------------------------------------------------------------------------------------

    /// <summary>Reserves the stock. Idempotent by order id: a retried command gets the same answer and no second reservation.</summary>
    public Reservation Reserve(ReserveRequest request)
    {
        lock (_gate)
        {
            if (_reservations.TryGetValue(request.OrderId, out var known)) return known;

            var available = _stock.TryGetValue(request.Product, out var total) ? total - ReservedOf(request.Product) : 0;
            Reservation reservation;
            if (request.Quantity > 0 && available >= request.Quantity)
            {
                reservation = new Reservation(true, $"RES-{request.OrderId}");
                _holds[reservation.Id!] = new Hold(request.OrderId, request.Product, request.Quantity);
                AddStep(request.OrderId, StepKind.StockReserved, $"{request.Quantity} × {request.Product} reserved", false);
            }
            else
            {
                reservation = new Reservation(false, null);
                AddStep(request.OrderId, StepKind.OutOfStock,
                    $"{request.Quantity} × {request.Product} requested, {available} available", false);
            }

            _reservations[request.OrderId] = reservation;
            Publish();
            return reservation;
        }
    }

    /// <summary>Compensation. Idempotent by reservation id. Returns false when there is no such reservation.</summary>
    public bool ReleaseStock(string reservationId)
    {
        lock (_gate)
        {
            if (!_holds.TryGetValue(reservationId, out var hold)) return false;
            if (!hold.Released)
            {
                hold.Released = true;
                AddStep(hold.OrderId, StepKind.StockReleased, $"{hold.Quantity} × {hold.Product} back to stock", true);
                Publish();
            }
            return true;
        }
    }

    // --- payment gateway --------------------------------------------------------------------------------------------

    /// <summary>
    /// The simulated gateway charges the card on file for the order. The first attempt of each order times out (the
    /// skail retry shows up), a card ending in 0002 is declined, anything else is approved. Idempotent by order id: an
    /// order that was already charged is not charged again, it gets the same answer.
    /// </summary>
    public ChargeOutcome ChargeCard(ChargeRequest request)
    {
        var (orderId, amount) = request;
        lock (_gate)
        {
            if (_charges.TryGetValue(orderId, out var known)) return new ChargeOutcome(false, known.Charge);

            var attempt = _attempts[orderId] = _attempts.GetValueOrDefault(orderId) + 1;
            if (attempt == 1)
            {
                AddStep(orderId, StepKind.ChargeTimeout, "the gateway did not answer in time (attempt 1), skail tries again", false);
                Publish();
                return new ChargeOutcome(true, null);
            }

            var card = Find(orderId)?.Card;
            Charge charge;
            if (card is null)
            {
                charge = Charge.Declined("no card on file");
            }
            else if (card.EndsWith("0002", StringComparison.Ordinal))
            {
                charge = Charge.Declined("insufficient funds");
                AddStep(orderId, StepKind.ChargeDeclined, $"card declined: {charge.Reason}", false);
            }
            else
            {
                charge = new Charge(true, $"COB-{orderId}", null);
                AddStep(orderId, StepKind.ChargeApproved, $"{Money(amount)} charged (attempt {attempt})", false);
            }

            _charges[orderId] = new ChargeRow(orderId, charge, amount);
            Publish();
            return new ChargeOutcome(false, charge);
        }
    }

    /// <summary>Compensation. Idempotent by charge id. Returns false when there is no such charge.</summary>
    public bool Refund(string chargeId)
    {
        lock (_gate)
        {
            var row = _charges.Values.FirstOrDefault(c => c.Charge.Id == chargeId);
            if (row is null) return false;
            if (!row.Refunded)
            {
                row.Refunded = true;
                AddStep(row.OrderId, StepKind.ChargeRefunded, $"{Money(row.Amount)} refunded", true);
                Publish();
            }
            return true;
        }
    }

    // --- invoice issuer ---------------------------------------------------------------------------------------------

    /// <summary>The simulated issuer: only products with a tax registration get an authorized invoice. Idempotent by order id.</summary>
    public TaxInvoice IssueInvoice(InvoiceRequest request)
    {
        lock (_gate)
        {
            if (_invoices.TryGetValue(request.OrderId, out var known)) return known;

            TaxInvoice invoice;
            if (WithTaxRegistration.Contains(request.Product))
            {
                invoice = new TaxInvoice(true, $"NF-{_nextInvoice++}", null);
                AddStep(request.OrderId, StepKind.InvoiceAuthorized, $"invoice {invoice.Number} issued", false);
            }
            else
            {
                invoice = TaxInvoice.Rejected($"{request.Product} has no NCM registered");
                AddStep(request.OrderId, StepKind.InvoiceRejected, $"invoice rejected: {invoice.Reason}", false);
            }

            _invoices[request.OrderId] = invoice;
            Publish();
            return invoice;
        }
    }

    // --- customer messaging and the result --------------------------------------------------------------------------

    /// <summary>The e-mail to the customer. Idempotent by order id and message.</summary>
    public void NotifyCustomer(string orderId, string message)
    {
        lock (_gate)
        {
            if (!_messages.Add($"{orderId}|{message}")) return;
            AddStep(orderId, StepKind.CustomerNotified, message, false);
            Publish();
        }
    }

    /// <summary>The saga ended. Returns false when the order is unknown or already has a result.</summary>
    public bool RecordResult(string orderId, string result)
    {
        lock (_gate)
        {
            if (Find(orderId) is not { } row || OrderStates.IsTerminal(row.State)) return false;
            row.State = OrderStates.FromResult(result);
            row.Result = result;
            Publish();
            return true;
        }
    }

    // --- the page ---------------------------------------------------------------------------------------------------

    public StateView Snapshot()
    {
        lock (_gate) return BuildView();
    }

    /// <summary>Back to the seed: stock as at the start, no orders. For running the demo again.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _stock.Clear();
            foreach (var (product, quantity) in SeedStock()) _stock[product] = quantity;
            _reservations.Clear();
            _holds.Clear();
            _attempts.Clear();
            _charges.Clear();
            _invoices.Clear();
            _messages.Clear();
            _orders.Clear();
            _nextOrder = FirstOrderNumber;
            _nextInvoice = 10001;
            Publish();
        }
    }

    public Subscription Subscribe()
    {
        // only the latest snapshot matters, so a slow reader just skips the older ones
        var channel = Channel.CreateBounded<StateView>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
        lock (_gate) _subscribers.Add(channel);
        return new Subscription(channel.Reader, () =>
        {
            lock (_gate) _subscribers.Remove(channel);
        });
    }

    // The members below must be called with _gate held.

    private OrderRow? Find(string orderId) => _orders.FirstOrDefault(o => o.Order.Id == orderId);

    private int ReservedOf(string product) =>
        _holds.Values.Where(h => h.Product == product && !h.Released).Sum(h => h.Quantity);

    private void AddStep(string orderId, StepKind kind, string detail, bool compensation)
    {
        if (Find(orderId) is not { } row) return;
        row.Steps.Add(new StepView(kind, detail, compensation));
        if (row.State == OrderState.Received) row.State = OrderState.Running;
    }

    private StateView BuildView() => new(
        _stock.Select(s => new StockView(s.Key, s.Value - ReservedOf(s.Key), ReservedOf(s.Key))).ToList(),
        _orders.Select(o => new OrderView(
            o.Order.Id, o.Order.Product, o.Order.Quantity, o.Order.Amount, MaskCard(o.Card), o.TaskId,
            o.State, o.Result, o.Steps.ToList())).ToList());

    private void Publish()
    {
        var view = BuildView();
        foreach (var channel in _subscribers) channel.Writer.TryWrite(view);
    }

    private static string MaskCard(string card) => card.Length > 4 ? $"•••• {card[^4..]}" : card;

    private static string Money(decimal value) => "R$ " + value.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);

    public sealed class Subscription(ChannelReader<StateView> reader, Action onDispose) : IDisposable
    {
        public ChannelReader<StateView> Reader { get; } = reader;
        public void Dispose() => onDispose();
    }
}
