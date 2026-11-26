using System.Net;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace TaxGateway.Infrastructure;

/// <summary>
/// Sondas dos workers (D-P25; revisão de arquitetura 18). Os workers não são um servidor web; sem probe, um processo
/// travado (pool de threads esgotado, laço morto) seguia "Running" para sempre. Este listener mínimo responde
/// <c>/health/live</c> (o processo atende) na porta <c>Workers:HealthPort</c>; 0 = desligado (padrão local e testes).
/// </summary>
public sealed partial class WorkerHealthServer(int port, ILogger<WorkerHealthServer> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://+:{port}/");
        listener.Start();
        LogListening(log, port);
        using var reg = stoppingToken.Register(listener.Stop);
        while (!stoppingToken.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
            {
                break;
            }

            var ok = ctx.Request.Url?.AbsolutePath is "/health/live" or "/health/ready";
            ctx.Response.StatusCode = ok ? 200 : 404;
            ctx.Response.ContentType = "text/plain";
            var body = ok ? "Healthy"u8.ToArray() : [];
            await ctx.Response.OutputStream.WriteAsync(body, stoppingToken).ConfigureAwait(false);
            ctx.Response.Close();
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Sonda dos workers em :{Port}/health/live.")]
    private static partial void LogListening(ILogger logger, int port);
}
