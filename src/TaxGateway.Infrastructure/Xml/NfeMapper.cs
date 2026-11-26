using System.Globalization;
using System.Xml;

using TaxGateway.Core.Audit;
using TaxGateway.Core.Engines;

namespace TaxGateway.Infrastructure.Xml;

/// <summary>NF-e/NFC-e lida: nós usados pela assinatura + o documento declarado.</summary>
public sealed record ParsedNfe(XmlElement Nfe, XmlElement InfNFe, DeclaredDocument Document);

/// <summary>
/// Mapeador NF-e/NFC-e 4.00 (leiaute PL_010, NT 2025.002) → declarado (SDD §8.2; D-305/D-306).
/// Lê só o necessário à auditoria; nome, endereço e e-mail de pessoas nunca saem daqui.
/// Grupos ainda não suportados geram I001 (MAP_UNSUPPORTED_FIELD), sem falha silenciosa.
/// </summary>
public static class NfeMapper
{
    private const string Ns = NfeSchemaValidator.NfeNamespace;

    /// <summary>Grupos do IBSCBS que o mapeador v1 não audita (choice do TTribNFe e créditos).</summary>
    private static readonly string[] Unsupported =
        ["gIBSCBSMono", "gTransfCred", "gAjusteCompet", "gEstornoCred", "gCredPresOper", "gCredPresIBSZFM"];

