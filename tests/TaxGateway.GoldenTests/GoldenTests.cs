using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace TaxGateway.GoldenTests;

/// <summary>
/// Critério de saída da Fase 2: a simulação reproduz 100% do corpus golden. Para cada caso, a API (em processo,
/// com o motor real) precisa devolver exatamente os grupos oficiais do motor do snapshot (tribCalc por item,
/// estados, erros e totais).
/// </summary>
[Collection(GoldenCollection.Name)]
public sealed class GoldenTests(GoldenFixture env)
{
    private static bool Regenerate => Environment.GetEnvironmentVariable("GOLDEN_REGENERATE") == "1";

    [Fact]
    public async Task Simulation_reproduces_the_whole_golden_corpus()
    {
        if (Regenerate)
        {
            await RegenerateAsync();
        }

        var files = Directory.Exists(Paths.CorpusDir) ? Directory.GetFiles(Paths.CorpusDir, "*.json").Order(StringComparer.Ordinal).ToArray() : [];
        Assert.True(files.Length >= 300, $"Corpus v0 com {files.Length} casos; esperado ≥ 300 (GOLDEN_REGENERATE=1 para gerar).");

        var failures = new List<string>();
        foreach (var file in files)
        {
            var c = JsonNode.Parse(await File.ReadAllTextAsync(file))!.AsObject();
            var id = c["id"]!.GetValue<string>();
            var (status, body) = await PostAsync(c["api_request"]!.ToJsonString());
            var problems = c["engine_response"] is JsonObject er
                ? Compare(er, status, body, c["engine_response_simples"] as JsonObject)
                : CompareSimplesOnly(c["engine_response_simples"]!.AsObject(), status, body);
            failures.AddRange(problems.Select(p => $"{id}: {p}"));
        }

        Assert.True(failures.Count == 0, $"{failures.Count} divergência(s) em {files.Length} casos:\n" + string.Join('\n', failures.Take(40)));
    }

    /// <summary>Simples pleno: a resposta da API traz os grupos do motor do Simples por item e os totais do DAS.</summary>
    private static List<string> CompareSimplesOnly(JsonObject snapshot, int status, JsonNode? body)
    {
        var problems = new List<string>();
        if (status != 200 || snapshot["status"]!.GetValue<int>() != 200)
        {
            problems.Add($"HTTP {status} (motor {snapshot["status"]}): {body?["code"]} {body?["detail"]}");
            return problems;
        }

        CompareSimplesItems(snapshot, body!, problems);
        var tot = snapshot["body"]!["total"];
        var t = body!["totais"]!;
        void Check(string name, JsonNode? v)
        {
            var e = v is null ? 0m : decimal.Parse(v.GetValue<string>(), CultureInfo.InvariantCulture);
            if (t[name]!.GetValue<decimal>() != e)
            {
                problems.Add($"totais.{name} {t[name]} ≠ {e}");
            }
        }

        Check("cbs_valor", tot?["vCBSSNtot"]);
        Check("ibs_total_valor", tot?["vIBSSNtot"]);
        Check("valor_base_total", tot?["vBC"]);
        return problems;
    }

    private static void CompareSimplesItems(JsonObject snapshot, JsonNode body, List<string> problems)
    {
        var objetos = snapshot["body"]!["objetos"]!.AsArray();
        var itens = body["itens"]!.AsArray();
        foreach (var o in objetos)
        {
            var n = o!["nObj"]!.GetValue<int>();
            var it = itens.FirstOrDefault(i => i!["numero_item"]!.GetValue<int>() == n)?["simples"];
            if (it is null)
            {
                problems.Add($"item {n}: bloco simples ausente");
                continue;
            }

            if (it["estado"]!.GetValue<string>() != o["estadoItem"]!.GetValue<string>())
            {
                problems.Add($"item {n}: estado simples {it["estado"]} ≠ {o["estadoItem"]}");
            }

            if (!JsonNode.DeepEquals(o["tribCalc"], it["tributos_simples"]))
            {
                problems.Add($"item {n}: tributos_simples diverge do snapshot");
            }
        }
    }

    private static List<string> Compare(JsonObject snapshot, int status, JsonNode? body, JsonObject? simples = null)
    {
        var problems = CompareRegular(snapshot, status, body, simples);
        if (simples is not null && status == 200)
        {
            CompareSimplesItems(simples, body!, problems);
        }

        return problems;
    }

