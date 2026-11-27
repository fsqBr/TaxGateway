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
    bool Provisoria,
    string? Produto = null);            // só IS (D-R17): "NCM:<prefixo>" ou "CCLASS:<cClassTrib>"

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
    private readonly Dictionary<(Tributo, string, string), RateRow[]> _byKey;

    public RateCatalog(IEnumerable<RateRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        _byKey = rows
            .GroupBy(r => (r.Tributo, r.EnteCodigo ?? "", r.Produto ?? ""))
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
        var referencia = Find(tributo, "", "", data);
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
        var propria = ente is null ? null : Find(tributo, ente, "", data);
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

    /// <summary>
    /// Imposto Seletivo do produto na data (D-R17): primeiro pelo cClassTrib exato, depois pelo prefixo de NCM mais
    /// longo (8 a 2 dígitos). null = o catálogo não tem IS para o produto (a auditoria avisa que não conferiu).
    /// </summary>
    public RateRow? ResolveIs(string? ncm, string? cClassTrib, DateOnly data)
    {
        if (!string.IsNullOrEmpty(cClassTrib) && Find(Tributo.Is, "", $"CCLASS:{cClassTrib}", data) is { } byClass)
        {
            return byClass;
        }

        var digits = new string((ncm ?? "").Where(char.IsAsciiDigit).ToArray());
        for (var len = Math.Min(8, digits.Length); len >= 2; len--)
        {
            if (Find(Tributo.Is, "", $"NCM:{digits[..len]}", data) is { } byNcm)
            {
                return byNcm;
            }
        }

        return null;
    }

    /// <summary>
    /// D-R22: até quando a referência nacional do tributo está coberta sem buraco a partir de <paramref name="from"/>
    /// (data exclusiva do fim). null = sem alíquota já em <paramref name="from"/>; <see cref="DateOnly.MaxValue"/> = sem fim.
    /// </summary>
    public DateOnly? CoveredUntil(Tributo tributo, DateOnly from)
    {
        var day = from;
        for (var guard = 0; guard < 1000; guard++)
        {
            var row = Find(tributo, "", "", day);
            if (row is null)
            {
                return day == from ? null : day;
            }

            if (row.ValidTo is not { } to)
            {
                return DateOnly.MaxValue;
            }

            day = to;
        }

        return day;
    }

    /// <summary>Linhas vigentes na data, para a consulta pública de alíquotas (D-R21).</summary>
    public IEnumerable<RateRow> ActiveOn(DateOnly data) =>
        _byKey.Values.Select(rows => rows.LastOrDefault(r => r.ValidFrom <= data && (r.ValidTo is null || data < r.ValidTo))).OfType<RateRow>();

    /// <summary>Todas as linhas (histórico vigente e futuro), para a tabela pública.</summary>
    public IEnumerable<RateRow> All => _byKey.Values.SelectMany(v => v);

    private RateRow? Find(Tributo tributo, string ente, string produto, DateOnly data)
    {
        if (!_byKey.TryGetValue((tributo, ente, produto), out var rows))
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
