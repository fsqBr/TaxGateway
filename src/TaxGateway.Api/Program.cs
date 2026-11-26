using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;

using Scalar.AspNetCore;

using TaxGateway.Api.Audit;
using TaxGateway.Api.Endpoints;
using TaxGateway.Api.Http;
using TaxGateway.Api.Simulation;
using TaxGateway.Contracts;
using TaxGateway.Identity;
using TaxGateway.Infrastructure;
using TaxGateway.Infrastructure.Engines;
using TaxGateway.Infrastructure.Params;
using TaxGateway.Infrastructure.Persistence;

var builder = WebApplication.CreateSlimBuilder(args);
builder.WebHost.ConfigureKestrel(k => k.AddServerHeader = false);

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.ConfigureHttpJsonOptions(o =>
{
    // Contrato público em snake_case (D-216); as opções HTTP prevalecem sobre as dos contextos gerados.
    o.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower;
    o.SerializerOptions.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
    o.SerializerOptions.TypeInfoResolverChain.Insert(0, ContractsJsonContext.Default);
    o.SerializerOptions.TypeInfoResolverChain.Insert(1, AdminJsonContext.Default);
    o.SerializerOptions.TypeInfoResolverChain.Insert(2, AuditoriaJsonContext.Default);
});

// Conexão do role tg_app (sujeito a RLS). Nunca usar tg_owner em runtime (SDD §4.1).
var appConnection = builder.Configuration.GetConnectionString("App")
    ?? throw new InvalidOperationException("ConnectionStrings:App não configurada.");
builder.Services.AddTaxGatewayPersistence(appConnection);
builder.Services.AddTaxGatewayRuntime(builder.Configuration);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddOptions<SimulationOptions>().Bind(builder.Configuration.GetSection(SimulationOptions.Section));
builder.Services.AddOptions<AdminOperatorOptions>().Bind(builder.Configuration.GetSection(AdminOperatorOptions.Section));
builder.Services.AddSingleton<SimulationService>();
// OAuth 2.0 client credentials (Fase 8; D-801): token, JWKS e validação local.
builder.Services.AddTaxGatewayIdentity(builder.Configuration);
builder.Services.AddSingleton<BearerAuthenticator>();
builder.Services.AddSingleton<TitularEndpoints.TitularDeps>();
builder.Services.AddTaxGatewayAudit(builder.Configuration);
builder.Services.AddSingleton<AuditService>();
builder.Services.AddSingleton<ReprocessService>();
builder.Services.AddTaxGatewayBatchesAndWebhooks(builder.Configuration);
builder.Services.AddTaxGatewayDfe(builder.Configuration);   // custódia do A1 (Fase 9)
builder.Services.AddSingleton<TaxGateway.Infrastructure.Batches.IBatchDocumentAuditor, BatchDocumentAuditor>();

// Admin API só sobe com a conexão do tg_admin configurada.
var adminConnection = builder.Configuration.GetConnectionString("Admin");

// Documentação (D-W01/D-W02): documento público "v1" sempre; "admin" só com a Admin API montada e Docs:IncludeAdmin
// (padrão: só em Development), para não publicar a superfície do backoffice na referência pública.
var includeAdminDocs = adminConnection is not null && builder.Configuration.GetValue("Docs:IncludeAdmin", builder.Environment.IsDevelopment());
builder.Services.AddTaxGatewayOpenApi(includeAdminDocs);
if (adminConnection is not null)
{
    builder.Services.AddTaxGatewayAdmin(adminConnection);
}

// Faturamento administrativo (fechamento manual, reconciliação, ajustes) só com a conexão do tg_billing.
var billingConnection = builder.Configuration.GetConnectionString("Billing");
if (billingConnection is not null)
{
    builder.Services.AddTaxGatewayBilling(builder.Configuration, billingConnection);
}

// Métricas (Hardening, D-H01): medidores nativos do ASP.NET Core, Kestrel, HttpClient (chamadas ao motor), runtime e
// Npgsql, expostos em /metrics SÓ na porta de gestão (Metrics:Port, padrão 9464) — fora do Service público.
var metricsPort = builder.Configuration.GetValue("Metrics:Port", 9464);
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("taxgateway-api"))
    .WithMetrics(m => m
        .AddMeter("Microsoft.AspNetCore.Hosting", "Microsoft.AspNetCore.Server.Kestrel", "System.Net.Http", "System.Runtime", "Npgsql")
        .AddView("http.server.request.duration", new OpenTelemetry.Metrics.ExplicitBucketHistogramConfiguration
        {
            // Baldes alinhados aos SLOs do §16.3 (5, 15, 40, 100, 150 ms...).
            Boundaries = [0.002, 0.005, 0.01, 0.015, 0.025, 0.04, 0.06, 0.1, 0.15, 0.25, 0.5, 1, 2.5, 5, 10],
        })
        .AddPrometheusExporter());

