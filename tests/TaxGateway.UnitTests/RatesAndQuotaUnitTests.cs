using TaxGateway.Core.Params;
using TaxGateway.Infrastructure.Billing;
using TaxGateway.Infrastructure.Params;

namespace TaxGateway.UnitTests;

/// <summary>Regras puras: IS por produto e cobertura do catálogo (D-R17/D-R22), exceções (D-R20) e consumo (D-R12).</summary>
public sealed class RatesAndQuotaUnitTests
{
    private static RateRow Row(Tributo t, decimal aliq, string from, string? to = null, string? ente = null, string? produto = null, FormaAplicacao? forma = null, bool prov = false) =>
        new(Guid.NewGuid(), t, ente, forma, aliq, DateOnly.Parse(from, System.Globalization.CultureInfo.InvariantCulture),
            to is null ? null : DateOnly.Parse(to, System.Globalization.CultureInfo.InvariantCulture), "ato", "LEI_FEDERAL", prov, produto);

    private static readonly DateOnly D2027 = new(2027, 6, 1);

    [Fact]
    public void IS_resolve_pelo_cClassTrib_antes_do_NCM_e_pelo_prefixo_mais_longo()
    {
        var c = new RateCatalog([
            Row(Tributo.Is, 10, "2027-01-01", produto: "NCM:22"),
            Row(Tributo.Is, 25, "2027-01-01", produto: "NCM:2203"),
            Row(Tributo.Is, 40, "2027-01-01", produto: "CCLASS:000001"),
        ]);
        Assert.Equal(40, c.ResolveIs("22030000", "000001", D2027)!.Aliquota);
        Assert.Equal(25, c.ResolveIs("2203.00.00", "999999", D2027)!.Aliquota);
        Assert.Equal(10, c.ResolveIs("22089000", null, D2027)!.Aliquota);
        Assert.Null(c.ResolveIs("84713012", null, D2027));
        Assert.Null(c.ResolveIs("22030000", null, new DateOnly(2026, 12, 31)));
        Assert.Null(c.ResolveIs(null, null, D2027));
    }

    [Fact]
    public void Cobertura_segue_vigencias_encadeadas_e_para_no_buraco()
    {
        var c = new RateCatalog([
            Row(Tributo.Cbs, 0.9m, "2026-01-01", "2027-01-01"),
            Row(Tributo.Cbs, 11.2m, "2027-01-01", "2028-01-01"),
            Row(Tributo.IbsUf, 0.05m, "2027-01-01"),
        ]);
        Assert.Equal(new DateOnly(2028, 1, 1), c.CoveredUntil(Tributo.Cbs, new DateOnly(2026, 11, 27)));
        Assert.Equal(DateOnly.MaxValue, c.CoveredUntil(Tributo.IbsUf, D2027));
        Assert.Null(c.CoveredUntil(Tributo.IbsUf, new DateOnly(2026, 6, 1)));
        Assert.Null(c.CoveredUntil(Tributo.IbsMun, D2027));
        Assert.Equal(2, c.ActiveOn(D2027).Count());
        Assert.Equal(3, c.All.Count());
    }

    [Fact]
    public void Aliquota_propria_do_ente_aplica_a_forma()
    {
        var c = new RateCatalog([
            Row(Tributo.IbsMun, 1m, "2027-01-01"),
            Row(Tributo.IbsMun, 0.5m, "2027-01-01", ente: "3550308", forma: FormaAplicacao.Acrescimo),
            Row(Tributo.IbsMun, 2m, "2027-01-01", ente: "3304557", forma: FormaAplicacao.Decrescimo),
            Row(Tributo.IbsUf, 7m, "2027-01-01"),
            Row(Tributo.IbsUf, 9m, "2027-01-01", ente: "35", forma: FormaAplicacao.Substituicao, prov: true),
        ]);
        Assert.Equal(1.5m, c.Resolve(Tributo.IbsMun, 3550308, D2027)!.Aliquota);
        Assert.Equal(0m, c.Resolve(Tributo.IbsMun, 3304557, D2027)!.Aliquota);
        var uf = c.Resolve(Tributo.IbsUf, 3550308, D2027)!;
        Assert.Equal(9m, uf.Aliquota);
        Assert.True(uf.Provisoria);
        Assert.Equal(uf.Propria!.Id, uf.CatalogId);
        Assert.Equal(7m, c.Resolve(Tributo.IbsUf, 3304557, D2027)!.Aliquota);
        Assert.Throws<InvalidOperationException>(() => new RateCatalog([Row(Tributo.Cbs, 1, "2027-01-01", "2028-01-01"), Row(Tributo.Cbs, 2, "2027-06-01")]));
    }

