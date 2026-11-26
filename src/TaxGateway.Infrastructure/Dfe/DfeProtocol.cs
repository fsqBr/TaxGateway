using System.Globalization;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Text;
using System.Xml;

using Microsoft.Extensions.Options;

namespace TaxGateway.Infrastructure.Dfe;

public sealed class DfeOptions
{
    public const string Section = "Dfe";

    /// <summary>1 = produção, 2 = homologação. Padrão homologação: produção é decisão explícita de deploy.</summary>
    public int Ambiente { get; set; } = 2;

    /// <summary>NFeDistribuicaoDFe do Ambiente Nacional (NT 2014.002). Homologação por padrão.</summary>
    public Uri DistUrl { get; set; } = new("https://hom1.nfe.fazenda.gov.br/NFeDistribuicaoDFe/NFeDistribuicaoDFe.asmx");

    /// <summary>NFeRecepcaoEvento4 do Ambiente Nacional (manifestação do destinatário, cOrgao 91).</summary>
    public Uri EventoUrl { get; set; } = new("https://hom1.nfe.fazenda.gov.br/NFeRecepcaoEvento4/NFeRecepcaoEvento4.asmx");

    /// <summary>Liga o laço em segundo plano dos workers (os testes chamam RunOnce com relógio controlado).</summary>
    public bool WorkerEnabled { get; set; } = true;

    public int PollSeconds { get; set; } = 30;

    /// <summary>Só testes: aceita o certificado TLS do servidor com este thumbprint (SEFAZ simulada).</summary>
    public string? TestTrustedServerThumbprint { get; set; }

    public int TimeoutSeconds { get; set; } = 30;
}

public sealed record DfeDoc(long Nsu, string Schema, byte[] Xml);

public sealed record DistResult(string CStat, string XMotivo, long UltNsu, long MaxNsu, IReadOnlyList<DfeDoc> Docs);

public sealed record EventoResult(string CStat, string XMotivo);

/// <summary>Mensagens da NT 2014.002 (distDFeInt 1.01) e do evento 210210 (envEvento 1.00), em SOAP 1.2.</summary>
public static class DfeProtocol
{
    public const string NfeNs = "http://www.portalfiscal.inf.br/nfe";
    public const string DistWsdl = "http://www.portalfiscal.inf.br/nfe/wsdl/NFeDistribuicaoDFe";
    public const string EventoWsdl = "http://www.portalfiscal.inf.br/nfe/wsdl/NFeRecepcaoEvento4";
    public const string Soap12 = "http://www.w3.org/2003/05/soap-envelope";
    public const int CienciaDaOperacao = 210210;

    public static string DistNsuRequest(int ambiente, int cUfAutor, string cnpj, long ultNsu) =>
        $"""<soap12:Envelope xmlns:soap12="{Soap12}"><soap12:Body><nfeDistDFeInteresse xmlns="{DistWsdl}"><nfeDadosMsg><distDFeInt xmlns="{NfeNs}" versao="1.01"><tpAmb>{ambiente}</tpAmb><cUFAutor>{cUfAutor}</cUFAutor><CNPJ>{cnpj}</CNPJ><distNSU><ultNSU>{ultNsu.ToString("D15", CultureInfo.InvariantCulture)}</ultNSU></distNSU></distDFeInt></nfeDadosMsg></nfeDistDFeInteresse></soap12:Body></soap12:Envelope>""";

    public static DistResult ParseDist(string soap)
    {
        var doc = SecureLoad(soap);
        var ns = Ns(doc);
        var ret = doc.SelectSingleNode("//n:retDistDFeInt", ns) ?? throw new FormatException("Resposta sem retDistDFeInt.");
        string Text(string x) => ret.SelectSingleNode(x, ns)?.InnerText ?? "";
        static long L(string s) => long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : 0;
        var docs = new List<DfeDoc>();
        foreach (XmlElement z in ret.SelectNodes("n:loteDistDFeInt/n:docZip", ns)!)
        {
            docs.Add(new DfeDoc(L(z.GetAttribute("NSU")), z.GetAttribute("schema"), Gunzip(Convert.FromBase64String(z.InnerText))));
        }

        return new DistResult(Text("n:cStat"), Text("n:xMotivo"), L(Text("n:ultNSU")), L(Text("n:maxNSU")), docs);
    }

