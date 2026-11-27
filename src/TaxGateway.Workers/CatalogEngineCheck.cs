using TaxGateway.Core.Engines;
using TaxGateway.Core.Params;
using TaxGateway.Infrastructure;

namespace TaxGateway.Workers;

/// <summary>
/// D-R22 (SDD §8.5 item 2): confere, a cada 6 h, o catálogo do TaxGateway contra a base de alíquotas do próprio motor
/// oficial nas datas em que o motor ainda decide a alíquota (antes de <c>AliquotasNominaisDesde</c>, hoje 2026): uma
/// operação de referência sem alíquotas nominais devolve a CBS e o IBS que o motor aplica; diferença do catálogo vigente
/// na mesma data vira métrica (<c>taxgateway_catalogo_divergente_motor</c>) e alerta — sinal de bundle novo com valores
/// que o catálogo ainda não acompanha (ou o contrário).
/// </summary>
public sealed partial class CatalogEngineCheck(IRfbTaxEngineAdapter engine, IParamSnapshotProvider snapshots, TimeProvider clock,
    Microsoft.Extensions.Options.IOptions<TaxGateway.Infrastructure.Engines.EngineOptions> options, ILogger<CatalogEngineCheck> log) : BackgroundService
{
    private const int Municipio = 3550308;   // São Paulo/SP: referência estável

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(6));
        do
        {
            try
            {
                await RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogError(log, ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    /// <summary>Lista de divergências (vazia = catálogo e motor concordam); null quando não há data a conferir.</summary>
    public async Task<IReadOnlyList<string>?> RunOnceAsync(CancellationToken ct)
    {
        var limit = options.Value.Regular.AliquotasNominaisDesde;
        var today = DateOnly.FromDateTime(clock.GetUtcNow().AddHours(-3).DateTime);
        var day = today < limit ? today : limit.AddDays(-1);
        if (day.Year < 2026)
        {
            return null;
        }

        var op = new EngineOperation("conferencia", new DateTimeOffset(day.ToDateTime(new TimeOnly(12, 0)), TimeSpan.FromHours(-3)), Municipio, "SP", null, null,
            [new EngineItemInput(1, "84713012", null, "000", "000001", 1000m, 1, "UN", null, null)]);
        var item = (await engine.CalculateAsync(op, ct).ConfigureAwait(false)).Itens[0];
        var rates = snapshots.Current.Rates;
        var problems = new List<string>();
        void Compare(string name, Tributo t, TaxLine? line)
        {
            var cat = rates.Resolve(t, Municipio, day)?.Aliquota;
            if (line is not null && cat is not null && Math.Abs(line.Aliquota - cat.Value) > 0.00005m)
            {
                problems.Add($"{name} em {day:yyyy-MM-dd}: motor {line.Aliquota:0.####}% × catálogo {cat:0.####}%");
            }
        }

        Compare("CBS", Tributo.Cbs, item.Cbs);
        Compare("IBS_UF", Tributo.IbsUf, item.IbsUf);
        Compare("IBS_MUN", Tributo.IbsMun, item.IbsMun);
        TaxGatewayMetrics.CatalogEngineDivergences = problems.Count;
        foreach (var p in problems)
        {
            LogDivergence(log, p);
        }

        return problems;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Catálogo de alíquotas diverge do motor oficial: {Detalhe}")]
    private static partial void LogDivergence(ILogger logger, string detalhe);

    [LoggerMessage(Level = LogLevel.Error, Message = "Falha na conferência catálogo × motor.")]
    private static partial void LogError(ILogger logger, Exception ex);
}
