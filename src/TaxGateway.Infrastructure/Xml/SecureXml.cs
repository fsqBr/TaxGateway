using System.Collections.Concurrent;
using System.Formats.Asn1;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Xml;
using System.Xml.Schema;

namespace TaxGateway.Infrastructure.Xml;

/// <summary>XML recusado antes de qualquer processamento (DTD, entidade externa, malformado, grande demais).</summary>
public sealed class XmlRejectedException(string reason, Exception? inner = null) : Exception(reason, inner);

/// <summary>
/// Leitura endurecida (SDD §4.4; D-303): DTD proibido (bloqueia XXE e "billion laughs"), sem resolver de URL,
/// limite de caracteres, whitespace preservado (exigência do XMLDSig).
/// </summary>
public static class SecureXmlLoader
{
    public const int DefaultMaxCharacters = 5 * 1024 * 1024;

    public static XmlDocument Load(byte[] xml, int maxCharacters = DefaultMaxCharacters)
    {
        ArgumentNullException.ThrowIfNull(xml);
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = maxCharacters,
            MaxCharactersFromEntities = 1024,
            IgnoreProcessingInstructions = true,
            CloseInput = true,
        };
        var doc = new XmlDocument { PreserveWhitespace = true, XmlResolver = null };
        try
        {
            using var reader = XmlReader.Create(new MemoryStream(xml, writable: false), settings);
            doc.Load(reader);
        }
        catch (XmlException ex)
        {
            throw new XmlRejectedException(ex.Message.Contains("DTD", StringComparison.OrdinalIgnoreCase)
                ? "DTD não é permitido (proteção contra XXE)." : $"XML malformado: {ex.Message}", ex);
        }

        return doc;
    }
}

/// <summary>
/// Validação XSD da NF-e/NFC-e 4.00 com o pacote oficial embutido (D-301). O <see cref="XmlSchemaSet"/> é
/// compilado uma vez; os includes são resolvidos só dentro dos recursos embutidos (nada de rede/disco).
/// </summary>
public sealed class NfeSchemaValidator
{
    public const string NfeNamespace = "http://www.portalfiscal.inf.br/nfe";
    private readonly XmlSchemaSet _schemas;

    public NfeSchemaValidator()
    {
        var resolver = new EmbeddedXsdResolver();
        _schemas = new XmlSchemaSet { XmlResolver = resolver };
        using (var root = EmbeddedXsdResolver.Open("nfe_v4.00.xsd"))
        using (var reader = XmlReader.Create(root, new XmlReaderSettings { DtdProcessing = DtdProcessing.Parse, XmlResolver = resolver }, "embedded:///nfe_v4.00.xsd"))
        {
            _schemas.Add(NfeNamespace, reader);
        }

        _schemas.Compile();
    }

    /// <summary>Valida o elemento NFe. Devolve até 20 erros (vazio = válido).</summary>
    public IReadOnlyList<string> Validate(XmlElement nfe)
    {
        ArgumentNullException.ThrowIfNull(nfe);
        var errors = new List<string>();
        var settings = new XmlReaderSettings { ValidationType = ValidationType.Schema, Schemas = _schemas, XmlResolver = null, DtdProcessing = DtdProcessing.Prohibit };
        settings.ValidationEventHandler += (_, e) =>
        {
            if (errors.Count < 20)
            {
                errors.Add(e.Message);
            }
        };
        using var reader = XmlReader.Create(new XmlNodeReader(nfe), settings);
        try
        {
            while (reader.Read())
            {
            }
        }
        catch (XmlException ex)
        {
            errors.Add(ex.Message);
        }

        return errors;
    }

    private sealed class EmbeddedXsdResolver : XmlResolver
    {
        private static readonly Assembly Asm = typeof(EmbeddedXsdResolver).Assembly;

        public static Stream Open(string file) =>
            Asm.GetManifestResourceStream($"xsd/nfe/{file}") ?? throw new FileNotFoundException($"XSD embutido ausente: {file}");

        public override object? GetEntity(Uri absoluteUri, string? role, Type? ofObjectToReturn) =>
            Open(Path.GetFileName(absoluteUri.LocalPath));

        public override Uri ResolveUri(Uri? baseUri, string? relativeUri) =>
            new($"embedded:///{Path.GetFileName(relativeUri ?? "")}");
    }
}

