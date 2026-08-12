using System.Text.Json;
using Confluent.Kafka;
using BudgetService.Application.Interfaces;

namespace BudgetService.Infrastructure.Messaging;

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
            Acks                  = Acks.All,
            EnableIdempotence     = true,
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

            _log.LogInformation("Published {Topic} | offset={Offset}", topic, result.Offset);
        }
        catch (ProduceException<string, string> ex)
        {
            _log.LogError(ex, "Failed to publish to {Topic}: {Error}", topic, ex.Error.Reason);
            throw;
        }
    }

    public void Dispose() => _producer.Dispose();
}

/// <summary>
/// Reusable Kafka consumer base: manual commit, 3-attempt retry, dead-letter logging.
/// </summary>
public abstract class KafkaConsumerBase<TMessage> : BackgroundService
    where TMessage : class
{
    private readonly IConsumer<string, string> _consumer;
    protected readonly ILogger Logger;
    private readonly string _topic;

    protected KafkaConsumerBase(IConfiguration config, ILogger log, string topic, string groupId)
    {
        Logger = log;
        _topic = topic;

        var cfg = new ConsumerConfig
        {
            BootstrapServers = config["Kafka:BootstrapServers"],
            GroupId          = groupId,
            AutoOffsetReset  = AutoOffsetReset.Earliest,
            EnableAutoCommit = false,
        };
        _consumer = new ConsumerBuilder<string, string>(cfg).Build();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _consumer.Subscribe(_topic);
        Logger.LogInformation("Consumer subscribed to {Topic}", _topic);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = _consumer.Consume(stoppingToken);
                if (result?.Message?.Value == null) continue;

                TMessage? message;
                try
                {
                    message = JsonSerializer.Deserialize<TMessage>(result.Message.Value);
                }
                catch (JsonException ex)
                {
                    Logger.LogError(ex, "Deserialize failed for {Topic}: {Value}", _topic, result.Message.Value);
                    _consumer.Commit(result);
                    continue;
                }

                if (message == null) continue;

                bool success = false;
                for (int attempt = 1; attempt <= 3 && !success; attempt++)
                {
                    try
                    {
                        await HandleAsync(message, stoppingToken);
                        success = true;
                    }
                    catch (Exception ex) when (attempt < 3)
                    {
                        Logger.LogWarning(ex, "Attempt {A}/3 failed for {Topic}. Retrying…", attempt, _topic);
                        await Task.Delay(500 * attempt, stoppingToken);
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError(ex, "Processing failed after 3 attempts for {Topic}", _topic);
                    }
                }

                _consumer.Commit(result);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Consumer loop error for {Topic}", _topic);
                await Task.Delay(1000, stoppingToken);
            }
        }

        _consumer.Close();
    }

    protected abstract Task HandleAsync(TMessage message, CancellationToken ct);

    public override void Dispose() { _consumer.Dispose(); base.Dispose(); }
}
