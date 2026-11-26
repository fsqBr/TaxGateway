using System.Security.Cryptography;
using System.Text;

using Amazon.S3;
using Amazon.S3.Model;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using StackExchange.Redis;

using TaxGateway.Infrastructure.Storage;

namespace TaxGateway.Infrastructure.Xml;

/// <summary>
/// LCR compartilhada (D-P23; revisão de arquitetura 8 e segurança B4). Em produção a API não tem saída para a internet
/// (NetworkPolicy) — antes toda nota saía "revogação não verificada" e o resultado variava por pod. Agora os WORKERS
/// baixam as LCRs da ICP-Brasil e as publicam no bucket (<c>crl/</c>); as APIs leem de lá. A assinatura da LCR continua
/// conferida contra o emissor em quem LÊ, então o bucket não precisa ser confiável para isso. A API registra no Valkey
/// as URLs que encontrou nos certificados e que ainda não estão publicadas (<c>crl:wanted</c>).
/// </summary>
public static class CrlShared
{
    public const string WantedSet = "crl:wanted";

    public static string ObjectKey(Uri source) =>
        "crl/" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(source.AbsoluteUri))) + ".crl";

    /// <summary>Busca usada pela API em modo compartilhado: lê o bucket; ausente = pede aos workers e tenta no próximo ciclo.</summary>
    public static Func<Uri, CancellationToken, Task<byte[]>> ReadFromBucket(IAmazonS3 s3, string bucket, IConnectionMultiplexer valkey) =>
        async (source, ct) =>
        {
            try
            {
                using var resp = await s3.GetObjectAsync(bucket, ObjectKey(source), ct).ConfigureAwait(false);
                using var ms = new MemoryStream();
                await resp.ResponseStream.CopyToAsync(ms, ct).ConfigureAwait(false);
                return ms.ToArray();
            }
            catch (Exception ex) when (XmlVault.IsNotFound(ex))
            {
                await valkey.GetDatabase().SetAddAsync(WantedSet, source.AbsoluteUri).ConfigureAwait(false);
                throw new InvalidDataException($"LCR {source} ainda não publicada pelos workers.");
            }
        };
}

/// <summary>Worker que baixa as LCRs pedidas e as publica no bucket, renovando a cada hora (D-P23).</summary>
public sealed partial class CrlPublisher(IHttpClientFactory http, IAmazonS3 s3, Microsoft.Extensions.Options.IOptions<StorageOptions> storage,
    IConnectionMultiplexer valkey, ILogger<CrlPublisher> log) : BackgroundService
{
    private const int MaxCrlBytes = 32 * 1024 * 1024;
    private readonly Dictionary<string, DateTimeOffset> _published = new(StringComparer.Ordinal);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            try
            {
                await RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is RedisException or AmazonS3Exception && !stoppingToken.IsCancellationRequested)
            {
                LogCycleFailed(log, ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    /// <summary>Um ciclo: publica as URLs pedidas ainda não publicadas e renova as publicadas há mais de 1 h.</summary>
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var wanted = await valkey.GetDatabase().SetMembersAsync(CrlShared.WantedSet).ConfigureAwait(false);
        var count = 0;
        foreach (var value in wanted)
        {
            var url = value.ToString();
            if (!Uri.TryCreate(url, UriKind.Absolute, out var source) || source.Scheme is not ("http" or "https")
                || (_published.TryGetValue(url, out var at) && at > DateTimeOffset.UtcNow.AddHours(-1)))
            {
                continue;
            }

            try
            {
                using var resp = await http.CreateClient("crl").GetAsync(source, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();
                if (resp.Content.Headers.ContentLength > MaxCrlBytes)
                {
                    throw new InvalidDataException("LCR acima do limite.");
                }

                var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                await s3.PutObjectAsync(new PutObjectRequest
                {
                    BucketName = storage.Value.Bucket,
                    Key = CrlShared.ObjectKey(source),
                    InputStream = new MemoryStream(bytes),
                    ContentType = "application/pkix-crl",
                    Metadata = { ["source-url"] = Uri.EscapeDataString(url) },
                }, ct).ConfigureAwait(false);
                _published[url] = DateTimeOffset.UtcNow;
                TaxGatewayMetrics.CrlPublished.Add(1);
                count++;
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                LogFetchFailed(log, ex, url);
            }
        }

        return count;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Falha ao baixar a LCR {Url}; nova tentativa no próximo ciclo.")]
    private static partial void LogFetchFailed(ILogger logger, Exception ex, string url);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Ciclo de publicação de LCR falhou.")]
    private static partial void LogCycleFailed(ILogger logger, Exception ex);
}
