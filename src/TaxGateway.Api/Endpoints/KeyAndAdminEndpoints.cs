using System.Text.Json;

using StackExchange.Redis;

using TaxGateway.Api.Http;
using TaxGateway.Contracts;
using TaxGateway.Core.Fiscal;
using TaxGateway.Infrastructure.Admin;
using TaxGateway.Infrastructure.Auth;
using TaxGateway.Infrastructure.Params;
using TaxGateway.Infrastructure.Persistence;

namespace TaxGateway.Api.Endpoints;

/// <summary>Gestão das API keys pelo próprio tenant (escopo keys:manage).</summary>
public static class ApiKeyEndpoints
{
    public static RouteGroupBuilder MapApiKeys(this RouteGroupBuilder v1)
    {
        v1.MapPost("/api-keys", CreateAsync).WithMetadata(new RequiredScope(Scopes.KeysManage));
        v1.MapDelete("/api-keys/{id:guid}", RevokeAsync).WithMetadata(new RequiredScope(Scopes.KeysManage));
        return v1;
    }

    private static async Task<IResult> CreateAsync(HttpContext http, CriarApiKeyRequest req, ApiKeyHasher hasher, ITenantDb db)
    {
        var caller = http.GetCaller();
        var scopes = req.Scopes is { Count: > 0 } s ? s.Distinct(StringComparer.Ordinal).ToList() : [.. Scopes.TenantDefault];
        // Uma chave não pode ganhar escopo que a chave que a criou não tem.
        var extra = scopes.Except(caller.Identity.Scopes, StringComparer.Ordinal).ToList();
        if (extra.Count > 0)
        {
            return ApiProblem.Result(403, "ERR_FORBIDDEN_SCOPE", "Escopo não permitido", $"Escopos fora da chave atual: {string.Join(", ", extra)}.");
        }

        if (req.ExpiraEm is { } exp && exp <= DateTimeOffset.UtcNow)
        {
            return ApiProblem.Result(400, "ERR_VALIDATION", "expira_em deve estar no futuro");
        }

        var key = hasher.Generate();
        var id = Guid.CreateVersion7();
        await db.InTenantAsync(caller.Identity.Tenant, async (scope, ct) =>
        {
            await using var cmd = scope.CreateCommand("""
                INSERT INTO api_keys (id, tenant_id, name, key_prefix, key_hash, scopes, expires_at) VALUES ($1, $2, $3, $4, $5, $6, $7)
                """);
            cmd.Parameters.Add(new() { Value = id });
            cmd.Parameters.Add(new() { Value = caller.Identity.Tenant.Value });
            cmd.Parameters.Add(new() { Value = string.IsNullOrWhiteSpace(req.Nome) ? "api-key" : req.Nome! });
            cmd.Parameters.Add(new() { Value = key.Prefix });
            cmd.Parameters.Add(new() { Value = key.Hash });
            cmd.Parameters.Add(new() { Value = scopes.ToArray() });
            cmd.Parameters.Add(new() { Value = (object?)req.ExpiraEm?.UtcDateTime ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.TimestampTz });
            return await cmd.ExecuteNonQueryAsync(ct);
        }, http.RequestAborted);

        return Results.Created($"/v1/api-keys/{id}", new CriarApiKeyResponse
        {
            Id = id,
            ApiKey = key.Plaintext,
            Prefixo = key.Prefix,
            Scopes = scopes,
            ExpiraEm = req.ExpiraEm,
        });
    }

