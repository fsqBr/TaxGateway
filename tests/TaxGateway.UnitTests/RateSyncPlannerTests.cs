using TaxGateway.Core.Params;
using TaxGateway.Infrastructure.Params;

namespace TaxGateway.UnitTests;

/// <summary>D-R30: reconstrução da série da Receita (data exata da mudança) e comparação com o catálogo.</summary>
public sealed class RateSyncPlannerTests
{
    private static readonly DateOnly S = RateSyncPlanner.WindowStart;
    private static readonly DateOnly E = RateSyncPlanner.WindowEnd;

    private static RateRow Row(Tributo t, string? ente, decimal v, DateOnly from, DateOnly? to, FormaAplicacao? forma = null, bool provisoria = false) =>
        new(Guid.NewGuid(), t, ente, ente is null ? null : forma ?? FormaAplicacao.Substituicao, v, from, to, "LC 214/2025", "SENADO", provisoria);

    [Fact]
    public async Task Sondagem_acha_o_dia_exato_da_mudanca_e_conta_poucas_consultas()
    {
        var change = new DateOnly(2027, 3, 17);
        var calls = 0;
        var segs = await RateSyncPlanner.ProbeAsync(d =>
        {
            calls++;
            return Task.FromResult<decimal?>(d < change ? 0.9m : 8.8m);
        }, S, E);

        Assert.Equal([new RateSegment(S, change, 0.9m), new RateSegment(change, E, 8.8m)], segs);
        Assert.True(calls < 96 + 10, $"{calls} consultas");
    }

    [Fact]
    public async Task Sondagem_trata_periodo_sem_publicacao_e_passo_anual()
    {
        var segs = await RateSyncPlanner.ProbeAsync(d => Task.FromResult<decimal?>(d.Year == 2026 ? 0m : null), S, E, stepMonths: 12);
        Assert.Equal([new RateSegment(S, new DateOnly(2027, 1, 1), 0m), new RateSegment(new DateOnly(2027, 1, 1), E, null)], segs);
        Assert.Null(RateSyncPlanner.ValueAt(segs, new DateOnly(2040, 1, 1)));
    }

    [Fact]
    public void Moda_escolhe_o_valor_mais_comum_entre_os_entes()
    {
        IReadOnlyList<RateSegment> a = [new(S, E, 0.1m)];
        IReadOnlyList<RateSegment> b = [new(S, E, 0.1m)];
        IReadOnlyList<RateSegment> c = [new(S, new DateOnly(2027, 1, 1), 0.2m), new(new DateOnly(2027, 1, 1), E, 0.1m)];
        Assert.Equal([new RateSegment(S, E, 0.1m)], RateSyncPlanner.Mode([a, b, c], S, E));
        Assert.Equal([new RateSegment(S, E, null)], RateSyncPlanner.Mode([], S, E));
    }

    [Fact]
    public void Catalogo_igual_a_Receita_nao_gera_mudanca()
    {
        var y27 = new DateOnly(2027, 1, 1);
        var catalog = new[]
        {
            Row(Tributo.Cbs, null, 0.9m, S, y27), Row(Tributo.Cbs, null, 8.8m, y27, null, provisoria: true),
            Row(Tributo.IbsUf, null, 0.1m, S, y27), Row(Tributo.IbsMun, null, 0m, S, y27),
        };
        IReadOnlyList<RateSegment> pub = [new(S, y27, 0.9m), new(y27, E, null)];   // 2027 ainda não publicado: mantém a provisória
        var ufs = new Dictionary<string, IReadOnlyList<RateSegment>> { ["35"] = [new(S, y27, 0.1m), new(y27, E, null)] };
        var mun = new Dictionary<string, IReadOnlyList<RateSegment>> { ["3550308"] = [new(S, y27, 0m), new(y27, E, null)] };
        var plan = RateSyncPlanner.Plan(pub, ufs, mun, catalog, "b1");
        Assert.Empty(plan.Rows);
        Assert.Empty(plan.Supersede);
        Assert.Empty(plan.Changes);
    }

