using System.Globalization;

using TaxGateway.Core.Audit.Rules;
using TaxGateway.Core.Engines;

namespace TaxGateway.Core.Audit;

/// <summary>
/// Auditoria de saída (SDD §8.3, regras determinísticas R010–R030): compara o declarado no XML com o recálculo do
/// motor oficial, sem I/O. O TaxGateway nunca recalcula imposto por fora: as referências são os valores do motor;
/// as únicas contas próprias são as de consistência do próprio XML (R011, R030), iguais às rejeições da SEFAZ.
/// </summary>
public static class OutboundAuditor
{
    /// <summary>Tolerância por item de R011/R013 (SDD §8.3; parâmetro "Tolerâncias" do §15.2).</summary>
    public const decimal Tolerancia = 0.01m;

    public static AuditOutcome Audit(DeclaredDocument doc, EngineCalculation? calc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        var findings = new List<Finding>();
        var indeterminado = false;
        var engineByItem = calc?.Itens.ToDictionary(i => i.Numero) ?? [];

        foreach (var item in doc.Itens)
        {
            if (item.GruposNaoSuportados.Count > 0)
            {
                findings.Add(F(RuleCatalog.I001, item.Numero, $"det[{item.Numero}]/imposto/IBSCBS", string.Join(",", item.GruposNaoSuportados), null,
                    $"Grupos ainda não suportados: {string.Join(", ", item.GruposNaoSuportados)}. Item não auditado."));
                indeterminado = true;
                continue;
            }

            if (item.Cst is null || item.CClassTrib is null)
            {
                findings.Add(F(RuleCatalog.I005, item.Numero, $"det[{item.Numero}]/imposto", null, null, "Item sem grupo IBSCBS."));
                indeterminado = true;
                continue;
            }

            if (!engineByItem.TryGetValue(item.Numero, out var eng))
            {
                indeterminado = true;
                continue;
            }

            if (eng.Estado == "NAO_IMPLEMENTADO")
            {
                findings.Add(F(RuleCatalog.I002, item.Numero, $"det[{item.Numero}]/imposto/IBSCBS/cClassTrib", item.CClassTrib, null,
                    (eng.Erros.Count > 0 ? eng.Erros[0].Detalhe : null) ?? "Classificação sem tratamento no motor."));
                indeterminado = true;
                continue;
            }

            if (eng.Erros.Count > 0)
            {
                foreach (var e in eng.Erros)
                {
                    var rule = RuleCatalog.ForEngineError(e.Codigo);
                    findings.Add(F(rule, item.Numero, FieldFor(rule, item.Numero), DeclaredFor(rule, item), null,
                        $"{e.Titulo}: {e.Detalhe} ({e.Codigo})"));
                }

                continue;
            }

            AuditItem(item, eng, findings);
        }

        AuditTotals(doc, findings);

        var declarado = new TaxSummary(
            doc.Itens.Sum(i => i.Cbs?.Valor ?? 0), doc.Itens.Sum(i => i.VIbs ?? 0), doc.Itens.Sum(i => i.ImpostoSeletivo?.Valor ?? 0));
        TaxSummary? recalculado = calc is null ? null : new TaxSummary(calc.Totais.Cbs, calc.Totais.Ibs, calc.Totais.ImpostoSeletivo);
        var diferenca = recalculado is null ? 0
            : recalculado.Cbs + recalculado.Ibs + recalculado.Is - (declarado.Cbs + declarado.Ibs + declarado.Is);

        var status = findings.Any(f => f.Natureza == Natureza.Deterministica) ? AuditStatus.Divergente
            : indeterminado || findings.Any(f => f.Regra == RuleCatalog.I003.Id) ? AuditStatus.Indeterminado
            : AuditStatus.Conforme;
        return new AuditOutcome(status, findings, declarado, recalculado, diferenca);
    }

