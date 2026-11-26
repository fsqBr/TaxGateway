using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

using TaxGateway.Infrastructure.Webhooks;
using TaxGateway.TestKit;

namespace TaxGateway.GoldenTests;

/// <summary>Receptor HTTPS local dos webhooks (Kestrel + certificado autoassinado; aceito só com AllowInsecureForTests).</summary>
public sealed class WebhookReceiver : IAsyncDisposable
{
    private readonly Microsoft.AspNetCore.Builder.WebApplication _app;

    public WebhookReceiver(System.Security.Cryptography.X509Certificates.X509Certificate2 cert)
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        Port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, Port, l => l.UseHttps(cert)));
        _app = builder.Build();
        _app.MapPost("/hook", async (Microsoft.AspNetCore.Http.HttpContext ctx) =>
        {
            using var ms = new MemoryStream();
            await ctx.Request.Body.CopyToAsync(ms);
            Received.Enqueue((ctx.Request.Headers["TaxGateway-Signature"].ToString(), ctx.Request.Headers["TaxGateway-Event"].ToString(), ms.ToArray()));
            return Microsoft.AspNetCore.Http.Results.Ok();
        });
        _app.StartAsync().GetAwaiter().GetResult();
    }

    public int Port { get; }
    public string Url => $"https://localhost:{Port}/hook";
    public ConcurrentQueue<(string Signature, string Event, byte[] Body)> Received { get; } = new();

    public async Task<(string Signature, string Event, byte[] Body)?> WaitAsync(Func<string, JsonNode, bool> match, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            foreach (var r in Received)
            {
                if (match(r.Event, JsonNode.Parse(r.Body)!))
                {
                    return r;
                }
            }

            await Task.Delay(200);
        }

        return null;
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();
}

