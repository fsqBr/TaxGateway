using System.Security.Cryptography.X509Certificates;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

using TaxGateway.Infrastructure.Persistence;
using TaxGateway.Infrastructure.Storage;
using TaxGateway.Infrastructure.Xml;

namespace TaxGateway.Infrastructure;

public sealed class XmlSecurityOptions
{
    public const string Section = "Xml";

    /// <summary>Âncoras extras (PEM) além da ICP-Brasil. Só em testes/homologação; vazio em produção (D-302).</summary>
    public List<string> ExtraTrustAnchorPemFiles { get; set; } = [];

    /// <summary>true = sem LCR no cache a assinatura falha; false (padrão) = aceita com aviso REVOGACAO_NAO_VERIFICADA (D-407).</summary>
    public bool StrictRevocation { get; set; }
}

public static class AuditServiceCollectionExtensions
{
    /// <summary>Pipeline de auditoria (Fase 3): XML, assinatura, cofre de chaves, S3 e gravação atômica.</summary>
    public static IServiceCollection AddTaxGatewayAudit(this IServiceCollection services, IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        services.AddOptions<KeyVaultOptions>().Bind(config.GetSection(KeyVaultOptions.Section));
        services.AddOptions<StorageOptions>().Bind(config.GetSection(StorageOptions.Section));
        services.AddOptions<XmlSecurityOptions>().Bind(config.GetSection(XmlSecurityOptions.Section));

        // KMS: OpenBao/Vault Transit quando configurado (produção, D-406); senão o cofre local de dev (D-309).
        services.AddOptions<OpenBaoOptions>().Bind(config.GetSection(OpenBaoOptions.Section));
        if (!string.IsNullOrEmpty(config[$"{OpenBaoOptions.Section}:Address"]))
        {
            services.AddHttpClient<IKeyVault, OpenBaoKeyVault>(c => c.Timeout = TimeSpan.FromSeconds(5));
        }
        else
        {
            services.AddSingleton<IKeyVault, LocalKeyVault>();
        }
        services.AddSingleton<CpfAnonymizer>();
        services.AddSingleton<AuditRepository>();
        services.AddSingleton<TenantSettingsService>();
        services.AddSingleton(sp =>
        {
            var extra = sp.GetRequiredService<IOptions<XmlSecurityOptions>>().Value.ExtraTrustAnchorPemFiles
                .Select(f => X509Certificate2.CreateFromPem(File.ReadAllText(f)));
            return TrustAnchors.IcpBrasil(extra);
        });
        services.AddHttpClient("crl", c => { c.Timeout = TimeSpan.FromSeconds(30); c.MaxResponseContentBufferSize = 32 * 1024 * 1024; });
        services.AddSingleton(sp => new CrlCache(
            (u, ct) => sp.GetRequiredService<IHttpClientFactory>().CreateClient("crl").GetByteArrayAsync(u, ct),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<CrlCache>>()));
        services.AddHostedService(sp => sp.GetRequiredService<CrlCache>());
        services.AddSingleton(sp => new NfeSignatureVerifier(sp.GetRequiredService<TrustAnchors>(), sp.GetRequiredService<CrlCache>(),
            sp.GetRequiredService<IOptions<XmlSecurityOptions>>().Value.StrictRevocation));

        // S3 só com credenciais configuradas; sem ele, todo tenant fica em zero_retention.
        if (!string.IsNullOrEmpty(config[$"{StorageOptions.Section}:AccessKey"]))
        {
            services.AddSingleton(sp => XmlVault.CreateClient(sp.GetRequiredService<IOptions<StorageOptions>>().Value));
            services.AddSingleton<XmlVault>();
            services.AddHostedService<BucketInitializer>();
        }

        return services;
    }

    private sealed class BucketInitializer(XmlVault vault) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => vault.EnsureBucketAsync(cancellationToken);

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}