    private static void AuditItem(DeclaredItem d, EngineItemResult e, List<Finding> findings)
    {
        var n = d.Numero;
        var basePath = $"det[{n}]/imposto/IBSCBS/gIBSCBS";

        // R010: base declarada × base que o motor calculou a partir dos componentes do item.
        if (e.BaseCalculo is { } eb && d.VBc is { } db && db != eb)
        {
            findings.Add(F(RuleCatalog.R010, n, $"{basePath}/vBC", Fmt(db), Fmt(eb),
                $"Base declarada {Fmt(db)} ≠ base calculada pelo motor {Fmt(eb)} (componentes do item).", (eb - db)));
        }

        Tax("gIBSUF", "IBSUF", d.IbsUf, e.IbsUf);
        Tax("gIBSMun", "IBSMun", d.IbsMun, e.IbsMun);
        Tax("gCBS", "CBS", d.Cbs, e.Cbs);

        // R011 (vIBS = vIBSUF + vIBSMun, regra de consistência do próprio XML)
        if (d.VIbs is { } vibs && d.IbsUf is not null && d.IbsMun is not null && vibs != d.IbsUf.Valor + d.IbsMun.Valor)
        {
            findings.Add(F(RuleCatalog.R011, n, $"{basePath}/vIBS", Fmt(vibs), Fmt(d.IbsUf.Valor + d.IbsMun.Valor),
                "vIBS deve ser a soma exata de vIBSUF e vIBSMun."));
        }

        // IS: regras do motor incompletas → divergência só informativa (SDD §8.1).
        if (d.ImpostoSeletivo is { } dis && e.ImpostoSeletivo is { } eis && Math.Abs(dis.Valor - eis.Valor) > Tolerancia)
        {
            findings.Add(F(RuleCatalog.I003, n, $"det[{n}]/imposto/IS/vIS", Fmt(dis.Valor), Fmt(eis.Valor),
                "Imposto Seletivo diferente do recálculo do motor; regras de IS do motor ainda incompletas.", eis.Valor - dis.Valor));
        }

        void Tax(string group, string suffix, DeclaredTax? dt, TaxLine? et)
        {
            var path = $"{basePath}/{(group == "gCBS" ? "gCBS" : group)}";
            if (dt is null || et is null)
            {
                if (dt is null != (et is null))
                {
                    findings.Add(F(RuleCatalog.R029, n, path, dt is null ? "ausente" : "presente", et is null ? "ausente" : "presente",
                        $"Grupo {group} {(dt is null ? "ausente no XML" : "não esperado")}."));
                }

                return;
            }

            // R012: alíquota nominal e efetiva (redução do cClassTrib) iguais às do motor (catálogo + tabela oficial).
            if (dt.Aliquota != et.Aliquota)
            {
                findings.Add(F(RuleCatalog.R012, n, $"{path}/p{suffix}", Fmt(dt.Aliquota), Fmt(et.Aliquota),
                    $"Alíquota {suffix} declarada {Fmt(dt.Aliquota)}% ≠ {Fmt(et.Aliquota)}% do catálogo para a data."));
            }

            var engEfet = et.AliquotaEfetiva ?? et.Aliquota;
            var decEfet = dt.AliquotaEfetiva ?? dt.Aliquota;
            if (decEfet != engEfet || (dt.PercentualReducao ?? 0) != (et.PercentualReducao ?? 0))
            {
                findings.Add(F(RuleCatalog.R012, n, $"{path}/gRed", $"{Fmt(dt.PercentualReducao ?? 0)}% / {Fmt(decEfet)}%",
                    $"{Fmt(et.PercentualReducao ?? 0)}% / {Fmt(engEfet)}%",
                    $"Redução/alíquota efetiva de {suffix} diferente da prevista para o cClassTrib {d.CClassTrib}."));
            }

            // R011: consistência interna do XML (mesma conta das rejeições da SEFAZ).
            if (d.VBc is { } vbc)
            {
                var esperado = Math.Round(vbc * decEfet / 100m, 2, MidpointRounding.ToEven) - (dt.ValorDiferimento ?? 0) - (dt.ValorDevolucao ?? 0);
                if (Math.Abs(esperado - dt.Valor) > Tolerancia)
                {
                    findings.Add(F(RuleCatalog.R011, n, $"{path}/v{suffix}", Fmt(dt.Valor), Fmt(esperado),
                        $"v{suffix} ≠ vBC × alíquota efetiva − vDif − vDevTrib (tolerância ±0,01).", esperado - dt.Valor));
                }
            }

            // R013: valor final igual ao do motor.
            if (Math.Abs(dt.Valor - et.Valor) > Tolerancia)
            {
                findings.Add(F(RuleCatalog.R013, n, $"{path}/v{suffix}", Fmt(dt.Valor), Fmt(et.Valor),
                    $"v{suffix} declarado {Fmt(dt.Valor)} ≠ {Fmt(et.Valor)} recalculado pelo motor.", et.Valor - dt.Valor));
            }
        }
    }