    public static ParsedNfe Map(XmlDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        var ns = new XmlNamespaceManager(doc.NameTable);
        ns.AddNamespace("n", Ns);
        var root = doc.DocumentElement ?? throw new XmlRejectedException("Documento vazio.");
        XmlElement nfe;
        var autorizada = false;
        if (root.LocalName == "nfeProc" && root.NamespaceURI == Ns)
        {
            nfe = root.SelectSingleNode("n:NFe", ns) as XmlElement ?? throw new XmlRejectedException("nfeProc sem NFe.");
            var cStat = root.SelectSingleNode("n:protNFe/n:infProt/n:cStat", ns)?.InnerText;
            autorizada = cStat is "100" or "150";
        }
        else if (root.LocalName == "NFe" && root.NamespaceURI == Ns)
        {
            nfe = root;
        }
        else
        {
            throw new XmlRejectedException("Raiz deve ser NFe ou nfeProc no namespace da NF-e (NFS-e entra na Fase 5).");
        }

        var infs = nfe.SelectNodes("n:infNFe", ns)!;
        if (infs.Count != 1)
        {
            throw new XmlRejectedException("NFe deve conter exatamente um infNFe.");
        }

        var inf = (XmlElement)infs[0]!;
        var id = inf.GetAttribute("Id");
        var chave = id.StartsWith("NFe", StringComparison.Ordinal) ? id[3..] : id;

        string? T(XmlNode ctx, string xpath) => ctx.SelectSingleNode(xpath, ns)?.InnerText;
        decimal? D(XmlNode? ctx, string xpath) =>
            ctx?.SelectSingleNode(xpath, ns)?.InnerText is { Length: > 0 } s
                && decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? v : null;

        var dhEmi = DateTimeOffset.TryParse(T(inf, "n:ide/n:dhEmi"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var dh) ? dh : default;
        var munFg = int.TryParse(T(inf, "n:ide/n:cMunFGIBS") ?? T(inf, "n:ide/n:cMunFG"), out var m) ? m : 0;
        CompraGovernamental? gov = inf.SelectSingleNode("n:ide/n:gCompraGov", ns) is { } g
            && int.TryParse(T(g, "n:tpEnteGov"), out var te)
            ? new CompraGovernamental(te, int.TryParse(T(g, "n:tpOperGov"), out var to) ? to : null)
            : null;

        var itens = new List<DeclaredItem>();
        foreach (XmlElement det in inf.SelectNodes("n:det", ns)!)
        {
            var n = int.TryParse(det.GetAttribute("nItem"), out var ni) ? ni : itens.Count + 1;
            var prod = det.SelectSingleNode("n:prod", ns);
            var imp = det.SelectSingleNode("n:imposto", ns);
            var comps = new BaseComponents(
                D(prod, "n:vProd") ?? 0, D(prod, "n:vFrete"), D(prod, "n:vSeg"), D(prod, "n:vOutro"), D(prod, "n:vDesc"),
                D(imp, "n:II/n:vII"), D(imp, "n:IS/n:vIS"), D(imp, "n:PIS/*/n:vPIS"), D(imp, "n:COFINS/*/n:vCOFINS"),
                D(imp, "n:ICMS/*/n:vICMS"), D(imp, "n:ICMSUFDest/n:vICMSUFDest"), D(imp, "n:ICMS/*/n:vFCP"),
                D(imp, "n:ICMSUFDest/n:vFCPUFDest"), D(imp, "n:ICMS/*/n:vICMSMono"), D(imp, "n:ISSQN/n:vISSQN"));

            var ibscbs = imp?.SelectSingleNode("n:IBSCBS", ns);
            var gib = ibscbs?.SelectSingleNode("n:gIBSCBS", ns);
            var naoSuportados = ibscbs is null ? []
                : Unsupported.Where(u => ibscbs.SelectSingleNode($"n:{u}", ns) is not null).ToList();

            DeclaredTax? Tax(string group, string p, string v)
            {
                var x = gib?.SelectSingleNode($"n:{group}", ns);
                return x is null ? null : new DeclaredTax(
                    D(x, $"n:{p}") ?? 0, D(x, $"n:{v}") ?? 0, D(x, "n:gRed/n:pRedAliq"), D(x, "n:gRed/n:pAliqEfet"),
                    D(x, "n:gDif/n:pDif"), D(x, "n:gDif/n:vDif"), D(x, "n:gDevTrib/n:vDevTrib"));
            }

            DeclaredIs? isDecl = imp?.SelectSingleNode("n:IS", ns) is { } isg
                ? new DeclaredIs(T(isg, "n:CSTIS") ?? "", T(isg, "n:cClassTribIS") ?? "", D(isg, "n:vBCIS") ?? 0, D(isg, "n:pIS") ?? 0,
                    D(isg, "n:adRemIS"), D(isg, "n:qTrib"), T(isg, "n:uTrib"), D(isg, "n:vIS") ?? 0)
                : null;
            RegularTaxation? reg = gib?.SelectSingleNode("n:gTribRegular", ns) is { } r
                ? new RegularTaxation(T(r, "n:CSTReg") ?? "", T(r, "n:cClassTribReg") ?? "")
                : null;

            var sn = ibscbs?.SelectSingleNode("n:gTribSN", ns);
            DeclaredSimples? simples = sn is null ? null : new DeclaredSimples(
                D(sn, "n:gIBSSN/n:pIBSSN"), D(sn, "n:gIBSSN/n:vIBSSN"), D(sn, "n:gCBSSN/n:pCBSSN") ?? 0, D(sn, "n:gCBSSN/n:vCBSSN") ?? 0);

            itens.Add(new DeclaredItem(n, T(det, "n:prod/n:NCM"), ibscbs is null ? null : T(ibscbs, "n:CST"),
                ibscbs is null ? null : T(ibscbs, "n:cClassTrib"), D(gib, "n:vBC"),
                Tax("gIBSUF", "pIBSUF", "vIBSUF"), Tax("gIBSMun", "pIBSMun", "vIBSMun"), D(gib, "n:vIBS"),
                Tax("gCBS", "pCBS", "vCBS"), isDecl, comps, reg, naoSuportados, simples, T(det, "n:prod/n:cAtivSN")));
        }

        DeclaredTotals? totais = null;
        if (inf.SelectSingleNode("n:total/n:IBSCBSTot", ns) is { } tot)
        {
            totais = new DeclaredTotals(
                D(tot, "n:vBCIBSCBS") ?? 0, D(tot, "n:gIBS/n:gIBSUF/n:vIBSUF") ?? 0, D(tot, "n:gIBS/n:gIBSMun/n:vIBSMun") ?? 0,
                D(tot, "n:gIBS/n:vIBS") ?? 0, D(tot, "n:gCBS/n:vCBS") ?? 0,
                D(tot, "n:gIBS/n:gIBSUF/n:vDif") ?? 0, D(tot, "n:gIBS/n:gIBSMun/n:vDif") ?? 0, D(tot, "n:gCBS/n:vDif") ?? 0,
                D(tot, "n:gIBS/n:gIBSUF/n:vDevTrib") ?? 0, D(tot, "n:gIBS/n:gIBSMun/n:vDevTrib") ?? 0, D(tot, "n:gCBS/n:vDevTrib") ?? 0,
                D(inf, "n:total/n:ISTot/n:vIS"), D(tot, "n:gTribSN/n:vIBSSN"), D(tot, "n:gTribSN/n:vCBSSN"));
        }

        var document = new DeclaredDocument(
            chave, T(inf, "n:ide/n:mod") ?? "", dhEmi, T(inf, "n:emit/n:CNPJ") ?? "", T(inf, "n:emit/n:CRT"),
            T(inf, "n:emit/n:tpRegimeIBSCBS"), T(inf, "n:dest/n:CNPJ"), T(inf, "n:dest/n:CPF"),
            T(inf, "n:ide/n:indFinal") == "1", munFg, int.TryParse(T(inf, "n:ide/n:finNFe"), out var fin) ? fin : 1,
            autorizada, gov, itens, totais);
        return new ParsedNfe(nfe, inf, document);
    }

    /// <summary>
    /// Operação do motor para auditar (D-305): CST/cClassTrib declarados, componentes do item (o motor calcula o
    /// vBC) e as alíquotas nominais do catálogo a partir de 2027 (não as declaradas: R012 compara as duas).
    /// </summary>
    /// <summary>Operação do motor do Simples a partir do gTribSN declarado (alíquotas efetivas do emitente, D-503).</summary>
    public static SimplesOperation ToSimplesOperation(DeclaredDocument d)
    {
        ArgumentNullException.ThrowIfNull(d);
        var itens = d.Itens.Where(i => i.Simples is not null && i.CClassTrib is not null)
            .Select(i => new SimplesItemInput(i.Numero, i.CodigoAtividadeSn, i.CClassTrib!, i.Ncm, null, i.BaseSimples,
                i.ImpostoSeletivo?.Valor ?? 0, d.Crt == "1" ? i.Simples!.AliquotaIbs : null, i.Simples!.AliquotaCbs))
            .ToList();
        return new SimplesOperation(DateOnly.FromDateTime(d.DataEmissao.DateTime), int.TryParse(d.Modelo, out var tp) ? tp : 55,
            d.Crt ?? "1", d.TpRegimeIbsCbs, d.CompraGovernamental?.TipoEnte, itens);
    }

    public static EngineOperation ToEngineOperation(DeclaredDocument d, string uf, Func<DeclaredItem, NominalRates?> rates)
    {
        ArgumentNullException.ThrowIfNull(d);
        ArgumentNullException.ThrowIfNull(rates);
        var itens = d.Itens
            .Where(i => i.Cst is not null && i.CClassTrib is not null && i.GruposNaoSuportados.Count == 0)
            .Select(i => new EngineItemInput(
                i.Numero, i.Ncm, null, i.Cst!, i.CClassTrib!, null, null, null,
                i.ImpostoSeletivo is { } s ? new EngineIsInput(s.Cst, s.CClassTrib, s.BaseCalculo, s.Quantidade, s.Unidade) : null,
                rates(i), i.Componentes, i.TributacaoRegular))
            .ToList();
        return new EngineOperation("tg-audit", d.DataEmissao, d.MunicipioIncidencia, uf,
            int.TryParse(d.Modelo, out var tp) ? tp : 55, d.CompraGovernamental, itens);
    }
}
