using System.Text.Json.Nodes;

using TaxGateway.Api.Http;
using TaxGateway.Core.Fiscal;
using TaxGateway.Infrastructure.Dfe;
using TaxGateway.Infrastructure.Persistence;

namespace TaxGateway.Api.Endpoints;

/// <summary>Captura automática de DF-e (SDD §6.5; D-901 a D-907). Escopo <c>dfe:manage</c>.</summary>
public static class DfeEndpoints
{
    public static RouteGroupBuilder MapDfe(this RouteGroupBuilder v1)
    {
        v1.MapPut("/cnpjs/{cnpj}/certificado", UploadAsync).WithMetadata(new RequiredScope(Scopes.DfeManage));
        v1.MapGet("/cnpjs/{cnpj}/certificado", GetCertAsync).WithMetadata(new RequiredScope(Scopes.DfeManage));
        v1.MapDelete("/cnpjs/{cnpj}/certificado", DeleteCertAsync).WithMetadata(new RequiredScope(Scopes.DfeManage));
        v1.MapPost("/cnpjs/{cnpj}/dfe/sync", SyncAsync).WithMetadata(new RequiredScope(Scopes.DfeManage));
        v1.MapGet("/cnpjs/{cnpj}/dfe/status", StatusAsync).WithMetadata(new RequiredScope(Scopes.DfeManage));
        return v1;
    }

    private static async Task<(string? Cnpj, IResult? Error)> ResolveAsync(HttpContext http, string raw, TenantCnpjService cnpjs)
    {
        if (!Cnpj.TryNormalize(raw, out var cnpj))
        {
            return (null, ApiProblem.Result(400, "ERR_VALIDATION", "CNPJ inválido"));
        }

        var caller = http.GetCaller();
        caller.EnsureOnBehalfOf(cnpj);
        return await cnpjs.EnsureAllowedAsync(caller.Identity.Tenant, cnpj, caller.Identity.AutoProvisionCnpj, http.RequestAborted)
            ? (cnpj, null)
            : (null, ApiProblem.Result(403, "ERR_CNPJ_NOT_PROVISIONED", "CNPJ não cadastrado para este tenant"));
    }

    /// <summary>Corpo: {"pfx_base64": "...", "senha": "..."}. A resposta nunca devolve o PFX nem a senha.</summary>
    private static async Task<IResult> UploadAsync(HttpContext http, string cnpj, TenantCnpjService cnpjs, CertificateStore store,
        TaxGateway.Infrastructure.Tenancy.CnpjCertificateVerifier verifier, Npgsql.NpgsqlDataSource app, StackExchange.Redis.IConnectionMultiplexer valkey, ITenantDb db)
    {
        var (c, error) = await ResolveAsync(http, cnpj, cnpjs);
        if (error is not null)
        {
            return error;
        }

        var body = await JsonNode.ParseAsync(http.Request.Body, cancellationToken: http.RequestAborted);
        byte[] pfx;
        try
        {
            pfx = Convert.FromBase64String(body?["pfx_base64"]?.GetValue<string>() ?? "");
        }
        catch (FormatException)
        {
            return ApiProblem.Result(400, "ERR_VALIDATION", "pfx_base64 inválido");
        }

        var senha = body?["senha"]?.GetValue<string>();
        if (pfx.Length is 0 or > 64 * 1024 || senha is null)
        {
            return ApiProblem.Result(400, "ERR_VALIDATION", "pfx_base64 (até 64 KB) e senha são obrigatórios");
        }

        try
        {
            var tenant = http.GetCaller().Identity.Tenant;
            var info = await store.StoreAsync(tenant, c!, pfx, senha, http.RequestAborted);
            // D-R10: A1 ICP-Brasil da mesma raiz da empresa prova a titularidade — a empresa fica verificada.
            if (http.GetCaller().Billing is not { CnpjVerified: true })
            {
                var root = await db.InTenantAsync(tenant, async (s, ct) =>
                {
                    await using var q = s.CreateCommand("SELECT root_cnpj FROM tenants");
                    return (string)(await q.ExecuteScalarAsync(ct))!;
                }, http.RequestAborted);
                try
                {
                    var proof = verifier.Verify(pfx, senha);
                    if (string.Equals(proof.Cnpj[..8], root[..8], StringComparison.Ordinal))
                    {
                        await using var mark = app.CreateCommand("SELECT app.mark_cnpj_verified($1, 'CERTIFICADO', $2)");
                        mark.Parameters.Add(new() { Value = tenant.Value });
                        mark.Parameters.Add(new() { Value = $"A1 {proof.Thumbprint}" });
                        await mark.ExecuteNonQueryAsync(http.RequestAborted);
                        await TenantProfileCache.PublishChangedAsync(valkey, tenant.Value);
                    }
                }
                catch (CertificateRejectedException)
                {
                    // guardado para o DF-e, mas não serve de prova (ex.: cadeia fora da ICP-Brasil)
                }
            }

            return Results.Ok(Dto(info));
        }
        catch (CertificateRejectedException ex)
        {
            return ApiProblem.Result(ex.Code == "ERR_CERT_INVALID" ? 400 : 422, ex.Code, "Certificado recusado", ex.Message);
        }
    }

    private static async Task<IResult> GetCertAsync(HttpContext http, string cnpj, TenantCnpjService cnpjs, CertificateStore store)
    {
        var (c, error) = await ResolveAsync(http, cnpj, cnpjs);
        if (error is not null)
        {
            return error;
        }

        var info = await store.GetInfoAsync(http.GetCaller().Identity.Tenant, c!, http.RequestAborted);
        return info is null ? ApiProblem.Result(404, "ERR_NOT_FOUND", "Nenhum certificado para este CNPJ") : Results.Ok(Dto(info));
    }

