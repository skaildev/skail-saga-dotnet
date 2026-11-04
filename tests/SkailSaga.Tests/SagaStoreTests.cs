using SkailSaga.Contracts;
using SkailSaga.Web.Store;

namespace SkailSaga.Tests;

public class SagaStoreTests
{
    private readonly SagaStore _store = new();

    private Order Place(string product = "Monitor", int quantity = 1, decimal amount = 1200m, string card = "4111111111111111") =>
        _store.CreateOrder(new NewOrder(product, quantity, amount, card), Guid.NewGuid());

    private OrderView View(string orderId) => _store.Snapshot().Orders.Single(o => o.OrderId == orderId);

    private StockView Stock(string product) => _store.Snapshot().Stock.Single(s => s.Product == product);

    // ---- orders -----------------------------------------------------------------------------------------------------

    [Fact]
    public void Orders_get_sequential_ids_starting_at_P_10()
    {
        Assert.Equal("P-10", Place().Id);
        Assert.Equal("P-11", Place().Id);
    }

    [Fact]
    public void The_card_stays_on_file_the_order_for_skail_has_none_and_the_view_masks_it()
    {
        var order = Place(card: "4111111111111111");
        Assert.Equal(new Order("P-10", "Monitor", 1, 1200m), order);
        Assert.Equal("•••• 1111", View(order.Id).Card);
    }

    [Fact]
    public void An_order_the_skail_server_did_not_accept_is_marked_as_such_until_accepted()
    {
        var order = Place();
        _store.MarkNotAccepted(order.Id);
        Assert.Equal(OrderState.NotAccepted, View(order.Id).State);

        _store.MarkAccepted(order.Id);
        Assert.Equal(OrderState.Received, View(order.Id).State);
    }

    // ---- stock ------------------------------------------------------------------------------------------------------

    [Fact]
    public void Reserving_takes_units_out_of_the_available_stock()
    {
        var order = Place(quantity: 2);

        var reservation = _store.Reserve(new ReserveRequest(order.Id, "Monitor", 2));

        Assert.True(reservation.Confirmed);
        Assert.Equal($"RES-{order.Id}", reservation.Id);
        Assert.Equal(new StockView("Monitor", 3, 2), Stock("Monitor"));
        Assert.Equal(StepKind.StockReserved, Assert.Single(View(order.Id).Steps).Kind);
    }

    [Fact]
    public void Reserving_twice_for_the_same_order_does_not_take_more_units()
    {
        var order = Place();
        _store.Reserve(new ReserveRequest(order.Id, "Monitor", 1));
        var again = _store.Reserve(new ReserveRequest(order.Id, "Monitor", 1));

        Assert.True(again.Confirmed);
        Assert.Equal(new StockView("Monitor", 4, 1), Stock("Monitor"));
        Assert.Single(View(order.Id).Steps);
    }

    [Fact]
    public void Asking_for_more_than_there_is_gives_a_result_and_reserves_nothing()
    {
        var order = Place(quantity: 6);

        var reservation = _store.Reserve(new ReserveRequest(order.Id, "Monitor", 6));

        Assert.False(reservation.Confirmed);
        Assert.Null(reservation.Id);
        Assert.Equal(new StockView("Monitor", 5, 0), Stock("Monitor"));
        Assert.Equal(StepKind.OutOfStock, Assert.Single(View(order.Id).Steps).Kind);
    }

    [Fact]
    public void An_unknown_product_has_no_stock()
    {
        var order = Place(product: "Mouse");
        Assert.False(_store.Reserve(new ReserveRequest(order.Id, "Mouse", 1)).Confirmed);
    }

    [Fact]
    public void Releasing_puts_the_units_back_once_and_is_marked_as_a_compensation()
    {
        var order = Place();
        var reservation = _store.Reserve(new ReserveRequest(order.Id, "Monitor", 1));

        Assert.True(_store.ReleaseStock(reservation.Id!));
        Assert.True(_store.ReleaseStock(reservation.Id!));

        Assert.Equal(new StockView("Monitor", 5, 0), Stock("Monitor"));
        var steps = View(order.Id).Steps;
        Assert.Equal(2, steps.Count);
        Assert.Equal(new StepView(StepKind.StockReleased, "1 × Monitor back to stock", true), steps[1]);
    }

