using System.Text.Json.Nodes;

using Npgsql;

using TaxGateway.Api.Http;
using TaxGateway.Identity.Accounts;
using TaxGateway.Infrastructure.Admin;

namespace TaxGateway.Api.Endpoints;

/// <summary>Operadores do backoffice com conta e MFA (D-U08) e a trilha de auditoria das ações administrativas (D-U07).</summary>
public static class OperatorEndpoints
{
    public static RouteGroupBuilder MapOperators(this RouteGroupBuilder admin)
    {
        admin.MapGet("/operadores", async (AdminDataSource db, CancellationToken ct) =>
        {
            await using var cmd = db.DataSource.CreateCommand("""
                SELECT coalesce(json_agg(json_build_object('id', id, 'email', email, 'nome', name, 'escopos', operator_scopes,
                         'mfa', totp_enabled_at IS NOT NULL, 'situacao', status, 'ultimo_acesso', last_login_at, 'criado_em', created_at)
                         ORDER BY created_at), '[]')::text
                  FROM users WHERE realm = 'operator'
                """);
            return Results.Text((string)(await cmd.ExecuteScalarAsync(ct))!, "application/json");
        }).WithMetadata(new RequiredScope(AdminEndpoints.Commercial));

        admin.MapPost("/operadores", async (HttpContext http, AccountService accounts) =>
        {
            var b = await JsonNode.ParseAsync(http.Request.Body, cancellationToken: http.RequestAborted) as JsonObject;
            var scopes = b?["escopos"] is JsonArray a ? a.Select(x => x!.GetValue<string>()).ToList() : [];
            await accounts.CreateOperatorAsync(b?["email"]?.GetValue<string>() ?? "", b?["nome"]?.GetValue<string>() ?? "", scopes,
                AdminKeyFilter.Get(http).Name, http.RequestAborted);
            return Results.Accepted(value: new JsonObject { ["mensagem"] = "Convite enviado por e-mail (vale 24 h)." });
        }).WithMetadata(new RequiredScope(AdminEndpoints.Commercial));

        admin.MapPost("/operadores/{id:guid}/desativar", async (Guid id, HttpContext http, AdminDataSource db, AccountService accounts) =>
        {
            await using var cmd = db.DataSource.CreateCommand("UPDATE users SET status = 'DISABLED' WHERE id = $1 AND realm = 'operator' AND email <> $2");
            cmd.Parameters.Add(new() { Value = id });
            cmd.Parameters.Add(new() { Value = AdminKeyFilter.Get(http).Name });   // ninguém desativa a si mesmo
            if (await cmd.ExecuteNonQueryAsync(http.RequestAborted) == 0)
            {
                return ApiProblem.Result(404, "ERR_NOT_FOUND", "Operador não encontrado (ou é você)");
            }

            await accounts.AuditAsync(null, "operator", AdminKeyFilter.Get(http).Name, "operador.desativado", id.ToString(), http.Connection.RemoteIpAddress?.ToString(), new JsonObject(), http.RequestAborted);
            return Results.NoContent();
        }).WithMetadata(new RequiredScope(AdminEndpoints.Commercial));

        // Trilha de auditoria (login, MFA, senha, membros, ações de operador): filtro por tenant ou ator.
        admin.MapGet("/auditoria-acoes", async (Guid? tenant, string? ator, AdminDataSource db, CancellationToken ct) =>
        {
            await using var cmd = db.DataSource.CreateCommand("""
                SELECT coalesce(json_agg(x ORDER BY x.em DESC), '[]')::text FROM (
                  SELECT at AS em, actor_type AS tipo_ator, actor_id AS ator, action AS acao, tenant_id, target AS alvo, ip, detail AS detalhe
                    FROM audit_log
                   WHERE ($1::uuid IS NULL OR tenant_id = $1) AND ($2::text IS NULL OR actor_id = $2)
                   ORDER BY at DESC LIMIT 200) x
                """);
            cmd.Parameters.Add(new() { Value = (object?)tenant ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Uuid });
            cmd.Parameters.Add(new() { Value = (object?)ator ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
            return Results.Text((string)(await cmd.ExecuteScalarAsync(ct))!, "application/json");
        }).WithMetadata(new RequiredScope(AdminEndpoints.Commercial));
        return admin;
    }
}
