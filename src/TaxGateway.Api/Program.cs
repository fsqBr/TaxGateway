using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;


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
ProductionGuard.EnsureSafe(builder.Configuration, builder.Environment);   // D-P13: não sobe com configuração de dev
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
builder.Services.AddSingleton<TaxGateway.Infrastructure.Params.RateExceptionService>();   // D-R20
builder.Services.AddSingleton<ApiCallLogWriter>();                                           // D-R27
builder.Services.AddHostedService(sp => sp.GetRequiredService<ApiCallLogWriter>());
builder.Services.AddSingleton<SimulationService>();
// OAuth 2.0 client credentials (Fase 8; D-801): token, JWKS e validação local.
builder.Services.AddTaxGatewayIdentity(builder.Configuration);
builder.Services.AddSingleton<BearerAuthenticator>();
// Contas de pessoas com MFA (D-U01): sessão em cookie no lugar da API key no navegador.
builder.Services.AddSingleton<SessionCookies>();
builder.Services.AddSingleton<TenantProfileCache>();
// Stripe (D-C02): contratação/troca de plano e webhook. Sem Stripe:SecretKey, o pagamento online fica desligado.
TaxGateway.Infrastructure.StripeServiceCollectionExtensions.AddTaxGatewayStripe(builder.Services, builder.Configuration);
builder.Services.AddSingleton<SessionAuthenticator>();
// Rotas anônimas de conta (entrar, cadastro, senha, MFA): 60 por minuto por IP (escritórios atrás de NAT dividem o IP);
// o bloqueio por conta (5 erros = 15 min) vem do AccountService.
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy(AccountEndpoints.RateLimitPolicy, ctx => System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "sem-ip",
        _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions { PermitLimit = builder.Configuration.GetValue("Accounts:RequestsPerMinutePerIp", 60), Window = TimeSpan.FromMinutes(1) }));
    o.OnRejected = async (ctx, ct) =>
    {
        ctx.HttpContext.Response.Headers.RetryAfter = "60";
        await ApiProblem.Result(429, "ERR_RATE_LIMITED", "Muitas tentativas", "Aguarde um minuto e tente de novo.").ExecuteAsync(ctx.HttpContext);
    };
});
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
builder.Services.AddTaxGatewayTelemetry(builder.Configuration, builder.Logging, "taxgateway-api", prometheus: true);   // D-P17
// D-R10: titularidade do CNPJ — certificado e-CNPJ (cadeia ICP-Brasil) e e-mail do cadastro público da Receita.
builder.Services.AddSingleton(sp => new TaxGateway.Infrastructure.Tenancy.CnpjCertificateVerifier(
    sp.GetRequiredService<TaxGateway.Infrastructure.Xml.TrustAnchors>(), sp.GetRequiredService<TimeProvider>()));
builder.Services.AddOptions<TaxGateway.Infrastructure.Tenancy.CnpjRegistryOptions>().Bind(builder.Configuration.GetSection(TaxGateway.Infrastructure.Tenancy.CnpjRegistryOptions.Section));
builder.Services.AddHttpClient<TaxGateway.Infrastructure.Tenancy.ICnpjRegistry, TaxGateway.Infrastructure.Tenancy.HttpCnpjRegistry>(c => c.Timeout = TimeSpan.FromSeconds(10));

// D-P25: até 30 s para drenar as requisições em voo após o SIGTERM (grace period do pod: 45 s, preStop: 10 s).
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(30));

builder.Services.AddHealthChecks()
    // D-P18: o banco NÃO entra no readiness — um failover do Postgres gerenciado (30–120 s) tiraria 100% dos pods do
    // balanceador de uma vez. Partições/banco ficam em /health/full (alerta), o readiness só com dependências locais.
    .AddCheck<PartitionHealthCheck>("partitions", tags: ["full"])
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

// D-R22: cobertura do catálogo de alíquotas (dias à frente com referência publicada) → métrica + alerta de 90 dias.
TaxGateway.Infrastructure.TaxGatewayMetrics.RateCoverageDays = () =>
    TaxGateway.Api.Endpoints.RatesEndpoints.Coverage(app.Services.GetRequiredService<TaxGateway.Core.Params.IParamSnapshotProvider>().Current,
        app.Services.GetRequiredService<TimeProvider>()).Select(c => (c.Tributo, (double)c.Dias));

app.UseForwardedHeaders();
app.UseVersionHeader();   // X-TaxGateway-Version em toda resposta (D-W11)
app.UseExceptionHandler();
if (metricsPort > 0)
{
    // D-P14: o filtro é a PORTA LOCAL da conexão, não o header Host (antes, "Host: x:9464" na porta pública expunha
    // /metrics com todas as rotas e nomes de pool do banco).
    app.Use(async (ctx, next) =>
    {
        if (ctx.Request.Path.StartsWithSegments("/metrics") && ctx.Connection.LocalPort != metricsPort)
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        await next(ctx);
    });
    app.MapPrometheusScrapingEndpoint().RequireHost($"*:{metricsPort}");
}

