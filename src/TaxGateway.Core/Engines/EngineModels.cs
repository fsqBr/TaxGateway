using System.Text.Json;

namespace TaxGateway.Core.Engines;

public enum RfbEngine { Regular, Simples }

[Flags]
public enum EngineCapability
{
    None = 0,
    CalcCbsIbs = 1,
    CalcIs = 2,
    XmlGenerate = 4,
    XmlValidate = 8,
    BaseCalculoUnificada = 16,
    DadosAbertos = 32,
}

/// <summary>Alíquotas nominais em % (campo <c>aliquotasNominais</c> do motor, obrigatório desde 2027).</summary>
public sealed record NominalRates(decimal? Cbs, decimal? IbsUf, decimal? IbsMun, decimal? IsAdValorem, decimal? IsAdRem);

public sealed record EngineIsInput(string Cst, string CClassTrib, decimal BaseCalculo, decimal? Quantidade, string? Unidade);

public sealed record EngineItemInput(
    int Numero,
    string? Ncm,
    string? Nbs,
    string Cst,
    string CClassTrib,
    decimal? BaseCalculo,
    decimal? Quantidade,
    string? Unidade,
    EngineIsInput? ImpostoSeletivo,
    NominalRates? AliquotasNominais,
    TaxGateway.Core.Audit.BaseComponents? Componentes = null,               // gComponentesBC: o motor calcula o vBC (auditoria)
    TaxGateway.Core.Audit.RegularTaxation? TributacaoRegular = null);

public sealed record CompraGovernamental(int TipoEnte, int? TipoOperacao);

/// <summary>Uma operação para o motor: um município por chamada (SDD §8.6; não há município por item).</summary>
public sealed record EngineOperation(
    string Id,
    DateTimeOffset FatoGerador,
    int Municipio,
    string Uf,
    int? TipoDocumento,
    CompraGovernamental? CompraGovernamental,
    IReadOnlyList<EngineItemInput> Itens);

public sealed record EngineError(string Codigo, string Titulo, string? Detalhe, string? Ponteiro);

public sealed record TaxLine(decimal Aliquota, decimal Valor, decimal? PercentualReducao, decimal? AliquotaEfetiva);

public sealed record IsLine(decimal BaseCalculo, decimal? AliquotaAdValorem, decimal? AliquotaAdRem, decimal Valor);

public sealed record EngineItemResult(
    int Numero,
    string Estado,                 // CALCULADO, CALCULO_SIMULADO, INCONSISTENCIA_ENTRADA...
    string? Cst,
    string? CClassTrib,
    decimal? BaseCalculo,
    TaxLine? Cbs,
    TaxLine? IbsUf,
    TaxLine? IbsMun,
    decimal? IbsTotal,
    IsLine? ImpostoSeletivo,
    IReadOnlyList<EngineError> Erros,
    JsonElement? TributosDfe);     // grupos oficiais (tribCalc) devolvidos pelo motor, sem alteração

public sealed record EngineTotals(decimal BaseCalculo, decimal Cbs, decimal IbsUf, decimal IbsMun, decimal Ibs, decimal ImpostoSeletivo);

public sealed record EngineVersion(string Bundle, RfbEngine Engine, string AppVersion, string DbVersion);

public sealed record EngineCalculation(
    IReadOnlyList<EngineItemResult> Itens,
    EngineTotals Totais,
    IReadOnlyList<string> AvisosDadosSimulados,   // header x-warning-dados-simulados
    EngineVersion Version,
    JsonElement? OperacaoDfe = null);              // grupo "oper" (ex.: gCompraGov com pRedutor), sem alteração

/// <summary>Erro de entrada recusado pelo motor como um todo (HTTP 4xx), não por item.</summary>
public sealed class EngineRejectedException(int status, IReadOnlyList<EngineError> errors)
    : Exception($"O motor recusou a operação (HTTP {status}).")
{
    public int Status { get; } = status;
    public IReadOnlyList<EngineError> Errors { get; } = errors;
}

/// <summary>Motor indisponível (timeout, circuito aberto, 5xx depois das tentativas).</summary>
public sealed class EngineUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Capacidade não suportada pelo motor (SDD §8): vira 422 ERR_ENGINE_CAPABILITY.</summary>
public sealed class EngineCapabilityNotSupportedException(RfbEngine engine, EngineCapability capability)
    : Exception($"O motor {engine} não suporta {capability}.")
{
    public RfbEngine Engine { get; } = engine;
    public EngineCapability Capability { get; } = capability;
}

/// <summary>Anti-corruption layer de um motor da RFB (SDD §8). Fase 2: cálculo síncrono do regime regular.</summary>
public interface IRfbTaxEngineAdapter
{
    RfbEngine Engine { get; }
    EngineCapability Capabilities { get; }
    Task<EngineCalculation> CalculateAsync(EngineOperation operation, CancellationToken ct);
}

/// <summary>Município do cadastro do motor (espelho de /dados-abertos, por bundle).</summary>
public interface IRfbReferenceData
{
    string Bundle { get; }
    bool IsLoaded { get; }
    /// <summary>Sigla da UF do município IBGE, ou null se o município não existe no bundle.</summary>
    string? UfOfMunicipio(int municipioIbge);

    /// <summary>Classificações cbs-ibs vigentes na data (redução, crédito do adquirente), em cache por bundle e data.</summary>
    Task<IReadOnlyDictionary<string, TaxGateway.Core.Audit.ClassificationInfo>> ClassificationsAsync(DateOnly onDate, CancellationToken ct);
}
