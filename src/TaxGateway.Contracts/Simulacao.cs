using System.Text.Json;
using System.Text.Json.Serialization;

namespace TaxGateway.Contracts;

// Contrato público v1 (SDD §6.1). JSON em snake_case; valores monetários e alíquotas como número decimal.

public sealed class SimulacaoRequest
{
    public string? CnpjEmitente { get; set; }

    /// <summary>REGIME_REGULAR ou SIMPLES_NACIONAL.</summary>
    public string? RegimeTributario { get; set; }

    /// <summary>Simples: CRT "1" (padrão) ou "2" (excesso de sublimite). Regime regular: "3" (padrão).</summary>
    public string? Crt { get; set; }

    /// <summary>"1" = optante do Simples que escolheu apurar IBS/CBS pelo regime regular (LC 214 art. 41).</summary>
    public string? TpRegimeIbsCbs { get; set; }

    /// <summary>ISO 8601 com offset (ex.: 2027-03-10T10:00:00-03:00). A data do fato gerador é a local desse offset.</summary>
    public string? DataFatoGerador { get; set; }

    public int? MunicipioIbge { get; set; }
    public string? Uf { get; set; }
    public int? TipoDocumento { get; set; }
    public CompraGovernamentalDto? CompraGovernamental { get; set; }
    public List<SimulacaoItemRequest>? Itens { get; set; }
}

public sealed class CompraGovernamentalDto
{
    public int? TipoEnte { get; set; }
    public int? TipoOperacao { get; set; }
}

public sealed class SimulacaoItemRequest
{
    public int? Numero { get; set; }
    public string? Ncm { get; set; }
    public string? Nbs { get; set; }
    public string? CstIbsCbs { get; set; }
    public string? CClassTrib { get; set; }
    public decimal? ValorBaseCalculo { get; set; }
    public decimal? Quantidade { get; set; }
    public string? Unidade { get; set; }
    public ImpostoSeletivoRequest? ImpostoSeletivo { get; set; }

    /// <summary>Simples: código da atividade (cAtivSN, 2 caracteres).</summary>
    public string? CodigoAtividadeSn { get; set; }

    /// <summary>Simples: alíquotas efetivas do DAS em % (o motor não calcula RBT12; SDD §8.1).</summary>
    public AliquotasSimplesDto? AliquotasSimples { get; set; }

    /// <summary>Sobrescreve o Catálogo de Alíquotas (§8.5); a resposta registra fonte_aliquota = CLIENTE. Só a partir de 2027.</summary>
    public AliquotasNominaisDto? AliquotasNominais { get; set; }
}

public sealed class ImpostoSeletivoRequest
{
    public string? Cst { get; set; }
    public string? CClassTrib { get; set; }
    public decimal? ValorBaseCalculo { get; set; }
    public decimal? Quantidade { get; set; }
    public string? Unidade { get; set; }
}

public sealed class AliquotasSimplesDto
{
    public decimal? Ibs { get; set; }
    public decimal? Cbs { get; set; }
}

public sealed class AliquotasNominaisDto
{
    public decimal? Cbs { get; set; }
    public decimal? IbsUf { get; set; }
    public decimal? IbsMun { get; set; }
    public decimal? IsAdValorem { get; set; }
    public decimal? IsAdRem { get; set; }
}

public sealed class SimulacaoResponse
{
    public string SimulationId { get; set; } = "";

    /// <summary>REGULAR | SN_PLENO | SN_SUBLIMITE | SOMENTE_RECEITA (tabela engine_routing, §8.1).</summary>
    public string RegimeCalculo { get; set; } = "REGULAR";
    public EngineVersionDto RfbEngineVersion { get; set; } = new();
    public long ParamSnapshotId { get; set; }
    public TotaisDto Totais { get; set; } = new();
    public List<ItemResultadoDto> Itens { get; set; } = [];
    public List<AvisoDto> Avisos { get; set; } = [];

    /// <summary>Grupo de operação devolvido pelo motor (ex.: gCompraGov com o redutor), sem alteração.</summary>
    public JsonElement? OperacaoDfe { get; set; }
}