    private static async Task<IResult> RevokeAsync(HttpContext http, Guid id, ITenantDb db, ApiKeyAuthenticator auth)
    {
        var caller = http.GetCaller();
        var hash = await db.InTenantAsync(caller.Identity.Tenant, async (scope, ct) =>
        {
            await using var cmd = scope.CreateCommand("UPDATE api_keys SET revoked_at = now() WHERE id = $1 AND revoked_at IS NULL RETURNING key_hash");
            cmd.Parameters.Add(new() { Value = id });
            return await cmd.ExecuteScalarAsync(ct) as byte[];
        }, http.RequestAborted);

        if (hash is null)
        {
            return ApiProblem.Result(404, "ERR_NOT_FOUND", "API key não encontrada ou já revogada");
        }

        await auth.InvalidateAsync(hash);   // efeito imediato em todas as instâncias (pub/sub)
        return Results.NoContent();
    }
}

/// <summary>Admin API (SDD §15.1 item 6): provisionamento e fluxo de duas pessoas do catálogo de alíquotas.</summary>
public static class AdminEndpoints
{
    public const string Fiscal = "admin:fiscal";
    public const string Commercial = "admin:commercial";

    public static RouteGroupBuilder MapAdmin(this RouteGroupBuilder admin)
    {
        admin.MapPost("/tenants", CreateTenantAsync).WithMetadata(new RequiredScope(Commercial));
        admin.MapGet("/params/rates", ListRatesAsync).WithMetadata(new RequiredScope(Fiscal));
        admin.MapPost("/params/rates/changes", ProposeAsync).WithMetadata(new RequiredScope(Fiscal));
        admin.MapPost("/params/changes/{id:guid}/approve", ApproveAsync).WithMetadata(new RequiredScope(Fiscal));
        admin.MapGet("/params/changes", ListChangesAsync).WithMetadata(new RequiredScope(Fiscal));
        return admin;
    }

