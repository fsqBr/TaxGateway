using System.Text.Json.Nodes;

using TaxGateway.Api.Http;
using TaxGateway.Core.Params;
using TaxGateway.Infrastructure.Admin;
using TaxGateway.Infrastructure.Persistence;

namespace TaxGateway.Api.Endpoints;

/// <summary>Listagens que o portal do cliente e o backoffice precisam (D-W04). Nada aqui devolve segredo.</summary>
public static class ListingEndpoints
{
    public static RouteGroupBuilder MapListings(this RouteGroupBuilder v1)
    {
        // Quem sou eu: qualquer credencial válida (sem escopo específico). Usado pelo portal no login.
        v1.MapGet("/me", MeAsync);
        v1.MapGet("/api-keys", ApiKeysAsync).WithMetadata(new RequiredScope(Scopes.KeysManage));
        v1.MapGet("/auditoria/lotes", BatchesAsync).WithMetadata(new RequiredScope(Scopes.AuditoriaWrite));
        return v1;
    }

    public static RouteGroupBuilder MapAdminListings(this RouteGroupBuilder admin)
    {
        admin.MapGet("/tenants", TenantsAsync).WithMetadata(new RequiredScope(AdminEndpoints.Commercial));
        admin.MapGet("/tenants/{id:guid}", TenantDetailAsync).WithMetadata(new RequiredScope(AdminEndpoints.Commercial));
        admin.MapGet("/plans", (IParamSnapshotProvider p) => Results.Ok(new JsonObject
        {
            ["planos"] = new JsonArray([.. p.Current.Plans.Values.OrderBy(x => x.PlanCode, StringComparer.Ordinal).Select(x => (JsonNode)new JsonObject
            {
                ["codigo"] = x.PlanCode, ["rpm"] = x.Rpm, ["concorrencia"] = x.Concurrency, ["lote_max_documentos"] = x.BatchMaxDocs,
            })]),
        })).WithMetadata(new RequiredScope(AdminEndpoints.Commercial));
        admin.MapGet("/me", (HttpContext http) =>
        {
            var op = AdminKeyFilter.Get(http);
            return Results.Ok(new JsonObject { ["operador"] = op.Name, ["scopes"] = new JsonArray([.. op.Scopes.Select(s => (JsonNode)s)]) });
        });
        return admin;
    }

    private static async Task<IResult> MeAsync(HttpContext http, ITenantDb db)
    {
        var caller = http.GetCaller();
        var (name, root) = await db.InTenantAsync(caller.Identity.Tenant, async (s, ct) =>
        {
            await using var cmd = s.CreateCommand("SELECT name, root_cnpj FROM tenants");
            await using var r = await cmd.ExecuteReaderAsync(ct);
            return await r.ReadAsync(ct) ? (r.GetString(0), r.GetString(1)) : ("", "");
        }, http.RequestAborted);
        return Results.Ok(new JsonObject
        {
            ["tenant_id"] = caller.Identity.Tenant.Value,
            ["nome"] = name,
            ["cnpj_raiz"] = root,
            ["plano"] = caller.Identity.PlanCode,
            ["situacao"] = caller.Identity.TenantStatus,
            ["auto_provisionar_cnpj"] = caller.Identity.AutoProvisionCnpj,
            ["credencial"] = SessionAuthenticator.User(http) is not null ? "sessao" : caller.Identity.KeyId != Guid.Empty ? "api_key" : "oauth",
            ["scopes"] = new JsonArray([.. caller.Identity.Scopes.Select(s => (JsonNode)s)]),
            ["limites"] = new JsonObject { ["rpm"] = caller.Limits.Rpm, ["concorrencia"] = caller.Limits.Concurrency },
        });
    }

