using System.Text.Json.Serialization;

namespace TaxGateway.Contracts;

// Admin API (SDD §15.1 item 6). Escopos: admin:fiscal (alíquotas), admin:commercial (tenants e planos).

public sealed class CriarTenantRequest
{
    public string? Nome { get; set; }
    public string? CnpjRaiz { get; set; }
    public string? Plano { get; set; }
    public bool AutoProvisionarCnpj { get; set; }
    public List<string>? Cnpjs { get; set; }

    /// <summary>30, 90, 365 ou 1825 dias; ausente = zero_retention (o XML não é gravado).</summary>
    public int? RetencaoXmlDias { get; set; }
}

public sealed class CriarTenantResponse
{
    public Guid TenantId { get; set; }

    /// <summary>Exibida uma única vez; só o HMAC fica guardado.</summary>
    public string ApiKey { get; set; } = "";
    public List<string> Scopes { get; set; } = [];
}

public sealed class CriarApiKeyRequest
{
    public string? Nome { get; set; }
    public List<string>? Scopes { get; set; }
    public DateTimeOffset? ExpiraEm { get; set; }
}

public sealed class CriarApiKeyResponse
{
    public Guid Id { get; set; }
    public string ApiKey { get; set; } = "";
    public string Prefixo { get; set; } = "";
    public List<string> Scopes { get; set; } = [];
    public DateTimeOffset? ExpiraEm { get; set; }
}

public sealed class PropostaAliquotaRequest
{
    public string? Justificativa { get; set; }
    public DateTimeOffset? VigenteEm { get; set; }
    public List<LinhaAliquotaDto>? Linhas { get; set; }

    /// <summary>Ids de linhas vigentes que esta mudança substitui (ficam marcadas como superadas).</summary>
    public List<Guid>? Substitui { get; set; }
}

public sealed class LinhaAliquotaDto
{
    public string? Tributo { get; set; }          // CBS | IBS_UF | IBS_MUN | IS
    public string? Tipo { get; set; }             // REFERENCIA | PROPRIA_ENTE
    public string? Esfera { get; set; }           // UNIAO | UF | MUNICIPIO
    public string? EnteCodigo { get; set; }
    public string? FormaAplicacao { get; set; }   // SUBSTITUICAO | ACRESCIMO | DECRESCIMO
    public decimal? Aliquota { get; set; }
    public DateOnly? ValidFrom { get; set; }
    public DateOnly? ValidTo { get; set; }
    public string? AtoLegal { get; set; }
    public string? Fonte { get; set; }
    public bool Provisoria { get; set; }

    /// <summary>Só IS (D-R17): "NCM:&lt;prefixo de 2 a 8 dígitos&gt;" ou "CCLASS:&lt;cClassTrib&gt;".</summary>
    public string? Produto { get; set; }
}

public sealed class PropostaCriadaResponse
{
    public Guid ChangeId { get; set; }
    public string Status { get; set; } = "";
}

public sealed class MudancaParametroDto
{
    public Guid Id { get; set; }
    public string Dominio { get; set; } = "";
    public string Status { get; set; } = "";
    public string PropostoPor { get; set; } = "";
    public string? AprovadoPor { get; set; }
    public string Justificativa { get; set; } = "";
    public DateTimeOffset VigenteEm { get; set; }
    public DateTimeOffset CriadoEm { get; set; }
    public System.Text.Json.JsonElement Payload { get; set; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CriarTenantRequest))]
[JsonSerializable(typeof(CriarTenantResponse))]
[JsonSerializable(typeof(CriarApiKeyRequest))]
[JsonSerializable(typeof(CriarApiKeyResponse))]
[JsonSerializable(typeof(PropostaAliquotaRequest))]
[JsonSerializable(typeof(PropostaCriadaResponse))]
[JsonSerializable(typeof(List<MudancaParametroDto>))]
public sealed partial class AdminJsonContext : JsonSerializerContext;
