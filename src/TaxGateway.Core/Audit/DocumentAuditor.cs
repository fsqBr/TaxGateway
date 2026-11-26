using System.Globalization;

using TaxGateway.Core.Audit.Rules;
using TaxGateway.Core.Engines;

namespace TaxGateway.Core.Audit;

/// <summary>Tudo o que o auditor precisa, já calculado pelos motores (sem I/O).</summary>
public sealed record AuditInput(
    DeclaredDocument Doc,
    string Direction,                                        // OUTBOUND | INBOUND
    bool UsaRegular,
    bool UsaSimples,
    EngineCalculation? Regular,
    SimplesCalculation? Simples,
    IReadOnlyDictionary<string, ClassificationInfo> Classificacoes,
    string CreditoStatusPadrao,                              // credit_rules vigente na data (§8.3)
    IReadOnlySet<string> IndiciosConfirmados);               // contexto do cliente (ex.: R021 confirmado)

/// <summary>
/// Auditoria por regime e direção (SDD §8.1, §8.3; D-503 a D-505): regime regular (R010–R030), Simples (gTribSN
/// contra o motor do Simples), sublimite (os dois), MEI (R050) e, na entrada, indícios (R021) e crédito (R040).
/// </summary>
public static class DocumentAuditor
{
    private static readonly string[] TratamentosCondicionados = ["redu", "isen", "difer", "suspens", "zero"];

    public static AuditOutcome Audit(AuditInput i)
    {
        ArgumentNullException.ThrowIfNull(i);
        var d = i.Doc;
        var findings = new List<Finding>();
        var indeterminado = false;

        // MEI (CRT 4): valores fixos no DAS-MEI, sem grupo IBSCBS (R050).
        if (d.Crt == "4")
        {
            foreach (var it in d.Itens.Where(x => x.Cst is not null))
            {
                findings.Add(F(RuleCatalog.R050, it.Numero, $"det[{it.Numero}]/imposto/IBSCBS", "presente", "ausente",
                    "MEI não destaca IBS/CBS: o grupo IBSCBS não deve ser informado."));
            }

            if (findings.Count == 0)
            {
                findings.Add(F(RuleCatalog.I007, null, "NFe/infNFe/emit/CRT", "4", null, "Emitente MEI sem IBS/CBS destacado."));
            }

            return Finish(i, findings, false, Summary(d), null, 0, credit: i.Direction == "INBOUND" ? (0m, "SEM_CREDITO_MEI") : null);
        }

        TaxSummary? recalc = null;
        decimal diferenca = 0;
        if (i.UsaRegular)
        {
            var reg = OutboundAuditor.Audit(d, i.Regular);
            findings.AddRange(reg.Findings);
            indeterminado |= reg.Status == AuditStatus.Indeterminado;
            recalc = reg.Recalculado;
        }

        if (i.UsaSimples)
        {
            indeterminado |= AuditSimples(d, i.Simples, findings);
            if (i.Simples is { } sim)
            {
                recalc = new TaxSummary((recalc?.Cbs ?? 0) + sim.Totais.Cbs, (recalc?.Ibs ?? 0) + sim.Totais.Ibs, recalc?.Is ?? 0);
            }
        }

        var declarado = Summary(d);
        if (recalc is not null)
        {
            diferenca = recalc.Cbs + recalc.Ibs + recalc.Is - (declarado.Cbs + declarado.Ibs + declarado.Is);
        }

        (decimal, string)? credit = null;
        if (i.Direction == "INBOUND")
        {
            AddIndicios(i, findings);
            credit = Credit(i, findings);
        }

        return Finish(i, findings, indeterminado, declarado, recalc, diferenca, credit);
    }

