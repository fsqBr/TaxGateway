using TaxGateway.Core.Audit;
using TaxGateway.Core.Engines;
using TaxGateway.Core.Fiscal;

namespace TaxGateway.UnitTests;

public sealed class AuditRulesTests
{
    private static readonly BaseComponents Comps = new(1000m, null, null, null, null, null, null, null, null, null, null, null, null, null, null);

    private static DeclaredTax T(decimal p, decimal v, decimal? red = null, decimal? efet = null) => new(p, v, red, efet, null, null, null);

    private static DeclaredItem Item(decimal vbc = 1000m, DeclaredTax? cbs = null, DeclaredTax? uf = null, DeclaredTax? mun = null, decimal? vibs = null, int n = 1,
        string? cst = "000", string? cClass = "000001", IReadOnlyList<string>? unsupported = null) =>
        new(n, "84713012", cst, cClass, vbc, uf ?? T(0.05m, 0.50m), mun ?? T(0.05m, 0.50m), vibs ?? 1.00m, cbs ?? T(11.2m, 112.00m),
            null, Comps, null, unsupported ?? []);

    private static DeclaredDocument Doc(params DeclaredItem[] itens) => Doc(Totals(itens), itens);

    private static DeclaredDocument Doc(DeclaredTotals? totals, params DeclaredItem[] itens) => new(
        "35270611222333000181550010000045901000000000", "55", new DateTimeOffset(2027, 6, 15, 10, 0, 0, TimeSpan.FromHours(-3)),
        "11222333000181", "3", null, null, null, false, 3550308, 1, false, null, itens, totals);

    private static DeclaredTotals Totals(DeclaredItem[] itens) => new(
        itens.Sum(i => i.VBc ?? 0), itens.Sum(i => i.IbsUf!.Valor), itens.Sum(i => i.IbsMun!.Valor), itens.Sum(i => i.VIbs ?? 0),
        itens.Sum(i => i.Cbs!.Valor), 0, 0, 0, 0, 0, 0, null);

    private static EngineItemResult Eng(int n = 1, decimal vbc = 1000m, decimal pCbs = 11.2m, decimal vCbs = 112.00m, string estado = "CALCULO_SIMULADO",
        IReadOnlyList<EngineError>? erros = null, decimal? red = null, decimal? efet = null) =>
        new(n, estado, "000", "000001", vbc, new TaxLine(pCbs, vCbs, red, efet), new TaxLine(0.05m, 0.50m, red, efet is null ? null : 0m),
            new TaxLine(0.05m, 0.50m, red, efet is null ? null : 0m), 1.00m, null, erros ?? [], null);

    private static EngineCalculation Calc(params EngineItemResult[] itens) => new(itens,
        new EngineTotals(itens.Sum(i => i.BaseCalculo ?? 0), itens.Sum(i => i.Cbs?.Valor ?? 0), 0.5m, 0.5m, 1m, 0), [],
        new EngineVersion("b", RfbEngine.Regular, "1.7.2", "V0073"));

    private static string[] Rules(AuditOutcome o) => o.Findings.Select(f => f.Regra).Distinct().Order().ToArray();

    [Fact]
    public void Correct_document_is_conforme_without_findings()
    {
        var o = OutboundAuditor.Audit(Doc(Item()), Calc(Eng()));
        Assert.Equal(AuditStatus.Conforme, o.Status);
        Assert.Empty(o.Findings);
        Assert.Equal(0m, o.Diferenca);
    }

    [Fact]
    public void Base_different_from_engine_is_R010()
    {
        var o = OutboundAuditor.Audit(Doc(Item(vbc: 900m, cbs: T(11.2m, 100.80m), uf: T(0.05m, 0.45m), mun: T(0.05m, 0.45m), vibs: 0.90m)),
            Calc(Eng(vbc: 1000m)));
        Assert.Equal(AuditStatus.Divergente, o.Status);
        Assert.Contains("R010", Rules(o));
    }

    [Fact]
    public void Value_inconsistent_with_base_and_rate_is_R011_and_tolerance_is_one_cent()
    {
        var off = OutboundAuditor.Audit(Doc(Item(cbs: T(11.2m, 112.02m))), Calc(Eng()));
        Assert.Contains("R011", Rules(off));
        var withinTolerance = OutboundAuditor.Audit(Doc(Item(cbs: T(11.2m, 112.01m))), Calc(Eng(vCbs: 112.01m)));
        Assert.DoesNotContain("R011", Rules(withinTolerance));
    }

