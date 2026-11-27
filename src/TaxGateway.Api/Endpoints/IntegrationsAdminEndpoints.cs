using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;

using Microsoft.Extensions.Options;

using TaxGateway.Api.Http;
using TaxGateway.Identity.Accounts;
using TaxGateway.Infrastructure.Admin;
using TaxGateway.Infrastructure.Billing;
using TaxGateway.Infrastructure.Engines;
using TaxGateway.Infrastructure.Persistence;

namespace TaxGateway.Api.Endpoints;

/// <summary>Configuração visível das integrações — só o que é seguro mostrar (nunca senha nem chave).</summary>
public sealed record IntegrationConfig(string SmtpHost, int SmtpPort, bool SmtpTls, string From, bool StripeKey, bool StripeWebhook, bool StripeTestMode,
    string Bundle, bool EngineOk, string? EngineDetail, long EngineMs, bool CacheOk);

/// <summary>
/// D-R39: avalia cada integração (e-mail, Stripe, motor oficial, sincronização com a Receita, DF-e, cache) a partir dos
/// números agregados do banco (<c>app.integration_status()</c>) e da configuração. Puro: testável sem infraestrutura.
/// Situação: OK, ATENCAO (funciona, mas há algo a olhar), FALHA (não está funcionando) ou DESLIGADO (não configurado).
/// </summary>
public static class IntegrationHealth
{
    private static string Br(DateTimeOffset? d) =>
        d is null ? "nunca" : d.Value.ToOffset(TimeSpan.FromHours(-3)).ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);

    private static DateTimeOffset? Ts(JsonNode? n) => n is JsonValue v && v.TryGetValue<DateTimeOffset>(out var d) ? d : null;

    private static long N(JsonNode? n) => n is JsonValue v && v.TryGetValue<long>(out var x) ? x : 0;

    private static JsonObject Item(string id, string nome, string situacao, string resumo, params (string Rotulo, string Valor)[] detalhes) => new()
    {
        ["id"] = id,
        ["nome"] = nome,
        ["situacao"] = situacao,
        ["resumo"] = resumo,
        ["detalhes"] = new JsonArray([.. detalhes.Select(d => (JsonNode)new JsonObject { ["rotulo"] = d.Rotulo, ["valor"] = d.Valor })]),
    };

    public static JsonArray Evaluate(JsonObject db, IntegrationConfig c, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(c);
        var items = new JsonArray();

        // ------------------------------------------------------------------ e-mail
        var e = db["email"] as JsonObject ?? [];
        var stuck = N(e["pendentes_antigos"]);
        var emailDetails = new[]
        {
            ("Servidor", c.SmtpHost.Length == 0 ? "não configurado" : $"{c.SmtpHost}:{c.SmtpPort}{(c.SmtpTls ? " (TLS)" : " (sem TLS)")}"),
            ("Remetente", c.From),
            ("Último aviso enviado", Br(Ts(e["ultimo_envio"]))),
            ("Enviados nas últimas 24 h", N(e["enviados_24h"]).ToString(CultureInfo.InvariantCulture)),
            ("Na fila", N(e["pendentes"]).ToString(CultureInfo.InvariantCulture)),
        };
        items.Add(c.SmtpHost.Length == 0
            ? Item("email", "E-mail (SMTP)", "DESLIGADO", "Sem servidor SMTP: os e-mails só vão para o log. Configure Email__SmtpHost.", emailDetails)
            : stuck > 0
                ? Item("email", "E-mail (SMTP)", "FALHA", $"{stuck} aviso(s) esperando há mais de 15 min: os workers estão parados ou o servidor SMTP está recusando. Use \"Enviar e-mail de teste\".", emailDetails)
                : Item("email", "E-mail (SMTP)", "OK", "Avisos saindo normalmente.", emailDetails));

        // ------------------------------------------------------------------ Stripe
        var s = db["stripe"] as JsonObject ?? [];
        var lastEvent = Ts(s["ultimo_evento"]);
        var stripeDetails = new[]
        {
            ("Chave da API", c.StripeKey ? (c.StripeTestMode ? "configurada (modo de teste)" : "configurada (produção)") : "não configurada"),
            ("Segredo do webhook", c.StripeWebhook ? "configurado" : "não configurado"),
            ("Último evento recebido", Br(lastEvent)),
            ("Eventos na fila", N(s["pendentes"]).ToString(CultureInfo.InvariantCulture)),
            ("Eventos com erro", N(s["com_erro"]).ToString(CultureInfo.InvariantCulture)),
            ("Planos ainda não enviados à Stripe", N(s["planos_sem_sincronizar"]).ToString(CultureInfo.InvariantCulture)),
        };
        if (s["ultimo_erro"]?.GetValue<string>() is { Length: > 0 } err)
        {
            stripeDetails = [.. stripeDetails, ("Último erro", err)];
        }

        items.Add(!c.StripeKey
            ? Item("stripe", "Pagamentos (Stripe)", "DESLIGADO", "Pagamento online desligado nesta instalação (sem Stripe__SecretKey): planos pagos só pelo suporte.", stripeDetails)
            : !c.StripeWebhook
                ? Item("stripe", "Pagamentos (Stripe)", "FALHA", "Falta o segredo do webhook (Stripe__WebhookSecret): pagamentos e cancelamentos não chegam ao TaxGateway.", stripeDetails)
                : N(s["com_erro"]) > 0 || N(s["pendentes_antigos"]) > 0
                    ? Item("stripe", "Pagamentos (Stripe)", "FALHA", "Há eventos da Stripe sem processar: assinaturas e faturas podem estar desatualizadas. Veja o último erro e os workers de cobrança.", stripeDetails)
                    : N(s["planos_sem_sincronizar"]) > 0
                        ? Item("stripe", "Pagamentos (Stripe)", "ATENCAO", "Há planos alterados ainda não enviados à Stripe (os workers enviam a cada 5 min).", stripeDetails)
                        : lastEvent is null
                            ? Item("stripe", "Pagamentos (Stripe)", "ATENCAO", "Nenhum evento recebido ainda: confira o endpoint do webhook no painel da Stripe (/webhooks/stripe). Use \"Testar Stripe\".", stripeDetails)
                            : Item("stripe", "Pagamentos (Stripe)", "OK", "Recebendo e processando eventos.", stripeDetails));

        // ------------------------------------------------------------------ motor oficial
        items.Add(Item("motor", "Motor oficial da Receita", c.EngineOk ? "OK" : "FALHA",
            c.EngineOk ? $"Respondendo ({c.EngineMs} ms)." : $"Sem resposta do motor: cálculos e auditorias falham. {c.EngineDetail}",
            ("Versão (bundle)", c.Bundle), ("Tempo da consulta", $"{c.EngineMs} ms")));

        // ------------------------------------------------------------------ sincronização com a Receita
        var r = db["receita"] as JsonObject;
        var synced = Ts(r?["sincronizado_em"]);
        var syncBundle = r?["bundle"]?.GetValue<string>();
        var syncDetails = new[]
        {
            ("Versão sincronizada", syncBundle ?? "nenhuma"),
            ("Última conferência", Br(synced)),
            ("Consultas na última completa", N(r?["consultas"]).ToString(CultureInfo.InvariantCulture)),
            ("Alíquotas alteradas pela sincronização", N(r?["mudancas"]).ToString(CultureInfo.InvariantCulture)),
            ("Última alteração de alíquota publicada", Br(Ts(r?["ultima_alteracao"]))),
        };
        items.Add(synced is null || syncBundle != c.Bundle
            ? Item("receita", "Sincronização de alíquotas", "ATENCAO", $"A versão {c.Bundle} do motor ainda não foi sincronizada: os workers fazem isso ao subir (cerca de 1 min).", syncDetails)
            : now - synced > TimeSpan.FromDays(2)
                ? Item("receita", "Sincronização de alíquotas", "FALHA", $"Sem conferência com a Receita desde {Br(synced)}: os workers (RateSyncWorker) parecem parados.", syncDetails)
                : Item("receita", "Sincronização de alíquotas", "OK", "Catálogo conferido com a base oficial da Receita.", syncDetails));

        // ------------------------------------------------------------------ DF-e
        var d = db["dfe"] as JsonObject ?? [];
        var dfeDetails = new[]
        {
            ("CNPJs com captura ligada", N(d["ligadas"]).ToString(CultureInfo.InvariantCulture)),
            ("Capturas paradas", N(d["paradas"]).ToString(CultureInfo.InvariantCulture)),
            ("Última consulta com erro ou bloqueio", N(d["com_erro"]).ToString(CultureInfo.InvariantCulture)),
            ("Certificados vencendo em 30 dias", N(d["certificados_vencendo"]).ToString(CultureInfo.InvariantCulture)),
            ("Última consulta à SEFAZ", Br(Ts(d["ultima_consulta"]))),
        };
        items.Add(N(d["ligadas"]) == 0
            ? Item("dfe", "Captura de notas (SEFAZ)", "DESLIGADO", "Nenhum cliente com a captura de DF-e ligada.", dfeDetails)
            : N(d["paradas"]) > 0 || N(d["com_erro"]) > 0
                ? Item("dfe", "Captura de notas (SEFAZ)", "ATENCAO", "Há capturas paradas ou com erro na última consulta; os clientes afetados já recebem aviso. Se forem muitas ao mesmo tempo, confira a SEFAZ e os workers.", dfeDetails)
                : Item("dfe", "Captura de notas (SEFAZ)", "OK", "Capturas consultando a SEFAZ normalmente.", dfeDetails));

        // ------------------------------------------------------------------ cache
        items.Add(Item("cache", "Cache e limites (Valkey)", c.CacheOk ? "OK" : "FALHA",
            c.CacheOk ? "Conectado." : "Sem conexão: limites de requisição e propagação de mudanças caem no modo local (mais lento para revogar acessos)."));
        return items;
    }
}