// D-P16: /oauth/token é atendido pelo OpenIddict antes do roteamento; o limite por IP fica num middleware próprio
// (30 por minuto por IP; client_credentials legítimo pede um token a cada ~15 min).
var tokenLimiter = System.Threading.RateLimiting.PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
    System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(ctx.Connection.RemoteIpAddress?.ToString() ?? "sem-ip",
        _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions { PermitLimit = app.Configuration.GetValue("Identity:TokenRequestsPerMinutePerIp", 30), Window = TimeSpan.FromMinutes(1) }));
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.Equals("/oauth/token", StringComparison.OrdinalIgnoreCase))
    {
        using var lease = await tokenLimiter.AcquireAsync(ctx, 1, ctx.RequestAborted);
        if (!lease.IsAcquired)
        {
            ctx.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            ctx.Response.Headers.RetryAfter = "60";
            ctx.Response.ContentType = "application/json";
            // Formato OAuth (RFC 6749 §5.2), descrição só em ASCII (D-P05).
            await ctx.Response.WriteAsync("{\"error\":\"slow_down\",\"error_description\":\"Too many token requests from this IP. Retry in 60 s.\"}", ctx.RequestAborted);
            return;
        }
    }

    await next(ctx);
});
app.UseAuthentication();   // atende /oauth/token e /.well-known/* (OpenIddict)
app.UseRateLimiter();
app.MapOpenApi();   // /openapi/v1.json (e /openapi/admin.json quando habilitado)
app.MapVersion();
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

// Liveness não toca dependências; readiness só com parâmetros e cadastro do motor carregados (D-112, D-P18); /health/full inclui o banco.
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready") });
app.MapHealthChecks("/health/full", new HealthCheckOptions { Predicate = _ => true });

var v1 = app.MapGroup("/v1")
    .AddEndpointFilter<ApiKeyFilter>()
    .AddEndpointFilter<RateLimitFilter>()
    .AddEndpointFilter<QuotaFilter>()    // franquia rígida do Sandbox (D-C07)
    .AddEndpointFilter<CallLogFilter>(); // D-R27: registro de chamadas para quem integra
v1.MapSimulation();
v1.MapApiKeys();
v1.MapAudit();
v1.MapBatchesAndWebhooks();
v1.MapUsage();
v1.MapOem();
v1.MapTitulares();
v1.MapDfe();
v1.MapListings();
v1.MapDocuments();        // D-J01
v1.MapClassification();   // D-J02
v1.MapNotices();          // D-J03/D-J04
v1.MapCompany();          // D-R08/D-R10: teto de excedente e titularidade do CNPJ
v1.MapRateExceptions();   // D-R20: exceções do contribuinte
v1.MapApuracao();         // D-R26: apuração mensal, fornecedores e exportação
v1.MapIntegration();      // D-R27: clientes OAuth no portal e registro de chamadas
v1.MapBilling();

app.MapAccounts(includeOperators: adminConnection is not null);
app.MapCompanyDispute();
app.MapPublicRates();     // D-R21: alíquotas por município e busca de município  // D-R10

// D-J05: rota inexistente da API responde problem+json (antes, 404 com corpo vazio — o cliente não sabia o motivo).
app.MapFallback("/{**path}", (HttpContext http) =>
    http.Request.Path.StartsWithSegments("/v1") || http.Request.Path.StartsWithSegments("/admin")
        ? ApiProblem.Result(404, "ERR_ROTA_INEXISTENTE", "Rota inexistente", $"{http.Request.Method} {http.Request.Path} não existe. Veja a referência em /docs.")
        : Results.NotFound()).ExcludeFromDescription();
app.MapPublicPlans();
app.MapStripeWebhook();

if (adminConnection is not null)
{
    var admin = app.MapGroup("/admin/v1").AddEndpointFilter<AdminKeyFilter>().MapAdmin().MapIdentityAdmin().MapAdminListings().MapOperators().MapAdminPlans().MapAdminCompany();
    admin.MapAdminCoverage();   // D-R22
    if (billingConnection is not null)
    {
        admin.MapBillingAdmin();
    }
}

// Primeiro operador do backoffice (D-U08): "create-operator --email x --nome y --escopos admin:commercial,admin:fiscal,admin:security".
// Imprime o link de primeiro acesso (definir senha + MFA); também é enviado por e-mail.
if (args.Length > 0 && args[0] == "create-operator")
{
    string Arg(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault()
        ?? throw new ArgumentException($"Informe {name}.");
    await app.StartAsync();
    var link = await app.Services.GetRequiredService<TaxGateway.Identity.Accounts.AccountService>().CreateOperatorAsync(
        Arg("--email"), Arg("--nome"), Arg("--escopos").Split(','), "cli", CancellationToken.None);
    Console.WriteLine($"Operador criado. Primeiro acesso (24 h): {link}");
    await app.StopAsync();
    return 0;
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
