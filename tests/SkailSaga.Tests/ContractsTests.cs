using System.Text.Json;
using SkailSaga.Contracts;
using SkailSaga.Worker;

namespace SkailSaga.Tests;

public class ContractsTests
{
    [Fact]
    public void Trigger_arguments_are_written_in_PascalCase_for_the_skail_runtime()
    {
        var json = JsonSerializer.Serialize(new object[] { new Order("P-10", "Monitor", 1, 1200m) }, Json.SkailArguments);

        Assert.Equal("""[{"Id":"P-10","Product":"Monitor","Quantity":1,"Amount":1200}]""", json);
    }

    [Fact]
    public void The_order_api_reads_camelCase()
    {
        var order = JsonSerializer.Deserialize<NewOrder>(
            """{"product":"Monitor","quantity":2,"amount":99.9,"card":"4111"}""", Json.Options);

        Assert.Equal(new NewOrder("Monitor", 2, 99.9m, "4111"), order);
    }

    [Fact]
    public void Enums_travel_as_strings_to_the_page()
    {
        var json = JsonSerializer.Serialize(new StepView(StepKind.ChargeRefunded, "x", true), Json.Options);

        Assert.Contains("\"kind\":\"ChargeRefunded\"", json);
        Assert.Contains("\"compensation\":true", json);
    }

    [Theory]
    [InlineData(SagaResults.Confirmed, OrderState.Confirmed)]
    [InlineData(SagaResults.OutOfStock, OrderState.OutOfStock)]
    [InlineData(SagaResults.PaymentDeclined, OrderState.Declined)]
    [InlineData(SagaResults.CancelledWithRefund, OrderState.Refunded)]
    public void Each_result_of_the_saga_is_its_own_state(string result, OrderState expected)
    {
        Assert.Equal(expected, OrderStates.FromResult(result));
        Assert.True(OrderStates.IsTerminal(expected));
    }

    [Fact]
    public void An_unknown_result_is_not_terminal()
    {
        Assert.False(OrderStates.IsTerminal(OrderStates.FromResult("something else")));
    }

    [Fact]
    public void The_trigger_function_name_is_the_name_of_the_worker_method()
    {
        Assert.Equal(nameof(OrderFunctions.CloseOrder), SkailFunctions.CloseOrder);
    }
}
