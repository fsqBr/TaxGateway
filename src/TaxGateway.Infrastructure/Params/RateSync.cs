using System.Globalization;
using System.Text.Json.Nodes;

using TaxGateway.Core.Params;

namespace TaxGateway.Infrastructure.Params;

/// <summary>Trecho de vigência com um valor (null = a Receita não publicou alíquota para o período). Fim exclusivo.</summary>
public sealed record RateSegment(DateOnly From, DateOnly To, decimal? Value);

/// <summary>Linha a inserir no catálogo pela sincronização (mesmo formato das propostas).</summary>
public sealed record SyncRow(string Tributo, string Tipo, string Esfera, string? EnteCodigo, string? FormaAplicacao, decimal Aliquota,
    DateOnly ValidFrom, DateOnly? ValidTo, string AtoLegal, string Fonte, bool Provisoria);

/// <summary>Resultado do planejamento: o que entra, o que é superado e um resumo legível.</summary>
public sealed record SyncPlan(IReadOnlyList<SyncRow> Rows, IReadOnlyList<Guid> Supersede, IReadOnlyList<string> Changes);

/// <summary>
/// D-R30: sincronização do catálogo com a base oficial da calculadora da Receita. A Receita expõe a alíquota vigente de
/// União, UF e município numa data; aqui a série no tempo é reconstruída com a data EXATA de cada mudança (sondagem
/// mensal + busca binária entre sondagens com valores diferentes) e comparada com o catálogo. Puro (sem E/S): testável.
/// </summary>
public static class RateSyncPlanner
{
    public static readonly DateOnly WindowStart = new(2026, 1, 1);
    public static readonly DateOnly WindowEnd = new(2034, 1, 1);   // fim da transição (2033) + 1

    /// <summary>
    /// Série no tempo a partir de uma função que consulta a alíquota numa data: sondagens no 1º dia de cada mês e, onde
    /// duas sondagens diferem, busca binária pelo primeiro dia com o valor novo. Mudança e reversão dentro do mesmo mês
    /// (sem diferença entre as sondagens) não são vistas — a Receita publica por vigência anual/mensal.
    /// </summary>
    public static async Task<List<RateSegment>> ProbeAsync(Func<DateOnly, Task<decimal?>> at, DateOnly start, DateOnly end, int stepMonths = 1)
    {
        ArgumentNullException.ThrowIfNull(at);
        var cache = new Dictionary<DateOnly, decimal?>();
        async Task<decimal?> Get(DateOnly d)
        {
            if (!cache.TryGetValue(d, out var v))
            {
                cache[d] = v = await at(d).ConfigureAwait(false);
            }

            return v;
        }

        var points = new List<DateOnly>();
        for (var d = start; d < end; d = d.AddMonths(stepMonths))
        {
            points.Add(d);
        }

        var changes = new SortedDictionary<DateOnly, decimal?> { [start] = await Get(start).ConfigureAwait(false) };
        for (var i = 1; i < points.Count; i++)
        {
            await FindChangesAsync(points[i - 1], await Get(points[i - 1]).ConfigureAwait(false), points[i], await Get(points[i]).ConfigureAwait(false), Get, changes)
                .ConfigureAwait(false);
        }

        var keys = changes.Keys.ToList();
        var segments = new List<RateSegment>();
        for (var i = 0; i < keys.Count; i++)
        {
            var to = i + 1 < keys.Count ? keys[i + 1] : end;
            if (segments.Count > 0 && Same(segments[^1].Value, changes[keys[i]]))
            {
                segments[^1] = segments[^1] with { To = to };
            }
            else
            {
                segments.Add(new RateSegment(keys[i], to, changes[keys[i]]));
            }
        }

        return segments;
    }

