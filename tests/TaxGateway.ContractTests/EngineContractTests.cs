using System.Text.Json.Nodes;

using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using TaxGateway.Core.Engines;
using TaxGateway.Infrastructure.Engines;

namespace TaxGateway.ContractTests;

/// <summary>
/// Sobe as imagens do bundle (versions.json; prefixo em RTC_IMAGE_PREFIX) como em produção: root somente leitura,
/// /tmp com exec, sem egress para o portal da RFB (SDD §2.1). Usado no CI do PR de atualização fiscal (§2.3 passo 2).
/// </summary>
public sealed class EngineFixture : IAsyncLifetime
{
    public IContainer Regular { get; private set; } = null!;
    public IContainer Simples { get; private set; } = null!;
    public JsonNode Versions { get; } = JsonNode.Parse(File.ReadAllText(Path.Combine(Paths.RepoRoot, "infra", "rfb", "versions.json")))!;

    public Uri RegularUrl => new($"http://{Regular.Hostname}:{Regular.GetMappedPublicPort(8080)}/api/");
    public Uri SimplesUrl => new($"http://{Simples.Hostname}:{Simples.GetMappedPublicPort(8082)}/api/");

    public async Task InitializeAsync()
    {
        var prefix = Environment.GetEnvironmentVariable("RTC_IMAGE_PREFIX") ?? "taxgateway";
        Regular = Engine($"{prefix}/rtc-regular:{Versions["regular"]!["tag"]}", 8080, 9101);
        Simples = Engine($"{prefix}/rtc-simples:{Versions["simples"]!["tag"]}", 8082, 9103);
        await Task.WhenAll(Regular.StartAsync(), Simples.StartAsync());
    }

