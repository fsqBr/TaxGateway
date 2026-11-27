using Microsoft.AspNetCore.Http;

using TaxGateway.Api.Endpoints;

namespace TaxGateway.UnitTests;

/// <summary>Filtros da consulta de documentos (D-J01, D-R06): entrada inválida vira 400, nunca exceção.</summary>
public sealed class DocumentFilterTests
{
    private static readonly DateOnly Today = new(2026, 11, 26);

    private static (DocumentEndpoints.Filter? Filter, IResult? Error) Parse(string query)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.QueryString = new QueryString(query.Length == 0 ? "" : "?" + query);
        return DocumentEndpoints.ParseFilter(ctx.Request, Today);
    }

    [Fact]
    public void Defaults_to_the_last_90_days()
    {
        var (f, err) = Parse("");
        Assert.Null(err);
        Assert.Equal((Today.AddDays(-90), Today), (f!.De, f.Ate));
    }

    [Theory]
    [InlineData("de=9999-12-30&ate=9999-12-31")]
    [InlineData("ate=0001-01-01")]
    [InlineData("de=2024-12-31")]
    [InlineData("de=2026-02-30")]
    [InlineData("de=2026-03-01&ate=2026-01-01")]
    [InlineData("de=2025-01-01&ate=2026-11-26")]
    [InlineData("status=CONFORME,XYZ")]
    [InlineData("cnpj=١١٢٢٢٣٣٣٠٠٠١٨١")]
    [InlineData("chave=123")]
    [InlineData("direcao=lateral")]
    [InlineData("lote=nao-e-uuid")]
    public void Invalid_input_is_a_validation_error(string query)
    {
        var (f, err) = Parse(query);
        Assert.Null(f);
        Assert.NotNull(err);
    }

    [Fact]
    public void Accepts_the_documented_values()
    {
        var (f, err) = Parse("de=2026-01-01&ate=2026-01-31&status=divergente,INDICIO&direcao=entrada&cnpj=11222333000181");
        Assert.Null(err);
        Assert.Equal(["DIVERGENTE", "INDICIO"], f!.Status);
        Assert.Equal("INBOUND", f.Direcao);
    }

    [Theory]
    [InlineData(null, true, 50)]
    [InlineData("10", true, 10)]
    [InlineData("999", true, 200)]
    [InlineData("0", false, 50)]
    [InlineData("abc", false, 50)]
    [InlineData("-1", false, 50)]
    public void Limit_parsing(string? raw, bool ok, int expected)
    {
        Assert.Equal(ok, DocumentEndpoints.TryLimit(raw, 50, 200, out var v));
        if (ok)
        {
            Assert.Equal(expected, v);
        }
    }

    [Theory]
    [InlineData("=HYPERLINK(\"x\")", "'=HYPERLINK(\"\"x\"\")")]
    [InlineData("+1", "'+1")]
    [InlineData("a;b", "\"a;b\"")]
    [InlineData("normal", "normal")]
    public void Csv_cells_are_escaped_and_formula_safe(string input, string expected)
    {
        var got = DocumentEndpoints.Csv(input);
        Assert.Equal(expected.Replace("'=HYPERLINK(\"\"x\"\")", "\"'=HYPERLINK(\"\"x\"\")\"", StringComparison.Ordinal), got);
    }
}