    private static List<string> CompareRegular(JsonObject snapshot, int status, JsonNode? body, JsonObject? simples)
    {
        var problems = new List<string>();
        var engineStatus = snapshot["status"]!.GetValue<int>();
        var eb = snapshot["body"]!;
        if (engineStatus != 200)
        {
            // Erro global do motor: a API devolve 422 ERR_ENGINE_REJECTED com os mesmos códigos.
            if (status != 422 || body?["code"]?.GetValue<string>() != "ERR_ENGINE_REJECTED")
            {
                problems.Add($"esperado 422 ERR_ENGINE_REJECTED, veio {status} {body?["code"]}");
                return problems;
            }

            var expected = eb["errors"]!.AsArray().Select(e => e!["code"]!.GetValue<string>()).ToList();
            var actual = body!["errors"]!.AsArray().Select(e => e!["codigo"]!.GetValue<string>()).ToList();
            if (!expected.SequenceEqual(actual))
            {
                problems.Add($"erros globais {string.Join(',', actual)} ≠ {string.Join(',', expected)}");
            }

            return problems;
        }

        if (status != 200)
        {
            problems.Add($"HTTP {status}: {body?["code"]} {body?["detail"]}");
            return problems;
        }

        var objetos = eb["objetos"]!.AsArray();
        var itens = body!["itens"]!.AsArray();
        if (objetos.Count != itens.Count)
        {
            problems.Add($"itens {itens.Count} ≠ {objetos.Count}");
            return problems;
        }

        for (var i = 0; i < objetos.Count; i++)
        {
            var o = objetos[i]!;
            var it = itens[i]!;
            if (o["nObj"]!.GetValue<int>() != it["numero_item"]!.GetValue<int>())
            {
                problems.Add($"item {i}: número {it["numero_item"]} ≠ {o["nObj"]}");
            }

            if (o["estadoItem"]!.GetValue<string>() != it["estado"]!.GetValue<string>())
            {
                problems.Add($"item {i}: estado {it["estado"]} ≠ {o["estadoItem"]}");
            }

            if (!JsonNode.DeepEquals(o["tribCalc"], it["tributos_dfe"]))
            {
                problems.Add($"item {i}: tributos_dfe diverge do snapshot");
            }

            var expErr = (o["erros"] as JsonArray)?.Select(e => e!["code"]!.GetValue<string>()).ToList() ?? [];
            var actErr = (it["erros"] as JsonArray)?.Select(e => e!["codigo"]!.GetValue<string>()).ToList() ?? [];
            if (!expErr.SequenceEqual(actErr))
            {
                problems.Add($"item {i}: erros {string.Join(',', actErr)} ≠ {string.Join(',', expErr)}");
            }
        }

        // Totais: mesmos valores do IBSCBSTot/ISTot do motor, sem novo arredondamento.
        var tot = eb["total"]?["tribCalc"];
        var t = body["totais"]!;
        void Check(string name, JsonNode? engineValue)
        {
            var expected = engineValue is null ? 0m : decimal.Parse(engineValue.GetValue<string>(), CultureInfo.InvariantCulture);
            var actual = t[name]!.GetValue<decimal>();
            if (expected != actual)
            {
                problems.Add($"totais.{name} {actual} ≠ {expected}");
            }
        }

        Check("valor_base_total", tot?["IBSCBSTot"]?["vBCIBSCBS"]);
        if (simples is null)
        {
            Check("cbs_valor", tot?["IBSCBSTot"]?["gCBS"]?["vCBS"]);
        }
        else
        {
            var cbsRegular = decimal.Parse(tot?["IBSCBSTot"]?["gCBS"]?["vCBS"]?.GetValue<string>() ?? "0", CultureInfo.InvariantCulture);
            var cbsDas = decimal.Parse(simples["body"]!["total"]?["vCBSSNtot"]?.GetValue<string>() ?? "0", CultureInfo.InvariantCulture);
            if (t["cbs_valor"]!.GetValue<decimal>() != cbsRegular + cbsDas)
            {
                problems.Add($"totais.cbs_valor {t["cbs_valor"]} ≠ {cbsRegular} + {cbsDas} (DAS)");
            }
        }
        Check("ibs_uf_valor", tot?["IBSCBSTot"]?["gIBS"]?["gIBSUF"]?["vIBSUF"]);
        Check("ibs_mun_valor", tot?["IBSCBSTot"]?["gIBS"]?["gIBSMun"]?["vIBSMun"]);
        Check("ibs_total_valor", tot?["IBSCBSTot"]?["gIBS"]?["vIBS"]);
        Check("imposto_seletivo_valor", tot?["ISTot"]?["vIS"]);

        if (!JsonNode.DeepEquals(eb["oper"], body["operacao_dfe"]))
        {
            problems.Add("operacao_dfe diverge do snapshot");
        }

        var warning = snapshot["warning"]?.GetValue<string>();
        var avisos = body["avisos"]!.AsArray().Select(a => a!["codigo"]!.GetValue<string>()).ToList();
        if (warning is not null && !avisos.Contains($"DADOS_SIMULADOS_{warning}"))
        {
            problems.Add($"aviso DADOS_SIMULADOS_{warning} ausente");
        }

        return problems;
    }