    private static async Task<IResult> CreateTenantAsync(CriarTenantRequest req, AdminRepository repo, ApiKeyHasher hasher, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Nome) || !Cnpj.TryNormalize(req.CnpjRaiz, out var root) || string.IsNullOrWhiteSpace(req.Plano))
        {
            return ApiProblem.Result(400, "ERR_VALIDATION", "nome, cnpj_raiz (válido) e plano são obrigatórios");
        }

        var cnpjs = new List<string>();
        foreach (var c in req.Cnpjs ?? [])
        {
            if (!Cnpj.TryNormalize(c, out var n))
            {
                return ApiProblem.Result(400, "ERR_VALIDATION", $"CNPJ inválido: {c}");
            }

            cnpjs.Add(n);
        }

        var key = hasher.Generate();
        try
        {
            var tenant = await repo.CreateTenantAsync(
                new NewTenant(req.Nome, root, req.Plano, req.AutoProvisionarCnpj, cnpjs, req.RetencaoXmlDias),
                "inicial", key.Prefix, key.Hash, Scopes.TenantDefault, ct);
            return Results.Created($"/admin/v1/tenants/{tenant}", new CriarTenantResponse
            {
                TenantId = tenant.Value,
                ApiKey = key.Plaintext,
                Scopes = [.. Scopes.TenantDefault],
            });
        }
        catch (Npgsql.PostgresException ex) when (ex.SqlState is Npgsql.PostgresErrorCodes.UniqueViolation or Npgsql.PostgresErrorCodes.ForeignKeyViolation)
        {
            return ApiProblem.Result(409, "ERR_CONFLICT", "Tenant não criado", ex.SqlState == Npgsql.PostgresErrorCodes.UniqueViolation
                ? "Já existe tenant com este CNPJ raiz." : "Plano inexistente.");
        }
    }

    private static async Task<IResult> ListRatesAsync(AdminRepository repo, CancellationToken ct) =>
        Results.Text(await repo.ListActiveRatesJsonAsync(ct), "application/json");

    private static readonly string[] Tributos = ["CBS", "IBS_UF", "IBS_MUN", "IS"];
    private static readonly string[] Fontes = ["LEI_FEDERAL", "SENADO", "TCU", "CG_IBS", "LEI_ENTE", "BUNDLE_RFB"];

    private static async Task<IResult> ProposeAsync(HttpContext http, PropostaAliquotaRequest req, AdminRepository repo, CancellationToken ct)
    {
        var op = AdminKeyFilter.Get(http);
        var rows = new List<RateChangeRow>();
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(req.Justificativa) || req.Linhas is not { Count: > 0 })
        {
            return ApiProblem.Result(400, "ERR_VALIDATION", "justificativa e linhas são obrigatórias");
        }

        foreach (var (l, i) in req.Linhas.Select((l, i) => (l, i)))
        {
            if (l.Tributo is null || !Tributos.Contains(l.Tributo) || l.Tipo is not ("REFERENCIA" or "PROPRIA_ENTE")
                || l.Esfera is not ("UNIAO" or "UF" or "MUNICIPIO") || l.Aliquota is not >= 0 || l.ValidFrom is null
                || string.IsNullOrWhiteSpace(l.AtoLegal) || l.Fonte is null || !Fontes.Contains(l.Fonte))
            {
                errors.Add($"linhas[{i}]: tributo, tipo, esfera, aliquota, valid_from, ato_legal e fonte são obrigatórios e válidos");
                continue;
            }

            rows.Add(new RateChangeRow(l.Tributo, l.Tipo, l.Esfera, l.EnteCodigo, l.FormaAplicacao, l.Aliquota.Value,
                l.ValidFrom.Value, l.ValidTo, l.AtoLegal!, l.Fonte, l.Provisoria));
        }

        if (errors.Count > 0)
        {
            return ApiProblem.Result(400, "ERR_VALIDATION", "Linhas inválidas", string.Join("; ", errors));
        }

        var id = await repo.ProposeRateChangeAsync(op.Name, req.Justificativa!, req.VigenteEm ?? DateTimeOffset.UtcNow, rows, req.Substitui ?? [], ct);
        return Results.Created($"/admin/v1/params/changes/{id}", new PropostaCriadaResponse { ChangeId = id, Status = "PROPOSED" });
    }

    private static async Task<IResult> ApproveAsync(HttpContext http, Guid id, AdminRepository repo, ParamSnapshotService snapshots,
        IConnectionMultiplexer valkey, CancellationToken ct)
    {
        var op = AdminKeyFilter.Get(http);
        try
        {
            await repo.ApproveRateChangeAsync(id, op.Name, ct);
        }
        catch (KeyNotFoundException)
        {
            return ApiProblem.Result(404, "ERR_NOT_FOUND", "Mudança não encontrada");
        }
        catch (ParamChangeConflictException ex)
        {
            return ApiProblem.Result(409, "ERR_PARAM_CONFLICT", "Mudança não aplicada", ex.Message);
        }

        // Propagação: esta instância recarrega já; as demais por pub/sub (meta ≤ 5 s, SDD §16.3).
        var snapshot = await snapshots.ReloadAsync(ct);
        try
        {
            await valkey.GetSubscriber().PublishAsync(RedisChannel.Literal(ParamSnapshotService.ChangedChannel), snapshot.ContentSha256);
        }
        catch (RedisException)
        {
            // As outras instâncias pegam a mudança na verificação periódica (30 s).
        }

        return Results.Ok(new PropostaCriadaResponse { ChangeId = id, Status = "APPLIED" });
    }

    private static async Task<IResult> ListChangesAsync(string? status, AdminRepository repo, CancellationToken ct)
    {
        var list = await repo.ListChangesAsync(status, ct);
        return Results.Ok(list.Select(c => new MudancaParametroDto
        {
            Id = c.Id,
            Dominio = c.Domain,
            Status = c.Status,
            PropostoPor = c.ProposedBy,
            AprovadoPor = c.ApprovedBy,
            Justificativa = c.Justification,
            VigenteEm = c.EffectiveAt,
            CriadoEm = c.CreatedAt,
            Payload = JsonDocument.Parse(c.PayloadJson).RootElement.Clone(),
        }).ToList());
    }
}