    private static async Task FindChangesAsync(DateOnly a, decimal? va, DateOnly b, decimal? vb, Func<DateOnly, Task<decimal?>> get, SortedDictionary<DateOnly, decimal?> changes)
    {
        if (Same(va, vb))
        {
            return;
        }

        if (b.DayNumber - a.DayNumber <= 1)
        {
            changes[b] = vb;
            return;
        }

        var mid = DateOnly.FromDayNumber((a.DayNumber + b.DayNumber) / 2);
        var vm = await get(mid).ConfigureAwait(false);
        await FindChangesAsync(a, va, mid, vm, get, changes).ConfigureAwait(false);
        await FindChangesAsync(mid, vm, b, vb, get, changes).ConfigureAwait(false);
    }

    public static bool Same(decimal? a, decimal? b) => a is null ? b is null : b is not null && Math.Abs(a.Value - b.Value) < 0.00005m;

    public static decimal? ValueAt(IReadOnlyList<RateSegment> segments, DateOnly d)
    {
        ArgumentNullException.ThrowIfNull(segments);
        foreach (var s in segments)
        {
            if (d >= s.From && d < s.To)
            {
                return s.Value;
            }
        }

        return null;
    }

    /// <summary>Referência = valor mais comum entre os entes em cada trecho (a maioria dos entes usa a referência).</summary>
    public static List<RateSegment> Mode(IReadOnlyCollection<IReadOnlyList<RateSegment>> series, DateOnly start, DateOnly end)
    {
        ArgumentNullException.ThrowIfNull(series);
        var cuts = series.SelectMany(s => s.SelectMany(x => new[] { x.From, x.To })).Append(start).Append(end)
            .Where(d => d >= start && d <= end).Distinct().Order().ToList();
        var result = new List<RateSegment>();
        for (var i = 0; i + 1 < cuts.Count; i++)
        {
            var values = series.Select(s => ValueAt(s, cuts[i])).Where(v => v is not null).Select(v => Math.Round(v!.Value, 4)).ToList();
            decimal? mode = values.Count == 0 ? null : values.GroupBy(v => v).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).First().Key;
            if (result.Count > 0 && Same(result[^1].Value, mode))
            {
                result[^1] = result[^1] with { To = cuts[i + 1] };
            }
            else
            {
                result.Add(new RateSegment(cuts[i], cuts[i + 1], mode));
            }
        }

