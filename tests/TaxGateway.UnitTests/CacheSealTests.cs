using System.Text;

using Microsoft.Extensions.Options;

using TaxGateway.Infrastructure.Auth;

namespace TaxGateway.UnitTests;

/// <summary>Selo de integridade do cache no Valkey (D-P10).</summary>
public sealed class CacheSealTests
{
    private static CacheSeal New(byte fill = 7) =>
        new(Options.Create(new ApiKeyOptions { ApiKeyPepper = Convert.ToBase64String(Enumerable.Repeat(fill, 32).ToArray()) }));

    [Fact]
    public void Valor_selado_abre_so_na_mesma_chave_e_sem_alteracao()
    {
        var seal = New();
        var sealedValue = seal.Seal("auth:apikey:abc", "{\"t\":\"A\"}");
        Assert.Equal("{\"t\":\"A\"}", seal.OpenString("auth:apikey:abc", sealedValue));

        Assert.Null(seal.OpenString("auth:apikey:outra", sealedValue));            // movido para outra chave
        var tampered = (byte[])sealedValue.Clone();
        tampered[^1] ^= 1;
        Assert.Null(seal.OpenString("auth:apikey:abc", tampered));                 // conteúdo alterado
        Assert.Null(seal.OpenString("auth:apikey:abc", Encoding.UTF8.GetBytes("{\"t\":\"B\"}")));   // sem selo (forjado)
        Assert.Null(New(fill: 9).OpenString("auth:apikey:abc", sealedValue));      // outro pepper
        Assert.Null(seal.Open("k", []));
    }
}