public enum SignatureFailure
{
    None,
    Ausente,
    MultiplasAssinaturas,
    ReferenciaInvalida,
    Wrapping,
    TransformNaoPermitido,
    AlgoritmoNaoPermitido,
    AssinaturaInvalida,
    CertificadoAusente,
    CadeiaNaoConfiavel,
    CertificadoNaoCorrespondeEmitente,
    CertificadoRevogado,
}

public sealed record SignatureCheck(SignatureFailure Failure, string Detail, string? CertificateThumbprint, bool RevocationChecked = false)
{
    public bool Valid => Failure == SignatureFailure.None;
}

/// <summary>Âncoras de confiança: raízes ICP-Brasil embutidas (D-302) e, em testes, âncoras extras.</summary>
public sealed class TrustAnchors
{
    public TrustAnchors(IEnumerable<X509Certificate2> roots, IEnumerable<X509Certificate2> intermediates)
    {
        Roots = new X509Certificate2Collection(roots.ToArray());
        Intermediates = new X509Certificate2Collection(intermediates.ToArray());
    }

    public X509Certificate2Collection Roots { get; }
    public X509Certificate2Collection Intermediates { get; }

    public static TrustAnchors IcpBrasil(IEnumerable<X509Certificate2>? extraRoots = null)
    {
        var asm = typeof(TrustAnchors).Assembly;
        var roots = new List<X509Certificate2>();
        foreach (var name in asm.GetManifestResourceNames().Where(n => n.StartsWith("icp/roots/", StringComparison.Ordinal)))
        {
            using var s = asm.GetManifestResourceStream(name)!;
            roots.Add(LoadCert(ReadAll(s)));
        }

        var inter = new List<X509Certificate2>();
        using (var zs = asm.GetManifestResourceStream("icp/intermediarias.zip"))
        {
            if (zs is not null)
            {
                using var zip = new ZipArchive(zs, ZipArchiveMode.Read);
                foreach (var e in zip.Entries.Where(e => e.Length is > 0 and < 64 * 1024))
                {
                    using var es = e.Open();
                    try
                    {
                        inter.Add(LoadCert(ReadAll(es)));
                    }
                    catch (CryptographicException)
                    {
                        // entrada que não é certificado: ignorada
                    }
                }
            }
        }

        roots.AddRange(extraRoots ?? []);
        return new TrustAnchors(roots, inter);
    }

    private static X509Certificate2 LoadCert(byte[] data) =>
        data.AsSpan().StartsWith("-----BEGIN"u8)
            ? X509Certificate2.CreateFromPem(System.Text.Encoding.ASCII.GetString(data))
            : X509CertificateLoader.LoadCertificate(data);

    private static byte[] ReadAll(Stream s)
    {
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }
}

/// <summary>
/// XMLDSig da NF-e com defesa contra signature wrapping (SDD §4.4; D-304):
/// exatamente uma assinatura (filha de NFe), exatamente uma Reference, cujo URI aponta para o Id do infNFe que
/// será auditado, e esse Id é único no documento; a resolução do Id é forçada para esse mesmo nó; transforms e
/// algoritmos numa lista fechada; cadeia até uma âncora ICP-Brasil na data de emissão; certificado do emitente
/// (raiz do CNPJ igual à do emitente).
/// </summary>
public sealed class NfeSignatureVerifier(TrustAnchors anchors, CrlCache? crls = null, bool strictRevocation = false)
{
    private const string DsigNs = SignedXml.XmlDsigNamespaceUrl;
    private static readonly HashSet<string> AllowedTransforms = new(StringComparer.Ordinal)
    {
        SignedXml.XmlDsigEnvelopedSignatureTransformUrl,
        SignedXml.XmlDsigC14NTransformUrl,
        SignedXml.XmlDsigExcC14NTransformUrl,
    };
    private static readonly HashSet<string> AllowedSignatureMethods = new(StringComparer.Ordinal)
    {
        SignedXml.XmlDsigRSASHA1Url, SignedXml.XmlDsigRSASHA256Url,
    };
    private static readonly HashSet<string> AllowedDigests = new(StringComparer.Ordinal)
    {
        SignedXml.XmlDsigSHA1Url, SignedXml.XmlDsigSHA256Url,
    };