    private static async Task<IResult> DeleteCertAsync(HttpContext http, string cnpj, TenantCnpjService cnpjs, CertificateStore store)
    {
        var (c, error) = await ResolveAsync(http, cnpj, cnpjs);
        if (error is not null)
        {
            return error;
        }

        return await store.DeleteAsync(http.GetCaller().Identity.Tenant, c!, http.RequestAborted)
            ? Results.NoContent()
            : ApiProblem.Result(404, "ERR_NOT_FOUND", "Nenhum certificado para este CNPJ");
    }

    /// <summary>
    /// Liga (ou reconfigura) a captura. Nunca antecipa a próxima consulta: um bloqueio de 1 h em curso continua valendo
    /// (forçar a consulta é justamente o que gera o 656). Corpo opcional: {"c_uf_autor": 35, "manifestar_ciencia": true}.
    /// </summary>
    private static async Task<IResult> SyncAsync(HttpContext http, string cnpj, TenantCnpjService cnpjs, CertificateStore store, ITenantDb db)
    {
        // D-R10: captura de notas de terceiros só para empresa com titularidade verificada (o envio do A1 da matriz já verifica).
        if (http.GetCaller().Billing is not { CnpjVerified: true })
        {
            return ApiProblem.Result(403, "ERR_EMPRESA_NAO_VERIFICADA", "Verifique a empresa antes de ligar a captura",
                "Envie o certificado A1 da matriz (ele mesmo comprova a titularidade) ou verifique em Plano e cobrança.");
        }

        var (c, error) = await ResolveAsync(http, cnpj, cnpjs);
        if (error is not null)
        {
            return error;
        }

        var tenant = http.GetCaller().Identity.Tenant;
        if (await store.GetInfoAsync(tenant, c!, http.RequestAborted) is null)
        {
            return ApiProblem.Result(409, "ERR_CERT_REQUIRED", "Envie o certificado A1 do CNPJ antes de ligar a captura");
        }

        JsonNode? body = null;
        if (http.Request.ContentLength is > 0)
        {
            body = await JsonNode.ParseAsync(http.Request.Body, cancellationToken: http.RequestAborted);
        }

        var uf = body?["c_uf_autor"]?.GetValue<int>() ?? 35;
        if (uf is < 11 or > 53)
        {
            return ApiProblem.Result(400, "ERR_VALIDATION", "c_uf_autor deve ser o código IBGE da UF");
        }

        var ciencia = body?["manifestar_ciencia"]?.GetValue<bool>() ?? true;
        await db.InTenantAsync(tenant, async (s, ct) =>
        {
            await using var cmd = s.CreateCommand("""
                INSERT INTO dfe_sync_state (tenant_id, cnpj, c_uf_autor, manifest_ciencia) VALUES ($1, $2, $3, $4)
                ON CONFLICT (tenant_id, cnpj) DO UPDATE SET enabled = TRUE, c_uf_autor = EXCLUDED.c_uf_autor, manifest_ciencia = EXCLUDED.manifest_ciencia
                """);
            cmd.Parameters.Add(new() { Value = tenant.Value });
            cmd.Parameters.Add(new() { Value = c! });
            cmd.Parameters.Add(new() { Value = (short)uf });
            cmd.Parameters.Add(new() { Value = ciencia });
            return await cmd.ExecuteNonQueryAsync(ct);
        }, http.RequestAborted);
        return await StatusAsync(http, c!, cnpjs, db);
    }

    private static async Task<IResult> StatusAsync(HttpContext http, string cnpj, TenantCnpjService cnpjs, ITenantDb db)
    {
        var (c, error) = await ResolveAsync(http, cnpj, cnpjs);
        if (error is not null)
        {
            return error;
        }

        var status = await db.InTenantAsync(http.GetCaller().Identity.Tenant, async (s, ct) =>
        {
            await using var cmd = s.CreateCommand("""
                SELECT enabled, ult_nsu, max_nsu, next_allowed_at, last_cstat, last_query_at, last_656_at, manifest_ciencia,
                       (SELECT coalesce(jsonb_object_agg(status, n), '{}')::text FROM (SELECT status, count(*) n FROM dfe_documents d WHERE d.cnpj = $1 GROUP BY status) x)
                  FROM dfe_sync_state WHERE cnpj = $1
                """);
            cmd.Parameters.Add(new() { Value = c! });
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct))
            {
                return null;
            }

            return new JsonObject
            {
                ["cnpj"] = c,
                ["ativo"] = r.GetBoolean(0),
                ["ult_nsu"] = r.GetInt64(1),
                ["max_nsu"] = r.IsDBNull(2) ? null : r.GetInt64(2),
                ["proxima_consulta"] = r.GetFieldValue<DateTimeOffset>(3),
                ["ultimo_cstat"] = r.IsDBNull(4) ? null : r.GetString(4),
                ["ultima_consulta"] = r.IsDBNull(5) ? null : r.GetFieldValue<DateTimeOffset>(5),
                ["ultimo_656"] = r.IsDBNull(6) ? null : r.GetFieldValue<DateTimeOffset>(6),
                ["manifestar_ciencia"] = r.GetBoolean(7),
                ["documentos"] = JsonNode.Parse(r.GetString(8)),
            };
        }, http.RequestAborted);
        return status is null ? ApiProblem.Result(404, "ERR_NOT_FOUND", "Captura de DF-e não configurada para este CNPJ") : Results.Ok(status);
    }

    private static JsonObject Dto(CertificateInfo i) => new()
    {
        ["cnpj"] = i.Cnpj,
        ["thumbprint"] = i.Thumbprint,
        ["titular"] = i.Subject,
        ["vence_em"] = i.NotAfter,
    };
}
