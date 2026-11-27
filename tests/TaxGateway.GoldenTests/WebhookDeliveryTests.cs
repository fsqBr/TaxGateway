using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json.Nodes;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

using TaxGateway.Infrastructure.Webhooks;
using TaxGateway.TestKit;

namespace TaxGateway.GoldenTests;

/// <summary>
/// Caminho de falha dos webhooks como o ERP do cliente vê (D-603/D-605): destino fora do ar → nova tentativa agendada
/// com o código devolvido; o cliente corrige o receptor e pede o retry manual → entrega assinada; entrega já feita não
/// é reenviada; 410 Gone desativa o webhook e manda a entrega para a fila morta.
/// </summary>
[Collection(GoldenCollection.Name)]
public sealed class WebhookDeliveryTests(GoldenFixture env)
{
    /// <summary>Receptor HTTPS local cujo código de resposta o teste controla.</summary>
    private sealed class FlakyReceiver : IAsyncDisposable
    {
        private readonly Microsoft.AspNetCore.Builder.WebApplication _app;

        public FlakyReceiver(System.Security.Cryptography.X509Certificates.X509Certificate2 cert)
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
                var status = Status;
                Received.Enqueue((ctx.Request.Headers["TaxGateway-Signature"].ToString(), ms.ToArray(), status));
                return Microsoft.AspNetCore.Http.Results.StatusCode(status);
            });
            _app.StartAsync().GetAwaiter().GetResult();
        }

        public int Port { get; }

        public string Url => $"https://localhost:{Port}/hook";

        public volatile int Status = 503;

        public ConcurrentQueue<(string Signature, byte[] Body, int Answered)> Received { get; } = new();

        public async ValueTask DisposeAsync() => await _app.DisposeAsync();
    }

    private async Task<(HttpStatusCode Status, JsonNode? Body)> ApiAsync(HttpMethod m, string path, JsonObject? body = null)
    {
        using var req = new HttpRequestMessage(m, path) { Content = body is null ? null : JsonContent.Create(body) };
        req.Headers.Add("X-Api-Key", env.ApiKey);
        using var resp = await env.Api.SendAsync(req);
        var text = await resp.Content.ReadAsStringAsync();
        return (resp.StatusCode, text.Length == 0 ? null : JsonNode.Parse(text));
    }

    private async Task AuditAsync(int numero)
    {
        var doc = new System.Xml.XmlDocument { PreserveWhitespace = true };
        doc.Load(Path.Combine(GoldenXmlTests.XmlDir, "A-000001-RO.xml"));
        GoldenXmlTests.Rekey(doc, numero);
        NfeBuilder.Sign(doc, env.Pki.Leaf);
        using var req = new HttpRequestMessage(HttpMethod.Post, "/v1/auditoria/xml?direction=OUTBOUND") { Content = new ByteArrayContent(NfeBuilder.Bytes(doc)) };
        req.Headers.Add("X-Api-Key", env.ApiKey);
        req.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        (await env.Api.SendAsync(req)).EnsureSuccessStatusCode();
    }

    private async Task<JsonNode> WaitDeliveryAsync(Guid webhook, Func<JsonNode, bool> match, int seconds = 60)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (true)
        {
            var list = (await ApiAsync(HttpMethod.Get, "/v1/webhooks/deliveries")).Body!.AsArray();
            var hit = list.FirstOrDefault(d => d!["webhook_id"]!.GetValue<Guid>() == webhook && match(d));
            if (hit is not null)
            {
                return hit;
            }

            Assert.True(DateTime.UtcNow < until, $"entrega esperada não apareceu em {seconds} s: {list.ToJsonString()}");
            await Task.Delay(300);
        }
    }

    [Fact]
    public async Task Destino_fora_do_ar_agenda_nova_tentativa_e_retry_manual_entrega_assinado()
    {
        await using var receiver = new FlakyReceiver(env.WebhookTls);
        var hook = (await ApiAsync(HttpMethod.Post, "/v1/webhooks", new JsonObject { ["url"] = receiver.Url, ["eventos"] = new JsonArray("document.audited") })).Body!;
        var id = hook["id"]!.GetValue<Guid>();
        var secret = hook["secret"]!.GetValue<string>();

        await AuditAsync(730_001);
        var failed = await WaitDeliveryAsync(id, d => d["status"]!.GetValue<string>() == "RETRYING" && d["last_status_code"]?.GetValue<int>() == 503);
        Assert.Equal(1, failed["attempts"]!.GetValue<int>());
        Assert.True(failed["next_attempt_at"]!.GetValue<DateTimeOffset>() > DateTimeOffset.UtcNow.AddSeconds(30), "nova tentativa deveria seguir a agenda (≥ 60 s)");

        // O cliente corrige o receptor e pede o retry manual: a entrega sai na hora, assinada com o segredo do webhook.
        receiver.Status = 200;
        var deliveryId = failed["id"]!.GetValue<Guid>();
        Assert.Equal(HttpStatusCode.Accepted, (await ApiAsync(HttpMethod.Post, $"/v1/webhooks/deliveries/{deliveryId}/retry")).Status);
        await WaitDeliveryAsync(id, d => d["id"]!.GetValue<Guid>() == deliveryId && d["status"]!.GetValue<string>() == "DELIVERED");
        var ok = receiver.Received.Last(r => r.Answered == 200);
        Assert.True(WebhookSignature.Verify(ok.Signature, ok.Body, secret, TimeSpan.FromMinutes(5), DateTimeOffset.UtcNow));

        // Já entregue: não reenvia.
        Assert.Equal(HttpStatusCode.NotFound, (await ApiAsync(HttpMethod.Post, $"/v1/webhooks/deliveries/{deliveryId}/retry")).Status);
        await ApiAsync(HttpMethod.Delete, $"/v1/webhooks/{id}");
    }

    [Fact]
    public async Task Destino_que_responde_410_desativa_o_webhook_e_vai_para_a_fila_morta()
    {
        await using var receiver = new FlakyReceiver(env.WebhookTls) { Status = 410 };
        var hook = (await ApiAsync(HttpMethod.Post, "/v1/webhooks", new JsonObject { ["url"] = receiver.Url, ["eventos"] = new JsonArray("document.audited") })).Body!;
        var id = hook["id"]!.GetValue<Guid>();

        await AuditAsync(730_101);
        await WaitDeliveryAsync(id, d => d["status"]!.GetValue<string>() == "DEAD");
        var dead = (await ApiAsync(HttpMethod.Get, "/v1/webhooks/deliveries?status=DEAD")).Body!.AsArray();
        Assert.Contains(dead, d => d!["webhook_id"]!.GetValue<Guid>() == id);
        var hooks = (await ApiAsync(HttpMethod.Get, "/v1/webhooks")).Body!;
        var mine = (hooks is JsonArray arr ? arr : hooks["webhooks"]!.AsArray()).First(w => w!["id"]!.GetValue<Guid>() == id)!;
        Assert.False(mine["active"]?.GetValue<bool>() ?? mine["ativo"]!.GetValue<bool>());
        await ApiAsync(HttpMethod.Delete, $"/v1/webhooks/{id}");
    }
}
