namespace BudgetService.Application.Interfaces;

public interface IKafkaPublisher
{
    Task PublishAsync<T>(string topic, T message, CancellationToken ct = default) where T : class;
}