public static class BatchWebhookServiceCollectionExtensions
{
    /// <summary>Lotes e webhooks (Fase 6): ingestão, repositório/entrega de webhooks. Workers registram os consumidores.</summary>
    public static IServiceCollection AddTaxGatewayBatchesAndWebhooks(this IServiceCollection services, IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        services.AddOptions<TaxGateway.Infrastructure.Batches.BatchOptions>().Bind(config.GetSection(TaxGateway.Infrastructure.Batches.BatchOptions.Section));
        services.AddOptions<TaxGateway.Infrastructure.Webhooks.WebhookOptions>().Bind(config.GetSection(TaxGateway.Infrastructure.Webhooks.WebhookOptions.Section));
        services.AddOptions<TaxGateway.Infrastructure.Messaging.MessagingOptions>().Bind(config.GetSection(TaxGateway.Infrastructure.Messaging.MessagingOptions.Section));
        services.AddSingleton<TaxGateway.Infrastructure.Batches.BatchIngestService>();
        services.AddSingleton<TaxGateway.Infrastructure.Webhooks.WebhookRepository>();
        services.AddSingleton<TaxGateway.Infrastructure.Webhooks.WebhookSender>();
        services.AddHttpClient(TaxGateway.Infrastructure.Webhooks.WebhookSender.Client)
            .ConfigurePrimaryHttpMessageHandler(sp =>
            {
                var o = sp.GetRequiredService<IOptions<TaxGateway.Infrastructure.Webhooks.WebhookOptions>>().Value;
                return TaxGateway.Infrastructure.Webhooks.SsrfGuard.CreateHandler(o.AllowInsecureForTests, o.TestTrustedThumbprint);
            });
        return services;
    }
}

public static class DfeServiceCollectionExtensions
{
    /// <summary>Fase 9: custódia do A1 (API e workers) e distribuição DF-e (workers).</summary>
    public static IServiceCollection AddTaxGatewayDfe(this IServiceCollection services, IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        services.AddOptions<TaxGateway.Infrastructure.Dfe.DfeOptions>().Bind(config.GetSection(TaxGateway.Infrastructure.Dfe.DfeOptions.Section));
        services.AddSingleton<TaxGateway.Infrastructure.Dfe.CertificateStore>();
        services.AddSingleton<TaxGateway.Infrastructure.Dfe.ISefazDfeClient, TaxGateway.Infrastructure.Dfe.SefazDfeClient>();
        services.AddSingleton<TaxGateway.Infrastructure.Dfe.DfeSyncService>();
        services.AddSingleton<TaxGateway.Infrastructure.Dfe.CertificateExpiryService>();
        return services;
    }
}

public static class BillingServiceCollectionExtensions
{
    /// <summary>Faturamento (Fase 7): role tg_billing, provedor Asaas e serviço de fechamento/reconciliação.</summary>
    public static IServiceCollection AddTaxGatewayBilling(this IServiceCollection services, IConfiguration config, string billingConnection)
    {
        ArgumentNullException.ThrowIfNull(config);
        services.AddSingleton(_ => new TaxGateway.Infrastructure.Billing.BillingDataSource(Npgsql.NpgsqlDataSource.Create(
            new Npgsql.NpgsqlConnectionStringBuilder(billingConnection) { GssEncryptionMode = Npgsql.GssEncryptionMode.Disable }.ConnectionString)));
        services.AddOptions<TaxGateway.Infrastructure.Billing.AsaasOptions>().Bind(config.GetSection(TaxGateway.Infrastructure.Billing.AsaasOptions.Section));
        services.AddHttpClient<TaxGateway.Infrastructure.Billing.IBillingProvider, TaxGateway.Infrastructure.Billing.AsaasBillingProvider>(c => c.Timeout = TimeSpan.FromSeconds(30));
        services.AddSingleton<TaxGateway.Infrastructure.Billing.BillingService>();
        return services;
    }
}