    /// <summary>Simples: vIBSSN/vCBSSN declarados contra o motor do Simples, com a mesma tolerância da R011/R013.</summary>
    private static bool AuditSimples(DeclaredDocument d, SimplesCalculation? sim, List<Finding> findings)
    {
        var indeterminado = false;
        var byItem = sim?.Itens.ToDictionary(x => x.Numero) ?? [];
        foreach (var it in d.Itens.Where(x => x.Cst is not null))
        {
            var path = $"det[{it.Numero}]/imposto/IBSCBS/gTribSN";
            if (it.Simples is null)
            {
                findings.Add(F(RuleCatalog.R029, it.Numero, path, "ausente", "presente", "Optante do Simples precisa informar o grupo gTribSN (NT 2026.005)."));
                continue;
            }

            if (!byItem.TryGetValue(it.Numero, out var e))
            {
                indeterminado = true;
                continue;
            }

            if (e.Erros.Count > 0 || e.Estado is not ("CALCULADO" or "CALCULO_SIMULADO"))
            {
                foreach (var err in e.Erros)
                {
                    var rule = RuleCatalog.ForEngineError(err.Codigo);
                    findings.Add(F(rule, it.Numero, path, null, null, $"{err.Titulo}: {err.Detalhe} ({err.Codigo})"));
                }

                if (e.Erros.Count == 0)
                {
                    findings.Add(F(RuleCatalog.R029, it.Numero, path, e.Estado, "CALCULADO", e.MotivoEstado ?? $"Motor do Simples: {e.Estado}."));
                }

                continue;
            }

            void Check(string tributo, decimal? p, decimal? v, decimal? ep, decimal? ev)
            {
                if (v is null || ev is null)
                {
                    return;
                }

                var esperado = Math.Round((e.BaseCalculo ?? 0) * (p ?? 0) / 100m, 2, MidpointRounding.ToEven);
                if (Math.Abs(esperado - v.Value) > OutboundAuditor.Tolerancia)
                {
                    findings.Add(F(RuleCatalog.R011, it.Numero, $"{path}/g{tributo}SN/v{tributo}SN", Fmt(v.Value), Fmt(esperado),
                        $"v{tributo}SN ≠ base do Simples × alíquota efetiva (tolerância ±0,01).", esperado - v.Value));
                }

                if (Math.Abs(ev.Value - v.Value) > OutboundAuditor.Tolerancia)
                {
                    findings.Add(F(RuleCatalog.R013, it.Numero, $"{path}/g{tributo}SN/v{tributo}SN", Fmt(v.Value), Fmt(ev.Value),
                        $"v{tributo}SN declarado ≠ recalculado pelo motor do Simples.", ev.Value - v.Value));
                }
            }

            Check("IBS", it.Simples.AliquotaIbs, it.Simples.ValorIbs, e.AliquotaIbs, e.ValorIbs);
            Check("CBS", it.Simples.AliquotaCbs, it.Simples.ValorCbs, e.AliquotaCbs, e.ValorCbs);
        }

        if (d.Totais is { } t && d.Itens.Any(x => x.Simples is not null))
        {
            var somaCbs = d.Itens.Sum(x => x.Simples?.ValorCbs ?? 0);
            var somaIbs = d.Itens.Sum(x => x.Simples?.ValorIbs ?? 0);
            if ((t.VCbsSn ?? 0) != somaCbs || (t.VIbsSn ?? 0) != somaIbs)
            {
                findings.Add(F(RuleCatalog.R030, null, "total/IBSCBSTot/gTribSN", $"{Fmt(t.VIbsSn ?? 0)}/{Fmt(t.VCbsSn ?? 0)}",
                    $"{Fmt(somaIbs)}/{Fmt(somaCbs)}", "Totais vIBSSN/vCBSSN ≠ soma dos itens."));
            }
        }

        return indeterminado;
    }

    /// <summary>R021 (entrada): benefício condicionado a requisitos que o XML não comprova. O cliente pode confirmar.</summary>
    private static void AddIndicios(AuditInput i, List<Finding> findings)
    {
        if (i.IndiciosConfirmados.Contains("R021"))
        {
            return;
        }

        foreach (var it in i.Doc.Itens.Where(x => x.CClassTrib is not null))
        {
            if (!i.Classificacoes.TryGetValue(it.CClassTrib!, out var c))
            {
                continue;
            }

            var trat = c.TratamentoTributario.ToLowerInvariant();
            if (c.PossuiPercentualReducao || TratamentosCondicionados.Any(t => trat.Contains(t, StringComparison.Ordinal)))
            {
                findings.Add(F(RuleCatalog.R021, it.Numero, $"det[{it.Numero}]/imposto/IBSCBS/cClassTrib", it.CClassTrib, null,
                    $"cClassTrib {it.CClassTrib} ({c.TratamentoTributario}) depende de requisitos que o XML não comprova. Confirme o enquadramento com o fornecedor "
                    + "(ou informe confirmar=R021).", ImpactoIndicio(it)));
            }
        }
    }

