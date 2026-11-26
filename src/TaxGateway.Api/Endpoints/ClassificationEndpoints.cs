using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.Extensions.Caching.Memory;

using TaxGateway.Api.Http;
using TaxGateway.Infrastructure.Engines;

namespace TaxGateway.Api.Endpoints;

/// <summary>
/// Consulta de cClassTrib/CST por NCM ou NBS (D-J02; revisão de produto L1 — a maior dor de integração). Junta os
/// dados abertos do motor oficial na data: a descrição do NCM/NBS (capítulo, posição, Imposto Seletivo) e as
/// classificações compatíveis com a nomenclatura e o modelo do documento, ordenadas por proximidade com a descrição do
/// item e com o texto informado. É um ATALHO de pesquisa, não uma determinação: as reduções dos anexos da LC 214/2025
/// dependem do enquadramento do produto, que é do contribuinte — a resposta traz esse aviso.
/// </summary>
public static class ClassificationEndpoints
{
    public static RouteGroupBuilder MapClassification(this RouteGroupBuilder v1)
    {
        v1.MapGet("/classificacao", LookupAsync).WithMetadata(new RequiredScope(Scopes.SimulacaoWrite, Scopes.AuditoriaRead));
        return v1;
    }

    private sealed record ClassRow(string Codigo, string Descricao, string Tratamento, string Nomenclatura, string TipoAliquota,
        decimal ReducaoCbs, decimal ReducaoIbsUf, decimal ReducaoIbsMun, bool CreditoCbs, bool CreditoIbs, int[] Modelos);

    private static readonly string[] DescriptionKeys = ["capitulo", "posicao", "subitem", "item"];

    private static readonly string[] Stop = ["de", "da", "do", "das", "dos", "e", "ou", "a", "o", "as", "os", "em", "com", "para", "por", "que", "na", "no", "nas", "nos", "outros", "outras", "exceto", "incluindo", "nao", "sem", "sob", "sobre", "como", "mais", "pelo", "pela", "pelos", "pelas", "entre", "ate", "seu", "sua", "seus", "suas", "outro", "outra", "qualquer", "demais", "partes", "tipo", "tipos", "uso"];

