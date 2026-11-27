using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Npgsql;

using StackExchange.Redis;

using TaxGateway.Core.Params;
using TaxGateway.Infrastructure.Engines;
using TaxGateway.Infrastructure.Params;

namespace TaxGateway.GoldenTests;

/// <summary>D-R30: sincronização automática do catálogo com a base oficial da calculadora da Receita.</summary>
[Collection(GoldenCollection.Name)]
public sealed class RateSyncTests(GoldenFixture env)
{
    private const int Municipio = 1100015;   // Alta Floresta D'Oeste/RO: fora do corpus e sem contas de teste

    private RateSyncService Service(IOfficialRateSource source, int municipios, bool dryRun = false) =>
        new(env.WorkerServices.GetRequiredService<NpgsqlDataSource>(), source, env.WorkerServices.GetRequiredService<ParamSnapshotService>(),
            env.WorkerServices.GetRequiredService<IConnectionMultiplexer>(), env.WorkerServices.GetRequiredService<IOptions<EngineOptions>>(),
            NullLogger<RateSyncService>.Instance)
        { MunicipioLimit = municipios, DryRun = dryRun };

    [Fact]
    public async Task Catalogo_concorda_com_a_base_oficial_do_motor()
    {
        var source = new EngineRateSource(env.WorkerServices.GetRequiredService<IHttpClientFactory>());
        Assert.Equal(0.9m, await source.UniaoAsync(new DateOnly(2026, 6, 1), CancellationToken.None));

        var result = await Service(source, 20, dryRun: true).RunOnceAsync(full: true, CancellationToken.None);
        Assert.True(result.Probes > 100);
        Assert.True(result.Changes.Count == 0, string.Join(" | ", result.Changes));
    }

    /// <summary>A Receita publica alíquota municipal nova com vigência futura: entra sozinha, com a data, e sai quando some.</summary>
    [Fact]
    public async Task Mudanca_publicada_entra_no_catalogo_com_a_vigencia_e_aviso()
    {
        var snapshots = env.WorkerServices.GetRequiredService<ParamSnapshotService>();
        var from = new DateOnly(2028, 3, 10);
        var fake = new CatalogMirror(snapshots, Municipio, from, 0.5m);

        var applied = await Service(fake, 5).RunOnceAsync(full: true, CancellationToken.None);
        Assert.True(applied.Changes.Any(c => c.StartsWith($"IBS_MUN {Municipio}: 0.5% a partir de 10/03/2028", StringComparison.Ordinal)), string.Join(" | ", applied.Changes));
        Assert.Equal(0.5m, snapshots.Current.Rates.Resolve(Tributo.IbsMun, Municipio, from)!.Aliquota);
        Assert.NotEqual(0.5m, snapshots.Current.Rates.Resolve(Tributo.IbsMun, Municipio, from.AddDays(-1))?.Aliquota);

        // O ciclo do NotificationMailer publica a mudança na linha do tempo e avisa as contas afetadas.
        await using (var fan = env.WorkerServices.GetRequiredService<NpgsqlDataSource>().CreateCommand("SELECT app.notify_param_changes()"))
        {
            await fan.ExecuteScalarAsync();
        }

        await using (var db = NpgsqlDataSource.Create(env.ConnectionFor("tg_owner")))
        {
            await using var cmd = db.CreateCommand("""
                SELECT pc.status, pc.approved_by, fu.title, fu.effective_from
                  FROM param_changes pc JOIN fiscal_updates fu ON fu.change_id = pc.id
                 WHERE pc.proposed_by = 'sincronizacao-rfb' AND pc.payload::text LIKE '%' || $1 || '%'
                 ORDER BY pc.created_at DESC LIMIT 1
                """);
            cmd.Parameters.Add(new() { Value = Municipio.ToString(System.Globalization.CultureInfo.InvariantCulture) });
            await using var r = await cmd.ExecuteReaderAsync();
            Assert.True(await r.ReadAsync(), "mudança sem entrada na linha do tempo");
            Assert.Equal("APPLIED", r.GetString(0));
            Assert.Equal("motor-oficial-rfb", r.GetString(1));
            Assert.Equal("Alíquota atualizada pela Receita Federal", r.GetString(2));
        }

        // Segunda rodada igual: nada a fazer.
        Assert.Empty((await Service(fake, 5).RunOnceAsync(full: true, CancellationToken.None)).Changes);

        // A Receita retira a alíquota própria: volta à referência.
        var back = await Service(new CatalogMirror(snapshots, Municipio, from, null), 5).RunOnceAsync(full: true, CancellationToken.None);
        Assert.True(back.Changes.Count == 1, string.Join(" | ", back.Changes));
        Assert.NotEqual(0.5m, snapshots.Current.Rates.Resolve(Tributo.IbsMun, Municipio, from)?.Aliquota);
    }