    private static void AuditTotals(DeclaredDocument doc, List<Finding> findings)
    {
        var itens = doc.Itens.Where(i => i.Cst is not null && i.GruposNaoSuportados.Count == 0).ToList();
        var comIbsCbs = itens.Any(i => i.VBc is not null);
        if (doc.Totais is null)
        {
            if (comIbsCbs)
            {
                findings.Add(F(RuleCatalog.R030, null, "total/IBSCBSTot", "ausente", "presente", "Itens com IBSCBS exigem o grupo IBSCBSTot."));
            }

            return;
        }

        if (itens.Count != doc.Itens.Count)
        {
            return;   // com item não suportado, a soma não é comparável (documento sai INDETERMINADO)
        }

        var t = doc.Totais;
        void Check(string campo, decimal total, decimal soma)
        {
            if (total != soma)
            {
                findings.Add(F(RuleCatalog.R030, null, $"total/IBSCBSTot/{campo}", Fmt(total), Fmt(soma),
                    $"{campo} ({Fmt(total)}) ≠ soma dos itens ({Fmt(soma)})."));
            }
        }

        Check("vBCIBSCBS", t.VBc, itens.Sum(i => i.VBc ?? 0));
        Check("gIBS/gIBSUF/vIBSUF", t.VIbsUf, itens.Sum(i => i.IbsUf?.Valor ?? 0));
        Check("gIBS/gIBSMun/vIBSMun", t.VIbsMun, itens.Sum(i => i.IbsMun?.Valor ?? 0));
        Check("gIBS/vIBS", t.VIbs, itens.Sum(i => i.VIbs ?? 0));
        Check("gCBS/vCBS", t.VCbs, itens.Sum(i => i.Cbs?.Valor ?? 0));
        Check("gIBS/gIBSUF/vDif", t.VDifUf, itens.Sum(i => i.IbsUf?.ValorDiferimento ?? 0));
        Check("gIBS/gIBSMun/vDif", t.VDifMun, itens.Sum(i => i.IbsMun?.ValorDiferimento ?? 0));
        Check("gCBS/vDif", t.VDifCbs, itens.Sum(i => i.Cbs?.ValorDiferimento ?? 0));
        Check("gIBS/gIBSUF/vDevTrib", t.VDevTribUf, itens.Sum(i => i.IbsUf?.ValorDevolucao ?? 0));
        Check("gIBS/gIBSMun/vDevTrib", t.VDevTribMun, itens.Sum(i => i.IbsMun?.ValorDevolucao ?? 0));
        Check("gCBS/vDevTrib", t.VDevTribCbs, itens.Sum(i => i.Cbs?.ValorDevolucao ?? 0));
        var somaIs = itens.Sum(i => i.ImpostoSeletivo?.Valor ?? 0);
        if ((t.VIs ?? 0) != somaIs)
        {
            findings.Add(F(RuleCatalog.R030, null, "total/ISTot/vIS", Fmt(t.VIs ?? 0), Fmt(somaIs), "ISTot/vIS ≠ soma dos itens."));
        }
    }

    private static string FieldFor(RuleDefinition rule, int n) => rule.Id switch
    {
        "R020" => $"det[{n}]/imposto/IBSCBS/CST",
        "R022" => $"det[{n}]/imposto/IBSCBS/cClassTrib",
        "R025" => $"det[{n}]/prod/NCM",
        _ => $"det[{n}]/imposto/IBSCBS",
    };

    private static string? DeclaredFor(RuleDefinition rule, DeclaredItem i) => rule.Id switch
    {
        "R020" => $"{i.Cst}/{i.CClassTrib}",
        "R022" => i.CClassTrib,
        "R025" => i.Ncm,
        _ => null,
    };

    private static Finding F(RuleDefinition r, int? item, string? campo, string? declarado, string? referencia, string msg, decimal? impacto = null) =>
        new(r.Id, r.Versao, r.Natureza, r.Severidade, item, campo, declarado, referencia, msg, r.Fundamentacao, impacto);

    private static string Fmt(decimal v) => v.ToString("0.00##", CultureInfo.InvariantCulture);
}
