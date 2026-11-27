using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

using StackExchange.Redis;

using TaxGateway.Api.Http;
using TaxGateway.Identity.Accounts;
using TaxGateway.Infrastructure.Admin;
using TaxGateway.Infrastructure.Dfe;
using TaxGateway.Infrastructure.Persistence;
using TaxGateway.Infrastructure.Tenancy;

namespace TaxGateway.Api.Endpoints;

/// <summary>
/// Empresa: teto de excedente (D-R08) e titularidade do CNPJ (D-R10) — verificação por certificado e-CNPJ, por código
/// no e-mail do cadastro da Receita ou manual pelo backoffice, e disputa de um CNPJ cadastrado por outra conta.
/// </summary>
public static class CompanyEndpoints
{
    public const long MaxCapCents = 100_000_000;   // R$ 1 milhão/mês: acima disso, contrato com o comercial

    public static RouteGroupBuilder MapCompany(this RouteGroupBuilder v1)
    {
        var billing = new RequiredScope(Scopes.BillingManage);
        v1.MapPut("/cobranca/teto", SetCapAsync).WithMetadata(billing).ExcludeFromDescription();
        v1.MapGet("/verificacao-cnpj", StatusAsync).WithMetadata(billing).ExcludeFromDescription();
        v1.MapPost("/verificacao-cnpj/certificado", ByCertificateAsync).WithMetadata(billing).ExcludeFromDescription();
        v1.MapPost("/verificacao-cnpj/email", SendCodeAsync).WithMetadata(billing).ExcludeFromDescription();
        v1.MapPost("/verificacao-cnpj/email/confirmar", ConfirmCodeAsync).WithMetadata(billing).ExcludeFromDescription();
        return v1;
    }

    /// <summary>Disputa: pessoa com conta (sem empresa, ou de outra empresa) prova pelo e-CNPJ que é titular de um CNPJ já cadastrado.</summary>
    public static IEndpointRouteBuilder MapCompanyDispute(this IEndpointRouteBuilder app)
    {
        app.MapPost("/v1/conta/disputa-cnpj", DisputeAsync).AddEndpointFilter<CsrfFilter>().RequireRateLimiting(AccountEndpoints.RateLimitPolicy)
            .WithTags("Conta").ExcludeFromDescription();
        return app;
    }

    public static RouteGroupBuilder MapAdminCompany(this RouteGroupBuilder admin)
    {
        admin.MapPut("/tenants/{id:guid}/teto", AdminSetCapAsync).WithMetadata(new RequiredScope(AdminEndpoints.Commercial));
        admin.MapPost("/tenants/{id:guid}/verificar", AdminVerifyAsync).WithMetadata(new RequiredScope(AdminEndpoints.Security));
        admin.MapGet("/disputas", ListDisputesAsync).WithMetadata(new RequiredScope(AdminEndpoints.Security));
        admin.MapPost("/disputas/{id:guid}/resolver", ResolveDisputeAsync).WithMetadata(new RequiredScope(AdminEndpoints.Security));
        return admin;
    }

