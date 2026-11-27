using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Text;
using System.Xml;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.Time.Testing;

using TaxGateway.Infrastructure.Dfe;
using TaxGateway.Infrastructure.Xml;

namespace TaxGateway.GoldenTests;

/// <summary>
/// SEFAZ simulada para a Fase 9 (D-907): NFeDistribuicaoDFe e NFeRecepcaoEvento4 do Ambiente Nacional em HTTPS com
/// certificado cliente OBRIGATÓRIO. Aplica as regras da NT 2014.002 com o relógio do fixture e responde 656 quando o
/// consumidor erra: consulta durante o bloqueio de 1 h (após 137 ou ultNSU = maxNSU, ou após um 656), NSU fora da
/// sequência devolvida, ou mais de 20 consultas na última hora. Não substitui a homologação real.
/// </summary>
public sealed class SefazSimulator : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly FakeTimeProvider _clock;
    private readonly ConcurrentDictionary<string, CnpjState> _state = new(StringComparer.Ordinal);

    public sealed class CnpjState
    {
        public List<(long Nsu, string Schema, byte[] Xml)> Docs { get; } = [];
        public Dictionary<string, byte[]> FullAfterCiencia { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Ciencias { get; } = new(StringComparer.Ordinal);
        public List<DateTimeOffset> Queries { get; } = [];
        public long LastUlt { get; set; }
        public DateTimeOffset BlockedUntil { get; set; } = DateTimeOffset.MinValue;
        public int Count656 { get; set; }
        public List<string> Reasons656 { get; } = [];
        public long NextNsu { get; set; } = 1;
    }

    public SefazSimulator(X509Certificate2 serverCert, FakeTimeProvider clock)
    {
        _clock = clock;
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        Port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, Port, l => l.UseHttps(new HttpsConnectionAdapterOptions
        {
            ServerCertificate = serverCert,
            ClientCertificateMode = ClientCertificateMode.RequireCertificate,
            ClientCertificateValidation = (_, _, _) => true,   // a cadeia ICP é da SEFAZ real; aqui conferimos o CNPJ
        })));
        _app = builder.Build();
        _app.MapPost("/NFeDistribuicaoDFe/NFeDistribuicaoDFe.asmx", DistAsync);
        // WSDL por GET, só com certificado cliente aceito (o "Testar conexão" usa isto; não conta como consulta).
        _app.MapGet("/NFeDistribuicaoDFe/NFeDistribuicaoDFe.asmx", (HttpContext ctx) =>
        {
            var cert = ctx.Connection.ClientCertificate;
            var cnpj = cert is null ? null : NfeSignatureVerifier.CnpjFromCertificate(cert);
            WsdlRequests++;
            return cnpj is null || RejectedRoots.Contains(cnpj[..8])
                ? Results.StatusCode(403)
                : Results.Text($"<definitions xmlns=\"http://schemas.xmlsoap.org/wsdl/\" targetNamespace=\"{DfeProtocol.DistWsdl}\"/>", "text/xml");
        });
        _app.MapPost("/NFeRecepcaoEvento4/NFeRecepcaoEvento4.asmx", EventoAsync);
        _app.StartAsync().GetAwaiter().GetResult();
    }

    public int Port { get; }

    /// <summary>Raízes de CNPJ cujo certificado a "SEFAZ" recusa no teste de conexão.</summary>
    public HashSet<string> RejectedRoots { get; } = new(StringComparer.Ordinal);

    public int WsdlRequests { get; private set; }
    public string DistUrl => $"https://localhost:{Port}/NFeDistribuicaoDFe/NFeDistribuicaoDFe.asmx";
    public string EventoUrl => $"https://localhost:{Port}/NFeRecepcaoEvento4/NFeRecepcaoEvento4.asmx";

    public CnpjState For(string cnpj) => _state.GetOrAdd(cnpj, _ => new CnpjState());

    public void Publish(string cnpj, string schema, byte[] xml)
    {
        var s = For(cnpj);
        lock (s)
        {
            s.Docs.Add((s.NextNsu++, schema, xml));
        }
    }

    /// <summary>Resumo agora; o procNFe completo só fica disponível depois da Ciência da Operação (como na SEFAZ).</summary>
    public void PublishResumoWithFull(string cnpj, string chave, byte[] resumo, byte[] full)
    {
        var s = For(cnpj);
        lock (s)
        {
            s.FullAfterCiencia[chave] = full;
        }

        Publish(cnpj, "resNFe_v1.01.xsd", resumo);
    }

    private async Task DistAsync(HttpContext ctx)
    {
        var doc = await LoadAsync(ctx);
        var ns = Ns(doc);
        var cnpj = doc.SelectSingleNode("//n:distDFeInt/n:CNPJ", ns)!.InnerText;
        var ult = long.Parse(doc.SelectSingleNode("//n:distNSU/n:ultNSU", ns)!.InnerText, CultureInfo.InvariantCulture);
        if (!CertMatches(ctx, cnpj))
        {
            await WriteAsync(ctx, Dist("215", "Rejeição: certificado não corresponde ao CNPJ", 0, 0, []));
            return;
        }

        var s = For(cnpj);
        string body;
        lock (s)
        {
            var now = _clock.GetUtcNow();
            s.Queries.Add(now);
            string? reason = null;
            if (now < s.BlockedUntil)
            {
                reason = $"consulta em {now:HH:mm} durante bloqueio até {s.BlockedUntil:HH:mm}";
            }
            else if (s.Queries.Count(q => now - q < TimeSpan.FromHours(1)) > 20)
            {
                reason = "mais de 20 consultas na última hora";
            }
            else if (ult != s.LastUlt)
            {
                reason = $"ultNSU {ult} fora da sequência (esperado {s.LastUlt})";
            }

            var max = s.Docs.Count == 0 ? 0 : s.Docs.Max(d => d.Nsu);
            if (reason is not null)
            {
                s.Count656++;
                s.Reasons656.Add(reason);
                s.BlockedUntil = now + TimeSpan.FromHours(1);   // nova tentativa no período reinicia a contagem
                body = Dist("656", "Rejeição: Consumo Indevido", s.LastUlt, max, []);
            }
            else
            {
                var page = s.Docs.Where(d => d.Nsu > ult).OrderBy(d => d.Nsu).Take(50).ToList();
                if (page.Count == 0)
                {
                    s.LastUlt = max;
                    s.BlockedUntil = now + TimeSpan.FromHours(1);
                    body = Dist("137", "Nenhum documento localizado", max, max, []);
                }
                else
                {
                    s.LastUlt = page[^1].Nsu;
                    if (s.LastUlt >= max)
                    {
                        s.BlockedUntil = now + TimeSpan.FromHours(1);   // ultNSU = maxNSU: esperar 1 h
                    }

                    body = Dist("138", "Documento localizado", s.LastUlt, max, page);
                }
            }
        }

        await WriteAsync(ctx, body);
    }

    private async Task EventoAsync(HttpContext ctx)
    {
        var doc = await LoadAsync(ctx);
        var ns = Ns(doc);
        var inf = (XmlElement)doc.SelectSingleNode("//n:evento/n:infEvento", ns)!;
        var cnpj = inf.SelectSingleNode("n:CNPJ", ns)!.InnerText;
        var chave = inf.SelectSingleNode("n:chNFe", ns)!.InnerText;
        string cStat, motivo;
        if (!CertMatches(ctx, cnpj) || !SignatureValid(doc) || inf.SelectSingleNode("n:tpEvento", ns)!.InnerText != "210210"
            || inf.SelectSingleNode("n:cOrgao", ns)!.InnerText != "91" || inf.GetAttribute("Id") != $"ID210210{chave}01")
        {
            (cStat, motivo) = ("297", "Rejeição: assinatura, órgão ou identificador do evento inválido");
        }
        else
        {
            var s = For(cnpj);
            lock (s)
            {
                if (!s.Ciencias.Add(chave))
                {
                    (cStat, motivo) = ("573", "Rejeição: Duplicidade de evento");
                }
                else
                {
                    (cStat, motivo) = ("135", "Evento registrado e vinculado a NF-e");
                    if (s.FullAfterCiencia.Remove(chave, out var full))
                    {
                        s.Docs.Add((s.NextNsu++, "procNFe_v4.00.xsd", full));
                    }
                }
            }
        }

        await WriteAsync(ctx, $"""<soap:Envelope xmlns:soap="{DfeProtocol.Soap12}"><soap:Body><nfeRecepcaoEventoNFResult xmlns="{DfeProtocol.EventoWsdl}"><retEnvEvento xmlns="{DfeProtocol.NfeNs}" versao="1.00"><cStat>128</cStat><xMotivo>Lote de evento processado</xMotivo><retEvento versao="1.00"><infEvento><cStat>{cStat}</cStat><xMotivo>{motivo}</xMotivo><chNFe>{chave}</chNFe></infEvento></retEvento></retEnvEvento></nfeRecepcaoEventoNFResult></soap:Body></soap:Envelope>""");
    }

    private static string Dist(string cStat, string motivo, long ult, long max, List<(long Nsu, string Schema, byte[] Xml)> docs)
    {
        var lote = docs.Count == 0 ? "" : "<loteDistDFeInt>" + string.Concat(docs.Select(d =>
            $"""<docZip NSU="{d.Nsu.ToString("D15", CultureInfo.InvariantCulture)}" schema="{d.Schema}">{Convert.ToBase64String(DfeProtocol.Gzip(d.Xml))}</docZip>""")) + "</loteDistDFeInt>";
        return $"""<soap:Envelope xmlns:soap="{DfeProtocol.Soap12}"><soap:Body><nfeDistDFeInteresseResponse xmlns="{DfeProtocol.DistWsdl}"><nfeDistDFeInteresseResult><retDistDFeInt xmlns="{DfeProtocol.NfeNs}" versao="1.01"><tpAmb>2</tpAmb><verAplic>SIM</verAplic><cStat>{cStat}</cStat><xMotivo>{motivo}</xMotivo><dhResp>2026-12-10T12:00:00-03:00</dhResp><ultNSU>{ult.ToString("D15", CultureInfo.InvariantCulture)}</ultNSU><maxNSU>{max.ToString("D15", CultureInfo.InvariantCulture)}</maxNSU>{lote}</retDistDFeInt></nfeDistDFeInteresseResult></nfeDistDFeInteresseResponse></soap:Body></soap:Envelope>""";
    }

    private static bool CertMatches(HttpContext ctx, string cnpj)
    {
        var cert = ctx.Connection.ClientCertificate;
        var certCnpj = cert is null ? null : NfeSignatureVerifier.CnpjFromCertificate(cert);
        return certCnpj is not null && certCnpj[..8] == cnpj[..8];
    }

    private static bool SignatureValid(XmlDocument soap)
    {
        // Como a SEFAZ: a assinatura é conferida no envEvento extraído do envelope SOAP (o C14N inclusivo herdaria
        // os namespaces do envelope).
        var env = soap.GetElementsByTagName("envEvento", DfeProtocol.NfeNs)[0]!;
        var doc = DfeProtocol.SecureLoad(env.OuterXml);
        var sig = (XmlElement?)doc.GetElementsByTagName("Signature", SignedXml.XmlDsigNamespaceUrl)[0];
        if (sig is null)
        {
            return false;
        }

        var signed = new SignedXml((XmlElement)sig.ParentNode!);
        signed.LoadXml(sig);
        return signed.CheckSignature();
    }

    private static async Task<XmlDocument> LoadAsync(HttpContext ctx)
    {
        using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8);
        return DfeProtocol.SecureLoad(await reader.ReadToEndAsync());
    }

    private static Task WriteAsync(HttpContext ctx, string body)
    {
        ctx.Response.ContentType = "application/soap+xml; charset=utf-8";
        return ctx.Response.WriteAsync(body);
    }

    private static XmlNamespaceManager Ns(XmlDocument doc)
    {
        var ns = new XmlNamespaceManager(doc.NameTable);
        ns.AddNamespace("n", DfeProtocol.NfeNs);
        return ns;
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();
}
