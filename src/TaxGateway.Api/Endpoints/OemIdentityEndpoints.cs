using System.Globalization;
using System.Text.Json.Nodes;

using TaxGateway.Api.Http;
using TaxGateway.Core.Fiscal;
using TaxGateway.Core.Tenancy;
using TaxGateway.Identity;
using TaxGateway.Infrastructure.Admin;
using TaxGateway.Infrastructure.Persistence;

namespace TaxGateway.Api.Endpoints;

/// <summary>Endpoints OEM (SDD §6.4; D-806): gestão dos CNPJs atendidos e uso por CNPJ lido do ledger.</summary>
public static class OemEndpoints
{
    public static RouteGroupBuilder MapOem(this RouteGroupBuilder v1)
    {
        v1.MapPost("/oem/cnpjs", ActivateAsync).WithMetadata(new RequiredScope(Scopes.CnpjsManage));
        v1.MapDelete("/oem/cnpjs/{cnpj}", DeactivateAsync).WithMetadata(new RequiredScope(Scopes.CnpjsManage));
        v1.MapGet("/oem/cnpjs", ListAsync).WithMetadata(new RequiredScope(Scopes.CnpjsManage));
        v1.MapGet("/oem/usage", UsageAsync).WithMetadata(new RequiredScope(Scopes.UsageRead));
        return v1;
    }

    private static async Task<IResult> ActivateAsync(HttpContext http, TenantCnpjService cnpjs)
    {
        var body = await JsonNode.ParseAsync(http.Request.Body, cancellationToken: http.RequestAborted);
        if (body?["cnpj"]?.GetValue<string>() is not { } raw || !Cnpj.TryNormalize(raw, out var cnpj))
        {
            return ApiProblem.Result(400, "ERR_VALIDATION", "cnpj (válido) é obrigatório");
        }

        var created = await cnpjs.ActivateAsync(http.GetCaller().Identity.Tenant, cnpj, http.RequestAborted);
        var dto = new JsonObject { ["cnpj"] = cnpj, ["ativo"] = true };
        return created ? Results.Created($"/v1/oem/cnpjs/{cnpj}", dto) : Results.Ok(dto);
    }

    private static async Task<IResult> DeactivateAsync(HttpContext http, string cnpj, TenantCnpjService cnpjs, TimeProvider clock)
    {
        if (!Cnpj.TryNormalize(cnpj, out var n))
        {
            return ApiProblem.Result(400, "ERR_VALIDATION", "CNPJ inválido");
        }

        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var next = new DateOnly(today.Year, today.Month, 1).AddMonths(1);
        return await cnpjs.DeactivateAsync(http.GetCaller().Identity.Tenant, n, next, http.RequestAborted)
            ? Results.NoContent()
            : ApiProblem.Result(404, "ERR_NOT_FOUND", "CNPJ não cadastrado ou já desativado");
    }

    private static async Task<IResult> ListAsync(HttpContext http, ITenantDb db)
    {
        var list = await db.InTenantAsync(http.GetCaller().Identity.Tenant, async (s, ct) =>
        {
            await using var cmd = s.CreateCommand("SELECT cnpj, is_active, deactivated_from, created_at FROM tenant_cnpjs ORDER BY cnpj");
            await using var r = await cmd.ExecuteReaderAsync(ct);
            var arr = new JsonArray();
            while (await r.ReadAsync(ct))
            {
                arr.Add(new JsonObject
                {
                    ["cnpj"] = r.GetString(0),
                    ["ativo"] = r.GetBoolean(1),
                    ["cobranca_encerrada_a_partir_de"] = r.IsDBNull(2) ? null : r.GetFieldValue<DateOnly>(2).ToString("yyyy-MM", CultureInfo.InvariantCulture),
                    ["criado_em"] = r.GetFieldValue<DateTimeOffset>(3),
                });
            }

            return arr;
        }, http.RequestAborted);
        return Results.Ok(new JsonObject { ["cnpjs"] = list });
    }

    /// <summary>Uso por CNPJ na competência, direto do ledger (fonte da cobrança, §10).</summary>
    private static async Task<IResult> UsageAsync(HttpContext http, string? month, ITenantDb db, TimeProvider clock)
    {
        DateOnly m;
        if (month is null)
        {
            var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
            m = new DateOnly(today.Year, today.Month, 1);
        }
        else if (month.Length != 7 || !DateOnly.TryParseExact(month + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out m))
        {
            return ApiProblem.Result(400, "ERR_VALIDATION", "month deve ser AAAA-MM");
        }

        var rows = await db.InTenantAsync(http.GetCaller().Identity.Tenant, async (s, ct) =>
        {
            await using var cmd = s.CreateCommand("""
                SELECT cnpj_subject,
                       coalesce(sum(quantity) FILTER (WHERE metric = 'DOC_AUDIT'), 0),
                       coalesce(sum(quantity) FILTER (WHERE metric = 'SIMULATION'), 0)
                  FROM usage_ledger WHERE billing_month = $1 GROUP BY cnpj_subject ORDER BY cnpj_subject
                """);
            cmd.Parameters.Add(new() { Value = m });
            await using var r = await cmd.ExecuteReaderAsync(ct);
            var arr = new JsonArray();
            while (await r.ReadAsync(ct))
            {
                arr.Add(new JsonObject { ["cnpj"] = r.GetString(0), ["documentos_auditados"] = r.GetInt64(1), ["simulacoes"] = r.GetInt64(2) });
            }

            return arr;
        }, http.RequestAborted);
        return Results.Ok(new JsonObject
        {
            ["competencia"] = m.ToString("yyyy-MM", CultureInfo.InvariantCulture),
            ["cnpjs_ativos"] = rows.Count,
            ["por_cnpj"] = rows,
        });
    }
}