/// <summary>D-R39: painel de integrações do backoffice e testes sob demanda (e-mail e Stripe).</summary>
public static class IntegrationsAdminEndpoints
{
    public static RouteGroupBuilder MapAdminIntegrations(this RouteGroupBuilder admin)
    {
        admin.MapGet("/integracoes", StatusAsync);
        admin.MapPost("/integracoes/email-teste", EmailTestAsync);
        admin.MapPost("/integracoes/stripe-teste", StripeTestAsync);
        return admin;
    }

    private static async Task<IResult> StatusAsync(HttpContext http, AdminDataSource db, IOptions<EmailOptions> email, IOptions<StripeOptions> stripe,
        IOptions<EngineOptions> engines, IHttpClientFactory httpFactory, StackExchange.Redis.IConnectionMultiplexer valkey, TimeProvider clock)
    {
        JsonObject status;
        await using (var cmd = db.DataSource.CreateCommand("SELECT app.integration_status()::text"))
        {
            status = (JsonObject)JsonNode.Parse((string)(await cmd.ExecuteScalarAsync(http.RequestAborted))!)!;
        }

        var sw = Stopwatch.StartNew();
        string? engineDetail = null;
        var engineOk = false;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            using var res = await httpFactory.CreateClient(RfbReferenceData.Client).GetAsync(
                new Uri($"calculadora/dados-abertos/aliquota-uniao?data={clock.GetUtcNow():yyyy-MM-dd}", UriKind.Relative), cts.Token);
            engineOk = res.IsSuccessStatusCode || res.StatusCode == System.Net.HttpStatusCode.NotFound;   // 404 = sem alíquota na data: respondeu
            engineDetail = engineOk ? null : $"HTTP {(int)res.StatusCode}.";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            engineDetail = ex is TaskCanceledException ? "Sem resposta em 5 s." : "Conexão recusada.";
        }

