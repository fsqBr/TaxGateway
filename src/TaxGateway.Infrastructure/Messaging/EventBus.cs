using System.Text;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Npgsql;

using RabbitMQ.Client;
using RabbitMQ.Client.Events;

using TaxGateway.Core.Tenancy;
using TaxGateway.Infrastructure.Webhooks;

namespace TaxGateway.Infrastructure.Messaging;

public sealed class MessagingOptions
{
    public const string Section = "Messaging";
    public Uri RabbitUri { get; set; } = new("amqp://guest:guest@127.0.0.1:5672/");
    public int RelayBatchSize { get; set; } = 500;
    public int RelayIdleDelayMs { get; set; } = 200;
}

/// <summary>
/// Topologia (SDD §7; D-601): exchange de eventos (topic, durável) → fila quorum de webhooks com dead-letter.
/// Quorum queues: replicação Raft e limite de reentregas (x-delivery-limit) contra mensagem envenenada.
/// </summary>
public static class Topology
{
    public const string Events = "taxgateway.events";
    public const string DeadLetter = "taxgateway.dlx";
    public const string WebhookQueue = "webhooks.dispatch";

    public static async Task DeclareAsync(IChannel ch, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ch);
        await ch.ExchangeDeclareAsync(Events, ExchangeType.Topic, durable: true, cancellationToken: ct).ConfigureAwait(false);
        await ch.ExchangeDeclareAsync(DeadLetter, ExchangeType.Fanout, durable: true, cancellationToken: ct).ConfigureAwait(false);
        await ch.QueueDeclareAsync(WebhookQueue + ".dlq", durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object?> { ["x-queue-type"] = "quorum" }, cancellationToken: ct).ConfigureAwait(false);
        await ch.QueueBindAsync(WebhookQueue + ".dlq", DeadLetter, "", cancellationToken: ct).ConfigureAwait(false);
        await ch.QueueDeclareAsync(WebhookQueue, durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object?>
            {
                ["x-queue-type"] = "quorum",
                ["x-dead-letter-exchange"] = DeadLetter,
                ["x-delivery-limit"] = 10,
            }, cancellationToken: ct).ConfigureAwait(false);
        await ch.QueueBindAsync(WebhookQueue, Events, "#", cancellationToken: ct).ConfigureAwait(false);
    }
}

/// <summary>Fonte de dados do role tg_relay (só outbox).</summary>
public sealed class RelayDataSource(NpgsqlDataSource dataSource)
{
    public NpgsqlDataSource DataSource { get; } = dataSource;
}

