using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

using Npgsql;

namespace TaxGateway.GoldenTests;

/// <summary>
/// Corpus golden v0 (SDD §13; D-008): casos sintéticos de cobertura nacional. Cada caso guarda a requisição da API,
/// a requisição equivalente do motor montada AQUI (independente do código da API) e a resposta do motor como
/// snapshot. Nenhuma alíquota é escrita à mão: as de 2027+ vêm do catálogo carregado no banco.
/// Regenerar só com aprovação humana do diff: GOLDEN_REGENERATE=1 dotnet test tests/TaxGateway.GoldenTests.
/// </summary>
public static class GoldenCorpus
{
    public static readonly (string Uf, int Municipio, string Offset)[] Capitais =
    [
        ("RO", 1100205, "-04:00"), ("AC", 1200401, "-05:00"), ("AM", 1302603, "-04:00"), ("RR", 1400100, "-04:00"),
        ("PA", 1501402, "-03:00"), ("AP", 1600303, "-03:00"), ("TO", 1721000, "-03:00"), ("MA", 2111300, "-03:00"),
        ("PI", 2211001, "-03:00"), ("CE", 2304400, "-03:00"), ("RN", 2408102, "-03:00"), ("PB", 2507507, "-03:00"),
        ("PE", 2611606, "-03:00"), ("AL", 2704302, "-03:00"), ("SE", 2800308, "-03:00"), ("BA", 2927408, "-03:00"),
        ("MG", 3106200, "-03:00"), ("ES", 3205309, "-03:00"), ("RJ", 3304557, "-03:00"), ("SP", 3550308, "-03:00"),
        ("PR", 4106902, "-03:00"), ("SC", 4205407, "-03:00"), ("RS", 4314902, "-03:00"), ("MS", 5002704, "-04:00"),
        ("MT", 5103403, "-04:00"), ("GO", 5208707, "-03:00"), ("DF", 5300108, "-03:00"),
    ];

    public sealed record Item(int Numero, string Cst, string CClassTrib, decimal Base, string? Ncm = null, string? Nbs = null,
        decimal? Quantidade = null, string? Unidade = null, JsonObject? Is = null, JsonObject? ClientRates = null,
        string? AtividadeSn = null, decimal? PIbsSn = null, decimal? PCbsSn = null);

    /// <summary>Crt: null = regime regular (CRT 3); "1"/"2" = Simples (pleno/sublimite). TpRegime "1" = opção pelo regime regular.</summary>
    public sealed record Spec(string Id, string Descricao, string Data, int Municipio, string Uf, Item[] Itens, JsonObject? CompraGov = null,
        string? Crt = null, string? TpRegime = null)
    {
        public bool UsaSimples => Crt is "1" or "2" && TpRegime is null;
        public bool UsaRegular => Crt != "1" || TpRegime == "1";
    }

    private static readonly Item Integral = new(1, "000", "000001", 3000m, Ncm: "84713012", Quantidade: 1, Unidade: "UN");
    private static readonly Item Arroz = new(2, "200", "200003", 100m, Ncm: "10063021", Quantidade: 20, Unidade: "KG");
    private static readonly Item Servico = new(3, "000", "000001", 1200m, Nbs: "122011200");

