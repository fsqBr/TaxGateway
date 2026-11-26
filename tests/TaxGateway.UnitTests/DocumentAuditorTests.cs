using TaxGateway.Core.Audit;
using TaxGateway.Core.Engines;

namespace TaxGateway.UnitTests;

public sealed class DocumentAuditorTests
{
    private static readonly BaseComponents Comps = new(1000m, null, null, null, null, null, null, null, null, null, null, null, null, null, null);
    private static readonly HashSet<string> None = [];

    private static DeclaredItem Reg(decimal vcbs = 112m, string cls = "000001") => new(1, "84713012", "000", cls, 1000m,
        new DeclaredTax(0.05m, 0.50m, null, null, null, null, null), new DeclaredTax(0.05m, 0.50m, null, null, null, null, null), 1.00m,
        new DeclaredTax(11.2m, vcbs, null, null, null, null, null), null, Comps, null, []);

    private static DeclaredDocument Doc(string crt, params DeclaredItem[] itens) => new(
        "35270611222333000181550010000045901000000000", "55", new DateTimeOffset(2027, 6, 15, 10, 0, 0, TimeSpan.FromHours(-3)),
        "11222333000181", crt, null, "11444777000161", null, false, 3550308, 1, true, null, itens,
        new DeclaredTotals(itens.Sum(i => i.VBc ?? 0), itens.Sum(i => i.IbsUf?.Valor ?? 0), itens.Sum(i => i.IbsMun?.Valor ?? 0),
            itens.Sum(i => i.VIbs ?? 0), itens.Sum(i => i.Cbs?.Valor ?? 0), 0, 0, 0, 0, 0, 0, null,
            itens.Sum(i => i.Simples?.ValorIbs ?? 0), itens.Sum(i => i.Simples?.ValorCbs ?? 0)));

    private static EngineCalculation Calc(decimal vcbs = 112m) => new(
        [new EngineItemResult(1, "CALCULO_SIMULADO", "000", "000001", 1000m, new TaxLine(11.2m, vcbs, null, null),
            new TaxLine(0.05m, 0.50m, null, null), new TaxLine(0.05m, 0.50m, null, null), 1.00m, null, [], null)],
        new EngineTotals(1000m, vcbs, 0.5m, 0.5m, 1m, 0), [], new EngineVersion("b", RfbEngine.Regular, "1.7.2", "V0073"));

    private static Dictionary<string, ClassificationInfo> Classes(bool credito = true, bool reducao = false, string trat = "Tributação integral") =>
        new() { ["000001"] = new ClassificationInfo("000001", trat, reducao, credito, credito) };

    private static AuditOutcome Audit(DeclaredDocument d, EngineCalculation? calc, string dir = "INBOUND", Dictionary<string, ClassificationInfo>? cls = null,
        IReadOnlySet<string>? confirmed = null) =>
        DocumentAuditor.Audit(new AuditInput(d, dir, true, false, calc, null, cls ?? Classes(), "APROPRIAVEL_POR_DESTAQUE", confirmed ?? None));

    [Fact]
    public void Inbound_credit_follows_classification_flags()
    {
        var ok = Audit(Doc("3", Reg()), Calc());
        Assert.Equal(AuditStatus.Conforme, ok.Status);
        Assert.Equal(113.00m, ok.CreditoPotencial);
        Assert.Equal("APROPRIAVEL_POR_DESTAQUE", ok.CreditoStatus);

        var semCredito = Audit(Doc("3", Reg()), Calc(), cls: Classes(credito: false));
        Assert.Equal(0m, semCredito.CreditoPotencial);
    }

    [Fact]
    public void Divergent_document_limits_credit_to_the_lower_value_and_marks_risk()
    {
        var o = Audit(Doc("3", Reg(vcbs: 150m)), Calc(vcbs: 112m));
        Assert.Equal(AuditStatus.Divergente, o.Status);
        Assert.Equal("RISCO_DIVERGENCIA", o.CreditoStatus);
        Assert.Equal(113.00m, o.CreditoPotencial);   // 112 (recalculado, menor) + 1 de IBS
    }

    [Fact]
    public void Conditioned_benefit_is_indicio_only_on_inbound_and_can_be_confirmed()
    {
        var cls = Classes(reducao: true, trat: "Redução de alíquota");
        Assert.Equal(AuditStatus.Indicio, Audit(Doc("3", Reg()), Calc(), cls: cls).Status);
        Assert.Equal(AuditStatus.Conforme, Audit(Doc("3", Reg()), Calc(), "OUTBOUND", cls).Status);
        Assert.Equal(AuditStatus.Conforme, Audit(Doc("3", Reg()), Calc(), cls: cls, confirmed: new HashSet<string> { "R021" }).Status);
    }

    [Fact]
    public void Mei_rules()
    {
        var comGrupo = Audit(Doc("4", Reg()), null);
        Assert.Equal(AuditStatus.Divergente, comGrupo.Status);
        Assert.Contains(comGrupo.Findings, f => f.Regra == "R050");
        Assert.Equal("SEM_CREDITO_MEI", comGrupo.CreditoStatus);

        var semGrupo = Audit(Doc("4", Reg() with { Cst = null, CClassTrib = null }), null, "OUTBOUND");
        Assert.Equal(AuditStatus.Conforme, semGrupo.Status);
    }

    [Fact]
    public void Simples_values_are_compared_with_the_simples_engine()
    {
        var item = Reg() with
        {
            VBc = null,
            IbsUf = null,
            IbsMun = null,
            VIbs = null,
            Cbs = null,
            Simples = new DeclaredSimples(0.50m, 5.00m, 1.20m, 12.50m),
            CodigoAtividadeSn = "01",
        };
        var sim = new SimplesCalculation([new SimplesItemResult(1, "CALCULADO", 1000m, 0.50m, 5.00m, 1.20m, 12.00m, [], null, null)],
            new SimplesTotals(1000m, 5m, 12m, 0), new EngineVersion("b", RfbEngine.Simples, "1.0.2", "V0073"));
        var o = DocumentAuditor.Audit(new AuditInput(Doc("1", item), "INBOUND", false, true, null, sim, Classes(), "APROPRIAVEL_POR_DESTAQUE", None));
        Assert.Equal(AuditStatus.Divergente, o.Status);
        Assert.Contains(o.Findings, f => f.Regra == "R013");
        Assert.Contains(o.Findings, f => f.Regra == "R011");
    }
}
