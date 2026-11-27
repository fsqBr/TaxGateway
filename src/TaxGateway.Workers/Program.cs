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
        ProductionGuard.EnsureSafe(config, builder.Environment, isApi: false);   // D-P13
        // Métricas e traces dos workers por OTLP (lotes, webhooks, DF-e, cobrança) e log JSON (D-P17).
        TaxGateway.Api.Http.Telemetry.AddTaxGatewayTelemetry(builder.Services, config, builder.Logging, "taxgateway-workers", prometheus: false);
        var worker = config.GetConnectionString("Worker") ?? throw new InvalidOperationException("ConnectionStrings:Worker não configurada.");
        builder.Services.AddTaxGatewayPersistence(worker, defaultMaxPoolSize: 20);   // D-P22
        builder.Services.AddTaxGatewayRuntime(config);
        builder.Services.AddTaxGatewayAudit(config);
        builder.Services.AddTaxGatewayBatchesAndWebhooks(config);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<AuditService>();
        builder.Services.AddSingleton<IBatchDocumentAuditor, BatchDocumentAuditor>();

        if (config.GetValue("Workers:OutboxRelay", true))
        {
            var relay = config.GetConnectionString("Relay") ?? throw new InvalidOperationException("ConnectionStrings:Relay não configurada.");
            builder.Services.AddSingleton(_ => new RelayDataSource(Npgsql.NpgsqlDataSource.Create(DbPools.Normalize(relay, DbPools.Auxiliary))));
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

        // Fechamento de faturamento (§10.3) e Stripe (D-C02): role tg_billing; eventos, catálogo e consumo medido.
        var billing = config.GetConnectionString("Billing");
        if (billing is not null && config.GetValue("Workers:Billing", true))
        {
            builder.Services.AddTaxGatewayBilling(config, billing);
            builder.Services.AddHostedService<TaxGateway.Infrastructure.Billing.BillingCloseWorker>();
            builder.Services.AddSingleton<TaxGateway.Infrastructure.Billing.StripeEventProcessor>();
            builder.Services.AddSingleton<TaxGateway.Infrastructure.Billing.StripeCatalogSync>();
            builder.Services.AddSingleton<TaxGateway.Infrastructure.Billing.StripeUsageReporter>();
            builder.Services.AddHostedService(sp => sp.GetRequiredService<TaxGateway.Infrastructure.Billing.StripeEventProcessor>());
            builder.Services.AddHostedService(sp => sp.GetRequiredService<TaxGateway.Infrastructure.Billing.StripeCatalogSync>());
            builder.Services.AddHostedService(sp => sp.GetRequiredService<TaxGateway.Infrastructure.Billing.StripeUsageReporter>());
            builder.Services.AddSingleton<TaxGateway.Infrastructure.Billing.DunningWorker>();   // D-R09
            builder.Services.AddHostedService(sp => sp.GetRequiredService<TaxGateway.Infrastructure.Billing.DunningWorker>());
        }

        // LCR compartilhada (D-P23): os workers têm saída para a internet; baixam as LCRs pedidas e publicam no bucket.
        // Uploads do modo 2 vencidos (D-P24). Ambos exigem o S3 configurado.
        if (!string.IsNullOrEmpty(config["Storage:AccessKey"]))
        {
            if (config.GetValue("Workers:CrlPublisher", true))
            {
                builder.Services.AddSingleton<TaxGateway.Infrastructure.Xml.CrlPublisher>();
                builder.Services.AddHostedService(sp => sp.GetRequiredService<TaxGateway.Infrastructure.Xml.CrlPublisher>());
            }

            if (config.GetValue("Workers:UploadJanitor", true))
            {
                builder.Services.AddSingleton<TaxGateway.Infrastructure.Storage.UploadJanitor>();
                builder.Services.AddHostedService(sp => sp.GetRequiredService<TaxGateway.Infrastructure.Storage.UploadJanitor>());
            }
        }

        // Avisos por e-mail e fan-out das mudanças fiscais (D-J03).
        if (config.GetValue("Workers:Notifications", true))
        {
            builder.Services.AddOptions<TaxGateway.Identity.Accounts.AccountOptions>().Bind(config.GetSection(TaxGateway.Identity.Accounts.AccountOptions.Section));
            builder.Services.AddOptions<TaxGateway.Identity.Accounts.EmailOptions>().Bind(config.GetSection(TaxGateway.Identity.Accounts.EmailOptions.Section));
            builder.Services.AddSingleton<TaxGateway.Identity.Accounts.IEmailSender, TaxGateway.Identity.Accounts.SmtpEmailSender>();
            builder.Services.AddSingleton<NotificationMailer>();
            builder.Services.AddHostedService(sp => sp.GetRequiredService<NotificationMailer>());
        }

        // Sonda HTTP mínima (D-P25) e desligamento com prazo: o Kubernetes dá 60 s; os laços param de pegar trabalho e
        // terminam o que estão fazendo (chunks com cerca, entregas com lease — o que não terminar volta à fila).
        var healthPort = config.GetValue("Workers:HealthPort", 0);
        if (healthPort > 0)
        {
            builder.Services.AddHostedService(sp => new WorkerHealthServer(healthPort,
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<WorkerHealthServer>>()));
        }

        builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(45));

        return builder;
    }
}
