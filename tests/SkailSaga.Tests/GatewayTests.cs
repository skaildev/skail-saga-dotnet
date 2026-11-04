using System.Net;
using System.Text.Json;
using SkailSaga.Contracts;
using SkailSaga.Web.Skail;

namespace SkailSaga.Tests;

public class GatewayTests
{
    private static readonly SkailOptions Opts = new()
    {
        BaseUrl = "https://skail.example.test/",
        Namespace = "tenant-1/env-1",
        Key = "env-key",
        Workload = "saga:v0.0.1",
    };

    private static readonly Order Order = new("P-10", "Monitor", 1, 1200m);
    private static readonly Guid TaskId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private static SkailApiGateway Gateway(StubHandler handler, SkailOptions? options = null) =>
        new(new HttpClient(handler), options ?? Opts, retryDelay: _ => TimeSpan.Zero);

    [Fact]
    public async Task Trigger_posts_the_order_to_the_function_route_with_the_task_id()
    {
        var handler = StubHandler.Status(HttpStatusCode.OK);

        await Gateway(handler).TriggerAsync(TaskId, Order);

        var (request, body) = Assert.Single(handler.Calls);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal($"https://skail.example.test/api/v1/trigger/saga/v0.0.1/CloseOrder/{TaskId}", request.RequestUri!.ToString());
        Assert.Equal("env-key", request.Headers.GetValues("skail-key").Single());
        Assert.Equal("Bearer env-key", request.Headers.GetValues("Authorization").Single());
        Assert.Equal("tenant-1/env-1", request.Headers.GetValues("Skail-namespace").Single());
        Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
        Assert.Contains(request.Headers.Accept, a => a.MediaType == "application/json");
        Assert.Equal("""[{"Id":"P-10","Product":"Monitor","Quantity":1,"Amount":1200}]""", body);
    }

    [Fact]
    public async Task The_body_is_an_array_with_one_argument_in_PascalCase()
    {
        var handler = StubHandler.Status(HttpStatusCode.OK);

        await Gateway(handler).TriggerAsync(TaskId, Order);

        using var doc = JsonDocument.Parse(Assert.Single(handler.Calls).Body);
        var argument = Assert.Single(doc.RootElement.EnumerateArray());
        Assert.Equal("P-10", argument.GetProperty("Id").GetString());
        Assert.False(argument.TryGetProperty("id", out _));
    }

    [Fact]
    public void Options_read_the_api_base_url_from_SKAIL_BASE_URL()
    {
        var names = new[] { "SKAIL_BASE_URL", "SKAIL_NAMESPACE", "SKAIL_KEY", "SKAIL_WORKLOAD" };
        var saved = names.ToDictionary(n => n, Environment.GetEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable("SKAIL_BASE_URL", "https://env.example.test");
            Environment.SetEnvironmentVariable("SKAIL_NAMESPACE", "t/e");
            Environment.SetEnvironmentVariable("SKAIL_KEY", "k");
            Environment.SetEnvironmentVariable("SKAIL_WORKLOAD", "w:v1");

            var options = SkailOptions.FromEnvironment();

            Assert.Equal("https://env.example.test", options.BaseUrl);
            Assert.Equal(("t/e", "k", "w:v1"), (options.Namespace, options.Key, options.Workload));
        }
        finally
        {
            foreach (var (name, value) in saved) Environment.SetEnvironmentVariable(name, value);
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task Transient_failures_are_retried_with_the_same_id_and_body(HttpStatusCode failure)
    {
        var handler = new StubHandler((_, n) => new HttpResponseMessage(n < 3 ? failure : HttpStatusCode.OK));

        await Gateway(handler).TriggerAsync(TaskId, Order);

        Assert.Equal(3, handler.Calls.Count);
        Assert.Single(handler.Calls.Select(c => c.Request.RequestUri).Distinct());
        Assert.Single(handler.Calls.Select(c => c.Body).Distinct());
    }

    [Fact]
    public async Task A_network_error_is_retried()
    {
        var handler = new StubHandler((_, n) => n < 2 ? throw new HttpRequestException("connection refused") : new HttpResponseMessage(HttpStatusCode.OK));

        await Gateway(handler).TriggerAsync(TaskId, Order);

        Assert.Equal(2, handler.Calls.Count);
    }

    [Fact]
    public async Task Gives_up_after_the_retry_budget_and_throws()
    {
        var handler = StubHandler.Status(HttpStatusCode.ServiceUnavailable);

        await Assert.ThrowsAsync<SkailGatewayException>(() => Gateway(handler).TriggerAsync(TaskId, Order));

        Assert.Equal(SkailApiGateway.MaxAttempts, handler.Calls.Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Auth_failures_are_not_retried_and_say_what_to_check(HttpStatusCode status)
    {
        var handler = StubHandler.Status(status);

        var error = await Assert.ThrowsAsync<SkailGatewayException>(() => Gateway(handler).TriggerAsync(TaskId, Order));

        Assert.Single(handler.Calls);
        Assert.Contains(((int)status).ToString(), error.Message);
        Assert.Contains("SKAIL_KEY", error.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("saga")]
    [InlineData(":v1")]
    [InlineData("saga:")]
    public async Task Invalid_workload_fails_before_any_call(string workload)
    {
        var handler = StubHandler.Status(HttpStatusCode.OK);
        var gateway = Gateway(handler, new SkailOptions { BaseUrl = Opts.BaseUrl, Namespace = "n", Key = "k", Workload = workload });

        var error = await Assert.ThrowsAsync<SkailGatewayException>(() => gateway.TriggerAsync(TaskId, Order));

        Assert.Contains("SKAIL_WORKLOAD", error.Message);
        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task Missing_base_url_fails_before_any_call()
    {
        var handler = StubHandler.Status(HttpStatusCode.OK);
        var gateway = Gateway(handler, new SkailOptions { Namespace = "n", Key = "k", Workload = "a:b" });

        var error = await Assert.ThrowsAsync<SkailGatewayException>(() => gateway.TriggerAsync(TaskId, Order));

        Assert.Contains("SKAIL_BASE_URL", error.Message);
        Assert.Empty(handler.Calls);
    }
}