    /// <summary>D-R31: bundle novo anuncia uma vez (com o resumo da regressão), sincroniza completo e só confere de novo no dia seguinte.</summary>
    [Fact]
    public async Task Worker_anuncia_o_bundle_novo_uma_vez_e_sincroniza()
    {
        var snapshots = env.WorkerServices.GetRequiredService<ParamSnapshotService>();
        var bundle = env.WorkerServices.GetRequiredService<IOptions<EngineOptions>>().Value.Bundle;
        await using var owner = NpgsqlDataSource.Create(env.ConnectionFor("tg_owner"));
        await using (var del = owner.CreateCommand("DELETE FROM rfb_sync_state WHERE bundle = $1"))
        {
            del.Parameters.Add(new() { Value = bundle });
            await del.ExecuteNonQueryAsync();
        }

        await using (var prev = owner.CreateCommand("""
            INSERT INTO rfb_sync_state (bundle, synced_at, probes, announced_at) VALUES ('2026.01.0', now() - interval '30 days', 1, now())
            ON CONFLICT (bundle) DO NOTHING
            """))
        {
            await prev.ExecuteNonQueryAsync();
        }

        var (tenant, _) = await env.CreateTenantWithIdAsync("Aviso de motor", GoldenFixture.NewCnpj());
        var worker = new TaxGateway.Workers.RateSyncWorker(env.WorkerServices.GetRequiredService<NpgsqlDataSource>(),
            Service(new CatalogMirror(snapshots, Municipio, new DateOnly(2028, 3, 10), null), 3),
            env.WorkerServices.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>(),
            env.WorkerServices.GetRequiredService<IOptions<EngineOptions>>(), NullLogger<TaxGateway.Workers.RateSyncWorker>.Instance);

        var first = await worker.RunOnceAsync(CancellationToken.None);
        Assert.True(first is { Full: true, Probes: > 0 });
        Assert.Null(await worker.RunOnceAsync(CancellationToken.None));   // completa feita e conferência diária já rodou

        // Outra réplica (ou o dia seguinte): bundle já sincronizado → só a conferência diária de União e UFs.
        var other = new TaxGateway.Workers.RateSyncWorker(env.WorkerServices.GetRequiredService<NpgsqlDataSource>(),
            Service(new CatalogMirror(snapshots, Municipio, new DateOnly(2028, 3, 10), null), 3),
            env.WorkerServices.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>(),
            env.WorkerServices.GetRequiredService<IOptions<EngineOptions>>(), NullLogger<TaxGateway.Workers.RateSyncWorker>.Instance);
        var daily = await other.RunOnceAsync(CancellationToken.None);
        Assert.True(daily is { Full: false, Changes.Count: 0 });

        var avisos = await env.Services.GetRequiredService<TaxGateway.Infrastructure.Persistence.ITenantDb>().InTenantAsync(new TaxGateway.Core.Tenancy.TenantId(tenant), async (sc, ct) =>
        {
            await using var q = sc.CreateCommand("SELECT count(*) FROM tenant_notifications WHERE kind = 'MOTOR_ATUALIZADO'");
            return (long)(await q.ExecuteScalarAsync(ct))!;
        }, CancellationToken.None);
        Assert.Equal(1L, avisos);

        await using var cmd = owner.CreateCommand("""
            SELECT (SELECT count(*) FROM fiscal_updates WHERE bundle = $1 AND kind = 'MOTOR'), (SELECT probes FROM rfb_sync_state WHERE bundle = $1)
            """);
        cmd.Parameters.Add(new() { Value = bundle });
        await using var r = await cmd.ExecuteReaderAsync();
        Assert.True(await r.ReadAsync());
        Assert.Equal(1L, r.GetInt64(0));
        Assert.True(r.GetInt32(1) > 0);
    }

