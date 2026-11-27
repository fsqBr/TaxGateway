using Microsoft.Extensions.Caching.Memory;

using TaxGateway.Core.Params;
using TaxGateway.Core.Tenancy;
using TaxGateway.Infrastructure.Persistence;

namespace TaxGateway.Infrastructure.Params;

/// <summary>Exceção do contribuinte ATIVA (liminar, regime especial, benefício), D-R20.</summary>
public sealed record TenantRateExemption(
    Guid Id, string? Cnpj, Tributo Tributo, string? EnteCodigo, string? Produto, decimal Aliquota,
    DateOnly ValidFrom, DateOnly? ValidTo, string Motivo, string Processo)
{
    /// <summary>
    /// A exceção vale para o item? CNPJ (ou todos), vigência, ente (UF = 2 primeiros dígitos do município; município
    /// exato) e produto (cClassTrib exato ou prefixo de NCM).
    /// </summary>
    public bool Matches(string cnpj, int municipio, DateOnly data, string? ncm, string? cClassTrib)
    {
        if (Cnpj is not null && !string.Equals(Cnpj, cnpj, StringComparison.Ordinal))
        {
            return false;
        }

        if (data < ValidFrom || (ValidTo is { } to && data >= to))
        {
            return false;
        }

        if (EnteCodigo is { Length: 2 } uf && uf != (municipio / 100000).ToString("D2", System.Globalization.CultureInfo.InvariantCulture))
        {
            return false;
        }

        if (EnteCodigo is { Length: 7 } mun && mun != municipio.ToString("D7", System.Globalization.CultureInfo.InvariantCulture))
        {
            return false;
        }

        return Produto switch
        {
            null => true,
            _ when Produto.StartsWith("CCLASS:", StringComparison.Ordinal) => string.Equals(Produto[7..], cClassTrib, StringComparison.Ordinal),
            _ => (ncm ?? "").StartsWith(Produto[4..], StringComparison.Ordinal),
        };
    }
}

/// <summary>Exceções ativas do tenant (cache de 30 s; a aprovação invalida).</summary>
public sealed class RateExceptionService(ITenantDb db, IMemoryCache cache)
{
    public async Task<IReadOnlyList<TenantRateExemption>> ActiveAsync(TenantId tenant, CancellationToken ct)
    {
        var key = $"rate-exceptions:{tenant}";
        if (cache.TryGetValue(key, out IReadOnlyList<TenantRateExemption>? hit) && hit is not null)
        {
            return hit;
        }

        var list = await db.InTenantAsync<IReadOnlyList<TenantRateExemption>>(tenant, async (s, c) =>
        {
            await using var cmd = s.CreateCommand("""
                SELECT id, cnpj, tributo, ente_codigo, produto, aliquota, valid_from, valid_to, motivo, processo
                  FROM tenant_rate_exceptions WHERE status = 'ATIVA'
                """);
            await using var r = await cmd.ExecuteReaderAsync(c).ConfigureAwait(false);
            var rows = new List<TenantRateExemption>();
            while (await r.ReadAsync(c).ConfigureAwait(false))
            {
                rows.Add(new TenantRateExemption(r.GetGuid(0), r.IsDBNull(1) ? null : r.GetString(1), ParseTributo(r.GetString(2)),
                    r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4), r.GetDecimal(5),
                    r.GetFieldValue<DateOnly>(6), r.IsDBNull(7) ? null : r.GetFieldValue<DateOnly>(7), r.GetString(8), r.GetString(9)));
            }

            return rows;
        }, ct).ConfigureAwait(false);
        cache.Set(key, list, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(30), Size = list.Count + 1 });
        return list;
    }

    public void Invalidate(TenantId tenant) => cache.Remove($"rate-exceptions:{tenant}");

    public static Tributo ParseTributo(string s) => s switch
    {
        "CBS" => Tributo.Cbs,
        "IBS_UF" => Tributo.IbsUf,
        "IBS_MUN" => Tributo.IbsMun,
        "IS" => Tributo.Is,
        _ => throw new ArgumentOutOfRangeException(nameof(s), s, "Tributo desconhecido"),
    };
}
