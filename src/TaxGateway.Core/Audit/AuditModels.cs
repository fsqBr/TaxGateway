using TaxGateway.Core.Engines;

namespace TaxGateway.Core.Audit;

public enum AuditStatus { Conforme, Divergente, Indicio, SchemaError, AssinaturaInvalida, Indeterminado }

public enum Natureza { Deterministica, Indicio, Informativa }

/// <summary>Um tributo do grupo gIBSUF/gIBSMun/gCBS como declarado no XML.</summary>
public sealed record DeclaredTax(
    decimal Aliquota,
    decimal Valor,
    decimal? PercentualReducao,
    decimal? AliquotaEfetiva,
    decimal? PercentualDiferimento,
    decimal? ValorDiferimento,
    decimal? ValorDevolucao);

public sealed record DeclaredIs(string Cst, string CClassTrib, decimal BaseCalculo, decimal Aliquota, decimal? AdRem, decimal? Quantidade, string? Unidade, decimal Valor);

/// <summary>Componentes da base (prod + impostos do item) que o motor usa para calcular/validar o vBC.</summary>
public sealed record BaseComponents(
    decimal VProd, decimal? VFrete, decimal? VSeg, decimal? VOutro, decimal? VDesc, decimal? VII,
    decimal? VIS, decimal? VPIS, decimal? VCOFINS, decimal? VICMS, decimal? VICMSUFDest, decimal? VFCP,
    decimal? VFCPUFDest, decimal? VICMSMono, decimal? VISSQN);

public sealed record RegularTaxation(string Cst, string CClassTrib);

/// <summary>Grupo gTribSN (optante do Simples): alíquota efetiva e valor no DAS. IBS ausente no sublimite.</summary>
public sealed record DeclaredSimples(decimal? AliquotaIbs, decimal? ValorIbs, decimal AliquotaCbs, decimal ValorCbs);

public sealed record DeclaredItem(
    int Numero,
    string? Ncm,
    string? Cst,
    string? CClassTrib,
    decimal? VBc,
    DeclaredTax? IbsUf,
    DeclaredTax? IbsMun,
    decimal? VIbs,
    DeclaredTax? Cbs,
    DeclaredIs? ImpostoSeletivo,
    BaseComponents Componentes,
    RegularTaxation? TributacaoRegular,
    IReadOnlyList<string> GruposNaoSuportados,
    DeclaredSimples? Simples = null,
    string? CodigoAtividadeSn = null)
{
    /// <summary>Base do Simples: vProd + vFrete + vSeg + vOutro − vDesc (sem IS), como o motor do Simples exige.</summary>
    public decimal BaseSimples => Componentes.VProd + (Componentes.VFrete ?? 0) + (Componentes.VSeg ?? 0) + (Componentes.VOutro ?? 0) - (Componentes.VDesc ?? 0);
}

public sealed record DeclaredTotals(decimal VBc, decimal VIbsUf, decimal VIbsMun, decimal VIbs, decimal VCbs,
    decimal VDifUf, decimal VDifMun, decimal VDifCbs, decimal VDevTribUf, decimal VDevTribMun, decimal VDevTribCbs, decimal? VIs,
    decimal? VIbsSn = null, decimal? VCbsSn = null);

/// <summary>NF-e/NFC-e já lida do XML (sem dados pessoais além do necessário para o hash do CPF).</summary>
public sealed record DeclaredDocument(
    string ChaveAcesso,
    string Modelo,                 // "55" ou "65"
    DateTimeOffset DataEmissao,
    string EmitenteCnpj,
    string? Crt,
    string? TpRegimeIbsCbs,
    string? DestinatarioCnpj,
    string? DestinatarioCpf,
    bool ConsumidorFinal,
    int MunicipioIncidencia,       // cMunFGIBS ?? cMunFG (D-306)
    int FinalidadeNFe,
    bool Autorizada,               // tem protNFe
    CompraGovernamental? CompraGovernamental,
    IReadOnlyList<DeclaredItem> Itens,
    DeclaredTotals? Totais);

public sealed record Finding(
    string Regra,
    int Versao,
    Natureza Natureza,
    string Severidade,
    int? NumeroItem,
    string? CampoXml,
    string? ValorDeclarado,
    string? ValorReferencia,
    string Mensagem,
    string Fundamentacao,
    decimal? ImpactoFinanceiro);

public sealed record TaxSummary(decimal Cbs, decimal Ibs, decimal Is);

public sealed record AuditOutcome(
    AuditStatus Status,
    IReadOnlyList<Finding> Findings,
    TaxSummary Declarado,
    TaxSummary? Recalculado,
    decimal Diferenca,
    decimal? CreditoPotencial = null,
    string? CreditoStatus = null);

/// <summary>Metadados do cClassTrib na data (dados abertos do motor), usados pelas regras de indício e de crédito.</summary>
public sealed record ClassificationInfo(
    string Codigo,
    string TratamentoTributario,
    bool PossuiPercentualReducao,
    bool CreditoAdquirenteCbs,
    bool CreditoAdquirenteIbs);