/// <summary>
/// Fase 6 ponta a ponta: auditoria → outbox → relay → RabbitMQ → fan-out → entrega assinada; lote ZIP → chunks →
/// fair-share → relatório → batch.audit.completed; zip bomb e webhook para IP privado recusados pela API.
/// </summary>
[Collection(GoldenCollection.Name)]
public sealed class PipelineTests(GoldenFixture env)
{
    private async Task<JsonNode> PostJsonAsync(string path, JsonObject body)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        req.Headers.Add("X-Api-Key", env.ApiKey);
        using var resp = await env.Api.SendAsync(req);
        var text = await resp.Content.ReadAsStringAsync();
        Assert.True(resp.IsSuccessStatusCode, $"{path}: HTTP {(int)resp.StatusCode} {text}");
        return JsonNode.Parse(text)!;
    }

    private static XmlDocument Doc(int numero)
    {
        var doc = new XmlDocument { PreserveWhitespace = true };
        doc.Load(Path.Combine(GoldenXmlTests.XmlDir, "A-000001-RO.xml"));
        GoldenXmlTests.Rekey(doc, numero);
        return doc;
    }

    [Fact]
    public async Task Audit_event_reaches_the_webhook_with_valid_signature_and_batch_completes()
    {
        await using var receiver = new WebhookReceiver(env.WebhookTls);
        var hook = await PostJsonAsync("/v1/webhooks", new JsonObject { ["url"] = receiver.Url, ["eventos"] = new JsonArray("*") });
        var secret = hook["secret"]!.GetValue<string>();

        // 1) Auditoria avulsa → document.audited assinado.
        var doc = Doc(700_001);
        NfeBuilder.Sign(doc, env.Pki.Leaf);
        using (var req = new HttpRequestMessage(HttpMethod.Post, "/v1/auditoria/xml?direction=OUTBOUND") { Content = new ByteArrayContent(NfeBuilder.Bytes(doc)) })
        {
            req.Headers.Add("X-Api-Key", env.ApiKey);
            req.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
            (await env.Api.SendAsync(req)).EnsureSuccessStatusCode();
        }

        var chave = GoldenXmlTests.Chave(doc);
        var got = await receiver.WaitAsync((ev, body) => ev == "document.audited" && body["data"]?["chave_acesso"]?.GetValue<string>() == chave, TimeSpan.FromSeconds(60));
        Assert.True(got is not null, "webhook document.audited não chegou em 60 s");
        Assert.True(WebhookSignature.Verify(got!.Value.Signature, got.Value.Body, secret, TimeSpan.FromMinutes(5), DateTimeOffset.UtcNow));

        // 2) Lote de 12 notas → relatório + batch.audit.completed.
        using var zip = new MemoryStream();
        using (var z = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
        {
            for (var i = 0; i < 12; i++)
            {
                var d = Doc(710_000 + i);
                NfeBuilder.Sign(d, env.Pki.Leaf);
                await using var s = z.CreateEntry($"nota-{i}.xml").Open();
                await s.WriteAsync(NfeBuilder.Bytes(d));
            }
        }

        string batchId;
        using (var req = new HttpRequestMessage(HttpMethod.Post, "/v1/auditoria/lotes?direction=OUTBOUND") { Content = new ByteArrayContent(zip.ToArray()) })
        {
            req.Content.Headers.ContentType = new("application/zip");
            req.Headers.Add("X-Api-Key", env.ApiKey);
            req.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
            using var resp = await env.Api.SendAsync(req);
            var text = await resp.Content.ReadAsStringAsync();
            Assert.True(resp.StatusCode == HttpStatusCode.Accepted, text);
            var created = JsonNode.Parse(text)!;
            Assert.Equal(12, created["documentos"]!.GetValue<int>());
            batchId = created["batch_id"]!.ToString();
        }

        JsonNode? status = null;
        for (var i = 0; i < 120; i++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"/v1/auditoria/lotes/{batchId}");
            req.Headers.Add("X-Api-Key", env.ApiKey);
            status = JsonNode.Parse(await (await env.Api.SendAsync(req)).Content.ReadAsStringAsync());
            if (status?["status"]?.GetValue<string>() == "COMPLETED")
            {
                break;
            }

            await Task.Delay(500);
        }

        Assert.Equal("COMPLETED", status?["status"]?.GetValue<string>());
        Assert.Equal(12, status!["processados"]!.GetValue<int>());
        Assert.Equal(12, status["conformes"]!.GetValue<int>());

        using (var req = new HttpRequestMessage(HttpMethod.Get, $"/v1/auditoria/lotes/{batchId}/relatorio"))
        {
            req.Headers.Add("X-Api-Key", env.ApiKey);
            var csv = await (await env.Api.SendAsync(req)).Content.ReadAsStringAsync();
            var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(13, lines.Length);   // cabeçalho + 12
            Assert.All(lines.Skip(1), l => Assert.Contains(",CONFORME,", l, StringComparison.Ordinal));
        }

        var done = await receiver.WaitAsync((ev, body) => ev == "batch.audit.completed" && body["data"]?["batch_id"]?.ToString() == batchId, TimeSpan.FromSeconds(60));
        Assert.True(done is not null, "webhook batch.audit.completed não chegou em 60 s");
    }

    [Fact]
    public async Task Usage_reports_the_ledger_of_the_current_month()
    {
        var doc = Doc(720_001);
        NfeBuilder.Sign(doc, env.Pki.Leaf);
        using (var req = new HttpRequestMessage(HttpMethod.Post, "/v1/auditoria/xml?direction=OUTBOUND") { Content = new ByteArrayContent(NfeBuilder.Bytes(doc)) })
        {
            req.Headers.Add("X-Api-Key", env.ZeroRetentionApiKey);
            req.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
            (await env.Api.SendAsync(req)).EnsureSuccessStatusCode();
        }

        using var get = new HttpRequestMessage(HttpMethod.Get, "/v1/usage");
        get.Headers.Add("X-Api-Key", env.ZeroRetentionApiKey);
        var usage = JsonNode.Parse(await (await env.Api.SendAsync(get)).Content.ReadAsStringAsync())!;
        Assert.True(usage["documentos_auditados"]!.GetValue<long>() >= 1, usage.ToJsonString());
        Assert.Equal("OEM", usage["plano"]!.GetValue<string>());
        Assert.True(usage["cnpjs_ativos"]!.GetValue<long>() >= 1);
    }

    [Fact]
    public async Task Api_rejects_zip_bomb_and_private_ip_webhook()
    {
        using var zip = new MemoryStream();
        using (var z = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
        {
            await using var s = z.CreateEntry("bomba.xml", CompressionLevel.SmallestSize).Open();
            await s.WriteAsync(new byte[30 * 1024 * 1024]);
        }

        using (var req = new HttpRequestMessage(HttpMethod.Post, "/v1/auditoria/lotes") { Content = new ByteArrayContent(zip.ToArray()) })
        {
            req.Content.Headers.ContentType = new("application/zip");
            req.Headers.Add("X-Api-Key", env.ApiKey);
            req.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
            using var resp = await env.Api.SendAsync(req);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
            Assert.Contains("BATCH_REJECTED_UNSAFE_ARCHIVE", await resp.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        foreach (var url in new[] { "https://10.0.0.7/hook", "https://169.254.169.254/latest/meta-data/", "https://192.168.1.1/x" })
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "/v1/webhooks")
            {
                Content = JsonContent.Create(new JsonObject { ["url"] = url, ["eventos"] = new JsonArray("*") }),
            };
            req.Headers.Add("X-Api-Key", env.ApiKey);
            using var resp = await env.Api.SendAsync(req);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
            Assert.Contains("ERR_WEBHOOK_URL_BLOQUEADA", Encoding.UTF8.GetString(await resp.Content.ReadAsByteArrayAsync()), StringComparison.Ordinal);
        }
    }
}

/// <summary>
/// Gerador do lote de carga do gate de capacidade (D-608): CAPACITY_ZIP=caminho CAPACITY_DOCS=N gera N NF-e
/// assinadas pela PKI de teste (chaves distintas). Sem as variáveis, não faz nada.
/// </summary>
public sealed class CapacityCorpusGenerator
{
    [Fact]
    public async Task Generate_capacity_batch_when_requested()
    {
        var path = Environment.GetEnvironmentVariable("CAPACITY_ZIP");
        if (path is null)
        {
            return;
        }

        var n = int.Parse(Environment.GetEnvironmentVariable("CAPACITY_DOCS") ?? "2000", System.Globalization.CultureInfo.InvariantCulture);
        var pki = TestPki.LoadOrCreate(Path.Combine(Paths.RepoRoot, "tests", "golden", "pki"));
        var template = new XmlDocument { PreserveWhitespace = true };
        template.Load(Path.Combine(GoldenXmlTests.XmlDir, "A-000001-RO.xml"));
        await using var file = File.Create(path);
        using var z = new ZipArchive(file, ZipArchiveMode.Create);
        var seed = Random.Shared.Next(1, 90) * 1_000_000;
        for (var i = 0; i < n; i++)
        {
            var d = (XmlDocument)template.Clone();
            GoldenXmlTests.Rekey(d, seed + i);
            NfeBuilder.Sign(d, pki.Leaf);
            await using var s = z.CreateEntry($"n{i:D6}.xml").Open();
            await s.WriteAsync(NfeBuilder.Bytes(d));
        }
    }
}
