using System.Numerics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Microsoft.Extensions.Logging.Abstractions;

using TaxGateway.Infrastructure.Xml;
using TaxGateway.TestKit;

namespace TaxGateway.SecurityTests.Xml;

/// <summary>Revogação por LCR (D-407): data de revogação × data de emissão, cache sem rede, LCR forjada.</summary>
public sealed class RevocationTests
{
    private const string CrlUrl = "http://crl.teste.invalid/raiz.crl";
    private static readonly TestPki Pki = TestPki.LoadOrCreate(Path.Combine(Path.GetTempPath(), "taxgateway-test-pki"));
    private static readonly DateTimeOffset Emissao = new(2027, 6, 15, 10, 0, 0, TimeSpan.FromHours(-3));

    private static CrlCache Cache() => new((_, _) => throw new HttpRequestException("sem rede no teste"), NullLogger<CrlCache>.Instance);

    private static X509Certificate2 Leaf() => TestPki.IssueLeaf(Pki.Root, "11222333000181",
        new DateTimeOffset(2025, 6, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2034, 1, 1, 0, 0, 0, TimeSpan.Zero), CrlUrl);

    private static byte[] Crl(X509Certificate2 issuer, params (X509Certificate2 Cert, DateTimeOffset When)[] revoked)
    {
        var b = new CertificateRevocationListBuilder();
        foreach (var (c, w) in revoked)
        {
            b.AddEntry(c, w);
        }

        return b.Build(issuer, BigInteger.One, DateTimeOffset.UtcNow.AddDays(7), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    }

    private static SignatureCheck Verify(X509Certificate2 leaf, CrlCache cache, bool strict = false)
    {
        var doc = NfeBuilder.Build(XmlSecurityTests.Spec());
        NfeBuilder.Sign(doc, leaf);
        var p = NfeMapper.Map(SecureXmlLoader.Load(NfeBuilder.Bytes(doc)));
        return new NfeSignatureVerifier(TrustAnchors.IcpBrasil([Pki.Root]), cache, strict).Verify(p.Nfe, p.InfNFe, p.Document.EmitenteCnpj, p.Document.DataEmissao);
    }

    [Fact]
    public void Distribution_points_are_read_from_the_certificate()
    {
        using var leaf = Leaf();
        Assert.Equal([new Uri(CrlUrl)], CrlCache.DistributionPoints(leaf));
    }

    [Fact]
    public void Certificate_revoked_before_emission_invalidates_the_signature()
    {
        using var leaf = Leaf();
        var cache = Cache();
        cache.Load(new Uri(CrlUrl), Crl(Pki.Root, (leaf, Emissao.AddDays(-10))), Pki.Root);
        var r = Verify(leaf, cache);
        Assert.Equal(SignatureFailure.CertificadoRevogado, r.Failure);
    }

    [Fact]
    public void Revocation_after_emission_keeps_the_document_valid()
    {
        using var leaf = Leaf();
        var cache = Cache();
        cache.Load(new Uri(CrlUrl), Crl(Pki.Root, (leaf, Emissao.AddDays(30))), Pki.Root);
        var r = Verify(leaf, cache);
        Assert.True(r.Valid, r.Detail);
        Assert.True(r.RevocationChecked);
    }

    [Fact]
    public void Missing_crl_is_a_warning_unless_policy_is_strict()
    {
        using var leaf = Leaf();
        var soft = Verify(leaf, Cache());
        Assert.True(soft.Valid);
        Assert.False(soft.RevocationChecked);
        Assert.Equal(SignatureFailure.CadeiaNaoConfiavel, Verify(leaf, Cache(), strict: true).Failure);
    }

    [Fact]
    public void Crl_signed_by_another_issuer_is_rejected()
    {
        using var leaf = Leaf();
        var rogue = TestPki.LoadOrCreate(Path.Combine(Path.GetTempPath(), $"taxgateway-rogue-crl-{Guid.NewGuid():N}"));
        Assert.Throws<CryptographicException>(() => Cache().Load(new Uri(CrlUrl), Crl(rogue.Root, (leaf, Emissao.AddDays(-1))), Pki.Root));
    }
}