    [Fact]
    public async Task Linha_do_tempo_publica_e_aviso_de_vigencia()
    {
        await using (var today = env.WorkerServices.GetRequiredService<NpgsqlDataSource>().CreateCommand("SELECT app.notify_rates_effective_today()"))
        {
            Assert.True((int)(await today.ExecuteScalarAsync())! >= 0);
        }

        var res = await env.Api.GetAsync(new Uri("/v1/publico/atualizacoes-fiscais?limite=500", UriKind.Relative));
        res.EnsureSuccessStatusCode();
        var body = System.Text.Json.Nodes.JsonNode.Parse(await res.Content.ReadAsStringAsync())!;
        Assert.Equal(env.WorkerServices.GetRequiredService<IOptions<EngineOptions>>().Value.Bundle, (string?)body["bundle"]);
        Assert.True(body["atualizacoes"]!.AsArray().Count <= 100);
    }

    [Fact]
    public void Resumo_do_aviso_usa_o_relatorio_de_regressao_quando_existe()
    {
        var dir = Directory.CreateTempSubdirectory("tg-bundles-").FullName;
        Directory.CreateDirectory(Path.Combine(dir, "fiscal-bundles"));
        Assert.DoesNotContain("casos", TaxGateway.Workers.RateSyncWorker.Summary("x", dir), StringComparison.Ordinal);

        File.WriteAllText(Path.Combine(dir, "fiscal-bundles", "b1.json"), """{"casos":158,"com_diferenca":2,"destaques":["Venda interestadual","Combustível"]}""");
        Assert.Contains("Em 2 de 158 casos de referência o cálculo mudou. Destaques: Venda interestadual; Combustível.",
            TaxGateway.Workers.RateSyncWorker.Summary("b1", dir), StringComparison.Ordinal);
        File.WriteAllText(Path.Combine(dir, "fiscal-bundles", "b2.json"), """{"casos":158,"com_diferenca":0}""");
        Assert.Contains("Nos 158 casos de referência do TaxGateway o cálculo não mudou.", TaxGateway.Workers.RateSyncWorker.Summary("b2", dir), StringComparison.Ordinal);
        File.WriteAllText(Path.Combine(dir, "fiscal-bundles", "b3.json"), "{");
        Assert.DoesNotContain("casos", TaxGateway.Workers.RateSyncWorker.Summary("b3", dir), StringComparison.Ordinal);
    }

    /// <summary>Responde o que o catálogo já tem, exceto o município escolhido a partir de uma data (null = igual ao catálogo).</summary>
    private sealed class CatalogMirror(ParamSnapshotService snapshots, int municipio, DateOnly from, decimal? value) : IOfficialRateSource
    {
        private readonly RateCatalog _base = snapshots.Current.Rates;

        public Task<decimal?> UniaoAsync(DateOnly onDate, CancellationToken ct) => Task.FromResult<decimal?>(null);

        public Task<decimal?> UfAsync(int codigoUf, DateOnly onDate, CancellationToken ct) => Task.FromResult<decimal?>(null);

        public Task<decimal?> MunicipioAsync(int codigoMunicipio, DateOnly onDate, CancellationToken ct) =>
            Task.FromResult(codigoMunicipio == municipio && value is not null && onDate >= from && Reference(codigoMunicipio, onDate) is not null
                ? value
                : Reference(codigoMunicipio, onDate));

        // Sem alíquota própria do município de teste (estado inicial): a referência nacional do catálogo.
        private decimal? Reference(int codigoMunicipio, DateOnly onDate) =>
            codigoMunicipio == municipio
                ? _base.All.Where(r => r.Tributo == Tributo.IbsMun && r.EnteCodigo is null && r.Produto is null && onDate >= r.ValidFrom && (r.ValidTo is null || onDate < r.ValidTo))
                    .Select(r => (decimal?)r.Aliquota).FirstOrDefault()
                : _base.Resolve(Tributo.IbsMun, codigoMunicipio, onDate)?.Aliquota;
    }
}