    [Fact]
    public void Releasing_an_unknown_reservation_is_reported()
    {
        Assert.False(_store.ReleaseStock("RES-nope"));
    }

    // ---- payment gateway --------------------------------------------------------------------------------------------

    [Fact]
    public void The_first_attempt_of_each_order_times_out_and_the_second_goes_through()
    {
        var order = Place();
        var request = new ChargeRequest(order.Id, order.Amount);

        var first = _store.ChargeCard(request);
        var second = _store.ChargeCard(request);

        Assert.True(first.TimedOut);
        Assert.False(second.TimedOut);
        Assert.Equal(new Charge(true, $"COB-{order.Id}", null), second.Charge);
        Assert.Equal([StepKind.ChargeTimeout, StepKind.ChargeApproved], View(order.Id).Steps.Select(s => s.Kind));
    }

    [Fact]
    public void A_charge_that_went_through_is_not_charged_again()
    {
        var order = Place();
        var request = new ChargeRequest(order.Id, order.Amount);
        _store.ChargeCard(request);
        _store.ChargeCard(request);

        var third = _store.ChargeCard(request);

        Assert.False(third.TimedOut);
        Assert.Equal($"COB-{order.Id}", third.Charge!.Id);
        Assert.Equal(2, View(order.Id).Steps.Count);   // the timeout and the one charge
    }

    [Fact]
    public void A_card_ending_in_0002_is_declined_and_it_is_a_result()
    {
        var order = Place(card: "4000000000000002");
        var request = new ChargeRequest(order.Id, order.Amount);
        _store.ChargeCard(request);

        var outcome = _store.ChargeCard(request);

        Assert.False(outcome.TimedOut);
        Assert.False(outcome.Charge!.Approved);
        Assert.Equal("insufficient funds", outcome.Charge.Reason);
        Assert.Equal(StepKind.ChargeDeclined, View(order.Id).Steps[^1].Kind);
    }

    [Fact]
    public void An_order_it_does_not_know_is_declined_because_there_is_no_card_on_file()
    {
        var request = new ChargeRequest("P-99", 1200m);
        _store.ChargeCard(request);

        Assert.Equal(Charge.Declined("no card on file"), _store.ChargeCard(request).Charge);
    }

    [Fact]
    public void Refunding_is_done_once_and_is_marked_as_a_compensation()
    {
        var order = Place(amount: 60m);
        var request = new ChargeRequest(order.Id, order.Amount);
        _store.ChargeCard(request);
        var charge = _store.ChargeCard(request).Charge!;

        Assert.True(_store.Refund(charge.Id!));
        Assert.True(_store.Refund(charge.Id!));

        var refunds = View(order.Id).Steps.Where(s => s.Kind == StepKind.ChargeRefunded).ToList();
        Assert.Equal(new StepView(StepKind.ChargeRefunded, "R$ 60.00 refunded", true), Assert.Single(refunds));
    }

    [Fact]
    public void Refunding_an_unknown_charge_is_reported()
    {
        Assert.False(_store.Refund("COB-nope"));
    }

    // ---- invoice issuer ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Monitor")]
    [InlineData("Teclado")]
    public void Products_with_a_tax_registration_get_an_authorized_invoice(string product)
    {
        var order = Place(product);

        var invoice = _store.IssueInvoice(new InvoiceRequest(order.Id, product, 100m));

        Assert.True(invoice.Authorized);
        Assert.StartsWith("NF-", invoice.Number);
        Assert.Equal(StepKind.InvoiceAuthorized, View(order.Id).Steps.Single().Kind);
    }

    [Fact]
    public void A_product_without_a_tax_registration_gets_its_invoice_rejected()
    {
        var order = Place("Cabo HDMI");

        var invoice = _store.IssueInvoice(new InvoiceRequest(order.Id, "Cabo HDMI", 60m));

        Assert.False(invoice.Authorized);
        Assert.Equal("Cabo HDMI has no NCM registered", invoice.Reason);
        Assert.Equal(StepKind.InvoiceRejected, View(order.Id).Steps.Single().Kind);
    }