    /// <summary>Evento de Ciência da Operação (tpEvento 210210) assinado com o A1 do destinatário.</summary>
    public static string CienciaRequest(int ambiente, string cnpj, string chave, DateTimeOffset when, X509Certificate2 cert)
    {
        var id = $"ID{CienciaDaOperacao}{chave}01";
        var xml = $"""<envEvento xmlns="{NfeNs}" versao="1.00"><idLote>{when.ToUnixTimeSeconds()}</idLote><evento versao="1.00"><infEvento Id="{id}"><cOrgao>91</cOrgao><tpAmb>{ambiente}</tpAmb><CNPJ>{cnpj}</CNPJ><chNFe>{chave}</chNFe><dhEvento>{when.ToOffset(TimeSpan.FromHours(-3)).ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture)}</dhEvento><tpEvento>{CienciaDaOperacao}</tpEvento><nSeqEvento>1</nSeqEvento><verEvento>1.00</verEvento><detEvento versao="1.00"><descEvento>Ciencia da Operacao</descEvento></detEvento></infEvento></evento></envEvento>""";
        var doc = new XmlDocument { PreserveWhitespace = true, XmlResolver = null };
        doc.LoadXml(xml);
        var evento = (XmlElement)doc.GetElementsByTagName("evento", NfeNs)[0]!;
        evento.AppendChild(doc.ImportNode(Sign(doc, id, cert), true));
        return $"""<soap12:Envelope xmlns:soap12="{Soap12}"><soap12:Body><nfeDadosMsg xmlns="{EventoWsdl}">{doc.DocumentElement!.OuterXml}</nfeDadosMsg></soap12:Body></soap12:Envelope>""";
    }

    /// <summary>cStat do evento (135/136 registrado; 573 duplicidade) ou, sem retEvento, do lote.</summary>
    public static EventoResult ParseEvento(string soap)
    {
        var doc = SecureLoad(soap);
        var ns = Ns(doc);
        var inf = doc.SelectSingleNode("//n:retEvento/n:infEvento", ns) ?? doc.SelectSingleNode("//n:retEnvEvento", ns)
            ?? throw new FormatException("Resposta sem retEnvEvento.");
        return new EventoResult(inf.SelectSingleNode("n:cStat", ns)?.InnerText ?? "", inf.SelectSingleNode("n:xMotivo", ns)?.InnerText ?? "");
    }

    /// <summary>
    /// XMLDSig envelopado, C14N, RSA-SHA1/SHA1: é o que o leiaute da NF-e 4.00 e dos eventos exige (Manual de Orientação).
    /// </summary>
#pragma warning disable CA5350 // SHA-1 imposto pelo leiaute da SEFAZ, não é escolha nossa
    private static XmlElement Sign(XmlDocument doc, string id, X509Certificate2 cert)
    {
        var signed = new SignedXml(doc) { SigningKey = cert.GetRSAPrivateKey() ?? throw new InvalidOperationException("Certificado sem chave RSA.") };
        signed.SignedInfo!.SignatureMethod = SignedXml.XmlDsigRSASHA1Url;
        signed.SignedInfo.CanonicalizationMethod = SignedXml.XmlDsigC14NTransformUrl;
        var reference = new Reference("#" + id) { DigestMethod = SignedXml.XmlDsigSHA1Url };
        reference.AddTransform(new XmlDsigEnvelopedSignatureTransform());
        reference.AddTransform(new XmlDsigC14NTransform());
        signed.AddReference(reference);
        var keyInfo = new KeyInfo();
        keyInfo.AddClause(new KeyInfoX509Data(cert));
        signed.KeyInfo = keyInfo;
        signed.ComputeSignature();
        return signed.GetXml();
    }
#pragma warning restore CA5350

    public static byte[] Gunzip(byte[] data)
    {
        using var input = new GZipStream(new MemoryStream(data), CompressionMode.Decompress);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        int n;
        while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            output.Write(buffer, 0, n);
            if (output.Length > 5 * 1024 * 1024)
            {
                throw new FormatException("docZip descompactado acima de 5 MB.");   // mesma defesa do lote
            }
        }