builder.Services.AddHealthChecks()
    .AddCheck<PartitionHealthCheck>("partitions", tags: ["ready"])
    .AddCheck<RuntimeReadinessCheck>("runtime", tags: ["ready"]);

// Atrás do ingress/nginx (D-W06): X-Forwarded-Proto/For só são aceitos de redes confiáveis (Http:TrustedProxyNetworks,
// CIDR). Sem isso a API vê HTTP e o OpenIddict recusa /oauth/token fora de dev; aceitar de qualquer origem permitiria
// falsificar o IP e o esquema.
builder.Services.Configure<Microsoft.AspNetCore.Builder.ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto;
    o.ForwardLimit = 2;
    foreach (var cidr in builder.Configuration.GetSection("Http:TrustedProxyNetworks").Get<string[]>() ?? [])
    {
        o.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(cidr));
    }
});

var app = builder.Build();

app.UseForwardedHeaders();
app.UseExceptionHandler();
if (metricsPort > 0)
{
    app.MapPrometheusScrapingEndpoint().RequireHost($"*:{metricsPort}");
}

app.UseAuthentication();   // atende /oauth/token e /.well-known/* (OpenIddict)
app.MapOpenApi();   // /openapi/v1.json (e /openapi/admin.json quando habilitado)
app.MapGet("/favicon.svg", () => Results.Text(Brand.Mark, "image/svg+xml")).ExcludeFromDescription();
app.MapScalarApiReference("/docs", o =>
{
    // Marca e tema (D-W09); sem telemetria nem o agente de IA embutido do Scalar.
    o.WithTitle("TaxGateway — Referência da API");
    o.WithTheme(ScalarTheme.None);
    o.WithCustomCss(Brand.DocsCss);
    o.AddHeaderContent(Brand.DocsHeader);
    o.WithFavicon("/favicon.svg");
    o.DisableTelemetry();
    o.DisableAgent();
    o.DisableMcp();
    o.AddPreferredSecuritySchemes(TaxGateway.Api.Http.OpenApiDocs.ApiKey);
    o.AddApiKeyAuthentication(TaxGateway.Api.Http.OpenApiDocs.ApiKey, k => k.Value = "cole-aqui-sua-api-key");   // o Scalar ignora vazio e preencheria um valor de demonstração dele
    o.HideDeveloperTools();   // barra promocional do Scalar (SDK, deploy)
    o.WithDefaultHttpClient(ScalarTarget.Shell, ScalarClient.Curl);
    o.AddDocument("v1", "API pública");
    if (includeAdminDocs)
    {
        o.AddDocument("admin", "Admin API");
    }
});

// Liveness não toca dependências; readiness inclui banco, parâmetros e cadastro do motor (D-112).
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready") });

var v1 = app.MapGroup("/v1")
    .AddEndpointFilter<ApiKeyFilter>()
    .AddEndpointFilter<RateLimitFilter>();
v1.MapSimulation();
v1.MapApiKeys();
v1.MapAudit();
v1.MapBatchesAndWebhooks();
v1.MapUsage();
v1.MapOem();
v1.MapTitulares();
v1.MapDfe();
v1.MapListings();

if (adminConnection is not null)
{
    var admin = app.MapGroup("/admin/v1").AddEndpointFilter<AdminKeyFilter>().MapAdmin().MapIdentityAdmin().MapAdminListings();
    if (billingConnection is not null)
    {
        admin.MapBillingAdmin();
    }
}

// Modo Job: "reprocess --tenant ID --from AAAA-MM --to AAAA-MM" (SDD §2.3 passo 7; D-408).
if (args.Length > 0 && args[0] == "reprocess")
{
    string Arg(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault()
        ?? throw new ArgumentException($"Informe {name}.");
    static DateOnly Month(string s) => DateOnly.ParseExact(s + "-01", "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
    await app.StartAsync();
    var summary = await app.Services.GetRequiredService<ReprocessService>().RunAsync(
        new TaxGateway.Core.Tenancy.TenantId(Guid.Parse(Arg("--tenant"))), Month(Arg("--from")), Month(Arg("--to")), CancellationToken.None);
    Console.WriteLine($"Reprocessamento: {summary}");
    await app.StopAsync();
    return summary.Falhas == 0 ? 0 : 1;
}

app.Run();
return 0;

/// <summary>Ponto de entrada visível para os testes (WebApplicationFactory).</summary>
public partial class Program;

/// <summary>Pronto só com parâmetros carregados e o cadastro de entes do motor espelhado (SDD §16.2 item 2).</summary>
internal sealed class RuntimeReadinessCheck(ParamSnapshotService parameters, RfbReferenceData reference) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (!parameters.IsLoaded)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy("Parâmetros não carregados."));
        }

        return Task.FromResult(reference.IsLoaded
            ? HealthCheckResult.Healthy($"snapshot {parameters.Current.Id}; {reference.MunicipioCount} municípios (bundle {reference.Bundle})")
            : HealthCheckResult.Unhealthy("Cadastro de municípios do motor ainda não carregado."));
    }
}
