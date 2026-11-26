using System.Diagnostics.Metrics;

namespace TaxGateway.Infrastructure;

/// <summary>
/// Medidores próprios (D-P17), exportados pelo medidor "TaxGateway". Os nomes seguem o padrão já usado
/// (taxgateway_*_total) para os painéis e alertas de docs/slo.md.
/// </summary>
public static class TaxGatewayMetrics
{
    public static readonly Meter Meter = new("TaxGateway");

    /// <summary>Cache de simulação por camada: l1 (memória), l2 (Valkey) ou miss — a "principal alavanca de custo" do §16.3.1.</summary>
    public static readonly Counter<long> SimulationCache = Meter.CreateCounter<long>("taxgateway_simulation_cache_total");

    /// <summary>Chamadas recusadas por franquia esgotada (planos de franquia rígida).</summary>
    public static readonly Counter<long> QuotaExhausted = Meter.CreateCounter<long>("taxgateway_quota_exhausted_total");

    /// <summary>Chunks de lote por resultado: done, retry (falhou, volta à fila), fenced (posse perdida), failed (esgotou tentativas).</summary>
    public static readonly Counter<long> BatchChunks = Meter.CreateCounter<long>("taxgateway_batch_chunks_total");

    /// <summary>Eventos da Stripe processados por tipo e resultado.</summary>
    public static readonly Counter<long> StripeEvents = Meter.CreateCounter<long>("taxgateway_stripe_events_total");

    /// <summary>Entradas de cache com selo inválido (possível adulteração do Valkey, D-P10).</summary>
    public static readonly Counter<long> CacheTampered = Meter.CreateCounter<long>("taxgateway_cache_tampered_total");

    /// <summary>LCRs baixadas e publicadas no bucket pelos workers (D-P23).</summary>
    public static readonly Counter<long> CrlPublished = Meter.CreateCounter<long>("taxgateway_crl_published_total");

    /// <summary>Idade (s) da LCR mais antiga no cache deste processo; o alerta dispara acima de 36 h (D-P23).</summary>
    public static Func<double?>? CrlMaxAge { get; set; }

    /// <summary>Medidor observável da idade da LCR.</summary>
    public static readonly ObservableGauge<double> CrlAgeGauge = Meter.CreateObservableGauge(
        "taxgateway_crl_max_age_seconds", () => CrlMaxAge?.Invoke() is double v ? [new Measurement<double>(v)] : Array.Empty<Measurement<double>>());
}