        return output.ToArray();
    }

    public static byte[] Gzip(byte[] data)
    {
        using var output = new MemoryStream();
        using (var gz = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            gz.Write(data);
        }

        return output.ToArray();
    }

    /// <summary>Chave de acesso de um resNFe / procNFe / resEvento.</summary>
    public static string? AccessKey(byte[] xml)
    {
        var doc = SecureLoad(Encoding.UTF8.GetString(xml));
        var ns = Ns(doc);
        var ch = doc.SelectSingleNode("//n:chNFe", ns)?.InnerText;
        if (ch is not null)
        {
            return ch;
        }

        var id = (doc.SelectSingleNode("//n:infNFe", ns) as XmlElement)?.GetAttribute("Id");
        return id is { Length: 47 } ? id[3..] : null;
    }

    public static XmlDocument SecureLoad(string xml)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 10_000_000 };
        using var reader = XmlReader.Create(new StringReader(xml), settings);
        var doc = new XmlDocument { PreserveWhitespace = true, XmlResolver = null };
        doc.Load(reader);
        return doc;
    }

    private static XmlNamespaceManager Ns(XmlDocument doc)
    {
        var ns = new XmlNamespaceManager(doc.NameTable);
        ns.AddNamespace("n", NfeNs);
        return ns;
    }
}

public interface ISefazDfeClient
{
    Task<DistResult> DistNsuAsync(X509Certificate2 cert, int cUfAutor, string cnpj, long ultNsu, CancellationToken ct);

    Task<EventoResult> CienciaAsync(X509Certificate2 cert, string cnpj, string chave, DateTimeOffset eventTime, CancellationToken ct);
}

/// <summary>
/// Cliente mTLS do Ambiente Nacional: um handler por chamada com o A1 do tenant como certificado cliente (o certificado
/// só existe em memória durante a chamada). Validação do servidor pelo sistema, ou pinning só em teste.
/// </summary>
public sealed class SefazDfeClient(IOptions<DfeOptions> options) : ISefazDfeClient
{
    public async Task<DistResult> DistNsuAsync(X509Certificate2 cert, int cUfAutor, string cnpj, long ultNsu, CancellationToken ct)
    {
        var o = options.Value;
        var body = await PostAsync(cert, o.DistUrl, DfeProtocol.DistNsuRequest(o.Ambiente, cUfAutor, cnpj, ultNsu),
            $"{DfeProtocol.DistWsdl}/nfeDistDFeInteresse", ct).ConfigureAwait(false);
        return DfeProtocol.ParseDist(body);
    }

    public async Task<EventoResult> CienciaAsync(X509Certificate2 cert, string cnpj, string chave, DateTimeOffset eventTime, CancellationToken ct)
    {
        var o = options.Value;
        var body = await PostAsync(cert, o.EventoUrl, DfeProtocol.CienciaRequest(o.Ambiente, cnpj, chave, eventTime, cert),
            $"{DfeProtocol.EventoWsdl}/nfeRecepcaoEventoNF", ct).ConfigureAwait(false);
        return DfeProtocol.ParseEvento(body);
    }

    private async Task<string> PostAsync(X509Certificate2 cert, Uri url, string soap, string action, CancellationToken ct)
    {
        var o = options.Value;
        using var handler = new SocketsHttpHandler
        {
            SslOptions = new SslClientAuthenticationOptions { ClientCertificates = [cert] },
            PooledConnectionLifetime = TimeSpan.FromMinutes(1),
        };
        if (o.TestTrustedServerThumbprint is { Length: > 0 } pin)
        {
            handler.SslOptions.RemoteCertificateValidationCallback = (_, c, _, errors) =>
                errors == SslPolicyErrors.None || (c is not null && string.Equals(c.GetCertHashString(), pin, StringComparison.OrdinalIgnoreCase));
        }

        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(o.TimeoutSeconds) };
        using var content = new StringContent(soap, Encoding.UTF8);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse($"application/soap+xml; charset=utf-8; action=\"{action}\"");
        using var resp = await http.PostAsync(url, content, ct).ConfigureAwait(false);
        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"SEFAZ respondeu {(int)resp.StatusCode}.", null, resp.StatusCode);
        }

        return text;
    }
}
