using System.Text.Json.Serialization;

namespace TaxGateway.Contracts;

// POST /v1/auditoria/xml (SDD §6.2). JSON em snake_case.

public sealed class AuditoriaResponse
{
    public string AuditId { get; set; } = "";
    public string ChaveAcesso { get; set; } = "";
    public string Direction { get; set; } = "OUTBOUND";

    /// <summary>CONFORME | DIVERGENTE | INDICIO | SCHEMA_ERROR | ASSINATURA_INVALIDA | INDETERMINADO.</summary>
    public string Status { get; set; } = "";
    public EngineVersionDto? RfbEngineVersion { get; set; }
    public long ParamSnapshotId { get; set; }
    public string RegrasVersao { get; set; } = "";
    public bool IsBillable { get; set; }
    public bool Replay { get; set; }
    public bool Reprocessamento { get; set; }
    public bool XmlArmazenado { get; set; }
    public ResumoFinanceiroDto ResumoFinanceiro { get; set; } = new();
    public List<OcorrenciaDto> Ocorrencias { get; set; } = [];
    public List<AvisoDto> Avisos { get; set; } = [];

    /// <summary>D-R21: alíquota nominal usada por tributo, com origem e ato legal (vazio antes de 2027: base do motor).</summary>
    public List<AliquotaAplicadaDto> AliquotasAplicadas { get; set; } = [];
    public string AvisoLegal { get; set; } = "Indícios não constituem parecer tributário. Achados determinísticos refletem as regras oficiais vigentes na data de emissão.";
}

public sealed class AliquotaAplicadaDto
{
    public string Tributo { get; set; } = "";
    public decimal Aliquota { get; set; }

    /// <summary>REFERENCIA | PROPRIA_DO_ENTE | PRODUTO (IS).</summary>
    public string Origem { get; set; } = "";
    public string? EnteCodigo { get; set; }
    public string? Produto { get; set; }
    public int? Item { get; set; }
    public string AtoLegal { get; set; } = "";
    public bool Provisoria { get; set; }
    public Guid CatalogoId { get; set; }
}

public sealed class ResumoFinanceiroDto
{
    public ValoresTributoDto TributoDeclarado { get; set; } = new();
    public ValoresTributoDto? TributoRecalculado { get; set; }
    public decimal Diferenca { get; set; }

    /// <summary>Só na auditoria de entrada (Fase 5).</summary>
    public decimal? CreditoPotencial { get; set; }
    public string? CreditoStatus { get; set; }
}

public sealed class ValoresTributoDto
{
    public decimal Cbs { get; set; }
    public decimal Ibs { get; set; }
    public decimal Is { get; set; }
}

public sealed class OcorrenciaDto
{
    public string Regra { get; set; } = "";
    public int Versao { get; set; }
    public string Natureza { get; set; } = "";
    public string Severidade { get; set; } = "";
    public int? NumeroItem { get; set; }
    public string? CampoXml { get; set; }
    public string? ValorDeclarado { get; set; }
    public string? ValorReferencia { get; set; }
    public string Mensagem { get; set; } = "";
    public string Fundamentacao { get; set; } = "";
    public decimal? ImpactoFinanceiroEstimado { get; set; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AuditoriaResponse))]
[JsonSerializable(typeof(List<OcorrenciaDto>))]
public sealed partial class AuditoriaJsonContext : JsonSerializerContext;