    public SignatureCheck Verify(XmlElement nfe, XmlElement infNFe, string emitenteCnpj, DateTimeOffset dataEmissao)
    {
        ArgumentNullException.ThrowIfNull(nfe);
        ArgumentNullException.ThrowIfNull(infNFe);
        var all = nfe.OwnerDocument.GetElementsByTagName("Signature", DsigNs);
        if (all.Count == 0)
        {
            return Fail(SignatureFailure.Ausente, "Documento sem assinatura.");
        }

        var direct = nfe.ChildNodes.OfType<XmlElement>().Where(e => e.LocalName == "Signature" && e.NamespaceURI == DsigNs).ToList();
        if (all.Count != 1 || direct.Count != 1)
        {
            return Fail(SignatureFailure.MultiplasAssinaturas, "Esperada exatamente uma assinatura, filha de NFe.");
        }

        var sig = direct[0];
        var ns = new XmlNamespaceManager(nfe.OwnerDocument.NameTable);
        ns.AddNamespace("ds", DsigNs);
        var refs = sig.SelectNodes("ds:SignedInfo/ds:Reference", ns)!;
        var id = infNFe.GetAttribute("Id");
        if (refs.Count != 1 || id.Length == 0 || ((XmlElement)refs[0]!).GetAttribute("URI") != "#" + id)
        {
            return Fail(SignatureFailure.ReferenciaInvalida, "A assinatura precisa de uma única Reference apontando para o Id do infNFe.");
        }

        // Anti-wrapping: o Id assinado precisa existir uma única vez e ser o infNFe que será auditado.
        var withId = nfe.OwnerDocument.SelectNodes($"//*[@Id='{id.Replace("'", "", StringComparison.Ordinal)}' or @ID='{id.Replace("'", "", StringComparison.Ordinal)}' or @id='{id.Replace("'", "", StringComparison.Ordinal)}']")!;
        if (withId.Count != 1 || !ReferenceEquals(withId[0], infNFe))
        {
            return Fail(SignatureFailure.Wrapping, "O Id referenciado não é único ou não é o infNFe processado.");
        }

        foreach (XmlElement t in sig.SelectNodes("ds:SignedInfo/ds:Reference/ds:Transforms/ds:Transform", ns)!)
        {
            if (!AllowedTransforms.Contains(t.GetAttribute("Algorithm")))
            {
                return Fail(SignatureFailure.TransformNaoPermitido, $"Transform não permitido: {t.GetAttribute("Algorithm")}.");
            }
        }

        var sm = (sig.SelectSingleNode("ds:SignedInfo/ds:SignatureMethod", ns) as XmlElement)?.GetAttribute("Algorithm") ?? "";
        var dm = (sig.SelectSingleNode("ds:SignedInfo/ds:Reference/ds:DigestMethod", ns) as XmlElement)?.GetAttribute("Algorithm") ?? "";
        if (!AllowedSignatureMethods.Contains(sm) || !AllowedDigests.Contains(dm))
        {
            return Fail(SignatureFailure.AlgoritmoNaoPermitido, $"Algoritmo não permitido ({sm}, {dm}).");
        }

        var certNodes = sig.SelectNodes("ds:KeyInfo/ds:X509Data/ds:X509Certificate", ns)!;
        if (certNodes.Count == 0)
        {
            return Fail(SignatureFailure.CertificadoAusente, "Assinatura sem certificado X.509.");
        }

        X509Certificate2 cert;
        var embedded = new List<X509Certificate2>();
        try
        {
            cert = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(certNodes[0]!.InnerText.Trim()));
            for (var i = 1; i < certNodes.Count; i++)
            {
                embedded.Add(X509CertificateLoader.LoadCertificate(Convert.FromBase64String(certNodes[i]!.InnerText.Trim())));
            }
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return Fail(SignatureFailure.CertificadoAusente, "Certificado X.509 ilegível.");
        }

