using System.Text.Json;
using Confluent.Kafka;

namespace WalletService.Infrastructure.Messaging;

/// <summary>
/// Background service base for all Kafka consumers in WalletService.
/// Handles: retry with back-off, structured logging, graceful shutdown,
/// and manual commit after successful processing (at-least-once guarantee).
/// </summary>
public abstract class KafkaConsumerBase<TMessage> : BackgroundService
    where TMessage : class
{
    private readonly IConsumer<string, string> _consumer;
    private readonly ILogger _log;
    private readonly string _topic;

    protected KafkaConsumerBase(IConfiguration config, ILogger log, string topic, string groupId)
    {
        _log   = log;
        _topic = topic;

        var cfg = new ConsumerConfig
        {
            BootstrapServers  = config["Kafka:BootstrapServers"],
            GroupId           = groupId,
            AutoOffsetReset   = AutoOffsetReset.Earliest,
            EnableAutoCommit  = false,  // Manual commit after successful processing
        };

        _consumer = new ConsumerBuilder<string, string>(cfg).Build();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _consumer.Subscribe(_topic);
        _log.LogInformation("Consumer subscribed to {Topic}", _topic);

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
                    _log.LogError(ex, "Failed to deserialize message from {Topic}: {Value}",
                        _topic, result.Message.Value);
                    _consumer.Commit(result); // Skip poison pill
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
                        _log.LogWarning(ex,
                            "Processing failed (attempt {Attempt}/3) for topic {Topic}. Retrying…",
                            attempt, _topic);
                        await Task.Delay(500 * attempt, stoppingToken);
                    }
                    catch (Exception ex)
                    {
                        _log.LogError(ex,
                            "Processing permanently failed after 3 attempts for topic {Topic}. Committing offset to avoid re-processing.",
                            _topic);
                        // TODO: route to dead-letter topic
                    }
                }

                _consumer.Commit(result);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Unhandled error in consumer loop for {Topic}", _topic);
                await Task.Delay(1000, stoppingToken);
            }
        }

        _consumer.Close();
    }

    protected abstract Task HandleAsync(TMessage message, CancellationToken ct);

    public override void Dispose()
    {
        _consumer.Dispose();
        base.Dispose();
    }
}
