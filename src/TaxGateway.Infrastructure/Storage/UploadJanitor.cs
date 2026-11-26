using Amazon.S3;
using Amazon.S3.Model;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TaxGateway.Infrastructure.Storage;

/// <summary>
/// Limpeza dos uploads do lote no modo 2 (D-P24; revisão de segurança B3). O ZIP enviado pela URL pré-assinada fica em
/// <c>uploads/{tenant}/</c> sem cifra própria até a ingestão; se o cliente nunca cria o lote (ou a ingestão cai), o
/// objeto ficava para sempre, fora do expurgo LGPD. Este worker apaga o que passou de 24 h — reforço para a regra de
/// lifecycle do bucket, que nem todo S3 compatível aplica.
/// </summary>
public sealed partial class UploadJanitor(IAmazonS3 s3, IOptions<StorageOptions> options, TimeProvider clock, ILogger<UploadJanitor> log) : BackgroundService
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            try
            {
                var removed = await RunOnceAsync(stoppingToken).ConfigureAwait(false);
                if (removed > 0)
                {
                    LogRemoved(log, removed);
                }
            }
            catch (AmazonS3Exception ex)
            {
                LogFailed(log, ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    /// <returns>Quantos uploads vencidos foram apagados.</returns>
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var cutoff = clock.GetUtcNow() - MaxAge;
        var removed = 0;
        string? token = null;
        do
        {
            var page = await s3.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = options.Value.Bucket,
                Prefix = XmlVault.UploadPrefix,
                ContinuationToken = token,
            }, ct).ConfigureAwait(false);
            foreach (var o in page.S3Objects ?? [])
            {
                if (o.LastModified is { } modified && new DateTimeOffset(modified.ToUniversalTime(), TimeSpan.Zero) < cutoff)
                {
                    await s3.DeleteObjectAsync(options.Value.Bucket, o.Key, ct).ConfigureAwait(false);
                    removed++;
                }
            }

            token = page.IsTruncated == true ? page.NextContinuationToken : null;
        }
        while (token is not null);

        return removed;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Uploads de lote vencidos apagados: {Count}.")]
    private static partial void LogRemoved(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Limpeza de uploads falhou; nova tentativa em 1 h.")]
    private static partial void LogFailed(ILogger logger, Exception ex);
}
