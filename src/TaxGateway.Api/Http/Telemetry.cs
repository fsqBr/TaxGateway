using Microsoft.Extensions.Logging.Console;

using Npgsql;

using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace TaxGateway.Api.Http;

/// <summary>
/// Observabilidade (D-P17; revisão de arquitetura 10). Métricas: as nativas (ASP.NET, Kestrel, HttpClient — chamadas ao
/// motor —, runtime, Npgsql) MAIS as próprias do medidor "TaxGateway" (descarte da fila de gravação = subcobrança, 429,
/// modo local do rate limit, cache de simulação, lotes) — antes registradas mas nunca exportadas. Tracing: ASP.NET,
/// HttpClient (motor, KMS, S3, Stripe) e Npgsql por OTLP quando <c>Telemetry:OtlpEndpoint</c> (ou OTEL_EXPORTER_OTLP_ENDPOINT)
/// está configurado. Logs: JSON com trace_id/span_id em produção (<c>Logging:Json</c>), correlacionáveis com os traces.
/// </summary>
public static class Telemetry
{
    public const string MeterName = "TaxGateway";

    public static void AddTaxGatewayTelemetry(this IServiceCollection services, IConfiguration config, ILoggingBuilder logging, string serviceName, bool prometheus)
    {
        var otlp = config["Telemetry:OtlpEndpoint"] ?? config["OTEL_EXPORTER_OTLP_ENDPOINT"];
        var builder = services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(serviceName, serviceVersion: AppVersion.Version))
            .WithMetrics(m =>
            {
                m.AddMeter("Microsoft.AspNetCore.Hosting", "Microsoft.AspNetCore.Server.Kestrel", "System.Net.Http", "System.Runtime", "Npgsql", MeterName)
                    .AddView("http.server.request.duration", new ExplicitBucketHistogramConfiguration
                    {
                        // Baldes alinhados aos SLOs do §16.3 (5, 15, 40, 100, 150 ms...).
                        Boundaries = [0.002, 0.005, 0.01, 0.015, 0.025, 0.04, 0.06, 0.1, 0.15, 0.25, 0.5, 1, 2.5, 5, 10],
                    });
                if (prometheus)
                {
                    m.AddPrometheusExporter();
                }

                if (!string.IsNullOrEmpty(otlp))
                {
                    m.AddOtlpExporter(o => o.Endpoint = new Uri(otlp));
                }
            });

        if (!string.IsNullOrEmpty(otlp))
        {
            builder.WithTracing(t => t
                .AddAspNetCoreInstrumentation(o => o.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/health") && !ctx.Request.Path.StartsWithSegments("/metrics"))
                .AddHttpClientInstrumentation()
                .AddNpgsql()
                .AddOtlpExporter(o => o.Endpoint = new Uri(otlp)));
        }

        if (config.GetValue("Logging:Json", false))
        {
            logging.AddJsonConsole(o =>
            {
                o.IncludeScopes = true;
                o.UseUtcTimestamp = true;
                o.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
            });
            logging.Configure(o => o.ActivityTrackingOptions = ActivityTrackingOptions.TraceId | ActivityTrackingOptions.SpanId);
        }
    }
}