    [Fact]
    public void Issuing_twice_for_the_same_order_gives_the_same_invoice()
    {
        var order = Place();
        var first = _store.IssueInvoice(new InvoiceRequest(order.Id, "Monitor", 1200m));
        var second = _store.IssueInvoice(new InvoiceRequest(order.Id, "Monitor", 1200m));

        Assert.Equal(first, second);
        Assert.Single(View(order.Id).Steps);
    }

    // ---- customer and result ----------------------------------------------------------------------------------------

    [Fact]
    public void The_same_message_is_sent_to_the_customer_once()
    {
        var order = Place();
        _store.NotifyCustomer(order.Id, "Order confirmed!");
        _store.NotifyCustomer(order.Id, "Order confirmed!");

        Assert.Equal(new StepView(StepKind.CustomerNotified, "Order confirmed!", false), Assert.Single(View(order.Id).Steps));
    }

    [Fact]
    public void The_first_step_moves_the_order_to_running()
    {
        var order = Place();
        Assert.Equal(OrderState.Received, View(order.Id).State);

        _store.Reserve(new ReserveRequest(order.Id, "Monitor", 1));

        Assert.Equal(OrderState.Running, View(order.Id).State);
    }

    [Theory]
    [InlineData(SagaResults.Confirmed, OrderState.Confirmed)]
    [InlineData(SagaResults.OutOfStock, OrderState.OutOfStock)]
    [InlineData(SagaResults.PaymentDeclined, OrderState.Declined)]
    [InlineData(SagaResults.CancelledWithRefund, OrderState.Refunded)]
    public void Recording_the_result_sets_the_final_state_and_keeps_the_result(string result, OrderState state)
    {
        var order = Place();

        Assert.True(_store.RecordResult(order.Id, result));

        Assert.Equal(state, View(order.Id).State);
        Assert.Equal(result, View(order.Id).Result);
    }

    [Fact]
    public void An_order_with_a_result_ignores_a_second_one_and_an_unknown_order_is_ignored()
    {
        var order = Place();
        _store.RecordResult(order.Id, SagaResults.Confirmed);

        Assert.False(_store.RecordResult(order.Id, SagaResults.PaymentDeclined));
        Assert.False(_store.RecordResult("P-99", SagaResults.Confirmed));
        Assert.Equal(OrderState.Confirmed, View(order.Id).State);
    }

    // ---- the saga, end to end on the store --------------------------------------------------------------------------

    [Fact]
    public void A_rejected_invoice_undoes_the_charge_first_and_then_the_stock()
    {
        var order = Place("Cabo HDMI", 2, 60m);
        var reservation = _store.Reserve(new ReserveRequest(order.Id, "Cabo HDMI", 2));
        var charge = ChargeThrough(order);
        _store.IssueInvoice(new InvoiceRequest(order.Id, "Cabo HDMI", 60m));

        _store.Refund(charge.Id!);
        _store.ReleaseStock(reservation.Id!);

        var kinds = View(order.Id).Steps.Select(s => s.Kind).ToList();
        Assert.Equal(
            [StepKind.StockReserved, StepKind.ChargeTimeout, StepKind.ChargeApproved, StepKind.InvoiceRejected,
             StepKind.ChargeRefunded, StepKind.StockReleased],
            kinds);
        Assert.Equal([false, false, false, false, true, true], View(order.Id).Steps.Select(s => s.Compensation));
    }

    private Charge ChargeThrough(Order order)
    {
        var request = new ChargeRequest(order.Id, order.Amount);
        _store.ChargeCard(request);
        return _store.ChargeCard(request).Charge!;
    }

    // ---- reset and subscribers --------------------------------------------------------------------------------------

    [Fact]
    public void Reset_goes_back_to_the_seed()
    {
        var order = Place();
        _store.Reserve(new ReserveRequest(order.Id, "Monitor", 1));

        _store.Reset();

        Assert.Empty(_store.Snapshot().Orders);
        Assert.Equal(new StockView("Monitor", 5, 0), Stock("Monitor"));
        Assert.Equal("P-10", Place().Id);
    }

    [Fact]
    public async Task Subscribers_get_the_latest_snapshot_after_a_change()
    {
        using var subscription = _store.Subscribe();

        Place();

        var snapshot = await subscription.Reader.ReadAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
        Assert.Single(snapshot.Orders);
    }
}