    private static async Task<IResult> ApiKeysAsync(HttpContext http, ITenantDb db)
    {
        var caller = http.GetCaller();
        var list = await db.InTenantAsync(caller.Identity.Tenant, async (s, ct) =>
        {
            await using var cmd = s.CreateCommand("""
                SELECT id, name, key_prefix, scopes, expires_at, revoked_at, last_used_at, created_at FROM api_keys ORDER BY created_at DESC
                """);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            var arr = new JsonArray();
            while (await r.ReadAsync(ct))
            {
                arr.Add(new JsonObject
                {
                    ["id"] = r.GetGuid(0),
                    ["nome"] = r.GetString(1),
                    ["prefixo"] = r.GetString(2),
                    ["scopes"] = new JsonArray([.. r.GetFieldValue<string[]>(3).Select(x => (JsonNode)x)]),
                    ["expira_em"] = r.IsDBNull(4) ? null : r.GetFieldValue<DateTimeOffset>(4),
                    ["revogada_em"] = r.IsDBNull(5) ? null : r.GetFieldValue<DateTimeOffset>(5),
                    ["ultimo_uso"] = r.IsDBNull(6) ? null : r.GetFieldValue<DateTimeOffset>(6),
                    ["criada_em"] = r.GetFieldValue<DateTimeOffset>(7),
                    ["atual"] = r.GetGuid(0) == caller.Identity.KeyId,
                });
            }

            return arr;
        }, http.RequestAborted);
        return Results.Ok(new JsonObject { ["api_keys"] = list });
    }

    private static async Task<IResult> BatchesAsync(HttpContext http, ITenantDb db)
    {
        var list = await db.InTenantAsync(http.GetCaller().Identity.Tenant, async (s, ct) =>
        {
            await using var cmd = s.CreateCommand("""
                SELECT id, status, direction, total_docs, processed_docs, failed_docs, conforme_docs, created_at, completed_at
                  FROM batches ORDER BY created_at DESC LIMIT 50
                """);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            var arr = new JsonArray();
            while (await r.ReadAsync(ct))
            {
                arr.Add(new JsonObject
                {
                    ["batch_id"] = r.GetGuid(0),
                    ["status"] = r.GetString(1),
                    ["direction"] = r.IsDBNull(2) ? null : r.GetString(2),
                    ["total"] = r.IsDBNull(3) ? null : r.GetInt32(3),
                    ["processados"] = r.GetInt32(4),
                    ["falhas"] = r.GetInt32(5),
                    ["conformes"] = r.IsDBNull(6) ? null : r.GetInt32(6),
                    ["criado_em"] = r.GetFieldValue<DateTimeOffset>(7),
                    ["concluido_em"] = r.IsDBNull(8) ? null : r.GetFieldValue<DateTimeOffset>(8),
                });
            }

            return arr;
        }, http.RequestAborted);
        return Results.Ok(new JsonObject { ["lotes"] = list });
    }

