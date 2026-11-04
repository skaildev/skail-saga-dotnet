using SkailSaga.Contracts;

namespace SkailSaga.Web.Skail;

public sealed class SkailGatewayException(string message, Exception? inner = null) : Exception(message, inner);

public interface ISkailGateway
{
    /// <summary>Starts the CloseOrder function; <paramref name="taskId"/> is the execution id.</summary>
    Task TriggerAsync(Guid taskId, Order order, CancellationToken ct = default);
}
