using System.Formats.Asn1;
using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Text;
using System.Xml;

namespace TaxGateway.TestKit;

/// <summary>
/// PKI só de teste: raiz + certificado e-CNPJ do emitente (SAN 2.16.76.1.3.3 e CN "NOME:CNPJ", como na ICP-Brasil).
/// As chaves ficam em tests/golden/pki (sem valor fora dos testes) para o corpus XML ser reproduzível.
/// </summary>
public sealed class TestPki
{
    public required X509Certificate2 Root { get; init; }
    public required X509Certificate2 Leaf { get; init; }
    public required string RootPemPath { get; init; }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, object> DirLocks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Carrega (ou cria) a PKI de teste da pasta. Exclusivo por pasta e com escrita atômica (D-Q11): várias classes de
    /// teste rodam em paralelo na mesma pasta temporária, e sem isso uma podia gravar o .crt de uma raiz e outra o .key
    /// de outra raiz ("certificate signature failure" intermitente no CI).
    /// </summary>
    public static TestPki LoadOrCreate(string dir, string cnpj = "11222333000181")
    {
        var full = Path.GetFullPath(dir);
        lock (DirLocks.GetOrAdd(full, _ => new object()))
        {
            return LoadOrCreateUnsafe(full, cnpj);
        }
    }

    private static TestPki LoadOrCreateUnsafe(string dir, string cnpj)
    {
        Directory.CreateDirectory(dir);
        var rootPem = Path.Combine(dir, "test-root.crt");
        var leafPfx = Path.Combine(dir, $"test-leaf-{cnpj}.pfx");
        var rootKey = Path.Combine(dir, "test-root.key");
        if (!File.Exists(rootPem) || !File.Exists(rootKey))
        {
            using var rsa = RSA.Create(2048);
            var req = new CertificateRequest("CN=TaxGateway TESTE Raiz (sem valor), O=TESTE, C=BR", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
            req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
            using var root = req.CreateSelfSigned(new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2035, 1, 1, 0, 0, 0, TimeSpan.Zero));
            WriteAtomic(rootKey, System.Text.Encoding.ASCII.GetBytes(rsa.ExportPkcs8PrivateKeyPem()));
            WriteAtomic(rootPem, System.Text.Encoding.ASCII.GetBytes(root.ExportCertificatePem()));
        }

        var rootCert = X509Certificate2.CreateFromPemFile(rootPem, rootKey);
        if (!File.Exists(leafPfx))
        {
            using var leaf = IssueLeaf(rootCert, cnpj, new DateTimeOffset(2025, 6, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2034, 1, 1, 0, 0, 0, TimeSpan.Zero));
            WriteAtomic(leafPfx, leaf.Export(X509ContentType.Pfx, "teste"));
        }

        return new TestPki
        {
            Root = rootCert,
            Leaf = X509CertificateLoader.LoadPkcs12FromFile(leafPfx, "teste", X509KeyStorageFlags.Exportable),
            RootPemPath = rootPem,
        };
    }

    /// <summary>Grava em arquivo temporário e renomeia: quem lê nunca vê arquivo pela metade.</summary>
    private static void WriteAtomic(string path, byte[] content)
    {
        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllBytes(tmp, content);
        File.Move(tmp, path, overwrite: true);
    }