    public static async Task<List<Spec>> BuildSpecsAsync(HttpClient engine)
    {
        var specs = new List<Spec>();
        static string Date(string day, string offset, string time = "14:00:00") => $"{day}T{time}{offset}";

        // A: todas as classificações cbs-ibs vigentes em 2027, uma capital por caso (rotativo).
        var classes = await engine.GetFromJsonAsync<JsonArray>("calculadora/dados-abertos/classificacoes-tributarias/cbs-ibs?data=2027-06-15");
        var i = 0;
        foreach (var c in classes!.Select(x => x!.AsObject()).OrderBy(x => x["codigo"]!.GetValue<string>(), StringComparer.Ordinal))
        {
            var code = c["codigo"]!.GetValue<string>();
            var nom = c["nomenclatura"]?.GetValue<string>() ?? "";
            var (uf, mun, off) = Capitais[i++ % Capitais.Length];
            var item = nom.Contains("NCM", StringComparison.Ordinal) || !nom.Contains("NBS", StringComparison.Ordinal)
                ? new Item(1, code[..3], code, 1000m, Ncm: "84713012", Quantidade: 1, Unidade: "UN")
                : new Item(1, code[..3], code, 1000m, Nbs: "122011200");
            specs.Add(new Spec($"A-{code}-{uf}", $"cClassTrib {code} ({c["descricaoTratamentoTributario"]}) em 2027", Date("2027-06-15", off), mun, uf, [item]));
        }

        // B: 2026 (alíquotas da base oficial do motor) em todas as UFs, itens integral + redução + serviço.
        foreach (var (uf, mun, off) in Capitais)
        {
            specs.Add(new Spec($"B-2026-{uf}", "2026: CBS 0,9% e IBS 0,1% da base oficial", Date("2026-03-10", off), mun, uf, [Integral, Arroz, Servico]));
        }

        // C: 2027 (catálogo) em todas as UFs, cinco itens.
        foreach (var (uf, mun, off) in Capitais)
        {
            specs.Add(new Spec($"C-2027-{uf}", "2027: catálogo (CBS provisória do TCU) com cinco itens", Date("2027-02-01", off, "09:30:00"), mun, uf,
                [Integral, Arroz, Servico, Integral with { Numero = 4, Base = 0.01m }, Servico with { Numero = 5, Base = 999_999.99m }]));
        }

        // D: uma classificação em cada três também em 2026.
        var j = 0;
        foreach (var a in specs.Where(s => s.Id.StartsWith("A-", StringComparison.Ordinal)).Where((_, k) => k % 3 == 0).ToList())
        {
            var (uf, mun, off) = Capitais[(j++ * 5) % Capitais.Length];
            specs.Add(a with { Id = a.Id.Replace("A-", "D-", StringComparison.Ordinal)[..^2] + uf, Descricao = a.Descricao.Replace("2027", "2026", StringComparison.Ordinal), Data = Date("2026-08-20", off), Municipio = mun, Uf = uf });
        }

        // F: municípios do interior (fora das capitais), 2027.
        foreach (var (uf, mun) in new[] { ("SP", 3509502), ("MG", 3170206), ("RS", 4305108), ("MG", 3136702), ("BA", 2910800),
                                           ("SC", 4209102), ("PR", 4113700), ("PA", 1506807) })
        {
            specs.Add(new Spec($"F-interior-{uf}-{mun}", "Município do interior (IBS municipal do destino)", Date("2027-06-15", "-03:00"), mun, uf, [Integral, Servico]));
        }

        // S: Simples Nacional (D-502). Pleno (CRT 1) nas 27 UFs; sublimite (CRT 2) e opção pelo regime regular.
        var k = 0;
        foreach (var (uf, mun, off) in Capitais)
        {
            var atv = (k % 4 + 1).ToString("D2", CultureInfo.InvariantCulture);
            var pi = 0.20m + (k % 7) * 0.15m;
            var pc = 0.60m + (k % 5) * 0.35m;
            specs.Add(new Spec($"S-pleno-{uf}", "Simples pleno (CRT 1): IBS e CBS no DAS", Date("2027-06-15", off), mun, uf,
                [Integral with { AtividadeSn = atv, PIbsSn = pi, PCbsSn = pc }, Arroz with { AtividadeSn = atv, PIbsSn = pi, PCbsSn = pc }],
                Crt: "1"));
            k++;
        }

        foreach (var (uf, mun, off) in Capitais.Where((_, idx) => idx % 3 == 0))
        {
            specs.Add(new Spec($"S-sublimite-{uf}", "Sublimite (CRT 2): CBS no DAS, IBS pelo regime regular", Date("2027-06-15", off), mun, uf,
                [Integral with { AtividadeSn = "01", PCbsSn = 1.10m }], Crt: "2"));
            specs.Add(new Spec($"S-opcao-regular-{uf}", "Optante que apura IBS/CBS pelo regime regular (tpRegimeIBSCBS = 1)", Date("2027-06-15", off), mun, uf,
                [Integral, Arroz], Crt: "1", TpRegime: "1"));
        }

        // E: casos-limite.
        specs.Add(new Spec("E-fuso-sp-2026-12-31", "23:30 de 31/12/2026 em SP (UTC já é 2027): vale 2026, sem alíquota nominal",
            "2026-12-31T23:30:00-03:00", 3550308, "SP", [Integral]));
        specs.Add(new Spec("E-fuso-ac-2027-01-01", "00:10 de 01/01/2027 no Acre (UTC−5): vale 2027", "2027-01-01T00:10:00-05:00", 1200401, "AC", [Integral]));
        specs.Add(new Spec("E-fuso-noronha", "Fernando de Noronha (UTC−2)", "2027-03-01T08:00:00-02:00", 2605459, "PE", [Integral, Servico]));
        specs.Add(new Spec("E-exterior", "Operação com o exterior (município 9999999, UF EX)", Date("2027-06-15", "-03:00"), 9999999, "EX", [Integral]));
        specs.Add(new Spec("E-cliente-sobrescreve", "Alíquotas nominais informadas pelo cliente", Date("2027-06-15", "-03:00"), 3550308, "SP",
            [Integral with { ClientRates = new JsonObject { ["cbs"] = 8.8m, ["ibs_uf"] = 0.1m, ["ibs_mun"] = 0.1m } }]));
        specs.Add(new Spec("E-cliente-parcial", "Cliente informa só a CBS; IBS vem do catálogo", Date("2027-06-15", "-03:00"), 4314902, "RS",
            [Servico with { ClientRates = new JsonObject { ["cbs"] = 9.5m } }]));
        specs.Add(new Spec("E-imposto-seletivo", "Imposto Seletivo com alíquotas do cliente", Date("2027-06-15", "-03:00"), 3550308, "SP",
            [new Item(1, "000", "000001", 100m, Ncm: "24022000", Quantidade: 10, Unidade: "UN",
                Is: new JsonObject { ["cst"] = "000", ["c_class_trib"] = "000001", ["valor_base_calculo"] = 100m, ["quantidade"] = 10m, ["unidade"] = "UN" },
                ClientRates: new JsonObject { ["is_ad_valorem"] = 10m, ["is_ad_rem"] = 0.5m })]));
        specs.Add(new Spec("E-compra-gov-uniao", "Compra governamental (União)", Date("2027-06-15", "-03:00"), 5300108, "DF", [Integral],
            new JsonObject { ["tipo_ente"] = 1, ["tipo_operacao"] = 1 }));
        specs.Add(new Spec("E-compra-gov-municipio-2026", "Compra governamental (município) em 2026", Date("2026-06-15", "-03:00"), 3106200, "MG", [Servico],
            new JsonObject { ["tipo_ente"] = 3, ["tipo_operacao"] = 1 }));
        specs.Add(new Spec("E-ncm-inexistente", "NCM inexistente: erro por item (REG-002)", Date("2027-06-15", "-03:00"), 3550308, "SP",
            [Integral, new Item(2, "000", "000001", 10m, Ncm: "99999999")]));
        specs.Add(new Spec("E-cclasstrib-inexistente", "cClassTrib inexistente", Date("2026-06-15", "-03:00"), 3550308, "SP",
            [new Item(1, "000", "000999", 10m, Ncm: "84713012")]));
        specs.Add(new Spec("E-base-zero", "Base de cálculo zero", Date("2027-06-15", "-03:00"), 2927408, "BA", [Integral with { Base = 0m }]));
        specs.Add(new Spec("E-vinte-itens", "Documento com 20 itens (orçamento do SLO)", Date("2027-06-15", "-03:00"), 4106902, "PR",
            Enumerable.Range(1, 20).Select(n => (n % 3) switch
            {
                0 => Servico with { Numero = n, Base = 10m * n },
                1 => Integral with { Numero = n, Base = 15.37m * n },
                _ => Arroz with { Numero = n, Base = 3.33m * n },
            }).ToArray()));
        return specs;
    }

