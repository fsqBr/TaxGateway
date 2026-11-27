using System.Text.Json;
using System.Text.Json.Nodes;

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

    private static readonly string[] MachineDefault = [Scopes.SimulacaoWrite, Scopes.AuditoriaWrite];

    private static async Task<IResult> CreateAsync(HttpContext http, CriarApiKeyRequest req, ApiKeyHasher hasher, ITenantDb db)
    {
        var caller = http.GetCaller();
        // Padrão mínimo (D-U09): sem "scopes", só simular e auditar — antes a chave nova recebia TODOS os escopos, inclusive
        // expurgo LGPD, certificado A1 e criar outras chaves. Escopos sensíveis só pedidos explicitamente.
        var scopes = req.Scopes is { Count: > 0 } s
            ? s.Distinct(StringComparer.Ordinal).ToList()
            : [.. MachineDefault.Intersect(caller.Identity.Scopes, StringComparer.Ordinal)];
        if (scopes.Count == 0)
        {
            return ApiProblem.Result(400, "ERR_VALIDATION", "Informe os escopos da chave", "Esta credencial não pode criar chave com os escopos padrão (simulacao:write, auditoria:write).");
        }

        var unknown = scopes.Except(Scopes.TenantDefault, StringComparer.Ordinal).ToList();
        if (unknown.Count > 0)
        {
            return ApiProblem.Result(403, "ERR_FORBIDDEN_SCOPE", "Escopo não permitido em API key", $"Escopos inexistentes ou só de pessoas: {string.Join(", ", unknown)}.");
        }

        if (req.Nome is { Length: > 80 })
        {
            return ApiProblem.Result(400, "ERR_VALIDATION", "nome até 80 caracteres");
        }

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

        // D-P31 (revisão de segurança A5): uma chave que expira não cria chave eterna nem que viva além dela — senão uma
        // chave temporária vazada virava acesso permanente.
        var parentExpiry = await db.InTenantAsync(caller.Identity.Tenant, async (scope, ct) =>
        {
            await using var cmd = scope.CreateCommand("SELECT expires_at FROM api_keys WHERE id = $1");
            cmd.Parameters.Add(new() { Value = caller.Identity.KeyId });
            return await cmd.ExecuteScalarAsync(ct) is DateTime dt ? new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)) : (DateTimeOffset?)null;
        }, http.RequestAborted);
        if (parentExpiry is { } pe && (req.ExpiraEm is null || req.ExpiraEm > pe))
        {
            return ApiProblem.Result(400, "ERR_VALIDATION", "expira_em obrigatória e até a validade da chave atual",
                $"Esta chave expira em {pe:yyyy-MM-dd HH:mm} UTC; a nova precisa expirar até lá.");
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
            await cmd.ExecuteNonQueryAsync(ct);
            // D-R32: credencial nova é fato de segurança — dono e administradores ficam sabendo (portal e e-mail).
            return await TenantNotices.SecurityAsync(scope, caller.Identity.Tenant.Value, "Nova chave de API criada",
                $"A chave \"{(string.IsNullOrWhiteSpace(req.Nome) ? "api-key" : req.Nome)}\" ({key.Prefix}…) foi criada com os escopos {string.Join(", ", scopes)}. " +
                "Se ninguém da empresa reconhece, revogue em Credenciais.", "/portal/credenciais", "chave:" + id, ct);
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

    /// <summary>Gerir operadores e ler a trilha de ações (D-R04).</summary>
    public const string Security = "admin:security";

    public static RouteGroupBuilder MapAdmin(this RouteGroupBuilder admin)
    {
        admin.MapPost("/tenants", CreateTenantAsync).WithMetadata(new RequiredScope(Commercial));
        admin.MapGet("/params/rates", ListRatesAsync).WithMetadata(new RequiredScope(Fiscal));
        admin.MapPost("/params/rates/changes", ProposeAsync).WithMetadata(new RequiredScope(Fiscal));
        admin.MapPost("/params/rates/changes/preview", PreviewAsync).WithMetadata(new RequiredScope(Fiscal));
        admin.MapPost("/params/changes/{id:guid}/approve", ApproveAsync).WithMetadata(new RequiredScope(Fiscal));
        admin.MapPost("/params/changes/{id:guid}/reject", RejectAsync).WithMetadata(new RequiredScope(Fiscal));
        admin.MapGet("/params/changes", ListChangesAsync).WithMetadata(new RequiredScope(Fiscal));
        return admin;
    }

    private static async Task<IResult> CreateTenantAsync(CriarTenantRequest req, AdminRepository repo, ApiKeyHasher hasher, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Nome) || !Cnpj.TryNormalize(req.CnpjRaiz, out var root) || string.IsNullOrWhiteSpace(req.Plano))
        {
            return ApiProblem.Result(400, "ERR_VALIDATION", "nome, cnpj_raiz (válido) e plano são obrigatórios");
        }

        // Retenção do XML (D-P12): 1 dia a 6 anos (guarda fiscal de 5 anos + margem). Antes: negativo → 500, 999999 aceito.
        if (req.RetencaoXmlDias is < 1 or > 2192 || req.Nome.Length > 200)
        {
            return ApiProblem.Result(400, "ERR_VALIDATION", "Dados inválidos", "retencao_xml_dias deve ficar entre 1 e 2192 (ou ausente para não guardar o XML); nome até 200 caracteres.");
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
        catch (Npgsql.PostgresException ex) when (ex.SqlState == Npgsql.PostgresErrorCodes.ForeignKeyViolation)
        {
            return ApiProblem.Result(400, "ERR_VALIDATION", "Plano inexistente", "Use um código da lista de planos (GET /admin/v1/planos), em maiúsculas.");
        }
        catch (Npgsql.PostgresException ex) when (ex.SqlState == Npgsql.PostgresErrorCodes.UniqueViolation)
        {
            return ApiProblem.Result(409, "ERR_CONFLICT", "Tenant não criado", "Já existe tenant com este CNPJ raiz.");
        }
    }

    private static async Task<IResult> ListRatesAsync(AdminRepository repo, CancellationToken ct) =>
        Results.Text(await repo.ListActiveRatesJsonAsync(ct), "application/json");

    private static readonly string[] Tributos = ["CBS", "IBS_UF", "IBS_MUN", "IS"];
    private static readonly string[] Fontes = ["LEI_FEDERAL", "SENADO", "TCU", "CG_IBS", "LEI_ENTE", "BUNDLE_RFB"];

    /// <summary>
    /// D-R18: valida as linhas de uma proposta — campos obrigatórios, coerência tributo × esfera × código do ente
    /// (CBS e IS só nacionais; IBS estadual com código de UF; IBS municipal com código de município), forma de aplicação
    /// para alíquota própria, produto só no IS, alíquota até 100%, vigência coerente e ente existente no IBGE.
    /// </summary>
    internal static async Task<(List<RateChangeRow> Rows, List<string> Errors)> ValidateLinesAsync(IReadOnlyList<LinhaAliquotaDto> linhas, AdminRepository repo, CancellationToken ct)
    {
        var rows = new List<RateChangeRow>();
        var errors = new List<string>();
        var entes = await repo.ExistingEntesAsync([.. linhas.Select(l => l.EnteCodigo).OfType<string>().Distinct()], ct);
        foreach (var (l, i) in linhas.Select((l, i) => (l, i)))
        {
            var p = $"linha {i + 1}";
            if (l.Tributo is null || !Tributos.Contains(l.Tributo) || l.Tipo is not ("REFERENCIA" or "PROPRIA_ENTE")
                || l.Esfera is not ("UNIAO" or "UF" or "MUNICIPIO") || l.Aliquota is null || l.ValidFrom is null
                || string.IsNullOrWhiteSpace(l.AtoLegal) || l.Fonte is null || !Fontes.Contains(l.Fonte))
            {
                errors.Add($"{p}: tributo, tipo, esfera, alíquota, início da vigência, ato legal e fonte são obrigatórios e válidos");
                continue;
            }

            var esferaOk = l.Tributo switch
            {
                "CBS" or "IS" => l.Esfera == "UNIAO" && l.Tipo == "REFERENCIA",
                "IBS_UF" => l.Esfera == "UF",
                _ => l.Esfera == "MUNICIPIO",
            };
            if (!esferaOk)
            {
                errors.Add($"{p}: {l.Tributo} não combina com esfera {l.Esfera}/{l.Tipo} (CBS e IS são nacionais; IBS_UF é estadual; IBS_MUN é municipal)");
            }

            if (l.Tipo == "REFERENCIA" && (l.EnteCodigo is not null || l.FormaAplicacao is not null))
            {
                errors.Add($"{p}: alíquota de referência não tem ente nem forma de aplicação");
            }

            if (l.Tipo == "PROPRIA_ENTE")
            {
                var len = l.Tributo == "IBS_UF" ? 2 : 7;
                if (l.EnteCodigo is null || l.EnteCodigo.Length != len || !l.EnteCodigo.All(char.IsAsciiDigit))
                {
                    errors.Add($"{p}: {l.Tributo} próprio exige o código IBGE do ente com {len} dígitos");
                }
                else if (!entes.Contains(l.EnteCodigo))
                {
                    errors.Add($"{p}: o código {l.EnteCodigo} não existe no cadastro do IBGE");
                }

                if (l.FormaAplicacao is not ("SUBSTITUICAO" or "ACRESCIMO" or "DECRESCIMO"))
                {
                    errors.Add($"{p}: alíquota própria exige a forma de aplicação (SUBSTITUICAO, ACRESCIMO ou DECRESCIMO)");
                }
            }

            if ((l.Tributo == "IS") != (l.Produto is not null)
                || (l.Produto is not null && !System.Text.RegularExpressions.Regex.IsMatch(l.Produto, "^(NCM:[0-9]{2,8}|CCLASS:[0-9]{6})$")))
            {
                errors.Add($"{p}: o IS exige o produto (NCM:<prefixo> ou CCLASS:<cClassTrib>); os demais tributos não têm produto");
            }

            if (l.Aliquota is < 0 or > 100)
            {
                errors.Add($"{p}: alíquota deve ficar entre 0% e 100%");
            }

            if (l.ValidTo is { } to && to <= l.ValidFrom)
            {
                errors.Add($"{p}: o fim da vigência deve ser depois do início");
            }

            rows.Add(new RateChangeRow(l.Tributo, l.Tipo, l.Esfera, l.EnteCodigo, l.FormaAplicacao, l.Aliquota!.Value,
                l.ValidFrom.Value, l.ValidTo, l.AtoLegal!, l.Fonte, l.Provisoria, l.Produto));
        }

        return (rows, errors);
    }

    private static async Task<IResult> ProposeAsync(HttpContext http, PropostaAliquotaRequest req, AdminRepository repo, CancellationToken ct)
    {
        var op = AdminKeyFilter.Get(http);
        if (string.IsNullOrWhiteSpace(req.Justificativa) || req.Linhas is not { Count: > 0 })
        {
            return ApiProblem.Result(400, "ERR_VALIDATION", "justificativa e linhas são obrigatórias");
        }

        if (req.Linhas.Count > 6000)
        {
            return ApiProblem.Result(400, "ERR_VALIDATION", "Até 6.000 linhas por proposta (todos os municípios cabem numa só)");
        }

        var (rows, errors) = await ValidateLinesAsync(req.Linhas, repo, ct);
        if (errors.Count > 0)
        {
            return ApiProblem.Result(400, "ERR_VALIDATION", "Linhas inválidas", string.Join("; ", errors.Take(50)) + (errors.Count > 50 ? $" (+{errors.Count - 50})" : ""));
        }

        // A vigência é a de cada linha (valid_from); "vigente_em" fica só como registro da proposta (D-R18).
        var id = await repo.ProposeRateChangeAsync(op.Name, req.Justificativa!, req.VigenteEm ?? DateTimeOffset.UtcNow, rows, req.Substitui ?? [], ct);
        return Results.Created($"/admin/v1/params/changes/{id}", new PropostaCriadaResponse { ChangeId = id, Status = "PROPOSED" });
    }

    /// <summary>
    /// D-R18: prévia de uma proposta (importação em lote incluída) — valida e mostra, linha a linha, o que está vigente
    /// hoje na mesma chave e vigência (o que precisa ir em "substitui"), sem gravar nada.
    /// </summary>
    private static async Task<IResult> PreviewAsync(PropostaAliquotaRequest req, AdminRepository repo, CancellationToken ct)
    {
        if (req.Linhas is not { Count: > 0 } || req.Linhas.Count > 6000)
        {
            return ApiProblem.Result(400, "ERR_VALIDATION", "Informe de 1 a 6.000 linhas");
        }

        var (rows, errors) = await ValidateLinesAsync(req.Linhas, repo, ct);
        var items = new JsonArray();
        var conflicts = 0;
        foreach (var row in rows)
        {
            var current = JsonNode.Parse(await repo.OverlappingRatesJsonAsync(row, ct))!.AsArray();
            conflicts += current.Count > 0 && !(req.Substitui ?? []).Any(id => current.Any(c => c!["id"]!.GetValue<Guid>() == id)) ? 1 : 0;
            items.Add(new JsonObject
            {
                ["tributo"] = row.Tributo,
                ["ente_codigo"] = row.EnteCodigo,
                ["produto"] = row.Produto,
                ["aliquota_nova"] = row.Aliquota,
                ["valid_from"] = row.ValidFrom.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                ["vigentes"] = current,
            });
        }

        return Results.Ok(new JsonObject
        {
            ["linhas"] = rows.Count,
            ["erros"] = new JsonArray([.. errors.Select(e => (JsonNode)e)]),
            ["conflitos_sem_substituicao"] = conflicts,
            ["itens"] = items,
        });
    }

    private static async Task<IResult> RejectAsync(HttpContext http, Guid id, AdminRepository repo, CancellationToken ct)
    {
        var body = await JsonNode.ParseAsync(http.Request.Body, cancellationToken: ct) as JsonObject;
        var reason = body?["motivo"]?.GetValue<string>()?.Trim();
        if (reason is null || reason.Length is < 5 or > 500)
        {
            return ApiProblem.Result(400, "ERR_VALIDATION", "Informe o motivo da recusa (5 a 500 caracteres)");
        }

        return await repo.RejectChangeAsync(id, AdminKeyFilter.Get(http).Name, reason, ct)
            ? Results.Ok(new PropostaCriadaResponse { ChangeId = id, Status = "REJECTED" })
            : ApiProblem.Result(409, "ERR_PARAM_CONFLICT", "Só propostas pendentes podem ser recusadas");
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