    private static async Task<JsonObject?> BodyAsync(HttpContext http)
    {
        try
        {
            return await JsonNode.ParseAsync(http.Request.Body, cancellationToken: http.RequestAborted) as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static long? Cents(JsonObject? b) =>
        b?["teto_centavos"] is JsonValue v && v.TryGetValue<long>(out var c) ? c : null;

    // ---------------------------------------------------------------- teto de excedente

    private static async Task<IResult> SetCapAsync(HttpContext http, Npgsql.NpgsqlDataSource app, IConnectionMultiplexer valkey, AccountService accounts)
    {
        var cents = Cents(await BodyAsync(http));
        if (cents is not (>= 0 and <= MaxCapCents))
        {
            return ApiProblem.Result(400, "ERR_VALIDATION", "teto_centavos de 0 a 100.000.000", "0 = parar ao esgotar a franquia; acima de R$ 1 milhão/mês, fale com o comercial.");
        }

        var tenant = http.GetCaller().Identity.Tenant.Value;
        await using (var cmd = app.CreateCommand("SELECT app.set_overage_cap($1, $2)"))
        {
            cmd.Parameters.Add(new() { Value = tenant });
            cmd.Parameters.Add(new() { Value = cents.Value });
            await cmd.ExecuteNonQueryAsync(http.RequestAborted);
        }

        await TenantProfileCache.PublishChangedAsync(valkey, tenant);
        await accounts.AuditAsync(new Core.Tenancy.TenantId(tenant), "user", SessionAuthenticator.User(http)?.Session.Email ?? "api", "cobranca.teto_alterado",
            tenant.ToString(), http.Connection.RemoteIpAddress?.ToString(), new JsonObject { ["teto_centavos"] = cents.Value }, http.RequestAborted);
        return Results.Ok(new JsonObject { ["teto_centavos"] = cents.Value });
    }

    private static async Task<IResult> AdminSetCapAsync(Guid id, HttpContext http, AdminDataSource db, IConnectionMultiplexer valkey, AccountService accounts)
    {
        var cents = Cents(await BodyAsync(http));
        if (cents is not >= 0)
        {
            return ApiProblem.Result(400, "ERR_VALIDATION", "teto_centavos maior ou igual a zero");
        }

        await using (var cmd = db.DataSource.CreateCommand("SELECT app.set_overage_cap($1, $2)"))
        {
            cmd.Parameters.Add(new() { Value = id });
            cmd.Parameters.Add(new() { Value = cents.Value });
            await cmd.ExecuteNonQueryAsync(http.RequestAborted);
        }

        await TenantProfileCache.PublishChangedAsync(valkey, id);
        await accounts.AuditAsync(null, "operator", AdminKeyFilter.Get(http).Name, "tenant.teto_alterado", id.ToString(), http.Connection.RemoteIpAddress?.ToString(),
            new JsonObject { ["teto_centavos"] = cents.Value }, http.RequestAborted);
        return Results.Ok(new JsonObject { ["tenant_id"] = id, ["teto_centavos"] = cents.Value });
    }

    // ---------------------------------------------------------------- verificação

    private sealed record CompanyState(string Name, string Root, DateTimeOffset? VerifiedAt, string? Method);

    private static Task<CompanyState> CompanyAsync(HttpContext http, ITenantDb db) =>
        db.InTenantAsync(http.GetCaller().Identity.Tenant, async (s, ct) =>
        {
            await using var cmd = s.CreateCommand("SELECT name, root_cnpj, cnpj_verified_at, cnpj_verification_method FROM tenants");
            await using var r = await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            return new CompanyState(r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetFieldValue<DateTimeOffset>(2), r.IsDBNull(3) ? null : r.GetString(3));
        }, http.RequestAborted);

    private static async Task<IResult> StatusAsync(HttpContext http, ITenantDb db, ICnpjRegistry registry)
    {
        var c = await CompanyAsync(http, db);
        return Results.Ok(new JsonObject
        {
            ["cnpj"] = c.Root,
            ["verificada"] = c.VerifiedAt is not null,
            ["verificada_em"] = c.VerifiedAt,
            ["metodo"] = c.Method,
            ["metodos_disponiveis"] = new JsonArray([.. new[] { "CERTIFICADO", registry.Enabled ? "EMAIL_RECEITA" : null, "MANUAL" }.Where(x => x is not null).Select(x => (JsonNode)x!)]),
            ["exigida_para"] = new JsonArray("contratar plano pago", "captura de DF-e"),
        });
    }

    private static async Task<IResult> MarkVerifiedAsync(HttpContext http, Npgsql.NpgsqlDataSource app, IConnectionMultiplexer valkey, AccountService accounts, string method, string detail)
    {
        var tenant = http.GetCaller().Identity.Tenant.Value;
        var by = SessionAuthenticator.User(http)?.Session.Email ?? "api";
        await using (var cmd = app.CreateCommand("SELECT app.mark_cnpj_verified($1, $2, $3)"))
        {
            cmd.Parameters.Add(new() { Value = tenant });
            cmd.Parameters.Add(new() { Value = method });
            cmd.Parameters.Add(new() { Value = by });
            await cmd.ExecuteNonQueryAsync(http.RequestAborted);
        }

        await TenantProfileCache.PublishChangedAsync(valkey, tenant);
        await accounts.AuditAsync(new Core.Tenancy.TenantId(tenant), "user", by, "empresa.verificada", tenant.ToString(), http.Connection.RemoteIpAddress?.ToString(),
            new JsonObject { ["metodo"] = method, ["detalhe"] = detail }, http.RequestAborted);
        return Results.Ok(new JsonObject { ["verificada"] = true, ["metodo"] = method });
    }

    private static async Task<(byte[]? Pfx, string? Password, IResult? Error)> PfxAsync(HttpContext http)
    {
        var b = await BodyAsync(http);
        var b64 = b?["pfx_base64"]?.GetValue<string>();
        var pwd = b?["senha"]?.GetValue<string>();
        if (string.IsNullOrEmpty(b64) || pwd is null || b64.Length > 200_000)
        {
            return (null, null, ApiProblem.Result(400, "ERR_VALIDATION", "Informe pfx_base64 (até ~150 KB) e senha do certificado e-CNPJ"));
        }

        try
        {
            return (Convert.FromBase64String(b64), pwd, null);
        }
        catch (FormatException)
        {
            return (null, null, ApiProblem.Result(400, "ERR_VALIDATION", "pfx_base64 não é base64 válido"));
        }
    }

    private static async Task<IResult> ByCertificateAsync(HttpContext http, ITenantDb db, Npgsql.NpgsqlDataSource app, CnpjCertificateVerifier verifier,
        IConnectionMultiplexer valkey, AccountService accounts)
    {
        var (pfx, pwd, error) = await PfxAsync(http);
        if (error is not null)
        {
            return error;
        }

        CertificateProof proof;
        try
        {
            proof = verifier.Verify(pfx!, pwd!);
        }
        catch (CertificateRejectedException ex)
        {
            return ApiProblem.Result(422, ex.Code, "Certificado recusado", ex.Message);
        }

        var c = await CompanyAsync(http, db);
        if (!string.Equals(proof.Cnpj[..8], c.Root[..8], StringComparison.Ordinal))
        {
            return ApiProblem.Result(422, "ERR_CERT_CNPJ_MISMATCH", "O certificado é de outra empresa",
                $"O certificado é do CNPJ {proof.Cnpj}; a raiz da empresa é {c.Root[..8]}.");
        }

        // O PFX não é guardado: só serve de prova (para a captura de DF-e, envie o A1 na tela de DF-e).
        return await MarkVerifiedAsync(http, app, valkey, accounts, "CERTIFICADO", $"{proof.Subject} ({proof.Thumbprint})");
    }

    private static byte[] CodeHash(Guid tenant, string code) => SHA256.HashData(Encoding.UTF8.GetBytes($"{tenant:N}:{code}"));

    private static async Task<IResult> SendCodeAsync(HttpContext http, ITenantDb db, ICnpjRegistry registry, IEmailSender email)
    {
        if (!registry.Enabled)
        {
            return ApiProblem.Result(503, "ERR_VERIFICACAO_INDISPONIVEL", "Verificação por e-mail indisponível", "Use o certificado e-CNPJ ou peça a verificação manual ao suporte.");
        }

        var c = await CompanyAsync(http, db);
        var to = await registry.EmailAsync(c.Root, http.RequestAborted);
        if (to is null)
        {
            return ApiProblem.Result(422, "ERR_SEM_EMAIL_NA_RECEITA", "O cadastro do CNPJ na Receita não tem e-mail",
                "Use o certificado e-CNPJ ou peça a verificação manual ao suporte.");
        }

        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
        var tenant = http.GetCaller().Identity.Tenant;
        await db.InTenantAsync(tenant, async (s, ct) =>
        {
            await using var cmd = s.CreateCommand("""
                INSERT INTO cnpj_verification_codes (tenant_id, code_hash, email, attempts, expires_at) VALUES ($1, $2, $3, 0, now() + interval '30 minutes')
                ON CONFLICT (tenant_id) DO UPDATE SET code_hash = EXCLUDED.code_hash, email = EXCLUDED.email, attempts = 0, expires_at = EXCLUDED.expires_at, created_at = now()
                """);
            cmd.Parameters.Add(new() { Value = tenant.Value });
            cmd.Parameters.Add(new() { Value = CodeHash(tenant.Value, code) });
            cmd.Parameters.Add(new() { Value = to });
            return await cmd.ExecuteNonQueryAsync(ct);
        }, http.RequestAborted);
        await email.SendAsync(EmailTemplates.CnpjVerificationCode(to, c.Name, code), http.RequestAborted);
        return Results.Accepted(value: new JsonObject { ["enviado_para"] = HttpCnpjRegistry.Mask(to), ["expira_em_minutos"] = 30 });
    }

    private static async Task<IResult> ConfirmCodeAsync(HttpContext http, ITenantDb db, Npgsql.NpgsqlDataSource app, IConnectionMultiplexer valkey, AccountService accounts)
    {
        var code = (await BodyAsync(http))?["codigo"]?.GetValue<string>()?.Trim() ?? "";
        var tenant = http.GetCaller().Identity.Tenant;
        var result = await db.InTenantAsync(tenant, async (s, ct) =>
        {
            await using var cmd = s.CreateCommand("""
                UPDATE cnpj_verification_codes SET attempts = attempts + 1
                 WHERE tenant_id = $1 AND expires_at > now() AND attempts < 5
                RETURNING code_hash = $2
                """);
            cmd.Parameters.Add(new() { Value = tenant.Value });
            cmd.Parameters.Add(new() { Value = CodeHash(tenant.Value, code) });
            var ok = await cmd.ExecuteScalarAsync(ct);
            if (ok is true)
            {
                await using var del = s.CreateCommand("DELETE FROM cnpj_verification_codes WHERE tenant_id = $1");
                del.Parameters.Add(new() { Value = tenant.Value });
                await del.ExecuteNonQueryAsync(ct);
            }

            return ok as bool?;
        }, http.RequestAborted);
        return result switch
        {
            true => await MarkVerifiedAsync(http, app, valkey, accounts, "EMAIL_RECEITA", "código no e-mail do cadastro da Receita"),
            false => ApiProblem.Result(400, "ERR_CODIGO_INVALIDO", "Código inválido", "Confira o código recebido (até 5 tentativas)."),
            _ => ApiProblem.Result(400, "ERR_CODIGO_EXPIRADO", "Código expirado ou tentativas esgotadas", "Peça um novo código."),
        };
    }

    // ---------------------------------------------------------------- disputa

    private static async Task<IResult> DisputeAsync(HttpContext http, AccountService accounts, SessionCookies cookies, Npgsql.NpgsqlDataSource app,
        CnpjCertificateVerifier verifier)
    {
        var session = await accounts.GetSessionAsync(cookies.Read(http, "customer"), "customer", http.RequestAborted);
        if (session is null || !session.MfaPassed)
        {
            return ApiProblem.Result(401, "ERR_UNAUTHENTICATED", "Entre com sua conta (e MFA) para contestar a titularidade");
        }

        var (pfx, pwd, error) = await PfxAsync(http);
        if (error is not null)
        {
            return error;
        }

        CertificateProof proof;
        try
        {
            proof = verifier.Verify(pfx!, pwd!);
        }
        catch (CertificateRejectedException ex)
        {
            return ApiProblem.Result(422, ex.Code, "Certificado recusado", ex.Message);
        }

        // Compara pela raiz (8 dígitos): o certificado pode ser da matriz ou de uma filial.
        await using var cmd = app.CreateCommand("SELECT tenant_id, verified FROM app.open_cnpj_dispute($1, $2, $3, $4)");
        cmd.Parameters.Add(new() { Value = Guid.CreateVersion7() });
        cmd.Parameters.Add(new() { Value = proof.Cnpj[..8] });
        cmd.Parameters.Add(new() { Value = session.UserId });
        cmd.Parameters.Add(new() { Value = $"{proof.Subject} ({proof.Thumbprint})" });
        await using var r = await cmd.ExecuteReaderAsync(http.RequestAborted);
        if (!await r.ReadAsync(http.RequestAborted))
        {
            return ApiProblem.Result(404, "ERR_NOT_FOUND", "Este CNPJ não tem cadastro", "Crie a conta da empresa normalmente em /cadastro.");
        }

        if (r.GetBoolean(1))
        {
            return ApiProblem.Result(409, "ERR_EMPRESA_VERIFICADA", "A empresa já foi verificada por quem administra a conta",
                "Peça um convite ao administrador da conta da empresa.");
        }

        await accounts.AuditAsync(new Core.Tenancy.TenantId(r.GetGuid(0)), "user", session.Email, "empresa.disputa_aberta", r.GetGuid(0).ToString(),
            http.Connection.RemoteIpAddress?.ToString(), new JsonObject { ["certificado"] = proof.Thumbprint }, http.RequestAborted);
        return Results.Accepted(value: new JsonObject
        {
            ["mensagem"] = "Pedido registrado. A conta atual foi avisada; o time do TaxGateway analisa e, confirmada a titularidade, transfere a empresa para você.",
        });
    }

    private static async Task<IResult> AdminVerifyAsync(Guid id, HttpContext http, AdminDataSource db, IConnectionMultiplexer valkey, AccountService accounts)
    {
        var note = (await BodyAsync(http))?["observacao"]?.GetValue<string>()?.Trim();
        if (note is null || note.Length < 5 || note.Length > 500)
        {
            return ApiProblem.Result(400, "ERR_VALIDATION", "Informe a observação (documento conferido, de 5 a 500 caracteres)");
        }

        var op = AdminKeyFilter.Get(http).Name;
        await using (var cmd = db.DataSource.CreateCommand("SELECT app.mark_cnpj_verified($1, 'MANUAL', $2)"))
        {
            cmd.Parameters.Add(new() { Value = id });
            cmd.Parameters.Add(new() { Value = op });
            await cmd.ExecuteNonQueryAsync(http.RequestAborted);
        }

        await TenantProfileCache.PublishChangedAsync(valkey, id);
        await accounts.AuditAsync(null, "operator", op, "empresa.verificada_manual", id.ToString(), http.Connection.RemoteIpAddress?.ToString(),
            new JsonObject { ["observacao"] = note }, http.RequestAborted);
        return Results.Ok(new JsonObject { ["tenant_id"] = id, ["verificada"] = true, ["metodo"] = "MANUAL" });
    }

    private static async Task<IResult> ListDisputesAsync(AdminDataSource db, CancellationToken ct)
    {
        await using var cmd = db.DataSource.CreateCommand("""
            SELECT coalesce(json_agg(json_build_object('id', d.id, 'cnpj', d.cnpj_root, 'tenant_id', d.tenant_id, 'empresa', t.name,
                     'requerente', u.email, 'requerente_nome', u.name, 'certificado', d.certificate, 'situacao', d.status,
                     'criada_em', d.created_at, 'resolvida_por', d.resolved_by, 'resolvida_em', d.resolved_at) ORDER BY d.created_at DESC), '[]')::text
              FROM cnpj_disputes d JOIN tenants t ON t.id = d.tenant_id JOIN users u ON u.id = d.claimant_user
            """);
        return Results.Text($$"""{"disputas":{{(string)(await cmd.ExecuteScalarAsync(ct))!}}}""", "application/json");
    }

    private static async Task<IResult> ResolveDisputeAsync(Guid id, HttpContext http, AdminDataSource db, IConnectionMultiplexer valkey, AccountService accounts)
    {
        var action = (await BodyAsync(http))?["acao"]?.GetValue<string>();
        if (action is not ("transferir" or "rejeitar"))
        {
            return ApiProblem.Result(400, "ERR_VALIDATION", "acao deve ser 'transferir' ou 'rejeitar'");
        }

        var op = AdminKeyFilter.Get(http).Name;
        await using var cmd = db.DataSource.CreateCommand("SELECT app.resolve_cnpj_dispute($1, $2, $3)");
        cmd.Parameters.Add(new() { Value = id });
        cmd.Parameters.Add(new() { Value = action == "transferir" });
        cmd.Parameters.Add(new() { Value = op });
        if (await cmd.ExecuteScalarAsync(http.RequestAborted) is not Guid tenant)
        {
            return ApiProblem.Result(404, "ERR_NOT_FOUND", "Disputa não encontrada ou já resolvida");
        }

        await TenantProfileCache.PublishChangedAsync(valkey, tenant);
        await accounts.AuditAsync(null, "operator", op, $"empresa.disputa_{action}", tenant.ToString(), http.Connection.RemoteIpAddress?.ToString(),
            new JsonObject { ["disputa"] = id }, http.RequestAborted);
        return Results.Ok(new JsonObject { ["disputa"] = id, ["acao"] = action, ["tenant_id"] = tenant });
    }
}
