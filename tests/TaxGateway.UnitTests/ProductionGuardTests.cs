using Microsoft.Extensions.Configuration;

using TaxGateway.Infrastructure;

namespace TaxGateway.UnitTests;

/// <summary>D-P13: configuração de dev não passa em produção.</summary>
public sealed class ProductionGuardTests
{
    private static IConfiguration Config(Dictionary<string, string?> values) => new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    [Fact]
    public void Configuracao_de_dev_e_recusada_com_todos_os_motivos()
    {
        var problems = ProductionGuard.Problems(Config(new()
        {
            ["Auth:ApiKeyPepper"] = "0k2C9K9HFb5Vq32thS/EFrvm6BXIlmD7rMLfWfpVzFI=",
            ["Kms:LocalMasterKey"] = "x",
            ["Xml:ExtraTrustAnchorPemFiles:0"] = "/pki/test-root.crt",
            ["Webhooks:AllowInsecureForTests"] = "true",
            ["Identity:AllowEphemeralKeys"] = "true",
            ["Identity:AllowHttp"] = "true",
            ["Accounts:CookieSecure"] = "false",
            ["Admin:Operators:0:Name"] = "gate",
        }));
        Assert.Equal(10, problems.Count);   // inclui OpenBao ausente, issuer ausente e chave estática de operador ligada
    }

    [Fact]
    public void Configuracao_de_producao_correta_passa()
    {
        Assert.Empty(ProductionGuard.Problems(Config(new()
        {
            ["OpenBao:Address"] = "https://kms.interno:8200",
            ["Auth:ApiKeyPepper"] = Convert.ToBase64String(new byte[32]),
            ["Admin:AllowStaticKeys"] = "false",
            ["Identity:Issuer"] = "https://api.exemplo.com.br/",
        })));
    }
}
