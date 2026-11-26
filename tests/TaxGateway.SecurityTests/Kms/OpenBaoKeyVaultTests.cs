using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;

using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

using Microsoft.Extensions.Options;

using TaxGateway.Infrastructure.Storage;

namespace TaxGateway.SecurityTests.Kms;

/// <summary>OpenBao real (modo dev) com o engine Transit habilitado.</summary>
public sealed class OpenBaoFixture : IAsyncLifetime
{
    public const string Token = "tg-test-root";
    private IContainer? _bao;

    public string Address => $"http://{_bao!.Hostname}:{_bao.GetMappedPublicPort(8200)}";

    public async Task InitializeAsync()
    {
        _bao = new ContainerBuilder("openbao/openbao:2.7.2")
            .WithEnvironment("BAO_DEV_ROOT_TOKEN_ID", Token)
            .WithCommand("server", "-dev", "-dev-listen-address=0.0.0.0:8200")
            .WithPortBinding(8200, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(8200).ForPath("/v1/sys/health")))
            .Build();
        await _bao.StartAsync();
        using var http = new HttpClient();
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{Address}/v1/sys/mounts/transit") { Content = JsonContent.Create(new { type = "transit" }) };
        req.Headers.Add("X-Vault-Token", Token);
        (await http.SendAsync(req)).EnsureSuccessStatusCode();
    }

    public async Task DisposeAsync()
    {
        if (_bao is not null)
        {
            await _bao.DisposeAsync();
        }
    }
}

/// <summary>KMS de produção (D-406): envelope, isolamento entre tenants, dado associado, HMAC no cofre, crypto-shredding.</summary>
public sealed class OpenBaoKeyVaultTests(OpenBaoFixture bao) : IClassFixture<OpenBaoFixture>
{
    private OpenBaoKeyVault Vault() => new(new HttpClient(), Options.Create(new OpenBaoOptions { Address = bao.Address, Token = OpenBaoFixture.Token }));

    [Fact]
    public async Task Wraps_and_unwraps_dek_bound_to_tenant_and_object_path()
    {
        var v = Vault();
        var dek = RandomNumberGenerator.GetBytes(32);
        var aad = "t/a/xml/2027/01/x.xml.enc"u8.ToArray();
        var wrapped = await v.WrapAsync("tenant/a", dek, aad, CancellationToken.None);
        Assert.StartsWith("vault:v1:", Encoding.ASCII.GetString(wrapped), StringComparison.Ordinal);
        Assert.Equal(dek, await v.UnwrapAsync("tenant/a", wrapped, aad, CancellationToken.None));

        // Outro tenant ou outro caminho: não abre.
        await Assert.ThrowsAsync<CryptographicException>(() => v.UnwrapAsync("tenant/b", wrapped, aad, CancellationToken.None));
        await Assert.ThrowsAsync<CryptographicException>(() => v.UnwrapAsync("tenant/a", wrapped, "t/b/xml/x"u8.ToArray(), CancellationToken.None));
    }

    [Fact]
    public async Task Cpf_hmac_is_computed_inside_the_vault_and_differs_per_tenant()
    {
        var v = Vault();
        var a1 = await v.HmacAsync("tenant/a", "52998224725"u8.ToArray(), CancellationToken.None);
        var a2 = await v.HmacAsync("tenant/a", "52998224725"u8.ToArray(), CancellationToken.None);
        var b = await v.HmacAsync("tenant/b", "52998224725"u8.ToArray(), CancellationToken.None);
        Assert.Equal(32, a1.Length);
        Assert.Equal(a1, a2);
        Assert.NotEqual(a1, b);
    }

    [Fact]
    public async Task Crypto_shredding_makes_previous_envelopes_unreadable()
    {
        var v = Vault();
        var aad = "t/z/xml/k"u8.ToArray();
        var wrapped = await v.WrapAsync("tenant/z", RandomNumberGenerator.GetBytes(32), aad, CancellationToken.None);
        await v.ShredAsync("tenant/z", CancellationToken.None);
        await Assert.ThrowsAsync<CryptographicException>(() => v.UnwrapAsync("tenant/z", wrapped, aad, CancellationToken.None));
    }
}