    private static IContainer Engine(string image, ushort port, ushort mgmt) => new ContainerBuilder(image)
        .WithPortBinding(port, true)
        .WithEnvironment("VERSAO_OFFLINE_URL", "http://127.0.0.1:9/disabled")
        .WithEnvironment("VERSAO_URL", "http://127.0.0.1:9/disabled")
        .WithCreateParameterModifier(p =>
        {
            p.HostConfig ??= new();
            p.HostConfig.ReadonlyRootfs = true;
            p.HostConfig.Tmpfs = new Dictionary<string, string> { ["/tmp"] = "exec,mode=1777,size=256m" };
        })
        .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(mgmt)
            .UntilHttpRequestIsSucceeded(r => r.ForPort(port).ForPath("/api/api-docs"), w => w.WithTimeout(TimeSpan.FromMinutes(3))))
        .Build();

    public async Task DisposeAsync()
    {
        await Regular.DisposeAsync();
        await Simples.DisposeAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class EngineCollection : ICollectionFixture<EngineFixture>
{
    public const string Name = "engines";
}

/// <summary>
/// Contrato que o TaxGateway usa dos motores (D-402): caminhos, propriedades dos schemas de entrada e os campos
/// de resposta que o adapter lê. Quebra = caminho ou propriedade usada removida, ou campo obrigatório novo numa
/// entrada. O contrato de referência fica versionado em contract/*.json (CONTRACT_REGENERATE=1 regera, com revisão).
/// </summary>
[Collection(EngineCollection.Name)]
public sealed class EngineContractTests(EngineFixture env)
{
    private static bool Regenerate => Environment.GetEnvironmentVariable("CONTRACT_REGENERATE") == "1";

    [Theory]
    [InlineData("regular")]
    [InlineData("simples")]
    public async Task OpenApi_keeps_the_contract_the_gateway_depends_on(string engine)
    {
        using var http = new HttpClient { BaseAddress = engine == "regular" ? env.RegularUrl : env.SimplesUrl };
        var spec = JsonNode.Parse(await http.GetStringAsync(new Uri("api-docs", UriKind.Relative)))!;
        var current = Extract(spec);
        var file = Path.Combine(Paths.RepoRoot, "tests", "TaxGateway.ContractTests", "contract", $"{engine}.json");
        if (Regenerate || !File.Exists(file))
        {
            await File.WriteAllTextAsync(file, current.ToJsonString(new() { WriteIndented = true }) + "\n");
        }

        var contract = JsonNode.Parse(await File.ReadAllTextAsync(file))!;
        var breaks = Compare(contract, current);
        Assert.True(breaks.Count == 0, $"Quebras de contrato no motor {engine}:\n" + string.Join('\n', breaks));
    }

    [Fact]
    public async Task Regular_adapter_parses_a_real_calculation_from_the_image()
    {
        var services = new ServiceCollection();
        services.AddHttpClient(RegularEngineAdapter.SyncClient, c => c.BaseAddress = env.RegularUrl);
        services.Configure<EngineOptions>(o => o.Bundle = env.Versions["bundle"]!.GetValue<string>());
        services.AddSingleton<RegularEngineAdapter>();
        await using var sp = services.BuildServiceProvider();
        var adapter = sp.GetRequiredService<RegularEngineAdapter>();

        var op = new EngineOperation("contrato", new DateTimeOffset(2027, 3, 10, 10, 0, 0, TimeSpan.FromHours(-3)), 3550308, "SP", 55, null,
        [
            new EngineItemInput(1, "84713012", null, "000", "000001", null, null, null, null, new NominalRates(11.2m, 0.05m, 0.05m, null, null),
                new TaxGateway.Core.Audit.BaseComponents(1000m, 50m, null, null, 30m, null, null, null, null, 120m, null, null, null, null, null)),
        ]);
        var calc = await adapter.CalculateAsync(op, CancellationToken.None);
        var item = Assert.Single(calc.Itens);
        Assert.Equal("CALCULO_SIMULADO", item.Estado);
        Assert.Equal(900.00m, item.BaseCalculo);            // vProd + frete − desconto − ICMS (motor)
        Assert.Equal(11.2m, item.Cbs!.Aliquota);
        Assert.Equal(100.80m, item.Cbs.Valor);
        Assert.Equal(calc.Totais.Cbs, item.Cbs.Valor);
        Assert.Equal(env.Versions["regular"]!["tag"]!.GetValue<string>().TrimStart('v'), calc.Version.AppVersion);
        Assert.NotNull(item.TributosDfe);
    }

    /// <summary>Subconjunto estável da spec: caminhos+métodos e, por schema de entrada/saída, propriedades e obrigatórios.</summary>
    private static JsonObject Extract(JsonNode spec)
    {
        var paths = new JsonArray();
        foreach (var (path, ops) in spec["paths"]!.AsObject().OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            foreach (var (method, _) in ops!.AsObject().OrderBy(m => m.Key, StringComparer.Ordinal))
            {
                paths.Add($"{method.ToUpperInvariant()} {path}");
            }
        }

        var schemas = new JsonObject();
        foreach (var (name, schema) in spec["components"]!["schemas"]!.AsObject().OrderBy(s => s.Key, StringComparer.Ordinal))
        {
            var props = schema?["properties"]?.AsObject().Select(p => (JsonNode)p.Key).ToArray() ?? [];
            var req = schema?["required"]?.AsArray().Select(r => (JsonNode)r!.GetValue<string>()).ToArray() ?? [];
            schemas[name] = new JsonObject { ["properties"] = new JsonArray(props), ["required"] = new JsonArray(req) };
        }

        return new JsonObject { ["paths"] = paths, ["schemas"] = schemas };
    }

    private static List<string> Compare(JsonNode contract, JsonNode current)
    {
        var breaks = new List<string>();
        var curPaths = current["paths"]!.AsArray().Select(p => p!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
        breaks.AddRange(contract["paths"]!.AsArray().Select(p => p!.GetValue<string>()).Where(p => !curPaths.Contains(p)).Select(p => $"caminho removido: {p}"));
        foreach (var (name, schema) in contract["schemas"]!.AsObject())
        {
            var cur = current["schemas"]![name];
            if (cur is null)
            {
                breaks.Add($"schema removido: {name}");
                continue;
            }

            var curProps = cur["properties"]!.AsArray().Select(p => p!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
            breaks.AddRange(schema!["properties"]!.AsArray().Select(p => p!.GetValue<string>()).Where(p => !curProps.Contains(p))
                .Select(p => $"propriedade removida: {name}.{p}"));
            if (name.EndsWith("Input", StringComparison.Ordinal))
            {
                var oldReq = schema["required"]!.AsArray().Select(p => p!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
                breaks.AddRange(cur["required"]!.AsArray().Select(p => p!.GetValue<string>()).Where(p => !oldReq.Contains(p))
                    .Select(p => $"novo campo obrigatório na entrada: {name}.{p}"));
            }
        }

        return breaks;
    }
}

public static class Paths
{
    public static string RepoRoot { get; } = Find();

    private static string Find()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "global.json")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("global.json não encontrado.");
    }
}
