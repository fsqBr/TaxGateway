using System.Text.Json;

namespace TaxGateway.Core.Engines;

/// <summary>Item para o motor do Simples (SDD §8.1): a alíquota efetiva vem do cliente/XML, nunca do motor.</summary>
public sealed record SimplesItemInput(
    int Numero,
    string? CodigoAtividade,       // cAtivSN (2 caracteres)
    string CClassTrib,
    string? Ncm,
    string? Nbs,
    decimal BaseCalculo,           // vProd + vFrete + vSeg + vOutro − vDesc (sem IS)
    decimal ValorImpostoSeletivo,
    decimal? AliquotaIbs,          // pIBSSN
    decimal? AliquotaCbs);         // pCBSSN

public sealed record SimplesOperation(DateOnly Data, int TipoDocumento, string Crt, string? TpRegimeIbsCbs, int? TipoEnteGov, IReadOnlyList<SimplesItemInput> Itens);

public sealed record SimplesItemResult(
    int Numero,
    string Estado,
    decimal? BaseCalculo,
    decimal? AliquotaIbs,
    decimal? ValorIbs,
    decimal? AliquotaCbs,
    decimal? ValorCbs,
    IReadOnlyList<EngineError> Erros,
    string? MotivoEstado,
    JsonElement? TributosSimples);

public sealed record SimplesTotals(decimal BaseCalculo, decimal Ibs, decimal Cbs, decimal ImpostoSeletivo);

public sealed record SimplesCalculation(IReadOnlyList<SimplesItemResult> Itens, SimplesTotals Totais, EngineVersion Version);

public interface ISimplesEngine
{
    Task<SimplesCalculation> CalculateAsync(SimplesOperation operation, CancellationToken ct);
}
