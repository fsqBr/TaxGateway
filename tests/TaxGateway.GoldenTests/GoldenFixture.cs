using System.Net.Http.Json;
using System.Text.Json.Nodes;

using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

using Npgsql;

using TaxGateway.Infrastructure.Persistence;
using TaxGateway.TestKit;

using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;
using Testcontainers.Redis;

namespace TaxGateway.GoldenTests;

/// <summary>
/// Ambiente do corpus golden: Postgres (imagem do projeto, migrations + carga inicial), Valkey e a API em processo,
/// apontando para o motor regular real (RTC_REGULAR_URL; padrão: o do docker compose). Um tenant OEM com
/// auto-provisionamento de CNPJ é criado pela Admin API.
/// </summary>
public sealed class GoldenFixture : IAsyncLifetime
{
    private static readonly string SigningPem = CreateSigningPem();

    private static string CreateSigningPem()
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        return rsa.ExportPkcs8PrivateKeyPem();
    }

    public const string OperatorKey = "tg_admin_golden_operador_00000000000000";
    private const string Pepper = "R29sZGVuVGVzdHNQZXBwZXJOYW9Vc2FyRW1Qcm9kdWNhbyE=";
    private PostgreSqlContainer? _pg;
    private RedisContainer? _valkey;
    private IContainer? _s3;
    private RabbitMqContainer? _rabbit;
    private Microsoft.Extensions.Hosting.IHost? _workers;
    private const string S3Config = """{"identities":[{"name":"t","credentials":[{"accessKey":"tg-test","secretKey":"tg-test-secret"}],"actions":["Read","Write","List","Tagging","Admin"]}]}""";
    private WebApplicationFactory<Program>? _factory;

    public static string EngineUrl => Environment.GetEnvironmentVariable("RTC_REGULAR_URL") ?? "http://127.0.0.1:8080/api/";

    public HttpClient Api { get; private set; } = null!;
    public string ApiKey { get; private set; } = "";
    public string AppConnection { get; private set; } = "";
    public string SuperuserConnection => _pg!.GetConnectionString();

    /// <summary>Tenant sem retenção de XML (zero_retention).</summary>
    public string ZeroRetentionApiKey { get; private set; } = "";

    /// <summary>Relógio da API: o teste de replay avança o mês de competência.</summary>
    public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 12, 10, 15, 0, 0, TimeSpan.Zero));

    public TestPki Pki { get; } = TestPki.LoadOrCreate(Path.Combine(Paths.RepoRoot, "tests", "golden", "pki"));

    public IServiceProvider Services => _factory!.Services;

    /// <summary>Certificado TLS do receptor de webhooks do teste (pinning por thumbprint nos workers).</summary>
    public System.Security.Cryptography.X509Certificates.X509Certificate2 WebhookTls { get; } = CreateTls();

    private static System.Security.Cryptography.X509Certificates.X509Certificate2 CreateTls()
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var req = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=localhost", rsa,
            System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        using var tmp = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        return System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12(
            tmp.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pfx), null);
    }

    public async Task InitializeAsync()
    {
        using (var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(5) })
        {
            try
            {
                (await probe.GetAsync(new Uri(new Uri(EngineUrl), "calculadora/dados-abertos/versao"))).EnsureSuccessStatusCode();
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                throw new InvalidOperationException(
                    $"Motor regular não responde em {EngineUrl}. Suba com 'docker compose up -d rtc-regular' ou defina RTC_REGULAR_URL.", ex);
            }
        }

        var image = new ImageFromDockerfileBuilder()
            .WithDockerfileDirectory(Path.Combine(Paths.RepoRoot, "infra", "postgres"))
            .WithDockerfile("Dockerfile")
            .WithName("taxgateway/postgres:18-test")
            .WithCleanUp(false)
            .Build();
        await image.CreateAsync();
        _pg = new PostgreSqlBuilder(image.FullName).WithUsername("tg_bootstrap").WithPassword("bootstrap").WithDatabase("taxgateway").Build();
        _valkey = new RedisBuilder("valkey/valkey:8.1.12").Build();
        _s3 = new ContainerBuilder("chrislusf/seaweedfs:4.54")
            .WithResourceMapping(System.Text.Encoding.UTF8.GetBytes(S3Config), FilePath.Of("/etc/seaweedfs/s3.json"))
            .WithCommand("server", "-dir=/data", "-ip.bind=0.0.0.0", "-master.volumeSizeLimitMB=64", "-s3", "-s3.port=8333", "-s3.config=/etc/seaweedfs/s3.json")
            .WithPortBinding(8333, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(8333).ForPath("/").ForStatusCodeMatching(_ => true)))
            .Build();
        _rabbit = new RabbitMqBuilder("rabbitmq:4.3.8-management").Build();
        await Task.WhenAll(_pg.StartAsync(), _valkey.StartAsync(), _s3.StartAsync(), _rabbit.StartAsync());

        await using (var owner = NpgsqlDataSource.Create(For("tg_owner")))
        {
            await new SqlMigrator(owner).MigrateAsync();
        }

        AppConnection = For("tg_app");
        var operatorHash = Convert.ToHexStringLower(System.Security.Cryptography.HMACSHA256.HashData(
            Convert.FromBase64String(Pepper), System.Text.Encoding.UTF8.GetBytes(OperatorKey)));
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:App"] = AppConnection,
            ["ConnectionStrings:Admin"] = For("tg_admin"),
            ["ConnectionStrings:Valkey"] = _valkey.GetConnectionString(),
            ["Engines:Bundle"] = Paths.Bundle,
            ["Engines:Regular:BaseUrl"] = EngineUrl,
            ["Engines:Simples:BaseUrl"] = Environment.GetEnvironmentVariable("RTC_SIMPLES_URL") ?? "http://127.0.0.1:8082/api/",
            ["Engines:Regular:SyncTimeoutMs"] = "5000",
            ["Auth:ApiKeyPepper"] = Pepper,
            ["Admin:Operators:0:Name"] = "golden",
            ["Admin:Operators:0:KeyHash"] = operatorHash,
            ["Admin:Operators:0:Scopes:0"] = "admin:commercial",
            ["Admin:Operators:0:Scopes:1"] = "admin:fiscal",
            ["Logging:LogLevel:Default"] = "Warning",
            ["Kms:LocalMasterKey"] = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData("golden-kms"u8)),
            ["Storage:ServiceUrl"] = $"http://{_s3.Hostname}:{_s3.GetMappedPublicPort(8333)}",
            ["Storage:AccessKey"] = "tg-test",
            ["Storage:SecretKey"] = "tg-test-secret",
            ["Storage:Bucket"] = "golden-xml",
            ["Xml:ExtraTrustAnchorPemFiles:0"] = Pki.RootPemPath,
            ["Webhooks:AllowInsecureForTests"] = "true",
            ["Webhooks:TestTrustedThumbprint"] = WebhookTls.Thumbprint,
            ["Messaging:RabbitUri"] = _rabbit.GetConnectionString(),
            ["Identity:AllowHttp"] = "true",
            // Chave de assinatura fixa e comum a todas as instâncias (como em produção); a efêmera é por processo.
            ["Identity:SigningKeys:0:Kid"] = "golden",
            ["Identity:SigningKeys:0:Pem"] = SigningPem,
        };
        Settings = settings;
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Golden");
            // UseSetting (e não ConfigureAppConfiguration): o Program lê a configuração antes do Build().
            foreach (var (k, v) in settings)
            {
                b.UseSetting(k, v);
            }

            b.ConfigureTestServices(s => s.AddSingleton<TimeProvider>(Clock));
        });
        Api = _factory.CreateClient();

        // Prontidão: parâmetros + cadastro de municípios do motor carregados.
        for (var i = 0; i < 120 && !(await Api.GetAsync(new Uri("/health/ready", UriKind.Relative))).IsSuccessStatusCode; i++)
        {
            await Task.Delay(500);
        }

        // Workers em processo (relay do outbox, webhooks, lotes), com os roles tg_worker e tg_relay.
        // SEFAZ simulada (Fase 9): os workers apontam para ela; o laço automático fica desligado e os testes chamam
        // DfeSyncService.RunOnceAsync com o relógio controlado.
        Sefaz = new SefazSimulator(WebhookTls, Clock);
        var workerSettings = new Dictionary<string, string?>(settings)
        {
            ["Dfe:DistUrl"] = Sefaz.DistUrl,
            ["Dfe:EventoUrl"] = Sefaz.EventoUrl,
            ["Dfe:TestTrustedServerThumbprint"] = WebhookTls.Thumbprint,
            ["Dfe:WorkerEnabled"] = "false",
            ["ConnectionStrings:Worker"] = For("tg_worker"),
            ["ConnectionStrings:Relay"] = For("tg_relay"),
        };
        var workerBuilder = TaxGateway.Workers.WorkerHost.CreateBuilder([], workerSettings);
        // DF-e com o relógio do fixture (bloqueio de 1 h, teto de 20/h, vencimento do A1). O resto dos workers segue no
        // relógio real: a assinatura dos webhooks é conferida contra a hora real nos testes de pipeline.
        workerBuilder.Services.AddSingleton(sp => ActivatorUtilities.CreateInstance<TaxGateway.Infrastructure.Dfe.DfeSyncService>(sp, (TimeProvider)Clock));
        workerBuilder.Services.AddSingleton(sp => ActivatorUtilities.CreateInstance<TaxGateway.Infrastructure.Dfe.CertificateExpiryService>(sp, (TimeProvider)Clock));
        _workers = workerBuilder.Build();
        await _workers.StartAsync();

        ApiKey = await CreateTenantAsync("Golden", "11222333000181", 90);
        ZeroRetentionApiKey = await CreateTenantAsync("Golden sem retenção", "11444777000161", null);
    }

    public SefazSimulator Sefaz { get; private set; } = null!;

    /// <summary>Serviços do host de workers em processo (DfeSyncService, CertificateExpiryService...).</summary>
    public IServiceProvider WorkerServices => _workers!.Services;

    /// <summary>CNPJ numérico válido e único por execução (raiz 8xxxxxxx, filial 0001).</summary>
    public static string NewCnpj()
    {
        var n = Interlocked.Increment(ref _cnpjSeq);
        var stem = $"{(80000000 + (Environment.ProcessId % 1000 * 1000) + n) % 100000000:D8}0001";
        static int Dv(string s, int[] w)
        {
            var sum = 0;
            for (var i = 0; i < w.Length; i++)
            {
                sum += (s[i] - '0') * w[i];
            }

            var r = sum % 11;
            return r < 2 ? 0 : 11 - r;
        }

        var d1 = Dv(stem, [5, 4, 3, 2, 9, 8, 7, 6, 5, 4, 3, 2]);
        var d2 = Dv(stem + d1, [6, 5, 4, 3, 2, 9, 8, 7, 6, 5, 4, 3, 2]);
        return $"{stem}{d1}{d2}";
    }

    private static int _cnpjSeq;

    /// <summary>Hash (HMAC com o pepper do fixture) de uma chave de operador, para configurar operadores extras.</summary>
    public static string OperatorHash(string key) => Convert.ToHexStringLower(System.Security.Cryptography.HMACSHA256.HashData(
        Convert.FromBase64String(Pepper), System.Text.Encoding.UTF8.GetBytes(key)));

    /// <summary>Configuração da API do fixture (para subir instâncias extras nos testes de identidade).</summary>
    public IReadOnlyDictionary<string, string?> Settings { get; private set; } = new Dictionary<string, string?>();

    /// <summary>Outra instância da API com a mesma infraestrutura (Postgres, Valkey) e configuração extra.</summary>
    public WebApplicationFactory<Program> NewApiInstance(IDictionary<string, string?> extra) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Golden");
            foreach (var (k, v) in Settings.Concat(extra))
            {
                b.UseSetting(k, v);
            }

            b.ConfigureTestServices(s => s.AddSingleton<TimeProvider>(Clock));
        });

    /// <summary>Cria tenant OEM e devolve (tenant_id, api_key).</summary>
    public async Task<(Guid Tenant, string ApiKey)> CreateTenantWithIdAsync(string name, string cnpj, bool autoProvision = true)
    {
        using var create = new HttpRequestMessage(HttpMethod.Post, "/admin/v1/tenants")
        {
            Content = JsonContent.Create(new JsonObject { ["nome"] = name, ["cnpj_raiz"] = cnpj, ["plano"] = "OEM", ["auto_provisionar_cnpj"] = autoProvision }),
        };
        create.Headers.Add("X-Admin-Key", OperatorKey);
        var resp = await Api.SendAsync(create);
        resp.EnsureSuccessStatusCode();
        var body = (await resp.Content.ReadFromJsonAsync<JsonObject>())!;
        return (Guid.Parse(body["tenant_id"]!.GetValue<string>()), body["api_key"]!.GetValue<string>());
    }

    private async Task<string> CreateTenantAsync(string name, string cnpj, int? retention)
    {
        using var create = new HttpRequestMessage(HttpMethod.Post, "/admin/v1/tenants")
        {
            Content = JsonContent.Create(new JsonObject
            {
                ["nome"] = name,
                ["cnpj_raiz"] = cnpj,
                ["plano"] = "OEM",
                ["auto_provisionar_cnpj"] = true,
                ["retencao_xml_dias"] = retention,
            }),
        };
        create.Headers.Add("X-Admin-Key", OperatorKey);
        var resp = await Api.SendAsync(create);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<JsonObject>())!["api_key"]!.GetValue<string>();
    }

    private string For(string role) =>
        new NpgsqlConnectionStringBuilder(_pg!.GetConnectionString()) { Username = role, Password = "dev-only" }.ConnectionString;

    public async Task DisposeAsync()
    {
        Api?.Dispose();
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        if (_pg is not null)
        {
            await _pg.DisposeAsync();
        }

        if (_valkey is not null)
        {
            await _valkey.DisposeAsync();
        }

        if (_s3 is not null)
        {
            await _s3.DisposeAsync();
        }

        if (_workers is not null)
        {
            await _workers.StopAsync();
            _workers.Dispose();
        }

        if (_rabbit is not null)
        {
            await _rabbit.DisposeAsync();
        }

        if (Sefaz is not null)
        {
            await Sefaz.DisposeAsync();
        }
    }
}

[CollectionDefinition(Name)]
public sealed class GoldenCollection : ICollectionFixture<GoldenFixture>
{
    public const string Name = "golden";
}

public static class Paths
{
    public static string RepoRoot { get; } = FindRoot();

    public static string CorpusDir => Path.Combine(RepoRoot, "tests", "golden", "corpus", "v0");

    public static string Bundle => JsonNode.Parse(File.ReadAllText(Path.Combine(RepoRoot, "infra", "rfb", "versions.json")))!["bundle"]!.GetValue<string>();

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "global.json")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Raiz do repositório (global.json) não encontrada.");
    }
}
