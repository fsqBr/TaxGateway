using TaxGateway.Api.Http;
using TaxGateway.Api.Simulation;
using TaxGateway.Core.Fiscal;
using TaxGateway.Core.Params;

namespace TaxGateway.UnitTests;

public sealed class CnpjTests
{
    [Theory]
    [InlineData("11.222.333/0001-81", "11222333000181")]   // numérico (máscara removida)
    [InlineData("12.ABC.345/01DE-35", "12ABC34501DE35")]   // exemplo alfanumérico da RFB
    [InlineData("12abc34501de35", "12ABC34501DE35")]       // minúsculas normalizadas
    public void Accepts_numeric_and_alphanumeric(string input, string expected)
    {
        Assert.True(Cnpj.TryNormalize(input, out var n));
        Assert.Equal(expected, n);
    }

    [Theory]
    [InlineData("11222333000182")]    // DV errado
    [InlineData("12ABC34501DE36")]    // DV alfanumérico errado
    [InlineData("12ABC34501DEA5")]    // DV com letra
    [InlineData("00000000000000")]
    [InlineData("1122233300018")]     // 13 caracteres
    [InlineData("11222333000181X")]
    [InlineData("11#22233300018")]
    [InlineData("")]
    [InlineData(null)]
    public void Rejects_invalid(string? input) => Assert.False(Cnpj.IsValid(input));

    [Fact]
    public void Letters_use_ascii_minus_48_and_no_letter_is_blocked()
    {
        // Qualquer letra é aceita na raiz (vedação de I, O, U, Q, F não confirmada, D-207): basta o DV bater.
        foreach (var letter in "IOUQF")
        {
            var baseChars = $"1{letter}ABC3450001";
            var dv = Dv(baseChars);
            Assert.True(Cnpj.IsValid(baseChars + dv), letter.ToString());
        }
    }

    private static string Dv(string twelve)
    {
        static int D(string s, int[] w)
        {
            var sum = 0;
            for (var i = 0; i < s.Length; i++)
            {
                sum += (s[i] - 48) * w[i];
            }

            var r = sum % 11;
            return r < 2 ? 0 : 11 - r;
        }

        var d1 = D(twelve, [5, 4, 3, 2, 9, 8, 7, 6, 5, 4, 3, 2]);
        var d2 = D(twelve + d1, [6, 5, 4, 3, 2, 9, 8, 7, 6, 5, 4, 3, 2]);
        return $"{d1}{d2}";
    }
}

public sealed class RateCatalogTests
{
    private static readonly DateOnly D2027 = new(2027, 1, 1);
    private static readonly DateOnly D2029 = new(2029, 1, 1);

    private static RateRow Row(Tributo t, decimal aliquota, DateOnly from, DateOnly? to, string? ente = null, FormaAplicacao? forma = null, bool prov = false) =>
        new(Guid.NewGuid(), t, ente, forma, aliquota, from, to, "ato", "LEI_FEDERAL", prov);

    [Fact]
    public void Resolves_reference_by_validity_with_exclusive_end()
    {
        var cat = new RateCatalog([
            Row(Tributo.Cbs, 0.9m, new(2026, 1, 1), D2027),
            Row(Tributo.Cbs, 11.2m, D2027, new(2028, 1, 1)),
        ]);
        Assert.Equal(0.9m, cat.Resolve(Tributo.Cbs, 3550308, new(2026, 12, 31))!.Aliquota);
        Assert.Equal(11.2m, cat.Resolve(Tributo.Cbs, 3550308, D2027)!.Aliquota);
        Assert.Null(cat.Resolve(Tributo.Cbs, 3550308, new(2028, 1, 1)));   // 2028 ainda sem lei
        Assert.Null(cat.Resolve(Tributo.Cbs, 3550308, new(2025, 6, 1)));
    }

    [Theory]
    [InlineData(FormaAplicacao.Substituicao, 18.0, 18.0)]
    [InlineData(FormaAplicacao.Acrescimo, 1.5, 19.0)]
    [InlineData(FormaAplicacao.Decrescimo, 2.0, 15.5)]
    [InlineData(FormaAplicacao.Decrescimo, 50.0, 0.0)]   // nunca negativa
    public void Applies_own_rate_of_state_over_reference(FormaAplicacao forma, double propria, double esperado)
    {
        var refRow = Row(Tributo.IbsUf, 17.5m, D2029, null);
        var sp = Row(Tributo.IbsUf, (decimal)propria, D2029, null, "35", forma);
        var cat = new RateCatalog([refRow, sp]);

        var r = cat.Resolve(Tributo.IbsUf, 3550308, new(2029, 5, 1))!;   // São Paulo/SP -> UF 35
        Assert.Equal((decimal)esperado, r.Aliquota);
        Assert.Equal(sp.Id, r.CatalogId);

        var rj = cat.Resolve(Tributo.IbsUf, 3304557, new(2029, 5, 1))!;  // Rio/RJ: sem linha própria
        Assert.Equal(17.5m, rj.Aliquota);
        Assert.Equal(refRow.Id, rj.CatalogId);
    }

    [Fact]
    public void Municipal_rate_uses_the_7_digit_municipality()
    {
        var cat = new RateCatalog([
            Row(Tributo.IbsMun, 2.0m, D2029, null),
            Row(Tributo.IbsMun, 2.5m, D2029, null, "4314902", FormaAplicacao.Substituicao),
        ]);
        Assert.Equal(2.5m, cat.Resolve(Tributo.IbsMun, 4314902, D2029)!.Aliquota);   // Porto Alegre
        Assert.Equal(2.0m, cat.Resolve(Tributo.IbsMun, 4304606, D2029)!.Aliquota);   // Canoas
    }

    [Fact]
    public void Provisional_flag_propagates()
    {
        var cat = new RateCatalog([Row(Tributo.Cbs, 11.2m, D2027, null, prov: true)]);
        Assert.True(cat.Resolve(Tributo.Cbs, 3550308, D2027)!.Provisoria);
    }

    [Fact]
    public void Overlapping_rows_are_rejected()
    {
        Assert.Throws<InvalidOperationException>(() => new RateCatalog([
            Row(Tributo.Cbs, 1m, D2027, null),
            Row(Tributo.Cbs, 2m, new(2027, 6, 1), null),
        ]));
    }
}

public sealed class SimulationHelpersTests
{
    [Theory]
    [InlineData("2027-01-01T02:30:00+00:00", 2026, 12)]   // 23:30 de 31/12 em Brasília: competência dezembro
    [InlineData("2027-01-01T03:00:00+00:00", 2027, 1)]
    [InlineData("2026-11-25T12:00:00+00:00", 2026, 11)]
    public void Billing_month_uses_sao_paulo_time(string utc, int year, int month) =>
        Assert.Equal(new DateOnly(year, month, 1), SimulationService.BillingMonth(DateTimeOffset.Parse(utc, System.Globalization.CultureInfo.InvariantCulture)));

    [Theory]
    [InlineData("#/itens/2/ncm", "itens[2].ncm")]
    [InlineData("#/itens/0/aliquotasNominais", "itens[0].aliquotas_nominais")]
    [InlineData("#/itens/-1", "itens")]
    [InlineData("#/itens/3/cClassTrib", "itens[3].c_class_trib")]
    [InlineData(null, null)]
    public void Engine_pointers_map_to_public_fields(string? jsonPointer, string? field) =>
        Assert.Equal(field, EngineFieldNames.Map(jsonPointer));
}