        return result;
    }

    /// <summary>
    /// Compara a base da Receita com o catálogo e devolve só o que mudou. Onde a Receita NÃO publicou (valor null), o
    /// catálogo fica como está (ex.: a CBS provisória de 2027 cadastrada pela equipe até a Receita publicar).
    /// </summary>
    public static SyncPlan Plan(IReadOnlyList<RateSegment> uniao, IReadOnlyDictionary<string, IReadOnlyList<RateSegment>> ufs,
        IReadOnlyDictionary<string, IReadOnlyList<RateSegment>> municipios, IReadOnlyCollection<RateRow> catalog, string bundle)
    {
        ArgumentNullException.ThrowIfNull(uniao);
        ArgumentNullException.ThrowIfNull(ufs);
        ArgumentNullException.ThrowIfNull(municipios);
        ArgumentNullException.ThrowIfNull(catalog);
        var ato = $"Base oficial da calculadora da Receita Federal (bundle {bundle})";
        var rows = new List<SyncRow>();
        var supersede = new List<Guid>();
        var changes = new List<string>();

        void Key(Tributo tributo, string tipo, string esfera, string? ente, IReadOnlyList<RateSegment> desired, IReadOnlyList<RateSegment>? reference,
            Func<RateRow, bool> isKey)
        {
            var existing = catalog.Where(isKey).OrderBy(r => r.ValidFrom).ToList();
            var covered = desired.Where(s => s.Value is not null).ToList();
            if (covered.Count == 0)
            {
                return;
            }

            // Valor do catálogo e da Receita em cada trecho coberto pela Receita.
            var cuts = covered.SelectMany(s => new[] { s.From, s.To })
                .Concat(existing.SelectMany(r => new[] { r.ValidFrom, r.ValidTo ?? WindowEnd }))
                .Concat(reference?.SelectMany(s => new[] { s.From, s.To }) ?? [])
                .Where(d => d >= WindowStart && d <= WindowEnd).Distinct().Order().ToList();
            var changedAt = new List<DateOnly>();
            for (var i = 0; i + 1 < cuts.Count; i++)
            {
                var d = cuts[i];
                var want = ValueAt(covered, d);
                if (want is null)
                {
                    continue;   // a Receita não publicou neste trecho: mantém o catálogo
                }

                var row = existing.FirstOrDefault(r => d >= r.ValidFrom && (r.ValidTo is null || d < r.ValidTo));
                decimal? have = reference is null
                    ? row?.Aliquota
                    : row is null ? ValueAt(reference, d) : Apply(row, ValueAt(reference, d));
                if (!Same(want, have))
                {
                    changedAt.Add(d);
                }
            }

            var differs = changedAt.Count > 0;

            if (!differs)
            {
                return;
            }

            // Supera as linhas que cruzam os trechos publicados e devolve ao catálogo as partes fora deles.
            foreach (var r in existing)
            {
                var rTo = r.ValidTo ?? WindowEnd;
                if (!covered.Any(s => s.From < rTo && r.ValidFrom < s.To))
                {
                    continue;
                }

                supersede.Add(r.Id);
                var cursor = r.ValidFrom;
                foreach (var s in covered.Where(s => s.From < rTo && r.ValidFrom < s.To).OrderBy(s => s.From))
                {
                    if (cursor < s.From)
                    {
                        rows.Add(Clip(r, cursor, s.From));
                    }

                    cursor = s.To > cursor ? s.To : cursor;
                }

                if (cursor < rTo)
                {
                    rows.Add(Clip(r, cursor, r.ValidTo is null ? null : rTo));
                }
            }

            foreach (var s in covered)
            {
                var own = reference is not null;
                if (own && Same(s.Value, ValueAt(reference!, s.From)) && Same(s.Value, ValueAt(reference!, s.To.AddDays(-1))))
                {
                    continue;   // igual à referência: o ente não precisa de alíquota própria
                }

                rows.Add(new SyncRow(Code(tributo), tipo, esfera, ente, own ? "SUBSTITUICAO" : null, s.Value!.Value, s.From,
                    s.To >= WindowEnd ? null : s.To, ato, "BUNDLE_RFB", false));
            }

            changes.Add($"{Code(tributo)}{(ente is null ? "" : " " + ente)}: " +
                string.Join(", ", covered.Where(s => changedAt.Any(d => d >= s.From && d < s.To)).Select(s => $"{s.Value!.Value.ToString("0.####", CultureInfo.InvariantCulture)}% a partir de {s.From:dd/MM/yyyy}")));
        }

        SyncRow Clip(RateRow r, DateOnly from, DateOnly? to) => new(Code(r.Tributo), r.EnteCodigo is null ? "REFERENCIA" : "PROPRIA_ENTE",
            Esfera(r.Tributo), r.EnteCodigo, r.Forma?.ToString().ToUpperInvariant(), r.Aliquota, from, to, r.AtoLegal, r.Fonte, r.Provisoria);

        Key(Tributo.Cbs, "REFERENCIA", "UNIAO", null, uniao, null, r => r.Tributo == Tributo.Cbs && r.EnteCodigo is null && r.Produto is null);
        foreach (var (tributo, series) in new[] { (Tributo.IbsUf, ufs), (Tributo.IbsMun, municipios) })
        {
            if (series.Count == 0)
            {
                continue;
            }

            var mode = Mode([.. series.Values], WindowStart, WindowEnd);
            Key(tributo, "REFERENCIA", Esfera(tributo), null, mode, null, r => r.Tributo == tributo && r.EnteCodigo is null && r.Produto is null);
            foreach (var (ente, s) in series)
            {
                Key(tributo, "PROPRIA_ENTE", Esfera(tributo), ente, s, mode, r => r.Tributo == tributo && r.EnteCodigo == ente);
            }
        }

        return new SyncPlan(rows, supersede.Distinct().ToList(), changes);
    }

    private static decimal? Apply(RateRow own, decimal? reference) => own.Forma switch
    {
        FormaAplicacao.Acrescimo => reference + own.Aliquota,
        FormaAplicacao.Decrescimo => reference is null ? null : Math.Max(0, reference.Value - own.Aliquota),
        _ => own.Aliquota,
    };

    private static string Code(Tributo t) => t switch { Tributo.Cbs => "CBS", Tributo.IbsUf => "IBS_UF", Tributo.IbsMun => "IBS_MUN", _ => "IS" };

    private static string Esfera(Tributo t) => t switch { Tributo.IbsUf => "UF", Tributo.IbsMun => "MUNICIPIO", _ => "UNIAO" };

    /// <summary>Linhas no formato JSON da função app.apply_rate_sync.</summary>
    public static string ToJson(IEnumerable<SyncRow> rows) => new JsonArray([.. rows.Select(r => (JsonNode)new JsonObject
    {
        ["tributo"] = r.Tributo, ["tipo"] = r.Tipo, ["esfera"] = r.Esfera, ["ente_codigo"] = r.EnteCodigo, ["forma_aplicacao"] = r.FormaAplicacao,
        ["aliquota"] = r.Aliquota, ["valid_from"] = r.ValidFrom.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        ["valid_to"] = r.ValidTo?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), ["ato_legal"] = r.AtoLegal, ["fonte"] = r.Fonte, ["provisoria"] = r.Provisoria,
    })]).ToJsonString();
}

/// <summary>Onde a sincronização lê a alíquota publicada (o motor oficial, ou um falso nos testes).</summary>
public interface IOfficialRateSource
{
    Task<decimal?> UniaoAsync(DateOnly onDate, CancellationToken ct);

    Task<decimal?> UfAsync(int codigoUf, DateOnly onDate, CancellationToken ct);

    Task<decimal?> MunicipioAsync(int codigoMunicipio, DateOnly onDate, CancellationToken ct);
}

/// <summary>Dados abertos da calculadora da Receita (<c>calculadora/dados-abertos/aliquota-*</c>); 404 = não publicada.</summary>
public sealed class EngineRateSource(IHttpClientFactory httpFactory) : IOfficialRateSource
{
    public Task<decimal?> UniaoAsync(DateOnly onDate, CancellationToken ct) => GetAsync($"aliquota-uniao?data={D(onDate)}", ct);

    public Task<decimal?> UfAsync(int codigoUf, DateOnly onDate, CancellationToken ct) =>
        GetAsync($"aliquota-uf?codigoUf={codigoUf.ToString(CultureInfo.InvariantCulture)}&data={D(onDate)}", ct);

    public Task<decimal?> MunicipioAsync(int codigoMunicipio, DateOnly onDate, CancellationToken ct) =>
        GetAsync($"aliquota-municipio?codigoMunicipio={codigoMunicipio.ToString(CultureInfo.InvariantCulture)}&data={D(onDate)}", ct);

    private static string D(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private async Task<decimal?> GetAsync(string path, CancellationToken ct)
    {
        var http = httpFactory.CreateClient(TaxGateway.Infrastructure.Engines.RfbReferenceData.Client);
        using var res = await http.GetAsync(new Uri("calculadora/dados-abertos/" + path, UriKind.Relative), ct).ConfigureAwait(false);
        if (res.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        res.EnsureSuccessStatusCode();
        return Parse(await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
    }

    /// <summary>Primeiro número do objeto (ex.: <c>{"aliquotaReferencia":0.9}</c>) ou o próprio número.</summary>
    public static decimal? Parse(string json)
    {
        var node = JsonNode.Parse(json);
        if (node is JsonValue v && v.TryGetValue<decimal>(out var d))
        {
            return d;
        }

        foreach (var (_, value) in node as JsonObject ?? [])
        {
            if (value is JsonValue jv && jv.TryGetValue<decimal>(out var x))
            {
                return x;
            }
        }

        return null;
    }
}
