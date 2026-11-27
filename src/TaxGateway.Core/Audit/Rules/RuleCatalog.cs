namespace TaxGateway.Core.Audit.Rules;

public sealed record RuleDefinition(string Id, int Versao, Natureza Natureza, string Severidade, string Descricao, string Fundamentacao);

/// <summary>
/// Catálogo de regras de auditoria (SDD §8.3). Fase 3: regras determinísticas da saída (R001–R030) e as
/// informativas de cobertura. Cada achado grava regra@versão; mudar a lógica de uma regra = nova versão.
/// R013, R025 e R029 foram acrescentadas na Fase 3 (D-307).
/// </summary>
public static class RuleCatalog
{
    public const string RulesVersion = "2026.11-fase5";

    public static readonly RuleDefinition R001 = new("R001", 1, Natureza.Deterministica, "ERROR",
        "XML inválido no XSD do leiaute vigente", "MOC NF-e; NT 2025.002 (pacote de schemas PL_010)");
    public static readonly RuleDefinition R002 = new("R002", 1, Natureza.Deterministica, "ERROR",
        "Assinatura inválida, adulterada ou cadeia não ICP-Brasil", "MP 2.200-2/2001; MOC NF-e (assinatura digital)");
    public static readonly RuleDefinition R010 = new("R010", 1, Natureza.Deterministica, "ERROR",
        "vBC declarado diferente da base calculada pelo motor a partir dos componentes do item", "LC 214/2025 art. 12; NT 2025.002 (vBC)");
    public static readonly RuleDefinition R011 = new("R011", 1, Natureza.Deterministica, "ERROR",
        "Valor do tributo ≠ vBC × alíquota (efetiva) − vDif − vDevTrib, tolerância ±0,01 por item", "NT 2025.002 v1.53, rejeições 1041/1052/1069");
    public static readonly RuleDefinition R012 = new("R012", 1, Natureza.Deterministica, "ERROR",
        "Alíquota declarada ≠ alíquota do Catálogo ajustada pela redução do cClassTrib", "LC 214/2025 arts. 14 a 19 e 343 a 347; SDD §8.5");
    public static readonly RuleDefinition R013 = new("R013", 1, Natureza.Deterministica, "ERROR",
        "Valor do tributo ≠ valor recalculado pelo motor oficial (tolerância ±0,01 por item)", "Calculadora oficial RTC (RFB)");
    public static readonly RuleDefinition R014 = new("R014", 1, Natureza.Deterministica, "ERROR",
        "Alíquota do Imposto Seletivo declarada ≠ alíquota do Catálogo para o produto", "LC 214/2025 arts. 409 a 434; SDD §8.5 (D-R17)");
    public static readonly RuleDefinition R020 = new("R020", 1, Natureza.Deterministica, "ERROR",
        "CST inexistente ou incompatível com o cClassTrib", "Tabela de classificação tributária (IT 2025.002)");
    public static readonly RuleDefinition R022 = new("R022", 1, Natureza.Deterministica, "ERROR",
        "cClassTrib inexistente ou fora de vigência na data de emissão", "Tabela de classificação tributária (IT 2025.002)");
    public static readonly RuleDefinition R025 = new("R025", 1, Natureza.Deterministica, "ERROR",
        "NCM/NBS inexistente ou não vinculado ao cClassTrib na tabela oficial", "Tabela de classificação tributária (anexos de NCM/NBS)");
    public static readonly RuleDefinition R029 = new("R029", 1, Natureza.Deterministica, "ERROR",
        "Grupo ou campo obrigatório para o cClassTrib ausente ou inconsistente (apontado pelo motor)", "Calculadora oficial RTC (RFB)");
    public static readonly RuleDefinition R030 = new("R030", 1, Natureza.Deterministica, "ERROR",
        "IBSCBSTot/ISTot ≠ soma exata dos itens", "NT 2025.002 v1.53, rejeições 1076/1080/1084/1085/1091");
    public static readonly RuleDefinition I001 = new("I001", 1, Natureza.Informativa, "INFO",
        "Grupo do leiaute ainda não suportado pelo mapeador: item não auditado", "SDD §8.2 (MAP_UNSUPPORTED_FIELD)");
    public static readonly RuleDefinition I002 = new("I002", 1, Natureza.Informativa, "INFO",
        "Item sem tratamento implementado no motor oficial (NAO_IMPLEMENTADO)", "Calculadora oficial RTC (RFB)");
    public static readonly RuleDefinition I003 = new("I003", 1, Natureza.Informativa, "INFO",
        "Imposto Seletivo divergente: regras de IS do motor ainda incompletas", "ROADMAP do motor (Release 16); SDD §8.1");
    public static readonly RuleDefinition I004 = new("I004", 1, Natureza.Informativa, "INFO",
        "Regime do emitente (CRT) fora do escopo desta auditoria", "SDD §8.1 (Simples Nacional na Fase 5)");
    public static readonly RuleDefinition I005 = new("I005", 1, Natureza.Informativa, "INFO",
        "Item sem grupo IBSCBS: não auditado", "NT 2025.002");

    public static readonly RuleDefinition R021 = new("R021", 1, Natureza.Indicio, "WARNING",
        "cClassTrib de redução, isenção, diferimento ou suspensão com requisitos que o XML não comprova", "LC 214/2025 arts. 125 a 147 (regimes diferenciados); SDD §8.3");
    public static readonly RuleDefinition R024 = new("R024", 1, Natureza.Indicio, "WARNING",
        "Alíquota efetiva do Simples fora da faixa do anexo (exige a tabela de faixas no catálogo de parâmetros)", "LC 123/2006 anexos I a V; LC 214/2025 art. 41");
    public static readonly RuleDefinition R040 = new("R040", 1, Natureza.Informativa, "INFO",
        "Crédito potencial do adquirente e situação conforme a regra vigente", "LC 214/2025 arts. 47 e 48; art. 47 §9º (fornecedor do Simples)");
    public static readonly RuleDefinition R050 = new("R050", 1, Natureza.Deterministica, "ERROR",
        "MEI (CRT 4) com grupo IBSCBS informado", "LC 123/2006 art. 18-A; NT 2026.005");
    public static readonly RuleDefinition I007 = new("I007", 1, Natureza.Informativa, "INFO",
        "MEI sem destaque de IBS/CBS: nada a recalcular", "LC 123/2006 art. 18-A");

    public static readonly RuleDefinition I006 = new("I006", 1, Natureza.Informativa, "INFO",
        "Sem alíquota nominal vigente no catálogo para a data de emissão: recálculo impossível", "SDD §8.5");

    public static readonly IReadOnlyList<RuleDefinition> All =
        [R001, R002, R010, R011, R012, R013, R014, R020, R021, R022, R024, R025, R029, R030, R040, R050, I001, I002, I003, I004, I005, I006, I007];

    /// <summary>Erros por item do motor → regra (D-307).</summary>
    public static RuleDefinition ForEngineError(string code) => code switch
    {
        "REG-004" or "REG-006" => R020,
        "REG-005" => R022,
        "REG-002" or "REG-003" or "REG-011" or "REG-012" => R025,
        _ => R029,
    };
}
