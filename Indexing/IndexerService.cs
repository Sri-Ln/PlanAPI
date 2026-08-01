using System.Text.Json;
using PlanApi.Messaging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace PlanApi.Indexing;

// Consumes the plan queue and applies each message to Elasticsearch.
// Manual ack: the message is only removed from the queue once ES has confirmed the write,
// so a crash mid-index leaves the message queued for redelivery.
public sealed class IndexerService : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly ConnectionFactory _factory;
    private readonly string _queue;
    private readonly IPlanIndexer _indexer;
    private readonly ILogger<IndexerService> _log;

    public IndexerService(IConfiguration config, IPlanIndexer indexer, ILogger<IndexerService> log)
    {
        _indexer = indexer;
        _log = log;
        _queue = config["RabbitMq:Queue"]
                 ?? throw new InvalidOperationException("RabbitMq:Queue is not configured");
        _factory = new ConnectionFactory
        {
            HostName = config["RabbitMq:HostName"]
                       ?? throw new InvalidOperationException("RabbitMq:HostName is not configured"),
            Port = config.GetValue("RabbitMq:Port", 5672),
            UserName = config["RabbitMq:UserName"] ?? "guest",
            Password = config["RabbitMq:Password"] ?? "guest",
            AutomaticRecoveryEnabled = true
        };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var connection = await ConnectWithRetryAsync(stoppingToken);
        if (connection is null) return;

        await using var conn = connection;
        await using var channel = await conn.CreateChannelAsync(cancellationToken: stoppingToken);

        // Declaring here too: the consumer must not depend on the publisher having run first.
        await channel.QueueDeclareAsync(
            queue: _queue, durable: true, exclusive: false, autoDelete: false, cancellationToken: stoppingToken);

        // One unacked message at a time. Keeps ordering, so a plan's create cannot be
        // processed concurrently with its own delete.
        await channel.BasicQosAsync(prefetchSize: 0, prefetchCount: 1, global: false,
            cancellationToken: stoppingToken);

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += (_, ea) => HandleAsync(channel, ea, stoppingToken);

        // autoAck: false is the whole point — see HandleAsync.
        await channel.BasicConsumeAsync(_queue, autoAck: false, consumer, cancellationToken: stoppingToken);
        _log.LogInformation("Indexer consuming {Queue}", _queue);

        // BasicConsumeAsync returns immediately; hold the service open until shutdown.
        await Task.Delay(Timeout.Infinite, stoppingToken).ContinueWith(_ => { }, TaskScheduler.Default);
    }

    private async Task HandleAsync(IChannel channel, BasicDeliverEventArgs ea, CancellationToken ct)
    {
        try
        {
            var message = JsonSerializer.Deserialize<PlanMessage>(ea.Body.Span, JsonOptions)
                          ?? throw new InvalidOperationException("message body deserialized to null");

            switch (message.Op)
            {
                case "create" or "update":
                    await _indexer.IndexPlanAsync(
                        message.Plan ?? throw new InvalidOperationException($"{message.Op} message has no plan"), ct);
                    break;

                case "delete":
                    await _indexer.DeletePlanAsync(message.DocIds ?? [], message.PlanId, ct);
                    break;

                default:
                    throw new InvalidOperationException($"unknown op '{message.Op}'");
            }

            // Only now is it safe to drop the message.
            await channel.BasicAckAsync(ea.DeliveryTag, multiple: false, cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Indexing failed; requeueing message");

            // Requeue for retry. Known limitation: a genuinely poison message loops forever.
            // The delay keeps that loop from saturating a CPU while it does.
            await Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None);
            await channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: true,
                cancellationToken: CancellationToken.None);
        }
    }

    // RabbitMQ may still be starting when the app boots; retry rather than dying.
    private async Task<IConnection?> ConnectWithRetryAsync(CancellationToken ct)
    {
        for (var attempt = 1; !ct.IsCancellationRequested; attempt++)
        {
            try
            {
                return await _factory.CreateConnectionAsync(ct);
            }
            catch (Exception ex) when (attempt < 10)
            {
                _log.LogWarning("RabbitMQ not reachable (attempt {Attempt}): {Message}", attempt, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(3), ct);
            }
        }
        return null;
    }
}
