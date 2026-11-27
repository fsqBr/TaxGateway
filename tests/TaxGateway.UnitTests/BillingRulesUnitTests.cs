using TaxGateway.Api.Http;
using TaxGateway.Core.Params;
using TaxGateway.Infrastructure.Tenancy;

namespace TaxGateway.UnitTests;

/// <summary>Regras puras do teto de excedente (D-R08) e da máscara do e-mail do cadastro da Receita (D-R10).</summary>
public sealed class BillingRulesUnitTests
{
    private static PlanLimits Plan(long docs, long sims, long docMilli, long simMilli) =>
        new("T", 600, 100, 10, IncludedDocs: docs, IncludedSimulations: sims, OverageDocMillicents: docMilli, OverageSimMillicents: simMilli);

    [Theory]
    [InlineData(1000, 100, 0)]          // dentro da franquia: nada
    [InlineData(1001, 100, 9)]          // 1 documento a R$ 0,09
    [InlineData(1000, 101, 0.5)]        // 1 simulação a R$ 0,005
    [InlineData(1100, 300, 1000)]       // 100 docs (900) + 200 sims (100)
    [InlineData(0, 0, 0)]
    public void Overage_counts_only_what_exceeds_the_franchise(long docs, long sims, double expectedCents)
    {
        var cents = QuotaFilter.OverageCents(Plan(1000, 100, 9000, 500), docs, sims);
        Assert.Equal((decimal)expectedCents, cents);
    }

    [Fact]
    public void Plan_without_overage_price_never_accrues()
    {
        Assert.Equal(0m, QuotaFilter.OverageCents(Plan(10, 10, 0, 0), 1_000_000, 1_000_000));
    }

    [Theory]
    [InlineData("titular-11222333@empresa.test", "t**************3@empresa.test")]
    [InlineData("ab@x.test", "a***b@x.test")]
    [InlineData("a@x.test", "***@x.test")]
    [InlineData("semarroba", "***")]
    public void Registry_email_is_masked_before_showing(string email, string expected)
    {
        var masked = HttpCnpjRegistry.Mask(email);
        Assert.Equal(expected, masked);
        Assert.DoesNotContain("11222333", masked, StringComparison.Ordinal);
    }
}
