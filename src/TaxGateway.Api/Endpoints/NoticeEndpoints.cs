using System.Text.Json.Nodes;

using TaxGateway.Api.Http;
using TaxGateway.Infrastructure.Persistence;

namespace TaxGateway.Api.Endpoints;

/// <summary>
/// Avisos ao cliente (D-J03) e checklist de primeiros passos (D-J04). Qualquer credencial do tenant lê; marcar como
/// lido é por tenant (o aviso é da empresa, não da pessoa).
/// </summary>
public static class NoticeEndpoints
{
    public static RouteGroupBuilder MapNotices(this RouteGroupBuilder v1)
    {
        v1.MapGet("/avisos", ListAsync);
        v1.MapPost("/avisos/{id:guid}/lido", (HttpContext http, Guid id, ITenantDb db) => MarkAsync(http, db, id));
        v1.MapPost("/avisos/lidos", (HttpContext http, ITenantDb db) => MarkAsync(http, db, null));
        v1.MapGet("/primeiros-passos", FirstStepsAsync);
        return v1;
    }

    private static async Task<IResult> ListAsync(HttpContext http, ITenantDb db, bool? nao_lidos)
    {
        var caller = http.GetCaller();
        var json = await db.InTenantAsync(caller.Identity.Tenant, async (s, ct) =>
        {
            long unread;
            await using (var count = s.CreateCommand("SELECT count(*) FROM tenant_notifications WHERE read_at IS NULL"))
            {
                unread = (long)(await count.ExecuteScalarAsync(ct))!;
            }

            await using var cmd = s.CreateCommand("""
                SELECT id, kind, severity, title, body, link, created_at, read_at
                  FROM tenant_notifications
                 WHERE NOT $1 OR read_at IS NULL
                 ORDER BY created_at DESC LIMIT 50
                """);
            cmd.Parameters.Add(new() { Value = nao_lidos ?? false });
            await using var r = await cmd.ExecuteReaderAsync(ct);
            var arr = new JsonArray();
            while (await r.ReadAsync(ct))
            {
                arr.Add(new JsonObject
                {
                    ["id"] = r.GetGuid(0),
                    ["tipo"] = r.GetString(1),
                    ["severidade"] = r.GetString(2),
                    ["titulo"] = r.GetString(3),
                    ["mensagem"] = r.GetString(4),
                    ["link"] = r.IsDBNull(5) ? null : r.GetString(5),
                    ["criado_em"] = r.GetFieldValue<DateTimeOffset>(6),
                    ["lido"] = !r.IsDBNull(7),
                });
            }

            return new JsonObject { ["avisos"] = arr, ["nao_lidos"] = unread };
        }, http.RequestAborted);
        return Results.Ok(json);
    }

    private static async Task<IResult> MarkAsync(HttpContext http, ITenantDb db, Guid? id)
    {
        var caller = http.GetCaller();
        var n = await db.InTenantAsync(caller.Identity.Tenant, async (s, ct) =>
        {
            await using var cmd = s.CreateCommand("UPDATE tenant_notifications SET read_at = now() WHERE read_at IS NULL AND ($1::uuid IS NULL OR id = $1)");
            cmd.Parameters.Add(new() { Value = (object?)id ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Uuid });
            return await cmd.ExecuteNonQueryAsync(ct);
        }, http.RequestAborted);
        return id is not null && n == 0 ? ApiProblem.Result(404, "ERR_NOT_FOUND", "Aviso não encontrado ou já lido") : Results.NoContent();
    }

    /// <summary>"Comece por aqui" (D-J04; UX B2): o que a empresa já fez, para a Visão geral guiar o primeiro valor.</summary>
    private static async Task<IResult> FirstStepsAsync(HttpContext http, ITenantDb db)
    {
        var caller = http.GetCaller();
        var steps = await db.InTenantAsync(caller.Identity.Tenant, async (s, ct) =>
        {
            await using var cmd = s.CreateCommand("""
                SELECT EXISTS (SELECT 1 FROM simulations),
                       EXISTS (SELECT 1 FROM fiscal_documents),
                       EXISTS (SELECT 1 FROM api_keys WHERE revoked_at IS NULL),
                       EXISTS (SELECT 1 FROM tenant_webhooks WHERE is_active),
                       (SELECT count(*) FROM user_memberships) > 1 OR EXISTS (SELECT 1 FROM user_invites WHERE used_at IS NULL),
                       (SELECT plan_code <> 'SANDBOX' FROM tenants),
                       (SELECT cnpj_verified_at IS NOT NULL FROM tenants),
                       EXISTS (SELECT 1 FROM dfe_sync_state WHERE enabled)
                """);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            return Enumerable.Range(0, 8).Select(i => !r.IsDBNull(i) && r.GetBoolean(i)).ToArray();
        }, http.RequestAborted);

        static JsonObject Step(string id, string title, string text, string link, bool done) =>
            new() { ["id"] = id, ["titulo"] = title, ["descricao"] = text, ["link"] = link, ["feito"] = done };
        // Só os passos que o papel de quem está vendo consegue fazer (D-R25): o financeiro não vê "crie uma chave de API".
        var scopes = caller.Identity.Scopes;
        bool Can(string s) => scopes.Contains(s, StringComparer.Ordinal);
        var all = new (string Scope, JsonObject Step, bool Done)[]
        {
            (Scopes.SimulacaoWrite, Step("simular", "Simule um cálculo", "Veja CBS, IBS e IS de um item com o motor oficial da Receita.", "/portal/simular", steps[0]), steps[0]),
            (Scopes.AuditoriaWrite, Step("auditar", "Confira uma nota", "Envie o XML de uma NF-e autorizada e compare o declarado com o recalculado.", "/portal/auditoria", steps[1]), steps[1]),
            (Scopes.CnpjsManage, Step("verificar", "Verifique a empresa", "Com o certificado e-CNPJ ou o e-mail do cadastro da Receita. Libera plano pago e a captura de notas.", "/portal/empresa", steps[6]), steps[6]),
            (Scopes.DfeManage, Step("dfe", "Capture as notas dos fornecedores", "Envie o certificado A1 e ligue a captura automática (DF-e).", "/portal/dfe", steps[7]), steps[7]),
            (Scopes.KeysManage, Step("api_key", "Crie uma chave de API", "Para o seu ERP ou e-commerce chamar o TaxGateway.", "/portal/api-keys", steps[2]), steps[2]),
            (Scopes.WebhooksManage, Step("webhook", "Receba resultados por webhook", "Lotes e DF-e avisam seu sistema quando terminam.", "/portal/webhooks", steps[3]), steps[3]),
            (Scopes.MembrosManage, Step("equipe", "Convide sua equipe", "Fiscal, desenvolvimento e financeiro, cada um com o seu papel.", "/portal/equipe", steps[4]), steps[4]),
            (Scopes.BillingManage, Step("plano", "Escolha um plano", "O Sandbox tem franquia pequena e sem excedente.", "/portal/cobranca", steps[5]), steps[5]),
        };
        var visible = all.Where(x => Can(x.Scope) || (x.Scope == Scopes.CnpjsManage && Can(Scopes.BillingManage))).ToList();
        var list = new JsonArray([.. visible.Select(x => (JsonNode)x.Step)]);
        return Results.Ok(new JsonObject { ["passos"] = list, ["concluidos"] = visible.Count(x => x.Done), ["total"] = visible.Count });
    }
}
