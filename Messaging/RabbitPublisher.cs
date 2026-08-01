using System.Text.Json;
using RabbitMQ.Client;

namespace PlanApi.Messaging;

public interface IPlanPublisher
{
    Task PublishAsync(PlanMessage message, CancellationToken ct = default);
}

// Singleton. Declares the queue at startup and reconnects lazily on publish if that failed,
// so the API still boots when RabbitMQ is down but the queue is visible before the first write.
public sealed class RabbitPublisher : IPlanPublisher, IHostedService, IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly ConnectionFactory _factory;
    private readonly string _queue;
    private readonly ILogger<RabbitPublisher> _log;

    // Async mutex. C# forbids `lock` around `await`, and IChannel is not safe for
    // concurrent use — two simultaneous POSTs would otherwise share one channel.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IConnection? _connection;
    private IChannel? _channel;

    public RabbitPublisher(IConfiguration config, ILogger<RabbitPublisher> log)
    {
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
            AutomaticRecoveryEnabled = true,                        // reconnect after a broker restart
            RequestedConnectionTimeout = TimeSpan.FromSeconds(5)    // fail fast; this runs inside an HTTP request
        };
    }

    // Runs once at startup. Best-effort: a broker outage logs a warning and the app still boots,
    // with EnsureChannelAsync retrying on the first publish. Declaring here means an empty queue
    // in the management UI unambiguously means "nothing was published yet".
    public async Task StartAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await EnsureChannelAsync(ct);
            _log.LogInformation("Declared durable queue {Queue}", _queue);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not declare queue {Queue} at startup; will retry on first publish", _queue);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    // Never throws. Redis is the source of truth and has already committed by the time we
    // get here, so a broker outage must not turn a persisted write into an HTTP error.
    // Elasticsearch catches up on the next write.
    public async Task PublishAsync(PlanMessage message, CancellationToken ct = default)
    {
        try
        {
            var body = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);

            await _gate.WaitAsync(ct);
            try
            {
                var channel = await EnsureChannelAsync(ct);

                // Persistent writes the message to disk. Separate from the queue being durable:
                // a durable queue holding non-persistent messages still empties on restart.
                var props = new BasicProperties { Persistent = true, ContentType = "application/json" };

                // Exchange "" is the default exchange, where routingKey is read as a queue name.
                // Publisher confirms are on, so this await completes only once the broker has acked.
                await channel.BasicPublishAsync(
                    exchange: "",
                    routingKey: _queue,
                    mandatory: true,                 // unroutable -> throws, rather than silently vanishing
                    basicProperties: props,
                    body: body,
                    cancellationToken: ct);

                _log.LogInformation("Published {Op} for plan {PlanId}", message.Op, message.PlanId);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to publish {Op} for plan {PlanId}; index will lag until the next write",
                message.Op, message.PlanId);
        }
    }

    private async Task<IChannel> EnsureChannelAsync(CancellationToken ct)
    {
        if (_channel is { IsOpen: true }) return _channel;

        if (_connection is not { IsOpen: true })
        {
            if (_connection is not null) await _connection.DisposeAsync();
            _connection = await _factory.CreateConnectionAsync(ct);
        }

        _channel = await _connection.CreateChannelAsync(
            new CreateChannelOptions(
                publisherConfirmationsEnabled: true,
                publisherConfirmationTrackingEnabled: true),
            ct);

        // durable keeps the queue definition across a broker restart. Declaring is idempotent,
        // so both the publisher and the consumer can declare it without conflict.
        await _channel.QueueDeclareAsync(
            queue: _queue, durable: true, exclusive: false, autoDelete: false, cancellationToken: ct);

        return _channel;
    }

    public async ValueTask DisposeAsync()
    {
        if (_channel is not null) await _channel.DisposeAsync();
        if (_connection is not null) await _connection.DisposeAsync();
        _gate.Dispose();
    }
}