    /// <summary>Requisição da API pública (snake_case) para o caso.</summary>
    public static JsonObject ApiRequest(Spec s)
    {
        var itens = new JsonArray();
        foreach (var it in s.Itens)
        {
            var o = new JsonObject { ["numero"] = it.Numero, ["cst_ibs_cbs"] = it.Cst, ["c_class_trib"] = it.CClassTrib, ["valor_base_calculo"] = it.Base };
            if (it.Ncm is not null) { o["ncm"] = it.Ncm; }
            if (it.Nbs is not null) { o["nbs"] = it.Nbs; }
            if (it.Quantidade is not null) { o["quantidade"] = it.Quantidade; }
            if (it.Unidade is not null) { o["unidade"] = it.Unidade; }
            if (it.Is is not null) { o["imposto_seletivo"] = it.Is.DeepClone(); }
            if (it.ClientRates is not null) { o["aliquotas_nominais"] = it.ClientRates.DeepClone(); }
            if (it.AtividadeSn is not null && s.TpRegime is null) { o["codigo_atividade_sn"] = it.AtividadeSn; }
            if ((it.PIbsSn is not null || it.PCbsSn is not null) && s.TpRegime is null)
            {
                var a = new JsonObject();
                if (it.PIbsSn is not null) { a["ibs"] = it.PIbsSn; }
                if (it.PCbsSn is not null) { a["cbs"] = it.PCbsSn; }
                o["aliquotas_simples"] = a;
            }

            itens.Add(o);
        }

        var req = new JsonObject
        {
            ["cnpj_emitente"] = "11222333000181",
            ["regime_tributario"] = s.Crt is null ? "REGIME_REGULAR" : "SIMPLES_NACIONAL",
            ["data_fato_gerador"] = s.Data,
            ["municipio_ibge"] = s.Municipio,
            ["uf"] = s.Uf,
            ["itens"] = itens,
        };
        if (s.Crt is not null) { req["crt"] = s.Crt; }
        if (s.TpRegime is not null) { req["tp_regime_ibs_cbs"] = s.TpRegime; }
        if (s.CompraGov is not null) { req["compra_governamental"] = s.CompraGov.DeepClone(); }
        return req;
    }

