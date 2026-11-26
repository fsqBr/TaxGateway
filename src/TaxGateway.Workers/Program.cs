using TaxGateway.Api.Audit;
using TaxGateway.Infrastructure;
using TaxGateway.Infrastructure.Batches;
using TaxGateway.Infrastructure.Messaging;
using TaxGateway.Infrastructure.Persistence;

namespace TaxGateway.Workers;

/// <summary>
/// Workers (SDD §2; D-607): relay do outbox → RabbitMQ, fan-out e entrega de webhooks, processamento de lotes.
/// Usam o role tg_worker (por tenant) e, no relay, o tg_relay. A auditoria do lote reaproveita o AuditService da API
/// (mesmas regras e mesma cobrança). Cada consumidor liga/desliga por configuração (Workers:*).
/// </summary>
public static class WorkerHost
{
    public static void Main(string[] args) => CreateBuilder(args).Build().Run();

    /// <summary>Monta o host (usado pelo Main e pelos testes de integração, com configuração em memória).</summary>
    public static HostApplicationBuilder CreateBuilder(string[] args, IDictionary<string, string?>? overrides = null)
    {
        var builder = Host.CreateApplicationBuilder(args);
        if (overrides is not null)
        {
            builder.Configuration.AddInMemoryCollection(overrides);
        }

        var config = builder.Configuration;
        var worker = config.GetConnectionString("Worker") ?? throw new InvalidOperationException("ConnectionStrings:Worker não configurada.");
        builder.Services.AddTaxGatewayPersistence(worker);
        builder.Services.AddTaxGatewayRuntime(config);
        builder.Services.AddTaxGatewayAudit(config);
        builder.Services.AddTaxGatewayBatchesAndWebhooks(config);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<AuditService>();
        builder.Services.AddSingleton<IBatchDocumentAuditor, BatchDocumentAuditor>();

        if (config.GetValue("Workers:OutboxRelay", true))
        {
            var relay = config.GetConnectionString("Relay") ?? throw new InvalidOperationException("ConnectionStrings:Relay não configurada.");
            builder.Services.AddSingleton(_ => new RelayDataSource(Npgsql.NpgsqlDataSource.Create(
                new Npgsql.NpgsqlConnectionStringBuilder(relay) { GssEncryptionMode = Npgsql.GssEncryptionMode.Disable }.ConnectionString)));
            builder.Services.AddHostedService<OutboxRelay>();
        }

        if (config.GetValue("Workers:Webhooks", true))
        {
            builder.Services.AddHostedService<WebhookFanOutConsumer>();
            builder.Services.AddSingleton<WebhookDeliveryScheduler>();
            builder.Services.AddHostedService(sp => sp.GetRequiredService<WebhookDeliveryScheduler>());
        }

        if (config.GetValue("Workers:Batches", true))
        {
            builder.Services.AddSingleton<BatchProcessor>();
            builder.Services.AddHostedService(sp => sp.GetRequiredService<BatchProcessor>());
        }

        // DF-e (Fase 9): distribuição por NSU, ciência da operação, auditoria INBOUND e alertas de vencimento do A1.
        builder.Services.AddTaxGatewayDfe(config);
        if (config.GetValue("Dfe:WorkerEnabled", true))
        {
            builder.Services.AddHostedService<TaxGateway.Infrastructure.Dfe.DfeWorker>();
        }

        // Fechamento de faturamento (§10.3): role tg_billing, provedor configurado em Billing:Asaas.
        var billing = config.GetConnectionString("Billing");
        if (billing is not null && config.GetValue("Workers:Billing", true))
        {
            builder.Services.AddTaxGatewayBilling(config, billing);
            builder.Services.AddHostedService<TaxGateway.Infrastructure.Billing.BillingCloseWorker>();
        }

        return builder;
    }
}