        var o = email.Value;
        var s = stripe.Value;
        var config = new IntegrationConfig(o.SmtpHost, o.SmtpPort, o.SmtpTls, o.From, s.SecretKey.Length > 0, s.WebhookSecret.Length > 0,
            s.SecretKey.StartsWith("sk_test_", StringComparison.Ordinal) || s.SecretKey.StartsWith("rk_test_", StringComparison.Ordinal),
            engines.Value.Bundle, engineOk, engineDetail, sw.ElapsedMilliseconds, valkey.IsConnected);
        var items = IntegrationHealth.Evaluate(status, config, clock.GetUtcNow());
        var all = items.Select(i => i!["situacao"]!.GetValue<string>()).ToList();
        return Results.Ok(new JsonObject
        {
            ["geral"] = all.Contains("FALHA") ? "FALHA" : all.Contains("ATENCAO") ? "ATENCAO" : "OK",
            ["verificado_em"] = clock.GetUtcNow(),
            ["integracoes"] = items,
        });
    }

    /// <summary>Envia um e-mail de teste para o operador (ou para "para", só com admin:security) e devolve o erro do SMTP.</summary>
    private static async Task<IResult> EmailTestAsync(HttpContext http, IEmailSender email, IOptions<EmailOptions> options)
    {
        var caller = AdminKeyFilter.Get(http);
        var body = http.Request.ContentLength > 0 ? await JsonNode.ParseAsync(http.Request.Body, cancellationToken: http.RequestAborted) : null;
        var to = body?["para"]?.GetValue<string>()?.Trim();
        if (to is { Length: > 0 } && !caller.Scopes.Contains(AdminEndpoints.Security, StringComparer.Ordinal))
        {
            return ApiProblem.Result(403, "ERR_FORBIDDEN_SCOPE", "Só operadores de segurança escolhem o destinatário", "Sem \"para\", o teste vai para o seu e-mail.");
        }

        to = to is { Length: > 0 } ? to : caller.Name;
        if (!to.Contains('@', StringComparison.Ordinal) || to.Length > 254)
        {
            return ApiProblem.Result(400, "ERR_VALIDATION", "Informe o e-mail de destino", "Operador por chave não tem e-mail: envie {\"para\": \"...\"}.");
        }

        if (options.Value.SmtpHost.Length == 0)
        {
            return Results.Ok(new JsonObject { ["enviado"] = false, ["para"] = to, ["mensagem"] = "Sem servidor SMTP configurado: o e-mail foi só para o log." });
        }

        try
        {
            await email.SendAsync(EmailTemplates.SecurityNotice(to, "operador",
                $"Este é um e-mail de teste do painel de integrações, pedido por {caller.Name}. Se chegou, o envio de e-mails está funcionando."), http.RequestAborted);
            return Results.Ok(new JsonObject { ["enviado"] = true, ["para"] = to, ["mensagem"] = $"E-mail de teste enviado para {to}. Confira a caixa de entrada (e o spam)." });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Results.Ok(new JsonObject { ["enviado"] = false, ["para"] = to, ["mensagem"] = $"O servidor SMTP recusou o envio: {ex.Message}" });
        }
    }

    /// <summary>Confere a chave da Stripe com uma leitura simples (lista de produtos), sem criar nada.</summary>
    private static async Task<IResult> StripeTestAsync(HttpContext http, CancellationToken ct)
    {
        var stripe = http.RequestServices.GetService<StripeClient>();
        if (stripe is null || !stripe.Enabled)
        {
            return Results.Ok(new JsonObject { ["ok"] = false, ["mensagem"] = "Stripe não configurada nesta instalação." });
        }

        try
        {
            var r = await stripe.SendAsync(HttpMethod.Get, "v1/products?limit=3&active=true", ct: ct);
            var n = r["data"] is JsonArray a ? a.Count : 0;
            return Results.Ok(new JsonObject { ["ok"] = true, ["mensagem"] = $"A Stripe aceitou a chave ({n} produto(s) ativo(s) na primeira página)." });
        }
        catch (Exception ex) when (ex is StripeException or HttpRequestException or TaskCanceledException)
        {
            return Results.Ok(new JsonObject { ["ok"] = false, ["mensagem"] = $"A Stripe recusou ou não respondeu: {ex.Message}" });
        }
    }
}
