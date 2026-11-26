using System.Net;
using System.Numerics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Amazon.S3;
using Amazon.S3.Model;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

using StackExchange.Redis;

using TaxGateway.Infrastructure.Storage;
using TaxGateway.Infrastructure.Xml;
using TaxGateway.TestKit;

namespace TaxGateway.GoldenTests;

/// <summary>Itens P1 de plataforma contra a pilha real (S3, Valkey, workers): LCR compartilhada e uploads vencidos.</summary>
[Collection(GoldenCollection.Name)]
public sealed class PlatformP1Tests(GoldenFixture env)
{
    private static readonly string[] TempScopes = ["keys:manage", "simulacao:write"];
    private static readonly string[] SimScope = ["simulacao:write"];
    private static readonly TestPki Pki = TestPki.LoadOrCreate(Path.Combine(Path.GetTempPath(), "taxgateway-test-pki"));

    /// <summary>
    /// D-P23: a API (sem internet) pede a LCR pelo Valkey; o worker baixa da origem e publica no bucket; a API passa a
    /// ler de lá — e confere a assinatura da LCR contra o emissor, então uma LCR adulterada no bucket é recusada.
    /// </summary>
    [Fact]
    public async Task Crl_requested_by_the_api_is_published_by_the_workers_and_verified_on_read()
    {
        var crl = new CertificateRevocationListBuilder().Build(Pki.Root, BigInteger.One, DateTimeOffset.UtcNow.AddDays(7),
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var origin = new HttpListener();
        var port = FreePort();
        origin.Prefixes.Add($"http://127.0.0.1:{port}/");
        origin.Start();
        var serve = Task.Run(async () =>
        {
            var ctx = await origin.GetContextAsync();
            await ctx.Response.OutputStream.WriteAsync(crl);
            ctx.Response.Close();
        });
        var url = new Uri($"http://127.0.0.1:{port}/ac-{Guid.NewGuid():N}.crl");

        var s3 = env.Services.GetRequiredService<IAmazonS3>();
        var bucket = env.Services.GetRequiredService<IOptions<StorageOptions>>().Value.Bucket;
        var valkey = env.Services.GetRequiredService<IConnectionMultiplexer>();
        var read = CrlShared.ReadFromBucket(s3, bucket, valkey);

        await Assert.ThrowsAsync<InvalidDataException>(() => read(url, CancellationToken.None));
        Assert.True(await valkey.GetDatabase().SetContainsAsync(CrlShared.WantedSet, url.AbsoluteUri));

        var publisher = env.WorkerServices.GetRequiredService<CrlPublisher>();
        Assert.True(await publisher.RunOnceAsync(CancellationToken.None) >= 1);
        await serve;

        var cache = new CrlCache(read, NullLogger<CrlCache>.Instance);
        var parsed = cache.Load(url, await read(url, CancellationToken.None), Pki.Root);
        Assert.Empty(parsed.Revoked);

        // Adulterada no bucket: a assinatura não confere com o emissor.
        var tampered = crl.ToArray();
        tampered[^5] ^= 0xFF;
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = CrlShared.ObjectKey(url), InputStream = new MemoryStream(tampered) });
        var bytes = await read(url, CancellationToken.None);
        Assert.ThrowsAny<CryptographicException>(() => cache.Load(url, bytes, Pki.Root));
        await valkey.GetDatabase().SetRemoveAsync(CrlShared.WantedSet, url.AbsoluteUri);
    }

    /// <summary>D-P24: uploads do modo 2 ficam em uploads/{tenant}/ e o janitor apaga os que passaram de 24 h.</summary>
    [Fact]
    public async Task Upload_url_uses_the_uploads_prefix_and_stale_uploads_are_removed()
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/v1/auditoria/lotes/upload-url");
        req.Headers.Add("X-Api-Key", env.ApiKey);
        using var resp = await env.Api.SendAsync(req);
        var body = System.Text.Json.Nodes.JsonNode.Parse(await resp.Content.ReadAsStringAsync())!;
        var key = body["storage_object_key"]!.GetValue<string>();
        Assert.StartsWith(XmlVault.UploadPrefix, key, StringComparison.Ordinal);

        var s3 = env.Services.GetRequiredService<IAmazonS3>();
        var options = env.Services.GetRequiredService<IOptions<StorageOptions>>();
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = options.Value.Bucket, Key = key, InputStream = new MemoryStream([1, 2, 3]) });
        var other = $"crl/janitor-{Guid.NewGuid():N}.crl";
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = options.Value.Bucket, Key = other, InputStream = new MemoryStream([1]) });

        // Hoje: nada vencido.
        var today = new UploadJanitor(s3, options, TimeProvider.System, NullLogger<UploadJanitor>.Instance);
        await today.RunOnceAsync(CancellationToken.None);
        Assert.True(await ExistsAsync(s3, options.Value.Bucket, key));

        // 25 h depois: o upload vence; o que está fora de uploads/ fica.
        var later = new UploadJanitor(s3, options, new FakeTimeProvider(DateTimeOffset.UtcNow.AddHours(25)), NullLogger<UploadJanitor>.Instance);
        Assert.True(await later.RunOnceAsync(CancellationToken.None) >= 1);
        Assert.False(await ExistsAsync(s3, options.Value.Bucket, key));
        Assert.True(await ExistsAsync(s3, options.Value.Bucket, other));
        await s3.DeleteObjectAsync(options.Value.Bucket, other);
    }

    /// <summary>D-P31 (A5): chave que expira não cria chave eterna nem que viva além dela.</summary>
    [Fact]
    public async Task Expiring_key_cannot_mint_a_longer_lived_key()
    {
        var exp = DateTimeOffset.UtcNow.AddDays(1);
        var temp = await CreateKeyAsync(env.ApiKey, new { nome = "temporaria", scopes = TempScopes, expira_em = exp });
        Assert.Equal(HttpStatusCode.Created, temp.Status);
        var tempKey = temp.Body!["api_key"]!.GetValue<string>();

        Assert.Equal(HttpStatusCode.BadRequest, (await CreateKeyAsync(tempKey, new { nome = "eterna", scopes = SimScope })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await CreateKeyAsync(tempKey, new { nome = "longa", scopes = SimScope, expira_em = exp.AddDays(30) })).Status);
        Assert.Equal(HttpStatusCode.Created, (await CreateKeyAsync(tempKey, new { nome = "curta", scopes = SimScope, expira_em = exp.AddHours(-1) })).Status);
    }

    /// <summary>D-P31 (B2): chaves de operador erradas em sequência viram 429 (antes: 30 tentativas, nenhum bloqueio).</summary>
    [Fact]
    public async Task Repeated_wrong_operator_keys_are_locked_out()
    {
        await using var api = env.NewApiInstance(new Dictionary<string, string?>());
        using var client = api.CreateClient();
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 12; i++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "/admin/v1/tenants");
            req.Headers.Add("X-Admin-Key", new string('x', 40));
            using var resp = await client.SendAsync(req);
            statuses.Add(resp.StatusCode);
        }

        Assert.All(statuses.Take(10), s => Assert.Equal(HttpStatusCode.Unauthorized, s));
        Assert.All(statuses.Skip(10), s => Assert.Equal(HttpStatusCode.TooManyRequests, s));
    }

    private async Task<(HttpStatusCode Status, System.Text.Json.Nodes.JsonNode? Body)> CreateKeyAsync(string apiKey, object body)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/v1/api-keys") { Content = System.Net.Http.Json.JsonContent.Create(body) };
        req.Headers.Add("X-Api-Key", apiKey);
        using var resp = await env.Api.SendAsync(req);
        var text = await resp.Content.ReadAsStringAsync();
        return (resp.StatusCode, text.Length > 0 ? System.Text.Json.Nodes.JsonNode.Parse(text) : null);
    }

    private static async Task<bool> ExistsAsync(IAmazonS3 s3, string bucket, string key)
    {
        try
        {
            await s3.GetObjectMetadataAsync(bucket, key);
            return true;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    private static int FreePort()
    {
        using var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }
}