    /// <summary>Requisição equivalente do motor, montada de forma independente do código da API.</summary>
    public static JsonObject EngineRequest(Spec s, IReadOnlyDictionary<string, decimal>? catalogRates)
    {
        var itens = new JsonArray();
        foreach (var it in s.Itens)
        {
            var o = new JsonObject { ["numero"] = it.Numero };
            if (it.Ncm is not null) { o["ncm"] = it.Ncm; }
            if (it.Nbs is not null) { o["nbs"] = it.Nbs; }
            o["cst"] = it.Cst;
            o["cClassTrib"] = it.CClassTrib;
            o["baseCalculo"] = it.Base;
            if (it.Quantidade is not null) { o["quantidade"] = it.Quantidade; }
            if (it.Unidade is not null) { o["unidade"] = it.Unidade; }
            if (it.Is is not null)
            {
                o["impostoSeletivo"] = new JsonObject
                {
                    ["cst"] = it.Is["cst"]!.DeepClone(),
                    ["cClassTrib"] = it.Is["c_class_trib"]!.DeepClone(),
                    ["baseCalculo"] = it.Is["valor_base_calculo"]!.DeepClone(),
                    ["quantidade"] = it.Is["quantidade"]?.DeepClone(),
                    ["unidade"] = it.Is["unidade"]?.DeepClone(),
                };
            }

            if (catalogRates is not null)
            {
                decimal Rate(string client, string catalog) =>
                    it.ClientRates?[client] is { } v ? v.GetValue<decimal>() : catalogRates[catalog];
                // Sublimite: CBS vai no DAS, então a CBS do regime regular é zero (§8.1).
                var cbs = s.Crt == "2" && s.TpRegime is null ? 0m : Rate("cbs", "CBS");
                var n = new JsonObject { ["cbs"] = cbs, ["ibsEstadual"] = Rate("ibs_uf", "IBS_UF"), ["ibsMunicipal"] = Rate("ibs_mun", "IBS_MUN") };
                if (it.ClientRates?["is_ad_valorem"] is not null || it.ClientRates?["is_ad_rem"] is not null)
                {
                    n["impostoSeletivo"] = new JsonObject { ["adValorem"] = it.ClientRates?["is_ad_valorem"]?.DeepClone(), ["adRem"] = it.ClientRates?["is_ad_rem"]?.DeepClone() };
                }

                o["aliquotasNominais"] = n;
            }

            itens.Add(o);
        }

        var req = new JsonObject
        {
            ["id"] = "golden",
            ["versao"] = "1.0.0",
            ["dhFatoGerador"] = s.Data,
            ["municipio"] = s.Municipio,
            ["uf"] = s.Uf,
        };
        if (s.CompraGov is not null)
        {
            req["gCompraGov"] = new JsonObject { ["tpEnteGov"] = s.CompraGov["tipo_ente"]!.DeepClone(), ["tpOperGov"] = s.CompraGov["tipo_operacao"]?.DeepClone() };
        }

        req["itens"] = itens;
        return req;
    }

