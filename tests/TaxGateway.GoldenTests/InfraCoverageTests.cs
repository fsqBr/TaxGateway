using System.Collections.Concurrent;
using System.Formats.Asn1;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml;

using Amazon.S3;
using Amazon.S3.Model;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

using Npgsql;

using StackExchange.Redis;

using TaxGateway.Core.Tenancy;
using TaxGateway.Identity;
using TaxGateway.Infrastructure;
using TaxGateway.Infrastructure.Auth;
using TaxGateway.Infrastructure.Batches;
using TaxGateway.Infrastructure.Dfe;
using TaxGateway.Infrastructure.Persistence;
using TaxGateway.Infrastructure.Storage;
using TaxGateway.Infrastructure.Webhooks;
using TaxGateway.Infrastructure.Xml;
using TaxGateway.TestKit;
using TaxGateway.Workers;

using Xunit.Abstractions;

namespace TaxGateway.GoldenTests;

/// <summary>Logger que guarda as mensagens (para conferir os avisos dos laços de fundo).</summary>
internal sealed class InfraLog<T> : ILogger<T>
{
    public ConcurrentQueue<(LogLevel Level, string Message, Exception? Error)> Entries { get; } = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Entries.Enqueue((logLevel, formatter(state, exception), exception));

    public bool Has(string fragment) => Entries.Any(e => e.Message.Contains(fragment, StringComparison.Ordinal));
}

internal static class InfraWait
{
    public static async Task Until(Func<bool> condition, int timeoutMs = 30000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(sw.ElapsedMilliseconds < timeoutMs, "Condição não atingida no prazo.");
            await Task.Delay(50);
        }
    }

    public static async Task Until(Func<Task<bool>> condition, int timeoutMs = 30000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!await condition())
        {
            Assert.True(sw.ElapsedMilliseconds < timeoutMs, "Condição não atingida no prazo.");
            await Task.Delay(100);
        }
    }

    /// <summary>Valkey numa porta sem ninguém: falha na hora (sem fila de comandos).</summary>
    public static IConnectionMultiplexer DeadValkey()
    {
        int port;
        using (var l = new TcpListener(IPAddress.Loopback, 0))
        {
            l.Start();
            port = ((IPEndPoint)l.LocalEndpoint).Port;
        }

        var o = new ConfigurationOptions { AbortOnConnectFail = false, ConnectTimeout = 300, ConnectRetry = 0, BacklogPolicy = BacklogPolicy.FailFast };
        o.EndPoints.Add(IPAddress.Loopback, port);
        return ConnectionMultiplexer.Connect(o);
    }

    public static int FreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }
}

/// <summary>XMLDSig da NF-e (D-304): algoritmos fora da lista, certificado ausente/ilegível, cadeia com intermediária embutida e CNPJ do certificado.</summary>
public sealed class NfeSignatureVerifierEdgeTests
{
    private const string Cnpj = "11222333000181";
    private static readonly TestPki Pki = TestPki.LoadOrCreate(Path.Combine(Path.GetTempPath(), "taxgateway-test-pki"));
    private static readonly TrustAnchors Anchors = TrustAnchors.IcpBrasil([Pki.Root]);
    private const string DsNs = SignedXml.XmlDsigNamespaceUrl;

    private const string Rtc = """
        <IBSCBS><CST>000</CST><cClassTrib>000001</cClassTrib><gIBSCBS><vBC>1000.00</vBC><gIBSUF><pIBSUF>0.05</pIBSUF><vIBSUF>0.50</vIBSUF></gIBSUF><gIBSMun><pIBSMun>0.05</pIBSMun><vIBSMun>0.50</vIBSMun></gIBSMun><vIBS>1.00</vIBS><gCBS><pCBS>11.20</pCBS><vCBS>112.00</vCBS></gCBS></gIBSCBS></IBSCBS>
        """;
    private const string Tot = """
        <IBSCBSTot><vBCIBSCBS>1000.00</vBCIBSCBS><gIBS><gIBSUF><vDif>0.00</vDif><vDevTrib>0.00</vDevTrib><vIBSUF>0.50</vIBSUF></gIBSUF><gIBSMun><vDif>0.00</vDif><vDevTrib>0.00</vDevTrib><vIBSMun>0.50</vIBSMun></gIBSMun><vIBS>1.00</vIBS><vCredPres>0.00</vCredPres><vCredPresCondSus>0.00</vCredPresCondSus></gIBS><gCBS><vDif>0.00</vDif><vDevTrib>0.00</vDevTrib><vCBS>112.00</vCBS><vCredPres>0.00</vCredPres><vCredPresCondSus>0.00</vCredPresCondSus></gCBS></IBSCBSTot>
        """;

    private static NfeSpec Spec() => new(Cnpj, 35, 3550308, "SP", new DateTimeOffset(2027, 6, 15, 10, 0, 0, TimeSpan.FromHours(-3)),
        "55", 4591, [new NfeItem(1, "84713012", 1000m, 0m, Rtc)], Tot, DestCnpj: "11444777000161");

    private static XmlDocument Signed(X509Certificate2 cert, string? sm = null, string? dm = null)
    {
        var doc = NfeBuilder.Build(Spec());
        if (sm is null)
        {
            NfeBuilder.Sign(doc, cert);
        }
        else
        {
            NfeBuilder.Sign(doc, cert, sm, dm!);
        }

        return SecureXmlLoader.Load(NfeBuilder.Bytes(doc));
    }

    private static SignatureCheck Verify(XmlDocument doc)
    {
        var p = NfeMapper.Map(doc);
        return new NfeSignatureVerifier(Anchors).Verify(p.Nfe, p.InfNFe, p.Document.EmitenteCnpj, p.Document.DataEmissao);
    }

    private static XmlNamespaceManager Ns(XmlDocument doc)
    {
        var ns = new XmlNamespaceManager(doc.NameTable);
        ns.AddNamespace("ds", DsNs);
        return ns;
    }