    [Theory]
    [InlineData(null, null, null, "11222333000181", 3550308, true)]
    [InlineData("11222333000181", null, null, "11222333000181", 3550308, true)]
    [InlineData("99999999000191", null, null, "11222333000181", 3550308, false)]
    [InlineData(null, "35", null, "11222333000181", 3550308, true)]
    [InlineData(null, "33", null, "11222333000181", 3550308, false)]
    [InlineData(null, "3550308", null, "11222333000181", 3550308, true)]
    [InlineData(null, "3304557", null, "11222333000181", 3550308, false)]
    [InlineData(null, null, "NCM:8471", "11222333000181", 3550308, true)]
    [InlineData(null, null, "NCM:2203", "11222333000181", 3550308, false)]
    [InlineData(null, null, "CCLASS:000001", "11222333000181", 3550308, true)]
    [InlineData(null, null, "CCLASS:200001", "11222333000181", 3550308, false)]
    public void Excecao_casa_por_cnpj_ente_e_produto(string? cnpj, string? ente, string? produto, string docCnpj, int municipio, bool expected)
    {
        var ex = new TenantRateExemption(Guid.NewGuid(), cnpj, Tributo.Cbs, ente, produto, 5, new DateOnly(2027, 1, 1), new DateOnly(2028, 1, 1), "LIMINAR", "p");
        Assert.Equal(expected, ex.Matches(docCnpj, municipio, D2027, "84713012", "000001"));
    }

    [Fact]
    public void Excecao_respeita_a_vigencia()
    {
        var ex = new TenantRateExemption(Guid.NewGuid(), null, Tributo.Cbs, null, null, 5, new DateOnly(2027, 1, 1), new DateOnly(2027, 6, 1), "LIMINAR", "p");
        Assert.False(ex.Matches("1", 3550308, new DateOnly(2026, 12, 31), null, null));
        Assert.True(ex.Matches("1", 3550308, new DateOnly(2027, 5, 31), null, null));
        Assert.False(ex.Matches("1", 3550308, new DateOnly(2027, 6, 1), null, null));
        Assert.Equal(Tributo.IbsMun, RateExceptionService.ParseTributo("IBS_MUN"));
        Assert.Throws<ArgumentOutOfRangeException>(() => RateExceptionService.ParseTributo("ICMS"));
    }

    private static PlanLimits Plan(long docs, bool hard, long docMilli = 0) =>
        new("T", 600, 100, 10, IncludedDocs: docs, IncludedSimulations: 10, HardQuota: hard, OverageDocMillicents: docMilli, OverageSimMillicents: 0);

    [Fact]
    public void Consumo_restrito_suspenso_e_contrato_sem_limite()
    {
        var u = new QuotaUsage(0, 0);
        Assert.Equal("ERR_PAGAMENTO_PENDENTE", PlanQuota.Check(Plan(10, true), "RESTRICTED", 0, u, PlanQuota.DocMetric)!.Code);
        Assert.Equal(403, PlanQuota.Check(Plan(10, true), "SUSPENDED", 0, u, PlanQuota.DocMetric)!.Status);
        Assert.Null(PlanQuota.Check(Plan(0, false), "ACTIVE", 0, new QuotaUsage(1_000_000, 0), PlanQuota.DocMetric));
    }

    [Fact]
    public void Franquia_rigida_conta_o_envio_inteiro_e_a_reserva()
    {
        var hard = Plan(10, true);
        Assert.Null(PlanQuota.Check(hard, "ACTIVE", 0, new QuotaUsage(5, 0), PlanQuota.DocMetric, 5));
        var d = PlanQuota.Check(hard, "ACTIVE", 0, new QuotaUsage(5, 0, ReservedDocs: 1), PlanQuota.DocMetric, 5)!;
        Assert.Equal("ERR_FRANQUIA_ESGOTADA", d.Code);
        Assert.Contains("restam 4", d.Detail, StringComparison.Ordinal);
        Assert.Equal("ERR_FRANQUIA_ESGOTADA", PlanQuota.Check(hard, "ACTIVE", 0, new QuotaUsage(0, 10), PlanQuota.SimMetric)!.Code);
    }

    [Fact]
    public void Plano_pago_passa_da_franquia_so_ate_o_teto()
    {
        var paid = Plan(10, false, docMilli: 1000);   // 1 centavo por documento excedente
        Assert.Null(PlanQuota.Check(paid, "ACTIVE", 0, new QuotaUsage(9, 0), PlanQuota.DocMetric));
        Assert.Contains("sem teto", PlanQuota.Check(paid, "ACTIVE", 0, new QuotaUsage(10, 0), PlanQuota.DocMetric)!.Title, StringComparison.Ordinal);
        Assert.Null(PlanQuota.Check(paid, "ACTIVE", 3, new QuotaUsage(12, 0), PlanQuota.DocMetric));
        Assert.Equal("Teto de excedente atingido", PlanQuota.Check(paid, "ACTIVE", 3, new QuotaUsage(13, 0), PlanQuota.DocMetric)!.Title);
        Assert.NotNull(PlanQuota.Check(paid, "ACTIVE", 3, new QuotaUsage(10, 0), PlanQuota.DocMetric, 5));
        Assert.Null(PlanQuota.Check(Plan(10, false), "ACTIVE", 0, new QuotaUsage(50, 0), PlanQuota.DocMetric));   // sem preço de excedente
    }

    [Theory]
    [InlineData("São Paulo", "sao paulo")]
    [InlineData("  JOÃO PESSOA ", "joao pessoa")]
    [InlineData("50%_off", "50off")]
    public void Busca_de_municipio_normaliza(string input, string expected) =>
        Assert.Equal(expected, TaxGateway.Api.Endpoints.RatesEndpoints.Normalize(input));
}