    /// <summary>
    /// R040 (entrada): crédito por destaque (art. 48) enquanto não houver split payment; só itens cuja classificação
    /// permite apropriação. Fornecedor do Simples: o valor pago no DAS (art. 47 §9º). Divergência determinística no
    /// documento = crédito limitado ao menor valor entre declarado e recalculado, em risco.
    /// </summary>
    private static (decimal, string) Credit(AuditInput i, List<Finding> findings)
    {
        var divergente = findings.Any(f => f.Natureza == Natureza.Deterministica);
        var regByItem = i.Regular?.Itens.ToDictionary(x => x.Numero) ?? [];
        decimal total = 0;
        foreach (var it in i.Doc.Itens)
        {
            if (it.Simples is { } sn)
            {
                total += (sn.ValorIbs ?? 0) + sn.ValorCbs;
            }

            if (it.CClassTrib is null || !i.Classificacoes.TryGetValue(it.CClassTrib, out var c))
            {
                continue;
            }

            decimal cbs = c.CreditoAdquirenteCbs ? it.Cbs?.Valor ?? 0 : 0;
            decimal ibs = c.CreditoAdquirenteIbs ? it.VIbs ?? 0 : 0;
            if (divergente && regByItem.TryGetValue(it.Numero, out var e))
            {
                cbs = Math.Min(cbs, c.CreditoAdquirenteCbs ? e.Cbs?.Valor ?? 0 : 0);
                ibs = Math.Min(ibs, c.CreditoAdquirenteIbs ? e.IbsTotal ?? 0 : 0);
            }

            total += cbs + ibs;
        }

        var status = divergente ? "RISCO_DIVERGENCIA" : i.CreditoStatusPadrao;
        findings.Add(F(RuleCatalog.R040, null, "NFe/infNFe/total/IBSCBSTot", null, Fmt(total),
            $"Crédito potencial de {Fmt(total)} ({status})."));
        return (total, status);
    }

    private static AuditOutcome Finish(AuditInput i, List<Finding> findings, bool indeterminado, TaxSummary declarado, TaxSummary? recalc,
        decimal diferenca, (decimal Valor, string Status)? credit)
    {
        var status = findings.Any(f => f.Natureza == Natureza.Deterministica) ? AuditStatus.Divergente
            : indeterminado || findings.Any(f => f.Regra == RuleCatalog.I003.Id) ? AuditStatus.Indeterminado
            : findings.Any(f => f.Natureza == Natureza.Indicio) ? AuditStatus.Indicio
            : AuditStatus.Conforme;
        return new AuditOutcome(status, findings, declarado, recalc, diferenca, credit?.Valor, credit?.Status);
    }

    private static TaxSummary Summary(DeclaredDocument d) => new(
        d.Itens.Sum(x => (x.Cbs?.Valor ?? 0) + (x.Simples?.ValorCbs ?? 0)),
        d.Itens.Sum(x => (x.VIbs ?? 0) + (x.Simples?.ValorIbs ?? 0)),
        d.Itens.Sum(x => x.ImpostoSeletivo?.Valor ?? 0));

    private static decimal? ImpactoIndicio(DeclaredItem it) => (it.Cbs?.Valor ?? 0) + (it.VIbs ?? 0) is var v && v > 0 ? v : null;

    private static Finding F(RuleDefinition r, int? item, string? campo, string? declarado, string? referencia, string msg, decimal? impacto = null) =>
        new(r.Id, r.Versao, r.Natureza, r.Severidade, item, campo, declarado, referencia, msg, r.Fundamentacao, impacto);

    private static string Fmt(decimal v) => v.ToString("0.00##", CultureInfo.InvariantCulture);
}
