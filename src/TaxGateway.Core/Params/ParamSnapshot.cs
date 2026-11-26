namespace TaxGateway.Core.Params;

/// <summary>Limites efetivos de um tenant: plano + <c>limits_override</c> (SDD §5.1).</summary>
public sealed record PlanLimits(string PlanCode, int Rpm, int Burst, int Concurrency, int BatchMaxDocs = 500, long BatchMaxBytes = 15 * 1024 * 1024, string QueuePriority = "normal",
    bool BillSchemaErrors = true)
{
    /// <summary>0 realtime · 1 high · 2 normal · 3 low (fila de lotes, §5.1).</summary>
    public short PriorityRank => QueuePriority.ToLowerInvariant() switch { "realtime" => 0, "high" => 1, "low" => 3, _ => 2 };
}

public sealed record EngineRoute(string Crt, string TpRegimeIbsCbs, IReadOnlyList<string> Engines, string? SimplesMode);

/// <summary>Regra de crédito vigente a partir de uma data (tabela credit_rules, §8.3).</summary>
public sealed record CreditRule(DateOnly ValidFrom, string DefaultStatus);

/// <summary>
/// Fotografia imutável dos parâmetros globais (SDD §15.1, itens 4 e 5). Cada cálculo grava <see cref="Id"/>;
/// o mesmo input com o mesmo bundle e o mesmo snapshot gera sempre o mesmo resultado.
/// Trocada de forma atômica (uma referência) quando um parâmetro muda.
/// </summary>
public sealed class ParamSnapshot(
    long id,
    string contentSha256,
    RateCatalog rates,
    IReadOnlyDictionary<string, PlanLimits> plans,
    IReadOnlyList<EngineRoute> routing,
    DateTimeOffset loadedAt,
    IReadOnlyList<CreditRule>? creditRules = null)
{
    public long Id { get; } = id;
    public string ContentSha256 { get; } = contentSha256;
    public RateCatalog Rates { get; } = rates;
    public IReadOnlyDictionary<string, PlanLimits> Plans { get; } = plans;
    public IReadOnlyList<EngineRoute> Routing { get; } = routing;
    public DateTimeOffset LoadedAt { get; } = loadedAt;
    public IReadOnlyList<CreditRule> CreditRules { get; } = creditRules ?? [];

    /// <summary>Status padrão do crédito na data (D-505): a regra vigente mais recente; sem regra, por destaque (art. 48).</summary>
    public string CreditStatusOn(DateOnly date) =>
        CreditRules.Where(r => r.ValidFrom <= date).OrderByDescending(r => r.ValidFrom).FirstOrDefault()?.DefaultStatus ?? "APROPRIAVEL_POR_DESTAQUE";

    /// <summary>Rota do §8.1 para (CRT, tpRegimeIBSCBS); "-" = ausente.</summary>
    public EngineRoute? Route(string crt, string? tpRegime) =>
        Routing.FirstOrDefault(r => r.Crt == crt && r.TpRegimeIbsCbs == (tpRegime ?? "-"));
}

/// <summary>Acesso ao snapshot vigente, sem lock no caminho da requisição.</summary>
public interface IParamSnapshotProvider
{
    ParamSnapshot Current { get; }
}