    /// <summary>
    /// Tenants com busca e paginação (D-J07): <c>limite</c> (1–200, padrão 50) e <c>pagina</c> (a partir de 1). Antes
    /// vinham os 200 mais recentes sem como chegar ao 201º.
    /// </summary>
    private static async Task<IResult> TenantsAsync(AdminDataSource admin, string? busca, int? limite, int? pagina, CancellationToken ct)
    {
        var limit = Math.Clamp(limite ?? 50, 1, 200);
        var page = Math.Clamp(pagina ?? 1, 1, 10_000);
        await using var count = admin.DataSource.CreateCommand("""
            SELECT count(*) FROM tenants t WHERE $1::text IS NULL OR t.name ILIKE '%' || $1 || '%' OR t.root_cnpj LIKE $1 || '%'
            """);
        count.Parameters.Add(new() { Value = (object?)busca ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
        var total = (long)(await count.ExecuteScalarAsync(ct))!;
        await using var cmd = admin.DataSource.CreateCommand("""
            SELECT coalesce(json_agg(x ORDER BY x.criado_em DESC), '[]')::text FROM (
              SELECT t.id AS tenant_id, t.name AS nome, t.root_cnpj AS cnpj_raiz, t.plan_code AS plano, t.status AS situacao,
                     t.auto_provision_cnpj AS auto_provisionar_cnpj, t.xml_retention_days AS retencao_xml_dias, t.created_at AS criado_em,
                     (SELECT coalesce(json_agg(json_build_object('client_id', a.client_id, 'nome', a.name, 'revogado_em', a.revoked_at,
                                                                 'criado_em', a.created_at) ORDER BY a.created_at), '[]')
                        FROM oauth_applications a WHERE a.tenant_id = t.id) AS clientes_oauth
                FROM tenants t
               WHERE $1::text IS NULL OR t.name ILIKE '%' || $1 || '%' OR t.root_cnpj LIKE $1 || '%'
               ORDER BY t.created_at DESC, t.id LIMIT $2 OFFSET $3) x
            """);
        cmd.Parameters.Add(new() { Value = (object?)busca ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
        cmd.Parameters.Add(new() { Value = limit });
        cmd.Parameters.Add(new() { Value = (page - 1) * limit });
        var json = (string)(await cmd.ExecuteScalarAsync(ct))!;
        return Results.Text($$"""{"tenants":{{json}},"total":{{total}},"pagina":{{page}},"limite":{{limit}}}""", "application/json");
    }

    /// <summary>
    /// Detalhe do cliente para o atendimento (D-J07): assinatura, pessoas com acesso, CNPJs, chaves (sem segredo), consumo
    /// dos últimos 3 meses e faturas. Tenant e pessoas por leitura direta; o resto por app.admin_tenant_overview (V0018, D-203).
    /// </summary>
    private static async Task<IResult> TenantDetailAsync(Guid id, AdminDataSource admin, CancellationToken ct)
    {
        await using var cmd = admin.DataSource.CreateCommand("""
            SELECT json_build_object(
              'tenant', (SELECT json_build_object('tenant_id', t.id, 'nome', t.name, 'cnpj_raiz', t.root_cnpj, 'plano', t.plan_code,
                         'situacao', t.status, 'auto_provisionar_cnpj', t.auto_provision_cnpj, 'retencao_xml_dias', t.xml_retention_days,
                         'criado_em', t.created_at, 'cobranca', json_build_object('provedor', t.billing_provider, 'cliente', t.billing_customer_ref,
                         'assinatura', t.stripe_subscription_id, 'situacao_assinatura', t.subscription_status, 'fim_do_periodo', t.current_period_end),
                         'teto_centavos', t.overage_cap_cents, 'verificada_em', t.cnpj_verified_at, 'metodo_verificacao', t.cnpj_verification_method,
                         'verificada_por', t.cnpj_verified_by, 'inadimplente_desde', t.payment_failed_at, 'motivo_suspensao', t.suspended_reason,
                         'limites', t.limits_override)
                         FROM tenants t WHERE t.id = $1),
              'pessoas', (SELECT coalesce(json_agg(json_build_object('nome', u.name, 'email', u.email, 'papel', m.role, 'mfa', u.totp_enabled_at IS NOT NULL,
                          'situacao', u.status, 'ultimo_acesso', u.last_login_at) ORDER BY m.role, u.email), '[]')
                          FROM user_memberships m JOIN users u ON u.id = m.user_id WHERE m.tenant_id = $1),
              -- CNPJs, chaves, consumo e faturas só pela função (D-203: o tg_admin não lê essas tabelas; V0018).
              'resto', app.admin_tenant_overview($1)
            )::text
            """);
        cmd.Parameters.Add(new() { Value = id });
        var json = JsonNode.Parse((string)(await cmd.ExecuteScalarAsync(ct))!)!.AsObject();
        if (json["resto"] is JsonObject resto)
        {
            json.Remove("resto");
            foreach (var (k, v) in resto.ToList())
            {
                resto.Remove(k);
                json[k] = v;
            }
        }

        return json["tenant"] is null
            ? ApiProblem.Result(404, "ERR_NOT_FOUND", "Tenant não encontrado")
            : Results.Text(json.ToJsonString(), "application/json");
    }
}
