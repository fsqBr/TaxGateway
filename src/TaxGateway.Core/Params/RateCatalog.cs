namespace TaxGateway.Core.Params;

public enum Tributo { Cbs, IbsUf, IbsMun, Is }

public enum FormaAplicacao { Substituicao, Acrescimo, Decrescimo }

/// <summary>Linha ativa do catálogo (tabela <c>rate_catalog</c>). Alíquota em %.</summary>
public sealed record RateRow(
    Guid Id,
    Tributo Tributo,
    string? EnteCodigo,                 // null = referência nacional; 2 dígitos = UF; 7 dígitos = município
    FormaAplicacao? Forma,
    decimal Aliquota,
    DateOnly ValidFrom,
    DateOnly? ValidTo,                  // exclusivo
    string AtoLegal,
    string Fonte,
    bool Provisoria);

/// <summary>Alíquota resolvida para (tributo, ente, data), com rastreabilidade (SDD §8.5).</summary>
public sealed record ResolvedRate(decimal Aliquota, RateRow Referencia, RateRow? Propria)
{
    public bool Provisoria => Referencia.Provisoria || (Propria?.Provisoria ?? false);
    public Guid CatalogId => (Propria ?? Referencia).Id;
}

/// <summary>
/// Catálogo de alíquotas em memória (SDD §8.5/§8.6, §16.2 item 3): referência por tributo e vigência,
/// ajustada pela alíquota própria do ente (SUBSTITUICAO, ACRESCIMO ou DECRESCIMO), como no motor
/// (<c>ALIQUOTA_REFERENCIA</c>/<c>ALIQUOTA_PADRAO</c>). Imutável: uma mudança gera um catálogo novo.
/// </summary>
public sealed class RateCatalog
{
    private readonly Dictionary<(Tributo, string), RateRow[]> _byKey;

    public RateCatalog(IEnumerable<RateRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        _byKey = rows
            .GroupBy(r => (r.Tributo, r.EnteCodigo ?? ""))
            .ToDictionary(g => g.Key, g => ValidateNoOverlap(g.OrderBy(r => r.ValidFrom).ToArray()));
        Count = _byKey.Values.Sum(v => v.Length);
    }

    public int Count { get; }

    public static RateCatalog Empty { get; } = new([]);

    /// <summary>
    /// Resolve a alíquota de <paramref name="tributo"/> na <paramref name="data"/>.
    /// <paramref name="municipioIbge"/> (7 dígitos) define o ente: IBS_UF usa os 2 primeiros dígitos (código da UF),
    /// IBS_MUN usa o município. Devolve null quando não há referência vigente.
    /// </summary>
    public ResolvedRate? Resolve(Tributo tributo, int municipioIbge, DateOnly data)
    {
        var referencia = Find(tributo, "", data);
        if (referencia is null)
        {
            return null;
        }

        var ente = tributo switch
        {
            Tributo.IbsUf => (municipioIbge / 100000).ToString("D2", System.Globalization.CultureInfo.InvariantCulture),
            Tributo.IbsMun => municipioIbge.ToString("D7", System.Globalization.CultureInfo.InvariantCulture),
            _ => null,
        };
        var propria = ente is null ? null : Find(tributo, ente, data);
        if (propria is null)
        {
            return new ResolvedRate(referencia.Aliquota, referencia, null);
        }

        var valor = propria.Forma switch
        {
            FormaAplicacao.Substituicao => propria.Aliquota,
            FormaAplicacao.Acrescimo => referencia.Aliquota + propria.Aliquota,
            FormaAplicacao.Decrescimo => Math.Max(0m, referencia.Aliquota - propria.Aliquota),
            _ => throw new InvalidOperationException($"Linha própria {propria.Id} sem forma de aplicação."),
        };
        return new ResolvedRate(valor, referencia, propria);
    }

    private RateRow? Find(Tributo tributo, string ente, DateOnly data)
    {
        if (!_byKey.TryGetValue((tributo, ente), out var rows))
        {
            return null;
        }

        // Busca binária pela última vigência que começa até a data.
        int lo = 0, hi = rows.Length - 1, found = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >>> 1;
            if (rows[mid].ValidFrom <= data)
            {
                found = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        if (found < 0)
        {
            return null;
        }

        var row = rows[found];
        return row.ValidTo is null || data < row.ValidTo ? row : null;
    }

    private static RateRow[] ValidateNoOverlap(RateRow[] sorted)
    {
        for (var i = 1; i < sorted.Length; i++)
        {
            var prev = sorted[i - 1];
            if (prev.ValidTo is null || prev.ValidTo > sorted[i].ValidFrom)
            {
                throw new InvalidOperationException(
                    $"Vigências sobrepostas no catálogo: {prev.Id} e {sorted[i].Id} ({prev.Tributo}, ente '{prev.EnteCodigo}').");
            }
        }

        return sorted;
    }
}