public sealed class EngineVersionDto
{
    public string Bundle { get; set; } = "";
    public string Engine { get; set; } = "";
    public string Tag { get; set; } = "";
    public string DbVersion { get; set; } = "";
}

public sealed class TotaisDto
{
    public decimal ValorBaseTotal { get; set; }
    public decimal CbsValor { get; set; }
    public decimal IbsUfValor { get; set; }
    public decimal IbsMunValor { get; set; }
    public decimal IbsTotalValor { get; set; }
    public decimal ImpostoSeletivoValor { get; set; }
    public decimal? CargaTributariaEfetivaPercent { get; set; }
}

public sealed class ItemResultadoDto
{
    public int NumeroItem { get; set; }

    /// <summary>Estado devolvido pelo motor: CALCULADO, CALCULO_SIMULADO, INCONSISTENCIA_ENTRADA...</summary>
    public string Estado { get; set; } = "";
    public string? CstIbsCbs { get; set; }
    public string? CClassTrib { get; set; }
    public decimal? BaseCalculo { get; set; }
    public TributoResultadoDto? Cbs { get; set; }
    public TributoResultadoDto? IbsUf { get; set; }
    public TributoResultadoDto? IbsMun { get; set; }
    public decimal? IbsTotalValor { get; set; }
    public ImpostoSeletivoResultadoDto? ImpostoSeletivo { get; set; }

    /// <summary>IBS/CBS recolhidos no DAS (Simples Nacional), como o motor do Simples devolve.</summary>
    public SimplesResultadoDto? Simples { get; set; }
    public List<ErroItemDto> Erros { get; set; } = [];

    /// <summary>Grupos oficiais do DF-e (IBSCBS, IS) exatamente como o motor devolve, para preencher o XML.</summary>
    public JsonElement? TributosDfe { get; set; }
}

public sealed class TributoResultadoDto
{
    public decimal Aliquota { get; set; }
    public decimal Valor { get; set; }
    public decimal? PercentualReducao { get; set; }
    public decimal? AliquotaEfetiva { get; set; }

    /// <summary>MOTOR (antes de 2027, alíquota da base oficial), CATALOGO ou CLIENTE.</summary>
    public string FonteAliquota { get; set; } = "";

    /// <summary>Linha do catálogo usada (rastreabilidade, §8.5).</summary>
    public Guid? CatalogoId { get; set; }
}

public sealed class SimplesResultadoDto
{
    public string Estado { get; set; } = "";
    public string? MotivoEstado { get; set; }
    public decimal? BaseCalculo { get; set; }
    public decimal? AliquotaIbs { get; set; }
    public decimal? ValorIbs { get; set; }
    public decimal? AliquotaCbs { get; set; }
    public decimal? ValorCbs { get; set; }
    public List<ErroItemDto> Erros { get; set; } = [];
    public JsonElement? TributosSimples { get; set; }
}

public sealed class ImpostoSeletivoResultadoDto
{
    public decimal BaseCalculo { get; set; }
    public decimal? AliquotaAdValorem { get; set; }
    public decimal? AliquotaAdRem { get; set; }
    public decimal Valor { get; set; }
}

public sealed class ErroItemDto
{
    public string Codigo { get; set; } = "";
    public string Titulo { get; set; } = "";
    public string? Detalhe { get; set; }
    public string? Campo { get; set; }
}

public sealed class AvisoDto
{
    public string Codigo { get; set; } = "";
    public string Mensagem { get; set; } = "";
}

/// <summary>Erro de validação de campo, dentro do problem+json (extensão "errors").</summary>
public sealed class CampoErroDto
{
    public string Campo { get; set; } = "";
    public string Codigo { get; set; } = "";
    public string Mensagem { get; set; } = "";
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    NumberHandling = JsonNumberHandling.Strict,
    // D-J05: campo desconhecido é erro, não silêncio — "aliquota_cbs" digitado errado era ignorado e o cálculo saía com
    // o catálogo, sem o cliente perceber.
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(SimulacaoRequest))]
[JsonSerializable(typeof(SimulacaoResponse))]
[JsonSerializable(typeof(List<CampoErroDto>))]
[JsonSerializable(typeof(List<ErroItemDto>))]
public sealed partial class ContractsJsonContext : JsonSerializerContext;