/// <summary>Admin API da identidade (Fase 8): clientes OAuth, suspensão de tenant e revogação de token.</summary>
public static class IdentityAdminEndpoints
{
    private static readonly string[] AllowedScopes = [.. Scopes.TenantDefault];

    public static RouteGroupBuilder MapIdentityAdmin(this RouteGroupBuilder admin)
    {
        admin.MapPost("/tenants/{id:guid}/oauth-clients", CreateClientAsync).WithMetadata(new RequiredScope(AdminEndpoints.Commercial));
        admin.MapDelete("/oauth-clients/{clientId}", RevokeClientAsync).WithMetadata(new RequiredScope(AdminEndpoints.Commercial));
        admin.MapPost("/tenants/{id:guid}/suspend", (Guid id, AdminRepository repo, RevocationService rev, CancellationToken ct) =>
            SetStatusAsync(id, "SUSPENDED", repo, rev, ct)).WithMetadata(new RequiredScope(AdminEndpoints.Commercial));
        admin.MapPost("/tenants/{id:guid}/reactivate", (Guid id, AdminRepository repo, RevocationService rev, CancellationToken ct) =>
            SetStatusAsync(id, "ACTIVE", repo, rev, ct)).WithMetadata(new RequiredScope(AdminEndpoints.Commercial));
        admin.MapPost("/oauth/revoked-tokens", RevokeTokenAsync).WithMetadata(new RequiredScope(AdminEndpoints.Commercial));
        return admin;
    }

    private static async Task<IResult> CreateClientAsync(Guid id, HttpContext http, AdminRepository repo)
    {
        var body = await JsonNode.ParseAsync(http.Request.Body, cancellationToken: http.RequestAborted);
        var name = body?["nome"]?.GetValue<string>() ?? "cliente";
        // Sem "scopes": só simular e auditar (D-U09). Escopos sensíveis precisam ser pedidos explicitamente.
        var scopes = body?["scopes"] is JsonArray a ? a.Select(x => x!.GetValue<string>()).Distinct(StringComparer.Ordinal).ToArray() : [Scopes.SimulacaoWrite, Scopes.AuditoriaWrite];
        var invalid = scopes.Except(AllowedScopes, StringComparer.Ordinal).ToList();
        if (invalid.Count > 0 || scopes.Length == 0)
        {
            return ApiProblem.Result(400, "ERR_VALIDATION", "Escopos inválidos", string.Join(", ", invalid));
        }

        var clientId = "tgc_" + ClientSecretHasher.NewSecret()[..20];
        var secret = ClientSecretHasher.NewSecret();
        var (hash, salt) = ClientSecretHasher.Hash(secret);
        if (!await repo.CreateOAuthClientAsync(id, clientId, name, hash, salt, scopes, http.RequestAborted))
        {
            return ApiProblem.Result(404, "ERR_NOT_FOUND", "Tenant não encontrado");
        }

        // O segredo aparece só nesta resposta (§3.1).
        return Results.Created($"/admin/v1/oauth-clients/{clientId}", new JsonObject
        {
            ["client_id"] = clientId,
            ["client_secret"] = secret,
            ["scopes"] = new JsonArray([.. scopes.Select(s => (JsonNode)s)]),
        });
    }

    private static async Task<IResult> RevokeClientAsync(string clientId, AdminRepository repo, RevocationService rev, CancellationToken ct)
    {
        if (!await repo.RevokeOAuthClientAsync(clientId, ct))
        {
            return ApiProblem.Result(404, "ERR_NOT_FOUND", "Cliente não encontrado ou já revogado");
        }

        await rev.RevokeClientAsync(clientId);   // tokens já emitidos param de valer na hora
        return Results.NoContent();
    }

    private static async Task<IResult> SetStatusAsync(Guid id, string status, AdminRepository repo, RevocationService rev, CancellationToken ct)
    {
        if (!await repo.SetTenantStatusAsync(id, status, ct))
        {
            return ApiProblem.Result(404, "ERR_NOT_FOUND", "Tenant não encontrado ou encerrado");
        }

        var tenant = new TenantId(id);
        await (status == "SUSPENDED" ? rev.SuspendTenantAsync(tenant) : rev.ReactivateTenantAsync(tenant));
        return Results.Ok(new JsonObject { ["tenant_id"] = id, ["status"] = status });
    }

    private static async Task<IResult> RevokeTokenAsync(HttpContext http, RevocationService rev, AdminRepository repo)
    {
        var body = await JsonNode.ParseAsync(http.Request.Body, cancellationToken: http.RequestAborted);
        if (body?["jti"]?.GetValue<string>() is not { Length: > 0 } jti || body["expira_em"]?.GetValue<DateTimeOffset>() is not { } exp)
        {
            return ApiProblem.Result(400, "ERR_VALIDATION", "jti e expira_em são obrigatórios");
        }

        await repo.RevokeTokenAsync(jti, exp, AdminKeyFilter.Get(http).Name, http.RequestAborted);   // fonte da verdade
        await rev.RevokeJtiAsync(jti, exp);   // propagação imediata
        return Results.NoContent();
    }
}