    private static async Task<IResult> LookupAsync(HttpContext http, IHttpClientFactory httpFactory, IMemoryCache cache, TimeProvider clock,
        string? ncm, string? nbs, string? q, string? data, int? modelo)
    {
        if ((ncm is null) == (nbs is null))
        {
            return ApiProblem.Result(400, "ERR_VALIDATION", "Informe ncm OU nbs", "Ex.: /v1/classificacao?ncm=22021000 ou ?nbs=101011100.");
        }

        var tipo = ncm is not null ? "NCM" : "NBS";
        var codigo = (ncm ?? nbs)!.Replace(".", "", StringComparison.Ordinal);
        if (!(tipo == "NCM" ? DocumentEndpoints.IsAsciiDigits(codigo, 8) : DocumentEndpoints.IsAsciiDigits(codigo, 9)))
        {
            return ApiProblem.Result(400, "ERR_VALIDATION", tipo == "NCM" ? "NCM com 8 dígitos" : "NBS com 9 dígitos");
        }

        DateOnly date;
        if (data is null)
        {
            date = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        }
        else if (!DateOnly.TryParseExact(data, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date)
                 || date.Year is < 2026 or > 2100)
        {
            return ApiProblem.Result(400, "ERR_VALIDATION", "data no formato AAAA-MM-DD, a partir de 2026");
        }

        var model = modelo ?? 55;
        if (model is not (55 or 65))
        {
            return ApiProblem.Result(400, "ERR_VALIDATION", "modelo 55 (NF-e) ou 65 (NFC-e)");
        }

        if (q is { Length: > 100 })
        {
            return ApiProblem.Result(400, "ERR_VALIDATION", "q com até 100 caracteres");
        }

        var engine = httpFactory.CreateClient(RfbReferenceData.Client);
        JsonObject? item;
        try
        {
            item = await cache.GetOrCreateAsync($"class:{tipo}:{codigo}:{date:yyyyMMdd}", async e =>
            {
                e.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(6);
                e.Size = 1;
                using var resp = await engine.GetAsync(new Uri($"calculadora/dados-abertos/{tipo.ToLowerInvariant()}?{tipo.ToLowerInvariant()}={codigo}&data={date:yyyy-MM-dd}", UriKind.Relative), http.RequestAborted);
                return resp.IsSuccessStatusCode ? JsonNode.Parse(await resp.Content.ReadAsStringAsync(http.RequestAborted)) as JsonObject : null;
            });
        }
        catch (HttpRequestException)
        {
            return ApiProblem.Result(503, "ERR_ENGINE_UNAVAILABLE", "Motor indisponível para a consulta", "Tente de novo em alguns segundos.");
        }

        if (item is null || item.Count == 0)
        {
            return ApiProblem.Result(404, "ERR_NOT_FOUND", $"{tipo} {codigo} não encontrado na tabela vigente em {date:dd/MM/yyyy}");
        }

        var classes = await cache.GetOrCreateAsync($"class:list:{date:yyyyMMdd}", async e =>
        {
            e.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(6);
            e.Size = 1;
            await using var s = await engine.GetStreamAsync(new Uri($"calculadora/dados-abertos/classificacoes-tributarias/cbs-ibs?data={date:yyyy-MM-dd}", UriKind.Relative), http.RequestAborted);
            using var doc = await JsonDocument.ParseAsync(s, cancellationToken: http.RequestAborted);
            return doc.RootElement.EnumerateArray().Select(c => new ClassRow(
                c.GetProperty("codigo").GetString()!, Str(c, "descricao"), Str(c, "descricaoTratamentoTributario"), Str(c, "nomenclatura"), Str(c, "tipoAliquota"),
                Num(c, "percentualReducaoCbs"), Num(c, "percentualReducaoIbsUf"), Num(c, "percentualReducaoIbsMun"),
                Bool(c, "indicaApropriacaoCreditoAdquirenteCbs"), Bool(c, "indicaApropriacaoCreditoAdquirenteIbs"),
                c.TryGetProperty("tiposDfeClassificacao", out var t) ? [.. t.EnumerateArray().Select(x => x.GetProperty("tipo").GetInt32())] : [])).ToList();
        }) ?? [];

        var descricao = string.Join(" — ", DescriptionKeys
            .Select(k => item[k]?.GetValue<string>()).Where(v => !string.IsNullOrWhiteSpace(v)));
        var words = Tokens(descricao + " " + q);
        var queryWords = Tokens(q ?? "");
        var compat = classes.Where(c => c.Modelos.Length == 0 || c.Modelos.Contains(model))
            .Where(c => c.Nomenclatura.Contains(tipo, StringComparison.OrdinalIgnoreCase) || c.Codigo == "000001");
        if (queryWords.Count > 0)
        {
            compat = compat.Where(c => c.Codigo == "000001" || Tokens(c.Descricao + " " + c.Tratamento).Overlaps(queryWords));
        }

        var ranked = compat
            .Select(c => (Row: c, Score: c.Codigo == "000001" ? int.MaxValue : Tokens(c.Descricao).Count(words.Contains)))
            .OrderByDescending(x => x.Score).ThenBy(x => x.Row.Codigo, StringComparer.Ordinal)
            .Take(15)
            .Select(x => (JsonNode)new JsonObject
            {
                ["c_class_trib"] = x.Row.Codigo,
                ["cst"] = x.Row.Codigo[..3],
                ["descricao"] = x.Row.Descricao,
                ["tratamento"] = x.Row.Tratamento,
                ["tipo_aliquota"] = x.Row.TipoAliquota,
                ["reducao_cbs_percentual"] = x.Row.ReducaoCbs,
                ["reducao_ibs_uf_percentual"] = x.Row.ReducaoIbsUf,
                ["reducao_ibs_mun_percentual"] = x.Row.ReducaoIbsMun,
                ["credito_adquirente"] = x.Row.CreditoCbs || x.Row.CreditoIbs,
                ["motivo"] = x.Row.Codigo == "000001" ? "Regra geral: tributação integral, quando nenhuma situação específica se aplica."
                    : x.Score > 0 ? "Descrição próxima à do item." : "Compatível com a nomenclatura e o modelo do documento.",
            });

        return Results.Ok(new JsonObject
        {
            ["consulta"] = new JsonObject { ["tipo"] = tipo, ["codigo"] = codigo, ["data"] = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), ["modelo"] = model },
            ["item"] = new JsonObject
            {
                ["descricao"] = descricao,
                ["imposto_seletivo"] = item["tributadoPeloImpostoSeletivo"]?.GetValue<bool>() ?? false,
                ["is_aliquota_ad_valorem"] = item["temAliquotaAdValorem"]?.GetValue<bool>() ?? false,
                ["is_aliquota_ad_rem"] = item["temAliquotaAdRem"]?.GetValue<bool>() ?? false,
            },
            ["sugestoes"] = new JsonArray([.. ranked]),
            ["aviso"] = "Sugestões por compatibilidade com a nomenclatura e com a descrição do item, a partir dos dados abertos do motor oficial "
                        + "na data. Não é parecer: reduções e regimes específicos dependem do enquadramento do produto ou serviço nos anexos da "
                        + "LC 214/2025 e da legislação, que cabe ao contribuinte confirmar.",
        });
    }

    private static string Str(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

    private static decimal Num(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDecimal() : 0;

    private static bool Bool(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.True;

    /// <summary>Palavras significativas, sem acento e sem palavras vazias, com radical simples (plural).</summary>
    private static HashSet<string> Tokens(string text)
    {
        var norm = new StringBuilder();
        foreach (var ch in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
            {
                norm.Append(char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : ' ');
            }
        }

        return norm.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 2 && !Stop.Contains(w))
            .Select(w => w.Length > 4 && w.EndsWith('s') ? w[..^1] : w)
            .ToHashSet(StringComparer.Ordinal);
    }
}