    [Theory]
    [InlineData("http://www.w3.org/2001/04/xmldsig-more#rsa-sha512", "http://www.w3.org/2001/04/xmlenc#sha512")]
    [InlineData(SignedXml.XmlDsigRSASHA256Url, "http://www.w3.org/2001/04/xmlenc#sha512")]
    public void Algoritmo_fora_da_lista_fechada_e_recusado(string signatureMethod, string digestMethod)
    {
        var r = Verify(Signed(Pki.Leaf, signatureMethod, digestMethod));
        Assert.Equal(SignatureFailure.AlgoritmoNaoPermitido, r.Failure);
        Assert.Contains(digestMethod, r.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Rsa_sha256_com_digest_sha256_e_aceito()
    {
        var r = Verify(Signed(Pki.Leaf, SignedXml.XmlDsigRSASHA256Url, SignedXml.XmlDsigSHA256Url));
        Assert.True(r.Valid, r.Detail);
        Assert.Equal(Pki.Leaf.Thumbprint, r.CertificateThumbprint);
        Assert.False(r.RevocationChecked);   // sem cache de LCR configurado
    }

    [Fact]
    public void Certificado_ausente_ou_ilegivel_no_KeyInfo()
    {
        var doc = Signed(Pki.Leaf);
        var node = doc.SelectSingleNode("//ds:X509Certificate", Ns(doc))!;
        node.ParentNode!.RemoveChild(node);
        var missing = Verify(doc);
        Assert.Equal(SignatureFailure.CertificadoAusente, missing.Failure);
        Assert.Contains("sem certificado", missing.Detail, StringComparison.Ordinal);

        foreach (var bad in new[] { "@@não é base64@@", Convert.ToBase64String("não é um certificado DER"u8.ToArray()) })
        {
            var d = Signed(Pki.Leaf);
            d.SelectSingleNode("//ds:X509Certificate", Ns(d))!.InnerText = bad;
            var r = Verify(d);
            Assert.Equal(SignatureFailure.CertificadoAusente, r.Failure);
            Assert.Contains("ilegível", r.Detail, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Cadeia_com_intermediaria_embutida_no_KeyInfo_e_valida()
    {
        using var inter = Intermediate();
        using var leaf = TestPki.IssueLeaf(inter, Cnpj, new DateTimeOffset(2025, 6, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2034, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var withoutInter = Verify(Signed(leaf));
        Assert.Equal(SignatureFailure.CadeiaNaoConfiavel, withoutInter.Failure);   // a intermediária não é âncora

        var doc = Signed(leaf);
        var data = doc.SelectSingleNode("//ds:X509Data", Ns(doc))!;
        var extra = doc.CreateElement("X509Certificate", DsNs);
        extra.InnerText = Convert.ToBase64String(inter.RawData);
        data.AppendChild(extra);
        var ok = Verify(doc);
        Assert.True(ok.Valid, ok.Detail);
        Assert.Equal(leaf.Thumbprint, ok.CertificateThumbprint);

        // Intermediária ilegível depois do certificado do signatário também é recusada.
        var broken = Signed(leaf);
        var bdata = broken.SelectSingleNode("//ds:X509Data", Ns(broken))!;
        var junk = broken.CreateElement("X509Certificate", DsNs);
        junk.InnerText = "AAAA";
        bdata.AppendChild(junk);
        Assert.Equal(SignatureFailure.CertificadoAusente, Verify(broken).Failure);
    }

    [Theory]
    [InlineData("assinatura-truncada")]
    [InlineData("assinatura-vazia")]
    [InlineData("c14n-trocada")]
    [InlineData("c14n-desconhecida")]
    public void Assinatura_adulterada_ou_canonicalizacao_trocada_e_invalida(string caso)
    {
        var d = Signed(Pki.Leaf);
        var ns = Ns(d);
        switch (caso)
        {
            case "assinatura-truncada":
                d.SelectSingleNode("//ds:SignatureValue", ns)!.InnerText = "AAAA";
                break;
            case "assinatura-vazia":
                d.SelectSingleNode("//ds:SignatureValue", ns)!.InnerText = "";
                break;
            case "c14n-trocada":
                ((XmlElement)d.SelectSingleNode("//ds:CanonicalizationMethod", ns)!).SetAttribute("Algorithm", SignedXml.XmlDsigExcC14NTransformUrl);
                break;
            default:
                ((XmlElement)d.SelectSingleNode("//ds:CanonicalizationMethod", ns)!).SetAttribute("Algorithm", "urn:taxgateway:desconhecida");
                break;
        }

        var r = Verify(d);
        Assert.Equal(SignatureFailure.AssinaturaInvalida, r.Failure);
        Assert.Equal(Pki.Leaf.Thumbprint, r.CertificateThumbprint);
    }

    [Fact]
    public void Chave_RSA_de_1024_bits_da_ICP_ainda_e_aceita()
    {
        using var small = SmallKeyLeaf();
        var r = Verify(Signed(small));
        Assert.True(r.Valid, r.Detail);
    }

    [Fact]
    public void Cnpj_do_certificado_pelo_SAN_ou_pelo_CN()
    {
        // SAN com dNSName, otherName de outro OID e CNPJ curto: cai no CN "NOME:CNPJ".
        var w = new AsnWriter(AsnEncodingRules.DER);
        using (w.PushSequence())
        {
            w.WriteCharacterString(UniversalTagNumber.IA5String, "empresa.test", new Asn1Tag(TagClass.ContextSpecific, 2));
            OtherName(w, "2.16.76.1.3.4", "12345678901");
            OtherName(w, "2.16.76.1.3.3", "123");
        }

        using (var viaCn = SelfSigned("CN=EMPRESA TESTE:11222333000181", new X509Extension("2.5.29.17", w.Encode(), false)))
        {
            Assert.Equal("11222333000181", NfeSignatureVerifier.CnpjFromCertificate(viaCn));
        }

        using (var broken = SelfSigned("CN=SEM CNPJ", new X509Extension("2.5.29.17", [0x30, 0x03, 0xA0, 0x05], false)))
        {
            Assert.Null(NfeSignatureVerifier.CnpjFromCertificate(broken));
        }

        using (var shortCn = SelfSigned("CN=EMPRESA:123", null))
        {
            Assert.Null(NfeSignatureVerifier.CnpjFromCertificate(shortCn));
        }

        var alnum = new AsnWriter(AsnEncodingRules.DER);
        using (alnum.PushSequence())
        {
            OtherName(alnum, "2.16.76.1.3.3", "12.ABC.345/0001-90");   // CNPJ alfanumérico (2026): letras mantidas, pontuação removida
        }

        using var cnpjAlnum = SelfSigned("CN=X", new X509Extension("2.5.29.17", alnum.Encode(), false));
        Assert.Equal("12ABC345000190", NfeSignatureVerifier.CnpjFromCertificate(cnpjAlnum));
        Assert.Throws<ArgumentNullException>(() => NfeSignatureVerifier.CnpjFromCertificate(null!));
    }

    private static void OtherName(AsnWriter w, string oid, string value)
    {
        using (w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0, true)))
        {
            w.WriteObjectIdentifier(oid);
            using (w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0, true)))
            {
                w.WriteOctetString(Encoding.ASCII.GetBytes(value));
            }
        }
    }

    private static X509Certificate2 SelfSigned(string subject, X509Extension? san)
    {
        using var key = RSA.Create(2048);
        var req = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        if (san is not null)
        {
            req.CertificateExtensions.Add(san);
        }

        return req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    private static X509Certificate2 Intermediate()
    {
        using var key = RSA.Create(2048);
        var req = new CertificateRequest("CN=TaxGateway TESTE Intermediaria, O=TESTE, C=BR", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
        var serial = RandomNumberGenerator.GetBytes(10);
        serial[0] &= 0x7F;
        using var cert = req.Create(Pki.Root, new DateTimeOffset(2025, 2, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2034, 12, 1, 0, 0, 0, TimeSpan.Zero), serial);
        return cert.CopyWithPrivateKey(key);
    }

    private static X509Certificate2 SmallKeyLeaf()
    {
        using var key = RSA.Create(1024);
        var req = new CertificateRequest($"CN=EMPRESA PEQUENA:{Cnpj}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var serial = RandomNumberGenerator.GetBytes(10);
        serial[0] &= 0x7F;
        using var cert = req.Create(Pki.Root, new DateTimeOffset(2025, 6, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2034, 1, 1, 0, 0, 0, TimeSpan.Zero), serial);
        return cert.CopyWithPrivateKey(key);
    }
}

/// <summary>
/// Infraestrutura e workers contra o ambiente golden (Postgres com RLS, Valkey, S3, SEFAZ simulada): lotes com falhas e
/// retomada, DF-e com SEFAZ instável, webhooks, cache de autenticação, revogações e o host dos workers.
/// </summary>
[Collection(GoldenCollection.Name)]
public sealed class InfraCoverageTests(GoldenFixture env, ITestOutputHelper output)
{
    private sealed class FakeAuditor(Func<byte[], CancellationToken, Task<BatchDocResult>> audit) : IBatchDocumentAuditor
    {
        public Task<BatchDocResult> AuditAsync(TenantId tenant, byte[] xml, string direction, CancellationToken ct) => audit(xml, ct);
    }

    private sealed class FakeSefaz(Func<long, DistResult> dist, Func<string, EventoResult>? ciencia = null) : ISefazDfeClient
    {
        public int DistCalls;
        public ConcurrentQueue<string> Ciencias { get; } = new();

        public Task<DistResult> DistNsuAsync(X509Certificate2 cert, int cUfAutor, string cnpj, long ultNsu, CancellationToken ct)
        {
            Interlocked.Increment(ref DistCalls);
            return Task.FromResult(dist(ultNsu));
        }

        public Task<SefazPing> PingAsync(X509Certificate2 cert, CancellationToken ct) => Task.FromResult(new SefazPing(true, "OK", "ok"));

        public Task<EventoResult> CienciaAsync(X509Certificate2 cert, string cnpj, string chave, DateTimeOffset eventTime, CancellationToken ct)
        {
            Ciencias.Enqueue(chave);
            return Task.FromResult((ciencia ?? (_ => new EventoResult("135", "Evento registrado")))(chave));
        }
    }

    private static byte[] Zip(bool withDirectory, params (string Name, string Content)[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            if (withDirectory)
            {
                zip.CreateEntry("pasta/");
            }

            foreach (var (name, content) in entries)
            {
                using var s = zip.CreateEntry(name).Open();
                s.Write(Encoding.UTF8.GetBytes(content));
            }
        }

        return ms.ToArray();
    }

    private async Task<long> ScalarAsync(string sql, params object[] args)
    {
        await using var su = new NpgsqlConnection(env.SuperuserConnection);
        await su.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, su);
        foreach (var a in args)
        {
            cmd.Parameters.AddWithValue(a);
        }

        return Convert.ToInt64(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<string?> TextAsync(string sql, params object[] args)
    {
        await using var su = new NpgsqlConnection(env.SuperuserConnection);
        await su.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, su);
        foreach (var a in args)
        {
            cmd.Parameters.AddWithValue(a);
        }

        return await cmd.ExecuteScalarAsync() as string;
    }

    private async Task ExecAsync(string sql, params object[] args)
    {
        await using var su = new NpgsqlConnection(env.SuperuserConnection);
        await su.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, su);
        foreach (var a in args)
        {
            cmd.Parameters.AddWithValue(a);
        }

        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<string> KekAsync(TenantId tenant) =>
        (await env.WorkerServices.GetRequiredService<TenantSettingsService>().GetAsync(tenant, CancellationToken.None)).KmsKeyRef;

    /// <summary>Para o processador de lotes do host durante o teste (só este teste reserva chunks) e o religa no fim.</summary>
    private async Task WithHostedBatchProcessorStoppedAsync(Func<Task> body)
    {
        await InfraWait.Until(async () => await ScalarAsync("SELECT count(*) FROM batch_chunks WHERE status = 'QUEUED'") == 0, 60000);
        var hosted = env.WorkerServices.GetRequiredService<BatchProcessor>();
        await hosted.StopAsync(CancellationToken.None);
        try
        {
            await body();
        }
        finally
        {
            await hosted.StartAsync(CancellationToken.None);
        }
    }

    // ---------------------------------------------------------------- lotes

    [Fact]
    public async Task Lote_com_documento_instavel_chunk_sumido_e_cerca_conclui_com_relatorio_completo()
    {
        var (tid, _) = await env.CreateTenantWithIdAsync("Lote infra A", GoldenFixture.NewCnpj());
        var tenant = new TenantId(tid);
        var kek = await KekAsync(tenant);
        var vault = env.WorkerServices.GetRequiredService<XmlVault>();
        var opts = new BatchOptions { ChunkSize = 2, MaxChunkAttempts = 1, LeaseSeconds = 1, HeartbeatSeconds = 1, WorkerSlots = 4, PerTenantInFlight = 4, DocParallelism = 2 };
        var batchId = Guid.Empty;
        var auditCalls = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        var auditor = new FakeAuditor(async (xml, ct) =>
        {
            var s = Encoding.UTF8.GetString(xml);
            auditCalls.AddOrUpdate(s, 1, (_, n) => n + 1);
            if (s.Contains("falha", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("motor reiniciando");
            }

            if (s.Contains("cerca", StringComparison.Ordinal))
            {
                // Outro worker assumiu o chunk (lease vencido): a tentativa deste worker deixa de valer.
                await ExecAsync("UPDATE batch_chunks SET attempts = attempts + 1 WHERE batch_id = $1 AND chunk_no = 2", batchId);
            }

            return new BatchDocResult("3527061122233300018155001000000001100000001" + s.Length % 10, "CONFORME", 0, 0m, null);
        });

        await WithHostedBatchProcessorStoppedAsync(async () =>
        {
            var ingest = ActivatorUtilities.CreateInstance<BatchIngestService>(env.Services, Options.Create(opts));
            var created = await ingest.IngestAsync(tenant, new MemoryStream(Zip(true,
                ("a/1.xml", "<ok n=\"1\"/>"), ("2.XML", "<falha/>"), ("3.xml", "<ok n=\"3\"/>"), ("4.xml", "<cerca/>"), ("5.xml", "<ok n=\"5\"/>"))),
                10_000_000, 100, 1, "OUTBOUND", CancellationToken.None);
            batchId = created.Id;
            Assert.Equal(5, created.Documents);
            Assert.Equal(3, created.Chunks);
            Assert.Equal([0L, 2L, 4L], await FirstDocsAsync(batchId));

            // O chunk 3 some do armazenamento: falha no processamento e volta para a fila até o teto (1).
            await vault.DeleteAsync($"t/{tenant}/batches/{batchId}/chunk-00003.bin.enc", CancellationToken.None);

            var log = new InfraLog<BatchProcessor>();
            var processor = ActivatorUtilities.CreateInstance<BatchProcessor>(env.WorkerServices, auditor, Options.Create(opts), log);
            Assert.Equal(3, await processor.RunOnceAsync(CancellationToken.None));

            Assert.Equal(3, auditCalls["<falha/>"]);                       // 3 tentativas por documento
            Assert.True(log.Has("Documento do lote falhou após 3 tentativas"));
            Assert.True(log.Has("perdeu a posse"));                         // cerca: resultado do chunk 2 descartado
            Assert.True(log.Has($"Chunk 3 do lote {batchId}"));             // chunk 3 sem o objeto no S3
            Assert.Equal("COMPLETED", await TextAsync("SELECT status FROM batches WHERE id = $1", batchId));
            Assert.Equal(5, await ScalarAsync("SELECT processed_docs FROM batches WHERE id = $1", batchId));
            Assert.Equal(4, await ScalarAsync("SELECT failed_docs FROM batches WHERE id = $1", batchId));
            Assert.Equal(1, await ScalarAsync("SELECT conforme_docs FROM batches WHERE id = $1", batchId));
            Assert.Equal(2, await ScalarAsync("SELECT count(*) FROM batch_chunks WHERE batch_id = $1 AND status = 'FAILED'", batchId));
            Assert.Equal(1, await ScalarAsync("SELECT count(*) FROM outbox WHERE topic = 'batch.audit.completed' AND payload->>'batch_id' = $1", batchId.ToString()));

            var report = Encoding.UTF8.GetString(await vault.GetAsync(kek, (await TextAsync("SELECT report_key FROM batches WHERE id = $1", batchId))!, CancellationToken.None));
            output.WriteLine(report);
            var lines = report.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(6, lines.Length);
            Assert.StartsWith("1,", lines[1], StringComparison.Ordinal);
            Assert.Contains(",CONFORME,", lines[1], StringComparison.Ordinal);
            Assert.StartsWith("2,,ERRO,", lines[2], StringComparison.Ordinal);
            Assert.Contains("Falha transitória", lines[2], StringComparison.Ordinal);
            for (var n = 3; n <= 5; n++)
            {
                Assert.StartsWith($"{n},,ERRO,0,0.00,", lines[n], StringComparison.Ordinal);
                Assert.Contains("após 1 tentativas", lines[n], StringComparison.Ordinal);
            }
        });
    }

    private async Task<List<long>> FirstDocsAsync(Guid batchId)
    {
        await using var su = new NpgsqlConnection(env.SuperuserConnection);
        await su.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT first_doc FROM batch_chunks WHERE batch_id = $1 ORDER BY chunk_no", su);
        cmd.Parameters.AddWithValue(batchId);
        await using var r = await cmd.ExecuteReaderAsync();
        var list = new List<long>();
        while (await r.ReadAsync())
        {
            list.Add(r.GetInt32(0));
        }

        return list;
    }

    [Fact]
    public async Task Conclusao_que_falha_volta_para_RUNNING_e_o_reconciliador_retoma()
    {
        var (tid, _) = await env.CreateTenantWithIdAsync("Lote infra B", GoldenFixture.NewCnpj());
        var tenant = new TenantId(tid);
        var kek = await KekAsync(tenant);
        var vault = env.WorkerServices.GetRequiredService<XmlVault>();
        var opts = new BatchOptions { ChunkSize = 1, WorkerSlots = 1, PerTenantInFlight = 1 };
        var auditor = new FakeAuditor((xml, _) => Task.FromResult(new BatchDocResult(null, "DIVERGENTE", 2, 1.5m, "linha 1\n\"aspas\"")));

        await WithHostedBatchProcessorStoppedAsync(async () =>
        {
            var ingest = ActivatorUtilities.CreateInstance<BatchIngestService>(env.Services, Options.Create(opts));
            var created = await ingest.IngestAsync(tenant, new MemoryStream(Zip(false, ("1.xml", "<a/>"), ("2.xml", "<b/>"))), 10_000_000, 10, 1, "INBOUND",
                CancellationToken.None);
            var log = new InfraLog<BatchProcessor>();
            var processor = ActivatorUtilities.CreateInstance<BatchProcessor>(env.WorkerServices, auditor, Options.Create(opts), log);
            Assert.Equal(1, await processor.RunOnceAsync(CancellationToken.None));
            var done = await ScalarAsync("SELECT chunk_no FROM batch_chunks WHERE batch_id = $1 AND status = 'DONE'", created.Id);

            // O resultado do chunk já concluído some antes da conclusão do lote.
            var resultKey = $"t/{tenant}/batches/{created.Id}/result-{done:D5}.csv.enc";
            var saved = await vault.GetAsync(kek, resultKey, CancellationToken.None);
            await vault.DeleteAsync(resultKey, CancellationToken.None);

            var ex = await Assert.ThrowsAnyAsync<Exception>(() => processor.RunOnceAsync(CancellationToken.None));
            Assert.True(XmlVault.IsNotFound(ex), ex.ToString());
            Assert.Equal("RUNNING", await TextAsync("SELECT status FROM batches WHERE id = $1", created.Id));   // voltou de COMPLETING
            Assert.True(log.Has($"lote {created.Id}"));

            // Objeto de volta: o reconciliador conclui.
            await vault.PutBlobAsync(kek, resultKey, saved, 30, CancellationToken.None);
            Assert.Equal(1, await processor.FinalizeStuckAsync(CancellationToken.None));
            Assert.Equal("COMPLETED", await TextAsync("SELECT status FROM batches WHERE id = $1", created.Id));
            var report = Encoding.UTF8.GetString(await vault.GetAsync(kek, (await TextAsync("SELECT report_key FROM batches WHERE id = $1", created.Id))!, CancellationToken.None));
            Assert.Contains("1,,DIVERGENTE,2,1.50,\"linha 1 \"\"aspas\"\"\"", report, StringComparison.Ordinal);
            Assert.Contains("2,,DIVERGENTE,2,1.50,", report, StringComparison.Ordinal);
            Assert.Equal(0, await processor.FinalizeStuckAsync(CancellationToken.None));
        });
    }

    [Fact]
    public async Task Laco_do_processador_cancela_chunk_que_perdeu_o_lease_e_para_com_slot_ocupado()
    {
        var (tid, _) = await env.CreateTenantWithIdAsync("Lote infra C", GoldenFixture.NewCnpj());
        var tenant = new TenantId(tid);
        var opts = new BatchOptions { ChunkSize = 10, WorkerSlots = 1, PerTenantInFlight = 2, LeaseSeconds = 30, HeartbeatSeconds = 1, MaxChunkAttempts = 5 };
        var lost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocking = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ids = new List<Guid>();
        var auditor = new FakeAuditor(async (xml, ct) =>
        {
            var s = Encoding.UTF8.GetString(xml);
            if (s.Contains("perde", StringComparison.Ordinal))
            {
                await ExecAsync("UPDATE batch_chunks SET attempts = attempts + 1 WHERE batch_id = $1", ids[0]);
                try
                {
                    await Task.Delay(Timeout.Infinite, ct);   // o heartbeat percebe a perda e cancela
                }
                catch (OperationCanceledException)
                {
                    lost.TrySetResult();
                    throw;
                }
            }

            if (s.Contains("segura", StringComparison.Ordinal))
            {
                blocking.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);       // segura o único slot até o desligamento
            }

            return new BatchDocResult(null, "CONFORME", 0, 0, null);
        });

        await WithHostedBatchProcessorStoppedAsync(async () =>
        {
            var ingest = ActivatorUtilities.CreateInstance<BatchIngestService>(env.Services, Options.Create(opts));
            ids.Add((await ingest.IngestAsync(tenant, new MemoryStream(Zip(false, ("1.xml", "<perde/>"))), 1_000_000, 10, 1, "OUTBOUND", CancellationToken.None)).Id);
            var log = new InfraLog<BatchProcessor>();
            using var processor = ActivatorUtilities.CreateInstance<BatchProcessor>(env.WorkerServices, auditor, Options.Create(opts), log);
            await processor.StartAsync(CancellationToken.None);
            try
            {
                await lost.Task.WaitAsync(TimeSpan.FromSeconds(30));
                ids.Add((await ingest.IngestAsync(tenant, new MemoryStream(Zip(false, ("1.xml", "<segura/>"))), 1_000_000, 10, 1, "OUTBOUND", CancellationToken.None)).Id);
                await blocking.Task.WaitAsync(TimeSpan.FromSeconds(30));
                Assert.Equal("RUNNING", await TextAsync("SELECT status FROM batch_chunks WHERE batch_id = $1", ids[1]));
            }
            finally
            {
                await processor.StopAsync(CancellationToken.None);
            }

            Assert.True(processor.ExecuteTask!.IsCompleted);
            Assert.Equal(2, await ScalarAsync("SELECT attempts FROM batch_chunks WHERE batch_id = $1", ids[0]));   // nada gravado pela tentativa que perdeu a posse
            Assert.Equal("RUNNING", await TextAsync("SELECT status FROM batch_chunks WHERE batch_id = $1", ids[0]));
            Assert.DoesNotContain(log.Entries, e => e.Level >= LogLevel.Error);
        });

        foreach (var id in ids)
        {
            await ExecAsync("DELETE FROM batch_chunks WHERE batch_id = $1", id);
            await ExecAsync("DELETE FROM batches WHERE id = $1", id);
        }
    }

    [Fact]
    public async Task Ingestao_recusa_zip_vazio_e_expansao_total_apagando_chunks_ja_enviados()
    {
        var (tid, _) = await env.CreateTenantWithIdAsync("Lote infra D", GoldenFixture.NewCnpj());
        var tenant = new TenantId(tid);
        var opts = new BatchOptions { ChunkSize = 1, TotalExpansionFactor = 1, MaxCompressionRatio = 100_000 };
        var ingest = ActivatorUtilities.CreateInstance<BatchIngestService>(env.Services, Options.Create(opts));

        var empty = Zip(true);
        var e1 = await Assert.ThrowsAsync<BatchRejectedException>(() => ingest.IngestAsync(tenant, new MemoryStream(empty), 1_000_000, 10, 1, "OUTBOUND", CancellationToken.None));
        Assert.Equal("ERR_BATCH_EMPTY", e1.Code);
        Assert.Equal(422, e1.Status);

        var zip = Zip(false, ("1.xml", "<a>" + new string('1', 40) + "</a>"), ("2.xml", "<b>" + new string('2', 40) + "</b>"), ("3.xml", new string('x', 200_000)));
        var e2 = await Assert.ThrowsAsync<BatchRejectedException>(() => ingest.IngestAsync(tenant, new MemoryStream(zip), zip.Length, 10, 1, "OUTBOUND", CancellationToken.None));
        Assert.Equal("ERR_BATCH_REJECTED_UNSAFE_ARCHIVE", e2.Code);
        Assert.Contains("Total descompactado", e2.Message, StringComparison.Ordinal);

        // O chunk 1 já tinha ido para o S3 quando o 3º documento estourou o limite: foi apagado.
        var s3 = env.WorkerServices.GetRequiredService<IAmazonS3>();
        var bucket = env.WorkerServices.GetRequiredService<IOptions<StorageOptions>>().Value.Bucket;
        var left = await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket, Prefix = $"t/{tenant}/batches/" });
        Assert.Empty(left.S3Objects ?? []);
        Assert.Equal(0, await ScalarAsync("SELECT count(*) FROM batches WHERE tenant_id = $1", tid));
    }

    // ---------------------------------------------------------------- DF-e

    /// <summary>
    /// Tenant com A1 e captura ligada para um CNPJ só deste teste (o simulador da SEFAZ guarda estado por CNPJ: usar o
    /// destinatário dos outros testes de DF-e faria as consultas deles e as deste somarem no bloqueio de 1 h).
    /// </summary>
    private async Task<(TenantId Tenant, string Cnpj)> DfeTenantAsync(string name, bool ciencia = true)
    {
        var cnpj = GoldenFixture.NewCnpj();
        var (tid, key) = await env.CreateTenantWithIdAsync(name, cnpj);
        using var leaf = TestPki.IssueLeaf(env.Pki.Root, cnpj, env.Clock.GetUtcNow().AddDays(-1), env.Clock.GetUtcNow().AddYears(1));
        var pfx = leaf.Export(X509ContentType.Pfx, "s");
        Assert.Equal(HttpStatusCode.OK, await SendAsync(key, HttpMethod.Put, $"/v1/cnpjs/{cnpj}/certificado",
            new JsonObject { ["pfx_base64"] = Convert.ToBase64String(pfx), ["senha"] = "s" }));
        Assert.Equal(HttpStatusCode.OK, await SendAsync(key, HttpMethod.Post, $"/v1/cnpjs/{cnpj}/dfe/sync",
            new JsonObject { ["c_uf_autor"] = 35, ["manifestar_ciencia"] = ciencia }));
        return (new TenantId(tid), cnpj);
    }

    private async Task<HttpStatusCode> SendAsync(string key, HttpMethod method, string path, JsonObject? body)
    {
        using var req = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
        req.Headers.Add("X-Api-Key", key);
        using var resp = await env.Api.SendAsync(req);
        return resp.StatusCode;
    }

    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    private static string Resumo(string chave) =>
        $"""<resNFe xmlns="{NfeBuilder.Ns}" versao="1.01"><chNFe>{chave}</chNFe><CNPJ>11222333000181</CNPJ><xNome>EMITENTE</xNome><IE>1</IE><dhEmi>2027-06-15T14:00:00-03:00</dhEmi><tpNF>1</tpNF><vNF>1000.00</vNF><dhRecbto>2027-06-15T14:00:01-03:00</dhRecbto><nProt>1</nProt><cSitNFe>1</cSitNFe></resNFe>""";

    [Fact]
    public async Task DFe_com_SEFAZ_instavel_656_teto_por_hora_resumos_e_certificado_removido()
    {
        var (tenant, cnpj) = await DfeTenantAsync("DF-e infra");
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
        const string ChaveA = "35270611222333000181550010000099011000000011";
        const string ChaveB = "35270611222333000181550010000099021000000021";
        var step = 0;
        var sefaz = new FakeSefaz(ult => step switch
        {
            0 => new DistResult("138", "Documentos localizados", 6, 6,
            [
                new DfeDoc(1, "resNFe_v1.01.xsd", Utf8(Resumo(ChaveA))),               // ciência falha (rede)
                new DfeDoc(2, "resNFe_v1.01.xsd", Utf8("<resNFe><quebrado")),           // XML malformado: sem chave
                new DfeDoc(3, "procNFe_v4.00.xsd", Utf8("<nfeProc/>")),
                new DfeDoc(4, "resEvento_v1.01.xsd", Utf8("<resEvento/>")),
                new DfeDoc(5, "procEventoNFe_v1.00.xsd", Utf8("<procEventoNFe/>")),
                new DfeDoc(6, "outro_v1.00.xsd", Utf8("<x/>")),
            ]),
            1 => new DistResult("138", "Documentos localizados", 7, 7,
                [new DfeDoc(6, "outro_v1.00.xsd", Utf8("<x/>")), new DfeDoc(7, "resNFe_v1.01.xsd", Utf8(Resumo(ChaveB)))]),   // NSU 6 já gravado
            2 => new DistResult("656", "Rejeição: Consumo Indevido", 9, 12, []),
            3 => throw new HttpRequestException("SEFAZ fora do ar"),
            _ => throw new InvalidOperationException("não deveria consultar"),
        }, chave => chave == ChaveA ? throw new HttpRequestException("timeout na recepção de evento") : new EventoResult("135", "Evento registrado"));
        var auditor = new FakeAuditor((xml, _) => Task.FromResult(new BatchDocResult(null, "SCHEMA_ERROR", 0, 0, "XML inválido no schema")));
        var log = new InfraLog<DfeSyncService>();
        var sync = ActivatorUtilities.CreateInstance<DfeSyncService>(env.WorkerServices, sefaz, auditor, (TimeProvider)clock, log);

        await sync.SyncOneAsync(tenant, cnpj, CancellationToken.None);
        var docs = await DfeDocsAsync(tenant);
        Assert.Equal(("RESUMO", "CIENCIA_FALHOU", "HttpRequestException"), (docs[1].Kind, docs[1].Status, docs[1].Detail));
        Assert.Equal(("RESUMO", "RECEBIDO", (string?)null), (docs[2].Kind, docs[2].Status, docs[2].Chave));
        Assert.Equal(("COMPLETO", "REJEITADO"), (docs[3].Kind, docs[3].Status));
        Assert.Equal(("EVENTO", "IGNORADO"), (docs[4].Kind, docs[4].Status));
        Assert.Equal(("EVENTO", "IGNORADO"), (docs[5].Kind, docs[5].Status));
        Assert.Equal(("OUTRO", "IGNORADO"), (docs[6].Kind, docs[6].Status));
        Assert.Equal(1, await ScalarAsync("SELECT count(*) FROM outbox WHERE tenant_id = $1 AND topic = 'dfe.document.audited'", tenant.Value));

        step = 1;
        clock.Advance(TimeSpan.FromHours(1));
        await sync.SyncOneAsync(tenant, cnpj, CancellationToken.None);
        docs = await DfeDocsAsync(tenant);
        Assert.Equal(7, docs.Count);
        Assert.Equal("CIENCIA_ENVIADA", docs[7].Status);
        Assert.Equal([ChaveA, ChaveB], sefaz.Ciencias);

        step = 2;
        clock.Advance(TimeSpan.FromHours(1));
        await sync.SyncOneAsync(tenant, cnpj, CancellationToken.None);
        Assert.Equal("656", await TextAsync("SELECT last_cstat FROM dfe_sync_state WHERE tenant_id = $1", tenant.Value));
        Assert.Equal(9, await ScalarAsync("SELECT ult_nsu FROM dfe_sync_state WHERE tenant_id = $1", tenant.Value));
        Assert.Equal(12, await ScalarAsync("SELECT max_nsu FROM dfe_sync_state WHERE tenant_id = $1", tenant.Value));
        Assert.Equal(1, await ScalarAsync("SELECT count(*) FROM dfe_sync_state WHERE tenant_id = $1 AND last_656_at IS NOT NULL", tenant.Value));
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("656", StringComparison.Ordinal));

        step = 3;
        clock.Advance(TimeSpan.FromHours(1));
        await sync.SyncOneAsync(tenant, cnpj, CancellationToken.None);
        Assert.Equal("ERRO", await TextAsync("SELECT last_cstat FROM dfe_sync_state WHERE tenant_id = $1", tenant.Value));
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Warning && e.Error is HttpRequestException);

        // Teto de 20 consultas por hora já atingido na janela: nem consulta.
        step = 4;
        var calls = sefaz.DistCalls;
        await ExecAsync("UPDATE dfe_sync_state SET window_start = $2, window_count = 20 WHERE tenant_id = $1", tenant.Value, clock.GetUtcNow().AddMinutes(-10));
        await sync.SyncOneAsync(tenant, cnpj, CancellationToken.None);
        Assert.Equal(calls, sefaz.DistCalls);
        Assert.Equal(clock.GetUtcNow().AddMinutes(50), await NextAllowedAsync(tenant));

        // Certificado removido: adia 1 h sem consultar.
        Assert.True(await env.Services.GetRequiredService<CertificateStore>().DeleteAsync(tenant, cnpj, CancellationToken.None));
        await sync.SyncOneAsync(tenant, cnpj, CancellationToken.None);
        Assert.Equal(calls, sefaz.DistCalls);
        Assert.Equal(clock.GetUtcNow().AddHours(1), await NextAllowedAsync(tenant));
        await ExecAsync("UPDATE dfe_sync_state SET enabled = FALSE WHERE tenant_id = $1", tenant.Value);
    }

    private async Task<DateTimeOffset> NextAllowedAsync(TenantId tenant)
    {
        await using var su = new NpgsqlConnection(env.SuperuserConnection);
        await su.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT next_allowed_at FROM dfe_sync_state WHERE tenant_id = $1", su);
        cmd.Parameters.AddWithValue(tenant.Value);
        await using var r = await cmd.ExecuteReaderAsync();
        Assert.True(await r.ReadAsync());
        return r.GetFieldValue<DateTimeOffset>(0);
    }

    private async Task<Dictionary<long, (string Kind, string Status, string? Detail, string? Chave)>> DfeDocsAsync(TenantId tenant)
    {
        await using var su = new NpgsqlConnection(env.SuperuserConnection);
        await su.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT nsu, kind, status, detail, access_key FROM dfe_documents WHERE tenant_id = $1", su);
        cmd.Parameters.AddWithValue(tenant.Value);
        await using var r = await cmd.ExecuteReaderAsync();
        var map = new Dictionary<long, (string, string, string?, string?)>();
        while (await r.ReadAsync())
        {
            map[r.GetInt64(0)] = (r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4));
        }

        return map;
    }

    [Fact]
    public async Task Worker_de_DFe_roda_o_ciclo_e_o_alerta_de_vencimento_uma_vez_por_hora()
    {
        var (tenant, _) = await DfeTenantAsync("DF-e worker", ciencia: false);
        // Relógio próprio no passado: só o CNPJ deste teste está "vencido" para consulta (os demais têm next_allowed_at depois).
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        await ExecAsync("UPDATE dfe_sync_state SET next_allowed_at = $2 WHERE tenant_id = $1", tenant.Value, clock.GetUtcNow().AddMinutes(-1));
        var sefaz = new FakeSefaz(_ => new DistResult("137", "Nenhum documento localizado", 0, 0, []));
        var sync = ActivatorUtilities.CreateInstance<DfeSyncService>(env.WorkerServices, sefaz, (TimeProvider)clock);
        var expiry = ActivatorUtilities.CreateInstance<CertificateExpiryService>(env.WorkerServices, (TimeProvider)clock);
        var log = new InfraLog<DfeWorker>();
        using var worker = new DfeWorker(sync, expiry, Options.Create(new DfeOptions { PollSeconds = 1 }), log);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            await InfraWait.Until(() => Volatile.Read(ref sefaz.DistCalls) >= 1);
            clock.Advance(TimeSpan.FromHours(2));   // próximo tique (5 s): consulta de novo; o alerta de vencimento não repete
            await InfraWait.Until(() => Volatile.Read(ref sefaz.DistCalls) >= 2);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        Assert.Empty(log.Entries);
        Assert.Equal("137", await TextAsync("SELECT last_cstat FROM dfe_sync_state WHERE tenant_id = $1", tenant.Value));
        await ExecAsync("UPDATE dfe_sync_state SET enabled = FALSE WHERE tenant_id = $1", tenant.Value);   // fora do laço dos outros testes
    }

    // ---------------------------------------------------------------- webhooks, autenticação, revogações

    [Fact]
    public async Task Webhook_rotacao_de_segredo_listagens_e_modo_seguro()
    {
        var (tid, _) = await env.CreateTenantWithIdAsync("Webhook infra", GoldenFixture.NewCnpj());
        var tenant = new TenantId(tid);
        var repo = env.Services.GetRequiredService<WebhookRepository>();
        var (id, secret) = await repo.CreateAsync(tenant, new Uri("https://localhost:9443/hook"), ["batch.audit.completed"], CancellationToken.None);
        Assert.StartsWith("whsec_", secret, StringComparison.Ordinal);

        var rotated = await repo.RotateSecretAsync(tenant, id, CancellationToken.None);
        Assert.NotNull(rotated);
        Assert.NotEqual(secret, rotated);
        var again = await repo.RotateSecretAsync(tenant, id, CancellationToken.None);
        Assert.NotEqual(rotated, again);
        Assert.Null(await repo.RotateSecretAsync(tenant, Guid.NewGuid(), CancellationToken.None));
        Assert.Null(await repo.RotateSecretAsync(new TenantId(Guid.NewGuid()), id, CancellationToken.None));   // outro tenant não enxerga (RLS)

        var list = await repo.ListAsync(tenant, CancellationToken.None);
        Assert.Equal(id, Assert.Single(list).Id);
        Assert.Empty(await repo.ListDeliveriesAsync(tenant, "DEAD", CancellationToken.None));
        Assert.Equal(1, await env.WorkerServices.GetRequiredService<WebhookRepository>().FanOutAsync(tenant, 987654321, "batch.audit.completed", """{"batch_id":"x"}""", CancellationToken.None));
        var delivery = Assert.Single(await repo.ListDeliveriesAsync(tenant, null, CancellationToken.None));
        Assert.Equal(1, await repo.RetryAsync(tenant, delivery.Id, CancellationToken.None));
        Assert.Equal("RETRYING", Assert.Single(await repo.ListDeliveriesAsync(tenant, "RETRYING", CancellationToken.None)).Status);
        Assert.Equal(1, await repo.DeleteAsync(tenant, id, CancellationToken.None));

        // Produção: https obrigatório e o host não pode resolver para endereço interno.
        var secure = new WebhookRepository(env.Services.GetRequiredService<ITenantDb>(), env.Services.GetRequiredService<IKeyVault>(),
            env.Services.GetRequiredService<TenantSettingsService>(), Options.Create(new WebhookOptions()), new MemoryCache(new MemoryCacheOptions()));
        var rejected = await Assert.ThrowsAsync<WebhookUrlRejectedException>(() => secure.CreateAsync(tenant, new Uri("https://localhost/hook"), ["*"], CancellationToken.None));
        Assert.Contains("interno", rejected.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<WebhookUrlRejectedException>(() => secure.CreateAsync(tenant, new Uri("http://exemplo.test/hook"), ["*"], CancellationToken.None));
        Assert.Empty(await repo.ListAsync(tenant, CancellationToken.None));
    }

    [Fact]
    public async Task Autenticacao_descarta_cache_adulterado_ouve_revogacao_e_segue_sem_Valkey()
    {
        var hasher = env.Services.GetRequiredService<ApiKeyHasher>();
        var seal = env.Services.GetRequiredService<CacheSeal>();
        var lookup = env.Services.GetRequiredService<ApiKeyLookup>();
        var valkey = env.Services.GetRequiredService<IConnectionMultiplexer>();
        var (tid, key) = await env.CreateTenantWithIdAsync("Auth infra", GoldenFixture.NewCnpj());
        var hex = Convert.ToHexStringLower(hasher.Hash(key));
        var cacheKey = $"auth:apikey:{hex}";
        var db = valkey.GetDatabase();

        // L2 adulterado (sem selo): ignorado, vai ao banco e regrava selado.
        await db.StringSetAsync(cacheKey, """{"t":"00000000-0000-0000-0000-000000000000"}""");
        var log = new InfraLog<ApiKeyAuthenticator>();
        using (var auth = new ApiKeyAuthenticator(hasher, lookup, valkey, seal, log))
        {
            var id = await auth.AuthenticateAsync(key, CancellationToken.None);
            Assert.Equal(tid, id!.Tenant.Value);
            Assert.True(log.Has(hex[..8]));
            Assert.NotNull(seal.OpenString(cacheKey, (byte[])(await db.StringGetAsync(cacheKey))!));
            Assert.Null(await auth.AuthenticateAsync("tg_test_sk_curta", CancellationToken.None));

            // Revogação vinda de outra instância (pub/sub) tira a chave do L1: a próxima leitura volta ao L2.
            var before = log.Entries.Count;
            await db.StringSetAsync(cacheKey, "adulterado");
            await valkey.GetSubscriber().PublishAsync(RedisChannel.Literal(ApiKeyAuthenticator.RevocationChannel), hex);
            await InfraWait.Until(async () =>
            {
                await auth.AuthenticateAsync(key, CancellationToken.None);
                return log.Entries.Count > before;
            });

            await auth.InvalidateAsync(hasher.Hash(key));
            Assert.False(await db.KeyExistsAsync(cacheKey));
        }

        // Valkey fora: assinatura do canal, leitura, escrita e invalidação falham sem derrubar a autenticação.
        using var dead = InfraWait.DeadValkey();
        var deadLog = new InfraLog<ApiKeyAuthenticator>();
        using var offline = new ApiKeyAuthenticator(hasher, lookup, dead, seal, deadLog);
        Assert.Equal(tid, (await offline.AuthenticateAsync(key, CancellationToken.None))!.Tenant.Value);
        Assert.Null(await offline.AuthenticateAsync("tg_test_sk_ABCDEFGH_" + new string('z', 32), CancellationToken.None));   // formato válido, chave inexistente
        await offline.InvalidateAsync(hasher.Hash(key));
        Assert.True(deadLog.Has("assinar o canal"));
        Assert.True(deadLog.Has("Valkey indisponível"));
        Assert.True(deadLog.Has("propagar revogação"));
    }

    [Fact]
    public async Task Revogacoes_leem_o_banco_e_limpam_jti_vencido_no_Valkey()
    {
        var (tid, _) = await env.CreateTenantWithIdAsync("Revogação infra", GoldenFixture.NewCnpj());
        var jti = $"jti-{Guid.NewGuid():N}";
        var oldJti = $"jti-velho-{Guid.NewGuid():N}";
        await ExecAsync("INSERT INTO revoked_tokens (jti, expires_at, revoked_by) VALUES ($1, now() + interval '1 hour', 'teste')", jti);
        var valkey = env.Services.GetRequiredService<IConnectionMultiplexer>();
        await valkey.GetDatabase().HashSetAsync("revoked:jtis", oldJti, DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds());
        try
        {
            await using var app = NpgsqlDataSource.Create(env.AppConnection);
            using var svc = new RevocationService(valkey, app, new InfraLog<RevocationService>());
            var tenant = new TenantId(tid);
            Assert.False(svc.IsRevoked(jti, null, tenant));
            await svc.LoadFromDatabaseAsync(CancellationToken.None);
            Assert.True(svc.IsRevoked(jti, null, new TenantId(Guid.NewGuid())));
            Assert.True(svc.IsRevoked(jti, "qualquer", tenant));
            Assert.False(svc.IsRevoked("outro", "outro", new TenantId(Guid.NewGuid())));
            Assert.False(svc.IsTenantSuspended(tenant));

            await svc.LoadAsync();
            Assert.False(await valkey.GetDatabase().HashExistsAsync("revoked:jtis", oldJti));   // vencido: apagado
            Assert.False(svc.IsRevoked(oldJti, null, new TenantId(Guid.NewGuid())));
        }
        finally
        {
            await ExecAsync("DELETE FROM revoked_tokens WHERE jti = $1", jti);
        }
    }

    // ---------------------------------------------------------------- host dos workers

    [Fact]
    public async Task Host_dos_workers_exige_conexoes_e_liga_sonda_cobranca_LCR_e_DFe_conforme_configuracao()
    {
        var noWorker = Assert.Throws<InvalidOperationException>(() => WorkerHost.Main(["--environment", "Testing"]));
        Assert.Contains("ConnectionStrings:Worker", noWorker.Message, StringComparison.Ordinal);

        var settings = new Dictionary<string, string?>(env.Settings)
        {
            ["ConnectionStrings:Worker"] = env.ConnectionFor("tg_worker"),
        };
        var noRelay = Assert.Throws<InvalidOperationException>(() => WorkerHost.CreateBuilder(["--environment", "Testing"], settings));
        Assert.Contains("ConnectionStrings:Relay", noRelay.Message, StringComparison.Ordinal);

        var port = InfraWait.FreePort();
        settings["ConnectionStrings:Relay"] = env.ConnectionFor("tg_relay");
        settings["ConnectionStrings:Billing"] = env.ConnectionFor("tg_billing");
        settings["Workers:HealthPort"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        using var host = WorkerHost.CreateBuilder(["--environment", "Testing"], settings).Build();
        var hosted = host.Services.GetServices<IHostedService>().ToList();
        Assert.Contains(hosted, h => h is DfeWorker);
        Assert.Contains(hosted, h => h is CrlPublisher);
        Assert.Contains(hosted, h => h is TaxGateway.Infrastructure.Billing.BillingCloseWorker);
        Assert.Contains(hosted, h => h is TaxGateway.Infrastructure.Billing.DunningWorker);
        var probe = Assert.Single(hosted.OfType<WorkerHealthServer>());
        await probe.StartAsync(CancellationToken.None);
        try
        {
            using var http = new HttpClient();
            Assert.Equal("Healthy", await http.GetStringAsync(new Uri($"http://127.0.0.1:{port}/health/ready")));
        }
        finally
        {
            await probe.StopAsync(CancellationToken.None);
        }

        Assert.Equal(TimeSpan.FromSeconds(45), host.Services.GetRequiredService<IOptions<HostOptions>>().Value.ShutdownTimeout);
    }
}
