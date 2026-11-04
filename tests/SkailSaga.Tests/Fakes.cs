using System.Net;
using SkailSaga.Contracts;
using SkailSaga.Web.Skail;

namespace SkailSaga.Tests;

/// <summary>HttpMessageHandler that returns scripted responses and records the requests.</summary>
public sealed class StubHandler(Func<HttpRequestMessage, int, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<(HttpRequestMessage Request, string Body)> Calls { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        Calls.Add((request, body));
        return respond(request, Calls.Count);
    }

    public static StubHandler Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new((_, _) => new HttpResponseMessage(status) { Content = new StringContent(json) });

    public static StubHandler Status(HttpStatusCode status) =>
        new((_, _) => new HttpResponseMessage(status));
}

/// <summary>Stands in for skail in the web tests: records the triggers, or fails like an unavailable server.</summary>
public sealed class FakeSkailGateway : ISkailGateway
{
    public List<(Guid TaskId, Order Order)> Triggers { get; } = [];
    public bool Fail { get; set; }

    public Task TriggerAsync(Guid taskId, Order order, CancellationToken ct = default)
    {
        if (Fail) throw new SkailGatewayException("skail answered 503");
        Triggers.Add((taskId, order));
        return Task.CompletedTask;
    }
}
