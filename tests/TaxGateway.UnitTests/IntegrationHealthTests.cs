using System.Text.Json.Nodes;

using TaxGateway.Api.Endpoints;

namespace TaxGateway.UnitTests;

/// <summary>D-R39: avaliação das integrações do painel do backoffice.</summary>
public sealed class IntegrationHealthTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 27, 15, 0, 0, TimeSpan.Zero);

    private static IntegrationConfig Cfg(string smtp = "smtp.x", bool key = true, bool webhook = true, bool engine = true, bool cache = true, string bundle = "b1") =>
        new(smtp, 587, true, "TaxGateway <x@y>", key, webhook, true, bundle, engine, engine ? null : "Conexão recusada.", 12, cache);

    private static JsonObject Db(long emailStuck = 0, string? lastEvent = "2026-11-27T10:00:00Z", long stripeErr = 0, long stripeStuck = 0, long plans = 0,
        string? synced = "2026-11-27T10:00:00Z", string syncBundle = "b1", long dfeOn = 2, long dfeStalled = 0, long dfeErr = 0, string? lastError = null) =>
        (JsonObject)JsonNode.Parse(new JsonObject
        {
            ["email"] = new JsonObject { ["ultimo_envio"] = "2026-11-27T14:00:00Z", ["pendentes"] = emailStuck, ["pendentes_antigos"] = emailStuck, ["enviados_24h"] = 10 },
            ["stripe"] = new JsonObject { ["ultimo_evento"] = lastEvent, ["pendentes"] = stripeStuck, ["pendentes_antigos"] = stripeStuck, ["com_erro"] = stripeErr, ["ultimo_erro"] = lastError, ["planos_sem_sincronizar"] = plans },
            ["receita"] = synced is null ? null : new JsonObject { ["bundle"] = syncBundle, ["sincronizado_em"] = synced, ["consultas"] = 97535, ["mudancas"] = 0, ["ultima_alteracao"] = null },
            ["dfe"] = new JsonObject { ["ligadas"] = dfeOn, ["paradas"] = dfeStalled, ["com_erro"] = dfeErr, ["certificados_vencendo"] = 1, ["ultima_consulta"] = "2026-11-27T14:30:00Z" },
        }.ToJsonString())!;

    private static Dictionary<string, string> Eval(JsonObject db, IntegrationConfig c) =>
        IntegrationHealth.Evaluate(db, c, Now).ToDictionary(i => i!["id"]!.GetValue<string>(), i => i!["situacao"]!.GetValue<string>());

    [Fact]
    public void Tudo_funcionando()
    {
        var r = Eval(Db(), Cfg());
        Assert.All(r.Values, v => Assert.Equal("OK", v));
        var items = IntegrationHealth.Evaluate(Db(), Cfg(), Now);
        Assert.Contains("smtp.x:587 (TLS)", items.ToJsonString(), StringComparison.Ordinal);
        Assert.Contains("modo de teste", items.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Cada_problema_aparece_na_integracao_certa()
    {
        Assert.Equal("DESLIGADO", Eval(Db(), Cfg(smtp: ""))["email"]);
        Assert.Equal("FALHA", Eval(Db(emailStuck: 3), Cfg())["email"]);
        Assert.Equal("DESLIGADO", Eval(Db(), Cfg(key: false))["stripe"]);
        Assert.Equal("FALHA", Eval(Db(), Cfg(webhook: false))["stripe"]);
        Assert.Equal("FALHA", Eval(Db(stripeErr: 1, lastError: "No such customer"), Cfg())["stripe"]);
        Assert.Contains("No such customer", IntegrationHealth.Evaluate(Db(stripeErr: 1, lastError: "No such customer"), Cfg(), Now).ToJsonString(), StringComparison.Ordinal);
        Assert.Equal("FALHA", Eval(Db(stripeStuck: 2), Cfg())["stripe"]);
        Assert.Equal("ATENCAO", Eval(Db(plans: 1), Cfg())["stripe"]);
        Assert.Equal("ATENCAO", Eval(Db(lastEvent: null), Cfg())["stripe"]);
        Assert.Equal("FALHA", Eval(Db(), Cfg(engine: false))["motor"]);
        Assert.Equal("ATENCAO", Eval(Db(synced: null), Cfg())["receita"]);
        Assert.Equal("ATENCAO", Eval(Db(syncBundle: "b0"), Cfg())["receita"]);
        Assert.Equal("FALHA", Eval(Db(synced: "2026-11-20T10:00:00Z"), Cfg())["receita"]);
        Assert.Equal("DESLIGADO", Eval(Db(dfeOn: 0), Cfg())["dfe"]);
        Assert.Equal("ATENCAO", Eval(Db(dfeStalled: 1), Cfg())["dfe"]);
        Assert.Equal("ATENCAO", Eval(Db(dfeErr: 1), Cfg())["dfe"]);
        Assert.Equal("FALHA", Eval(Db(), Cfg(cache: false))["cache"]);
        Assert.Equal("ATENCAO", Eval(new JsonObject(), Cfg())["receita"]);   // banco sem nada ainda
    }
}