    [Fact]
    public void Mudanca_da_Receita_supera_a_linha_e_preserva_o_que_ela_nao_publicou()
    {
        var y27 = new DateOnly(2027, 1, 1);
        var jul = new DateOnly(2026, 7, 15);
        var old = Row(Tributo.Cbs, null, 0.9m, S, null);
        IReadOnlyList<RateSegment> pub = [new(S, jul, 0.9m), new(jul, y27, 1m), new(y27, E, null)];
        var plan = RateSyncPlanner.Plan(pub, new Dictionary<string, IReadOnlyList<RateSegment>>(), new Dictionary<string, IReadOnlyList<RateSegment>>(), [old], "b2");

        Assert.Equal([old.Id], plan.Supersede);
        Assert.Contains(plan.Rows, r => r.Tributo == "CBS" && r.Aliquota == 1m && r.ValidFrom == jul && r.ValidTo == y27 && r.Fonte == "BUNDLE_RFB");
        Assert.Contains(plan.Rows, r => r.Aliquota == 0.9m && r.ValidFrom == S && r.ValidTo == jul);
        // Fora do publicado (2027 em diante) a linha antiga volta, com o ato legal dela.
        Assert.Contains(plan.Rows, r => r.Aliquota == 0.9m && r.ValidFrom == y27 && r.ValidTo is null && r.AtoLegal == "LC 214/2025");
        Assert.Contains("1% a partir de 15/07/2026", plan.Changes.Single(), StringComparison.Ordinal);
        Assert.Contains("\"valid_to\":null", RateSyncPlanner.ToJson(plan.Rows), StringComparison.Ordinal);
    }

    [Fact]
    public void Ente_diferente_da_referencia_ganha_aliquota_propria_e_some_quando_volta_a_referencia()
    {
        var y27 = new DateOnly(2027, 1, 1);
        var catalog = new List<RateRow> { Row(Tributo.IbsUf, null, 0.1m, S, null) };
        var ufs = new Dictionary<string, IReadOnlyList<RateSegment>>
        {
            ["35"] = [new(S, y27, 0.1m), new(y27, E, null)],
            ["33"] = [new(S, y27, 0.1m), new(y27, E, null)],
            ["41"] = [new(S, y27, 0.15m), new(y27, E, null)],
        };
        var plan = RateSyncPlanner.Plan([new(S, E, null)], ufs, new Dictionary<string, IReadOnlyList<RateSegment>>(), catalog, "b3");
        var own = Assert.Single(plan.Rows);
        Assert.Equal(("IBS_UF", "PROPRIA_ENTE", "UF", "41", "SUBSTITUICAO", 0.15m), (own.Tributo, own.Tipo, own.Esfera, own.EnteCodigo, own.FormaAplicacao, own.Aliquota));
        Assert.Empty(plan.Supersede);

        // A UF volta para a referência: a alíquota própria é superada e nada novo entra para ela.
        var ownRow = Row(Tributo.IbsUf, "41", 0.15m, S, y27);
        ufs["41"] = [new(S, y27, 0.1m), new(y27, E, null)];
        var back = RateSyncPlanner.Plan([new(S, E, null)], ufs, new Dictionary<string, IReadOnlyList<RateSegment>>(), [.. catalog, ownRow], "b4");
        Assert.Equal([ownRow.Id], back.Supersede);
        Assert.Empty(back.Rows);
    }

    [Fact]
    public void Acrescimo_do_ente_e_comparado_sobre_a_referencia()
    {
        var y27 = new DateOnly(2027, 1, 1);
        var catalog = new[] { Row(Tributo.IbsMun, null, 0m, S, null), Row(Tributo.IbsMun, "3550308", 0.05m, S, y27, FormaAplicacao.Acrescimo),
            Row(Tributo.IbsMun, "3304557", 0.05m, S, y27, FormaAplicacao.Decrescimo) };
        var mun = new Dictionary<string, IReadOnlyList<RateSegment>>
        {
            ["3550308"] = [new(S, y27, 0.05m), new(y27, E, null)],
            ["3304557"] = [new(S, y27, 0m), new(y27, E, null)],
            ["3106200"] = [new(S, y27, 0m), new(y27, E, null)],
        };
        var plan = RateSyncPlanner.Plan([new(S, E, null)], new Dictionary<string, IReadOnlyList<RateSegment>>(), mun, catalog, "b5");
        Assert.Empty(plan.Rows);
        Assert.Empty(plan.Supersede);
    }

    [Theory]
    [InlineData("{\"aliquotaReferencia\":0.9}", 0.9)]
    [InlineData("8.8", 8.8)]
    [InlineData("{\"nome\":\"x\",\"aliquota\":0.1}", 0.1)]
    [InlineData("{\"nome\":\"x\"}", null)]
    public void Leitura_da_resposta_do_motor(string json, double? expected) =>
        Assert.Equal(expected is null ? null : (decimal)expected.Value, EngineRateSource.Parse(json));
}