/// <summary>
/// Relay do outbox (SDD §7, §10.2; D-601): lê eventos não publicados (FOR UPDATE SKIP LOCKED: várias réplicas sem
/// duplicar), publica com publisher confirms e só então marca published_at na mesma transação. Falhou a publicação
/// = rollback, nada marcado: entrega "pelo menos uma vez"; o consumidor deduplica pelo id do evento.
/// </summary>
public sealed partial class OutboxRelay(RelayDataSource relay, IOptions<MessagingOptions> options, ILogger<OutboxRelay> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var factory = new ConnectionFactory { Uri = options.Value.RabbitUri, ClientProvidedName = "taxgateway-outbox-relay" };
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var conn = await factory.CreateConnectionAsync(stoppingToken).ConfigureAwait(false);
                await using var ch = await conn.CreateChannelAsync(new CreateChannelOptions(true, true), stoppingToken).ConfigureAwait(false);
                await Topology.DeclareAsync(ch, stoppingToken).ConfigureAwait(false);
                while (!stoppingToken.IsCancellationRequested)
                {
                    var n = await PublishBatchAsync(ch, stoppingToken).ConfigureAwait(false);
                    if (n == 0)
                    {
                        await Task.Delay(options.Value.RelayIdleDelayMs, stoppingToken).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogRelayError(log, ex);
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Publica um lote; devolve quantos eventos saíram.</summary>
    public async Task<int> PublishBatchAsync(IChannel ch, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ch);
        await using var conn = await relay.DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        var events = new List<(long Id, Guid Tenant, string Topic, string Payload)>();
        await using (var cmd = new NpgsqlCommand(
            "SELECT id, tenant_id, topic, payload::text FROM outbox WHERE published_at IS NULL ORDER BY id LIMIT $1 FOR UPDATE SKIP LOCKED", conn, tx))
        {
            cmd.Parameters.Add(new() { Value = options.Value.RelayBatchSize });
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                events.Add((r.GetInt64(0), r.GetGuid(1), r.GetString(2), r.GetString(3)));
            }
        }

        if (events.Count == 0)
        {
            return 0;
        }

        foreach (var e in events)
        {
            var props = new BasicProperties
            {
                MessageId = e.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Persistent = true,
                ContentType = "application/json",
                Headers = new Dictionary<string, object?> { ["tenant_id"] = e.Tenant.ToString(), ["topic"] = e.Topic },
            };
            // Com confirmação habilitada, o await só termina quando o broker confirma (nack = exceção = rollback).
            await ch.BasicPublishAsync(Topology.Events, e.Topic, true, props, Encoding.UTF8.GetBytes(e.Payload), ct).ConfigureAwait(false);
        }

        await using (var mark = new NpgsqlCommand("UPDATE outbox SET published_at = now() WHERE id = ANY($1)", conn, tx))
        {
            mark.Parameters.Add(new() { Value = events.Select(e => e.Id).ToArray() });
            await mark.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return events.Count;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Relay do outbox falhou; reconectando.")]
    private static partial void LogRelayError(ILogger logger, Exception ex);
}

/// <summary>Consome eventos e cria as entregas de webhook de cada tenant (fan-out idempotente).</summary>
public sealed partial class WebhookFanOutConsumer(WebhookRepository repo, IOptions<MessagingOptions> options, ILogger<WebhookFanOutConsumer> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var factory = new ConnectionFactory { Uri = options.Value.RabbitUri, ClientProvidedName = "taxgateway-webhook-fanout" };
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var conn = await factory.CreateConnectionAsync(stoppingToken).ConfigureAwait(false);
                await using var ch = await conn.CreateChannelAsync(cancellationToken: stoppingToken).ConfigureAwait(false);
                await Topology.DeclareAsync(ch, stoppingToken).ConfigureAwait(false);
                await ch.BasicQosAsync(0, 50, false, stoppingToken).ConfigureAwait(false);
                // D-P28: desligamento limpo. O trabalho em andamento NÃO usa o stoppingToken (antes, o SIGTERM cancelava o
                // fan-out no meio); ao parar, cancela o consumo, espera as mensagens em voo por até 20 s e só então fecha.
                using var hard = new CancellationTokenSource();
                var inFlight = 0;
                var consumer = new AsyncEventingBasicConsumer(ch);
                consumer.ReceivedAsync += async (_, ea) =>
                {
                    Interlocked.Increment(ref inFlight);
                    try
                    {
                        var tenant = new TenantId(Guid.Parse(Encoding.UTF8.GetString((byte[])ea.BasicProperties.Headers!["tenant_id"]!)));
                        var topic = Encoding.UTF8.GetString((byte[])ea.BasicProperties.Headers["topic"]!);
                        var id = long.Parse(ea.BasicProperties.MessageId!, System.Globalization.CultureInfo.InvariantCulture);
                        await repo.FanOutAsync(tenant, id, topic, Encoding.UTF8.GetString(ea.Body.Span), hard.Token).ConfigureAwait(false);
                        await ch.BasicAckAsync(ea.DeliveryTag, false, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        LogFanOutError(log, ex);
                        await ch.BasicNackAsync(ea.DeliveryTag, false, true, CancellationToken.None).ConfigureAwait(false);   // x-delivery-limit manda para a DLQ
                    }
                    finally
                    {
                        Interlocked.Decrement(ref inFlight);
                    }
                };
                var tag = await ch.BasicConsumeAsync(Topology.WebhookQueue, false, consumer, stoppingToken).ConfigureAwait(false);
                try
                {
                    await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    await ch.BasicCancelAsync(tag, false, CancellationToken.None).ConfigureAwait(false);
                    var deadline = DateTime.UtcNow.AddSeconds(20);
                    while (Volatile.Read(ref inFlight) > 0 && DateTime.UtcNow < deadline)
                    {
                        await Task.Delay(100, CancellationToken.None).ConfigureAwait(false);
                    }

                    await hard.CancelAsync().ConfigureAwait(false);   // o que sobrar volta à fila (sem ack); o fan-out é idempotente
                    throw;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogFanOutError(log, ex);
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken).ConfigureAwait(false);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Falha no fan-out de evento para webhooks.")]
    private static partial void LogFanOutError(ILogger logger, Exception ex);
}

/// <summary>Agenda de entregas: reserva as vencidas entre todos os tenants (função do agendador) e entrega.</summary>
public sealed partial class WebhookDeliveryScheduler(NpgsqlDataSource workerDb, WebhookSender sender, ILogger<WebhookDeliveryScheduler> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var n = await RunOnceAsync(stoppingToken).ConfigureAwait(false);
                if (n == 0)
                {
                    await Task.Delay(500, stoppingToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogError(log, ex);
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken).ConfigureAwait(false);
            }
        }
    }

    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var due = new List<(Guid Tenant, Guid Id)>();
        await using (var cmd = workerDb.CreateCommand("SELECT tenant_id, delivery_id FROM app.claim_due_webhook_deliveries(50, 60, 8)"))
        await using (var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                due.Add((r.GetGuid(0), r.GetGuid(1)));
            }
        }

        await Parallel.ForEachAsync(due, new ParallelOptions { MaxDegreeOfParallelism = 16, CancellationToken = ct },
            async (d, c) => await sender.DeliverAsync(new TenantId(d.Tenant), d.Id, c).ConfigureAwait(false)).ConfigureAwait(false);
        return due.Count;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Falha no agendador de webhooks.")]
    private static partial void LogError(ILogger logger, Exception ex);
}