    public static X509Certificate2 IssueLeaf(X509Certificate2 issuer, string cnpj, DateTimeOffset from, DateTimeOffset to, string? crlUrl = null)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest($"CN=EMPRESA TESTE LTDA:{cnpj}, OU=TESTE, O=TESTE, C=BR", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation, true));
        req.CertificateExtensions.Add(new X509Extension("2.5.29.17", CnpjSan(cnpj), false));
        if (crlUrl is not null)
        {
            req.CertificateExtensions.Add(CertificateRevocationListBuilder.BuildCrlDistributionPointExtension([crlUrl]));
        }
        var serial = RandomNumberGenerator.GetBytes(12);
        serial[0] &= 0x7F;
        using var cert = req.Create(issuer, from, to, serial);
        return cert.CopyWithPrivateKey(rsa);
    }

    /// <summary>SAN com otherName 2.16.76.1.3.3 (CNPJ), como nos e-CNPJ da ICP-Brasil.</summary>
    private static byte[] CnpjSan(string cnpj)
    {
        var w = new AsnWriter(AsnEncodingRules.DER);
        using (w.PushSequence())
        {
            using (w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0, true)))
            {
                w.WriteObjectIdentifier("2.16.76.1.3.3");
                using (w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0, true)))
                {
                    w.WriteOctetString(Encoding.ASCII.GetBytes(cnpj));
                }
            }
        }

        return w.Encode();
    }
}

public sealed record NfeItem(int Numero, string Ncm, decimal VProd, decimal VICMS, string ImpostoRtcXml, decimal? VFrete = null, decimal? VDesc = null,
    string? CAtivSn = null);

public sealed record NfeSpec(
    string EmitCnpj,
    int CodigoUf,
    int CMunFG,
    string Uf,
    DateTimeOffset DhEmi,
    string Modelo,
    int Numero,
    IReadOnlyList<NfeItem> Itens,
    string TotalRtcXml,            // IBSCBSTot (e ISTot) do emitente
    string? DestCnpj = null,
    string? DestCpf = null,
    string? GCompraGovXml = null,
    string Crt = "3");

/// <summary>NF-e/NFC-e 4.00 mínima válida no XSD PL_010h, com os grupos RTC fornecidos (do motor, nos casos golden).</summary>
public static class NfeBuilder
{
    public const string Ns = "http://www.portalfiscal.inf.br/nfe";

    public static string AccessKey(NfeSpec s)
    {
        var cNF = (s.Numero * 7919 % 99999999).ToString("D8", CultureInfo.InvariantCulture);
        var baseKey = $"{s.CodigoUf:D2}{s.DhEmi:yyMM}{s.EmitCnpj}{s.Modelo}001{s.Numero:D9}1{cNF}";
        var sum = 0;
        var weight = 2;
        for (var i = baseKey.Length - 1; i >= 0; i--)
        {
            sum += (baseKey[i] - 48) * weight;
            weight = weight == 9 ? 2 : weight + 1;
        }

        var rest = sum % 11;
        return baseKey + (rest < 2 ? 0 : 11 - rest).ToString(CultureInfo.InvariantCulture);
    }

    public static XmlDocument Build(NfeSpec s)
    {
        static string M(decimal v) => v.ToString("0.00", CultureInfo.InvariantCulture);
        var chave = AccessKey(s);
        var nfce = s.Modelo == "65";
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"<NFe xmlns=\"{Ns}\"><infNFe versao=\"4.00\" Id=\"NFe{chave}\">");
        sb.Append(CultureInfo.InvariantCulture, $"<ide><cUF>{s.CodigoUf}</cUF><cNF>{chave[35..43]}</cNF><natOp>VENDA</natOp><mod>{s.Modelo}</mod><serie>1</serie><nNF>{s.Numero}</nNF>");
        sb.Append(CultureInfo.InvariantCulture, $"<dhEmi>{s.DhEmi:yyyy-MM-ddTHH:mm:sszzz}</dhEmi><tpNF>1</tpNF><idDest>1</idDest><cMunFG>{s.CMunFG}</cMunFG>");
        sb.Append(CultureInfo.InvariantCulture, $"<tpImp>{(nfce ? 4 : 1)}</tpImp><tpEmis>1</tpEmis><cDV>{chave[43]}</cDV><tpAmb>2</tpAmb><finNFe>1</finNFe>");
        sb.Append(CultureInfo.InvariantCulture, $"<indFinal>{(nfce ? 1 : 0)}</indFinal><indPres>1</indPres><procEmi>0</procEmi><verProc>TaxGateway-teste</verProc>{s.GCompraGovXml}</ide>");
        sb.Append(CultureInfo.InvariantCulture, $"<emit><CNPJ>{s.EmitCnpj}</CNPJ><xNome>EMPRESA TESTE LTDA</xNome><enderEmit><xLgr>RUA TESTE</xLgr><nro>1</nro><xBairro>CENTRO</xBairro><cMun>{s.CMunFG}</cMun><xMun>MUNICIPIO</xMun><UF>{s.Uf}</UF><CEP>01001000</CEP><cPais>1058</cPais><xPais>BRASIL</xPais></enderEmit><IE>ISENTO</IE><CRT>{s.Crt}</CRT></emit>");
        if (s.DestCnpj is not null || s.DestCpf is not null)
        {
            var id = s.DestCnpj is not null ? $"<CNPJ>{s.DestCnpj}</CNPJ>" : $"<CPF>{s.DestCpf}</CPF>";
            sb.Append(CultureInfo.InvariantCulture, $"<dest>{id}<xNome>NF-E EMITIDA EM AMBIENTE DE HOMOLOGACAO - SEM VALOR FISCAL</xNome><enderDest><xLgr>RUA DEST</xLgr><nro>2</nro><xBairro>CENTRO</xBairro><cMun>{s.CMunFG}</cMun><xMun>MUNICIPIO</xMun><UF>{s.Uf}</UF><CEP>01001000</CEP><cPais>1058</cPais><xPais>BRASIL</xPais></enderDest><indIEDest>9</indIEDest></dest>");
        }