    /// <summary>Requisição do motor do Simples, montada de forma independente da API.</summary>
    public static JsonObject SimplesRequest(Spec s)
    {
        var itens = new JsonArray();
        foreach (var it in s.Itens)
        {
            var o = new JsonObject
            {
                ["numero"] = it.Numero,
                ["cAtivSN"] = it.AtividadeSn,
                ["cClassTrib"] = it.CClassTrib,
                ["baseCalculo"] = it.Base.ToString("0.00", CultureInfo.InvariantCulture),
                ["valorImpostoSeletivo"] = "0.00",
            };
            if (it.Ncm is not null) { o["ncm"] = it.Ncm; }
            if (it.Nbs is not null) { o["nbs"] = it.Nbs; }
            if (s.Crt == "1" && it.PIbsSn is { } pi) { o["pIBSSN"] = pi.ToString("0.00##", CultureInfo.InvariantCulture); }
            if (it.PCbsSn is { } pc) { o["pCBSSN"] = pc.ToString("0.00##", CultureInfo.InvariantCulture); }
            itens.Add(o);
        }

        var date = DateOnly.FromDateTime(DateTimeOffset.Parse(s.Data, CultureInfo.InvariantCulture).DateTime);
        return new JsonObject
        {
            ["id"] = "tg",
            ["versao"] = "1.0.0",
            ["data"] = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["tpDoc"] = "55",
            ["CRT"] = s.Crt,
            ["itens"] = itens,
        };
    }

    /// <summary>Alíquotas nacionais de referência vigentes na data, lidas do catálogo carregado no banco.</summary>
    public static async Task<Dictionary<string, decimal>?> CatalogRatesAsync(string appConnection, string dataFatoGerador)
    {
        // Data local do offset do documento (SDD §8.6). Antes de 2027 o motor não aceita alíquota nominal.
        var date = DateOnly.FromDateTime(DateTimeOffset.Parse(dataFatoGerador, CultureInfo.InvariantCulture).DateTime);
        if (date < new DateOnly(2027, 1, 1))
        {
            return null;
        }

        await using var conn = new NpgsqlConnection(appConnection);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("""
            SELECT tributo, aliquota FROM rate_catalog
             WHERE superseded_at IS NULL AND ente_codigo IS NULL AND valid_from <= $1 AND (valid_to IS NULL OR valid_to > $1)
            """, conn);
        cmd.Parameters.Add(new() { Value = date });
        var rates = new Dictionary<string, decimal>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            rates[r.GetString(0)] = r.GetDecimal(1);
        }

        return rates;
    }

    public static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
}
