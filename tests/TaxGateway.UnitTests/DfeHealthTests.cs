using TaxGateway.Infrastructure.Dfe;

namespace TaxGateway.UnitTests;

/// <summary>D-R37: situação da captura de DF-e em linguagem simples.</summary>
public sealed class DfeHealthTests
{
    private static readonly DateTimeOffset Now = new(2027, 3, 10, 15, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Cert = Now.AddDays(200);

    private static DfeHealthStatus Eval(bool configured = true, bool enabled = true, DateTimeOffset? cert = null, DateTimeOffset? last = null,
        string? cstat = "137", DateTimeOffset? last656 = null, DateTimeOffset? next = null, bool noCert = false) =>
        DfeHealth.Evaluate(configured, enabled, noCert ? null : cert ?? Cert, last, cstat, last656, next ?? Now.AddMinutes(30), Now);

    [Fact]
    public void Cada_situacao()
    {
        Assert.Equal("SEM_CERTIFICADO", Eval(noCert: true).Situacao);
        var vencido = Eval(cert: Now.AddDays(-1));
        Assert.Equal(("CERTIFICADO_VENCIDO", false), (vencido.Situacao, vencido.Ok));
        Assert.Contains("09/03/2027", vencido.Mensagem, StringComparison.Ordinal);
        Assert.Equal("DESLIGADO", Eval(enabled: false).Situacao);
        Assert.Equal("DESLIGADO", Eval(configured: false).Situacao);
        Assert.Equal(("AGUARDANDO", true), (Eval().Situacao, Eval().Ok));
        Assert.Equal("PARADO", Eval(last: Now.AddHours(-8), next: Now.AddHours(-7)).Situacao);
        Assert.Equal("COM_PROBLEMA", Eval(last: Now.AddMinutes(-10), cstat: "656").Situacao);
        Assert.Equal("COM_PROBLEMA", Eval(last: Now.AddMinutes(-10), cstat: "137", last656: Now.AddMinutes(-20)).Situacao);
        Assert.Contains("falhou", Eval(last: Now.AddMinutes(-10), cstat: "ERRO").Mensagem, StringComparison.Ordinal);
        Assert.Contains("retorno 593", Eval(last: Now.AddMinutes(-10), cstat: "593").Mensagem, StringComparison.Ordinal);
        var ok = Eval(last: Now.AddMinutes(-10), cstat: "138");
        Assert.Equal(("FUNCIONANDO", true, 200), (ok.Situacao, ok.Ok, ok.CertificadoVenceEmDias));
        Assert.DoesNotContain("Atenção", ok.Mensagem, StringComparison.Ordinal);
        Assert.Contains("vence em 10 dia(s)", Eval(last: Now.AddMinutes(-10), cert: Now.AddDays(10).AddHours(1)).Mensagem, StringComparison.Ordinal);
    }
}