        decimal tProd = 0, tIcms = 0, tFrete = 0, tDesc = 0, tBcIcms = 0;
        foreach (var it in s.Itens)
        {
            var bcIcms = it.VProd + (it.VFrete ?? 0) - (it.VDesc ?? 0);
            tProd += it.VProd; tIcms += it.VICMS; tFrete += it.VFrete ?? 0; tDesc += it.VDesc ?? 0; tBcIcms += bcIcms;
            sb.Append(CultureInfo.InvariantCulture, $"<det nItem=\"{it.Numero}\"><prod><cProd>{it.Numero}</cProd><cEAN>SEM GTIN</cEAN><xProd>PRODUTO {it.Numero}</xProd><NCM>{it.Ncm}</NCM><CFOP>5102</CFOP>");
            sb.Append(CultureInfo.InvariantCulture, $"<uCom>UN</uCom><qCom>1.0000</qCom><vUnCom>{M(it.VProd)}</vUnCom><vProd>{M(it.VProd)}</vProd><cEANTrib>SEM GTIN</cEANTrib><uTrib>UN</uTrib><qTrib>1.0000</qTrib><vUnTrib>{M(it.VProd)}</vUnTrib>");
            if (it.VFrete is { } f) { sb.Append(CultureInfo.InvariantCulture, $"<vFrete>{M(f)}</vFrete>"); }
            if (it.VDesc is { } d) { sb.Append(CultureInfo.InvariantCulture, $"<vDesc>{M(d)}</vDesc>"); }
            sb.Append("<indTot>1</indTot>");
            if (it.CAtivSn is not null) { sb.Append(CultureInfo.InvariantCulture, $"<cAtivSN>{it.CAtivSn}</cAtivSN>"); }
            sb.Append("</prod><imposto>");
            var pIcms = it.VICMS == 0 || bcIcms == 0 ? 0m : Math.Round(it.VICMS * 100 / bcIcms, 2);
            sb.Append(CultureInfo.InvariantCulture, $"<ICMS><ICMS00><orig>0</orig><CST>00</CST><modBC>3</modBC><vBC>{M(bcIcms)}</vBC><pICMS>{pIcms.ToString("0.00", CultureInfo.InvariantCulture)}</pICMS><vICMS>{M(it.VICMS)}</vICMS></ICMS00></ICMS>");
            sb.Append("<PIS><PISNT><CST>07</CST></PISNT></PIS><COFINS><COFINSNT><CST>07</CST></COFINSNT></COFINS>");
            sb.Append(it.ImpostoRtcXml);
            sb.Append("</imposto></det>");
        }