    [Fact]
    public void Rate_different_from_catalog_is_R012()
    {
        var o = OutboundAuditor.Audit(Doc(Item(cbs: T(8.8m, 88.00m))), Calc(Eng()));
        Assert.Contains("R012", Rules(o));
        Assert.Contains("R013", Rules(o));   // valor também diverge do motor
        Assert.Equal(24.00m, o.Findings.Single(f => f.Regra == "R013").ImpactoFinanceiro);
    }

    [Fact]
    public void Missing_reduction_declared_against_engine_reduction_is_R012()
    {
        var o = OutboundAuditor.Audit(Doc(Item(cbs: T(11.2m, 0m))), Calc(Eng(vCbs: 0m, red: 100m, efet: 0m)));
        Assert.Contains("R012", Rules(o));
    }

    [Fact]
    public void Vibs_must_be_sum_of_uf_and_mun()
    {
        var o = OutboundAuditor.Audit(Doc(Item(vibs: 1.01m)), Calc(Eng()));
        Assert.Contains("R011", Rules(o));
    }

    [Theory]
    [InlineData("REG-006", "R020")]
    [InlineData("REG-004", "R020")]
    [InlineData("REG-005", "R022")]
    [InlineData("REG-011", "R025")]
    [InlineData("REG-002", "R025")]
    [InlineData("REG-009", "R029")]
    public void Engine_item_errors_map_to_deterministic_rules(string code, string rule)
    {
        var o = OutboundAuditor.Audit(Doc(Item()), Calc(Eng(estado: "INCONSISTENCIA_ENTRADA", erros: [new EngineError(code, "t", "d", "#/itens/0")])));
        Assert.Equal(AuditStatus.Divergente, o.Status);
        Assert.Equal([rule], Rules(o));
    }

    [Fact]
    public void Totals_must_match_sum_of_items_R030()
    {
        var items = new[] { Item(), Item(n: 2) };
        var wrong = Totals(items) with { VCbs = 224.01m };
        var o = OutboundAuditor.Audit(Doc(wrong, items), Calc(Eng(), Eng(2)));
        Assert.Equal(["R030"], Rules(o));
        Assert.Contains("R030", Rules(OutboundAuditor.Audit(Doc((DeclaredTotals?)null, items), Calc(Eng(), Eng(2)))));
    }

    [Fact]
    public void Not_implemented_or_unsupported_items_make_the_document_indeterminado()
    {
        var ni = OutboundAuditor.Audit(Doc(Item()), Calc(Eng(estado: "NAO_IMPLEMENTADO", erros: [new EngineError("CAL-007", "t", "d", null)])));
        Assert.Equal(AuditStatus.Indeterminado, ni.Status);
        Assert.Equal(["I002"], Rules(ni));

        var uns = OutboundAuditor.Audit(Doc(Item(unsupported: ["gIBSCBSMono"])), null);
        Assert.Equal(AuditStatus.Indeterminado, uns.Status);
    }

    [Fact]
    public void Deterministic_violation_wins_over_indeterminate()
    {
        var o = OutboundAuditor.Audit(Doc((DeclaredTotals?)null, Item(cbs: T(8.8m, 88m)), Item(n: 2, unsupported: ["gTransfCred"])), Calc(Eng()));
        Assert.Equal(AuditStatus.Divergente, o.Status);
    }

    [Theory]
    [InlineData("35270611222333000181550010000045901000000000", false)]   // DV errado
    [InlineData("3527061122233300018155001000004590100000000", false)]   // 43 posições
    public void Access_key_validation(string key, bool valid) => Assert.Equal(valid, AccessKey.IsValid(key));

    [Fact]
    public void Access_key_accepts_alphanumeric_cnpj_and_checks_dv()
    {
        var baseKey = "352706" + "12ABC34501DE35" + "55001000004590" + "1" + "00000000";
        Assert.Equal(43, baseKey.Length);
        var valid = Enumerable.Range(0, 10).Select(d => baseKey + d).Single(AccessKey.IsValid);
        Assert.Equal("12ABC34501DE35", AccessKey.EmitterCnpj(valid));
        Assert.Equal("55", AccessKey.Model(valid));
    }
}
