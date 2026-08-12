using System.Text.Json;
using Confluent.Kafka;
using WalletService.Application.Interfaces;

namespace WalletService.Infrastructure.Messaging;

public class KafkaPublisher : IKafkaPublisher, IDisposable
{
    private readonly IProducer<string, string> _producer;
    private readonly ILogger<KafkaPublisher> _log;

    public KafkaPublisher(IConfiguration config, ILogger<KafkaPublisher> log)
    {
        _log = log;
        var cfg = new ProducerConfig
        {
            BootstrapServers      = config["Kafka:BootstrapServers"],
            Acks                  = Acks.All,           // Wait for all in-sync replicas
            EnableIdempotence     = true,               // Exactly-once producer semantics
            MessageSendMaxRetries = 3,
            RetryBackoffMs        = 300,
        };
        _producer = new ProducerBuilder<string, string>(cfg).Build();
    }

    public async Task PublishAsync<T>(string topic, T message, CancellationToken ct = default)
        where T : class
    {
        var payload = JsonSerializer.Serialize(message);
        var key     = Guid.NewGuid().ToString();

        try
        {
            var result = await _producer.ProduceAsync(topic,
                new Message<string, string> { Key = key, Value = payload }, ct);

            _log.LogInformation(
                "Published {Topic} | offset={Offset} | key={Key}",
                topic, result.Offset, key);
        }
        catch (ProduceException<string, string> ex)
        {
            _log.LogError(ex, "Failed to publish to topic {Topic}: {Error}", topic, ex.Error.Reason);
            throw;
        }
    }

    public void Dispose() => _producer.Dispose();
}