    private async Task<(int Status, JsonNode? Body)> PostAsync(string json)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "/v1/calculo/simular")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
            req.Headers.Add("X-Api-Key", env.ApiKey);
            using var resp = await env.Api.SendAsync(req);
            if (resp.StatusCode == HttpStatusCode.TooManyRequests && attempt < 20)
            {
                await Task.Delay(TimeSpan.FromSeconds(resp.Headers.RetryAfter?.Delta?.TotalSeconds ?? 1));
                continue;
            }

            var text = await resp.Content.ReadAsStringAsync();
            return ((int)resp.StatusCode, text.Length == 0 ? null : JsonNode.Parse(text));
        }
    }

    private static string SimplesUrl => Environment.GetEnvironmentVariable("RTC_SIMPLES_URL") ?? "http://127.0.0.1:8082/api/";

    private static async Task<JsonObject> SnapshotAsync(HttpClient http, string path, JsonObject request)
    {
        using var resp = await http.PostAsync(path, new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json"));
        var body = JsonNode.Parse(await resp.Content.ReadAsStringAsync())!;
        if (body is JsonObject bo)
        {
            bo.Remove("timestamp");   // varia a cada chamada
            bo.Remove("instance");
        }

        var snapshot = new JsonObject { ["status"] = (int)resp.StatusCode, ["body"] = body };
        if (resp.Headers.TryGetValues("x-warning-dados-simulados", out var w))
        {
            snapshot["warning"] = w.First();
        }

        return snapshot;
    }

    private async Task RegenerateAsync()
    {
        using var engine = new HttpClient { BaseAddress = new Uri(GoldenFixture.EngineUrl) };
        var specs = await GoldenCorpus.BuildSpecsAsync(engine);
        Directory.CreateDirectory(Paths.CorpusDir);
        foreach (var old in Directory.GetFiles(Paths.CorpusDir, "*.json"))
        {
            File.Delete(old);
        }

        using var simplesEngine = new HttpClient { BaseAddress = new Uri(SimplesUrl) };
        foreach (var s in specs)
        {
            var doc = new JsonObject
            {
                ["id"] = s.Id,
                ["descricao"] = s.Descricao,
                ["bundle"] = Paths.Bundle,
                ["api_request"] = GoldenCorpus.ApiRequest(s),
            };
            if (s.UsaRegular)
            {
                var rates = await GoldenCorpus.CatalogRatesAsync(env.AppConnection, s.Data);
                var engineReq = GoldenCorpus.EngineRequest(s, rates);
                doc["engine_request"] = engineReq;
                doc["engine_response"] = await SnapshotAsync(engine, "calculadora/observabilidade/regime-geral", engineReq);
            }

            if (s.UsaSimples)
            {
                var simplesReq = GoldenCorpus.SimplesRequest(s);
                doc["engine_request_simples"] = simplesReq;
                doc["engine_response_simples"] = await SnapshotAsync(simplesEngine, "calculadora/observabilidade/simples-nacional", simplesReq);
            }
            await File.WriteAllTextAsync(Path.Combine(Paths.CorpusDir, $"{s.Id}.json"), doc.ToJsonString(GoldenCorpus.Pretty) + "\n");
        }
    }
}