        using (cert)
        {
            var signed = new PinnedSignedXml(nfe.OwnerDocument, infNFe);
            signed.LoadXml(sig);
            using var key = cert.GetRSAPublicKey();
            bool ok;
            try
            {
                ok = key is not null && signed.CheckSignature(key);
            }
            catch (CryptographicException)
            {
                ok = false;
            }

            if (!ok)
            {
                return Fail(SignatureFailure.AssinaturaInvalida, "Assinatura não confere com o conteúdo (documento alterado após assinar).", cert.Thumbprint);
            }

            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.AddRange(anchors.Roots);
            chain.ChainPolicy.ExtraStore.AddRange(anchors.Intermediates);
            chain.ChainPolicy.ExtraStore.AddRange(embedded.ToArray());
            // Revogação: sem consulta online no caminho da requisição (pendência: cache de LCR, D-304).
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.DisableCertificateDownloads = true;
            chain.ChainPolicy.VerificationTime = dataEmissao.UtcDateTime;
            chain.ChainPolicy.VerificationTimeIgnored = false;
            if (!chain.Build(cert))
            {
                var why = string.Join("; ", chain.ChainStatus.Select(s => s.StatusInformation.Trim()).Distinct());
                return Fail(SignatureFailure.CadeiaNaoConfiavel, $"Cadeia não confiável na data de emissão: {why}", cert.Thumbprint);
            }

            // Revogação (D-407): só cache, nunca rede no caminho da requisição.
            var revocationChecked = false;
            if (crls is not null)
            {
                revocationChecked = true;
                for (var i = 0; i < chain.ChainElements.Count - 1; i++)
                {
                    var r = crls.Check(chain.ChainElements[i].Certificate, chain.ChainElements[i + 1].Certificate, dataEmissao);
                    if (r.Status == RevocationStatus.Revoked)
                    {
                        return Fail(SignatureFailure.CertificadoRevogado, r.Detail, cert.Thumbprint);
                    }

                    if (r.Status == RevocationStatus.Unknown)
                    {
                        if (strictRevocation)
                        {
                            return Fail(SignatureFailure.CadeiaNaoConfiavel, $"Revogação não verificável: {r.Detail}", cert.Thumbprint);
                        }

                        revocationChecked = false;
                    }
                }
            }

            var certCnpj = CnpjFromCertificate(cert);
            if (certCnpj is null || emitenteCnpj.Length < 8 || !certCnpj.StartsWith(emitenteCnpj[..8], StringComparison.Ordinal))
            {
                return Fail(SignatureFailure.CertificadoNaoCorrespondeEmitente,
                    $"O certificado ({certCnpj ?? "sem CNPJ"}) não pertence à raiz do CNPJ emitente.", cert.Thumbprint);
            }

            foreach (var e in embedded)
            {
                e.Dispose();
            }

            return new SignatureCheck(SignatureFailure.None, "Assinatura válida.", cert.Thumbprint, revocationChecked);
        }
    }

    private static SignatureCheck Fail(SignatureFailure f, string detail, string? thumb = null) => new(f, detail, thumb);

    /// <summary>
    /// CNPJ do e-CNPJ ICP-Brasil: SAN otherName 2.16.76.1.3.3; se ausente, sufixo ":CNPJ" do CN (padrão das ACs).
    /// </summary>
    public static string? CnpjFromCertificate(X509Certificate2 cert)
    {
        ArgumentNullException.ThrowIfNull(cert);
        var san = cert.Extensions["2.5.29.17"];
        if (san is not null)
        {
            try
            {
                var reader = new AsnReader(san.RawData, AsnEncodingRules.DER).ReadSequence();
                while (reader.HasData)
                {
                    var tag = reader.PeekTag();
                    if (tag.TagClass == TagClass.ContextSpecific && tag.TagValue == 0)
                    {
                        var other = reader.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0, true));
                        var oid = other.ReadObjectIdentifier();
                        var explicitValue = other.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0, true));
                        var raw = explicitValue.ReadEncodedValue().Span;
                        if (oid == "2.16.76.1.3.3")
                        {
                            var digits = new string(System.Text.Encoding.ASCII.GetString(raw).Where(c => char.IsAsciiLetterOrDigit(c)).ToArray());
                            if (digits.Length >= 14)
                            {
                                return digits[^14..].ToUpperInvariant();
                            }
                        }
                    }
                    else
                    {
                        reader.ReadEncodedValue();
                    }
                }
            }
            catch (AsnContentException)
            {
                // SAN fora do padrão: tenta o CN
            }
        }

        var cn = cert.GetNameInfo(X509NameType.SimpleName, false);
        var idx = cn.LastIndexOf(':');
        return idx >= 0 && cn.Length - idx - 1 == 14 ? cn[(idx + 1)..].ToUpperInvariant() : null;
    }

    /// <summary>SignedXml que só resolve o Id para o infNFe processado (nunca para um nó injetado).</summary>
    private sealed class PinnedSignedXml(XmlDocument doc, XmlElement target) : SignedXml(doc)
    {
        public override XmlElement? GetIdElement(XmlDocument? document, string idValue) =>
            target.GetAttribute("Id") == idValue ? target : null;
    }
}

/// <summary>Cache de compilação: um validador por processo (XmlSchemaSet compilado é thread-safe para leitura).</summary>
public static class XmlSecurityCache
{
    private static readonly ConcurrentDictionary<string, NfeSchemaValidator> Validators = new();

    public static NfeSchemaValidator Nfe => Validators.GetOrAdd("nfe", _ => new NfeSchemaValidator());
}