        var vNF = tProd + tFrete - tDesc;
        sb.Append(CultureInfo.InvariantCulture, $"<total><ICMSTot><vBC>{M(tBcIcms)}</vBC><vICMS>{M(tIcms)}</vICMS><vICMSDeson>0.00</vICMSDeson><vFCP>0.00</vFCP><vBCST>0.00</vBCST><vST>0.00</vST><vFCPST>0.00</vFCPST><vFCPSTRet>0.00</vFCPSTRet>");
        sb.Append(CultureInfo.InvariantCulture, $"<vProd>{M(tProd)}</vProd><vFrete>{M(tFrete)}</vFrete><vSeg>0.00</vSeg><vDesc>{M(tDesc)}</vDesc><vII>0.00</vII><vIPI>0.00</vIPI><vIPIDevol>0.00</vIPIDevol><vPIS>0.00</vPIS><vCOFINS>0.00</vCOFINS><vOutro>0.00</vOutro><vNF>{M(vNF)}</vNF></ICMSTot>");
        sb.Append(s.TotalRtcXml);
        sb.Append("</total><transp><modFrete>9</modFrete></transp>");
        sb.Append(CultureInfo.InvariantCulture, $"<pag><detPag><tPag>01</tPag><vPag>{M(vNF)}</vPag></detPag></pag>");
        sb.Append("</infNFe></NFe>");

        var doc = new XmlDocument { PreserveWhitespace = true, XmlResolver = null };
        doc.LoadXml(sb.ToString());
        return doc;
    }

    /// <summary>Assina como os emissores da NF-e: RSA-SHA1, digest SHA1, enveloped + C14N, Reference ao infNFe.</summary>
    public static void Sign(XmlDocument doc, X509Certificate2 cert, string signatureMethod = SignedXml.XmlDsigRSASHA1Url,
        string digestMethod = SignedXml.XmlDsigSHA1Url)
    {
        ArgumentNullException.ThrowIfNull(doc);
        var ns = new XmlNamespaceManager(doc.NameTable);
        ns.AddNamespace("n", Ns);
        var nfe = (XmlElement)doc.SelectSingleNode("//n:NFe", ns)!;
        foreach (var old in nfe.ChildNodes.OfType<XmlElement>().Where(e => e.LocalName == "Signature").ToList())
        {
            nfe.RemoveChild(old);
        }

        var inf = (XmlElement)nfe.SelectSingleNode("n:infNFe", ns)!;
        using var key = cert.GetRSAPrivateKey()!;
        var signed = new IdSignedXml(doc, inf) { SigningKey = key };
        signed.SignedInfo!.SignatureMethod = signatureMethod;
        signed.SignedInfo.CanonicalizationMethod = SignedXml.XmlDsigC14NTransformUrl;
        var reference = new Reference("#" + inf.GetAttribute("Id")) { DigestMethod = digestMethod };
        reference.AddTransform(new XmlDsigEnvelopedSignatureTransform());
        reference.AddTransform(new XmlDsigC14NTransform());
        signed.AddReference(reference);
        var keyInfo = new KeyInfo();
        keyInfo.AddClause(new KeyInfoX509Data(cert));
        signed.KeyInfo = keyInfo;
        signed.ComputeSignature();
        nfe.AppendChild(doc.ImportNode(signed.GetXml(), true));
    }

    public static byte[] Bytes(XmlDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        using var ms = new MemoryStream();
        using (var w = XmlWriter.Create(ms, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = false }))
        {
            doc.Save(w);
        }

        return ms.ToArray();
    }

    private sealed class IdSignedXml(XmlDocument doc, XmlElement target) : SignedXml(doc)
    {
        public override XmlElement? GetIdElement(XmlDocument? document, string idValue) =>
            target.GetAttribute("Id") == idValue ? target : base.GetIdElement(document, idValue);
    }
}
