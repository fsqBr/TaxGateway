using System.Text.Json.Nodes;

using TaxGateway.Api.Http;
using TaxGateway.Core.Params;
using TaxGateway.Core.Tenancy;
using TaxGateway.Identity.Accounts;

namespace TaxGateway.Api.Endpoints;

/// <summary>
/// Contas de pessoas (D-U01 a D-U08): cadastro, confirmação de e-mail, login com MFA obrigatório, sessão em cookie,
/// senha, empresas e convites. /v1/conta/* para clientes; /admin/v1/conta/* para operadores do backoffice. Rotas
/// anônimas têm limite por IP (política "conta"); as que mudam estado exigem o header anti-CSRF.
/// </summary>
public static class AccountEndpoints
{
    public const string RateLimitPolicy = "conta";

    public static void MapAccounts(this WebApplication app, bool includeOperators)
    {
        var c = app.MapGroup("/v1/conta").AddEndpointFilter<CsrfFilter>().WithTags("Conta");
        MapCommon(c, "customer");
        c.MapPost("/cadastro", SignupAsync).RequireRateLimiting(RateLimitPolicy);
        c.MapPost("/confirmar-email", async (HttpContext h, AccountService a) =>
        {
            await a.VerifyEmailAsync(Str(await Body(h), "token"), h.RequestAborted);
            return Results.Ok(new JsonObject { ["confirmado"] = true });
        }).RequireRateLimiting(RateLimitPolicy);
        c.MapPost("/reenviar-confirmacao", async (HttpContext h, AccountService a) =>
        {
            await a.ResendVerificationAsync(Str(await Body(h), "email"), h.RequestAborted);
            return Results.Accepted();
        }).RequireRateLimiting(RateLimitPolicy);
        c.MapPost("/empresa-ativa", SetTenantAsync);
        c.MapGet("/convite", async (string token, AccountService a, CancellationToken ct) =>
        {
            var i = await a.InviteInfoAsync(token, ct);
            return Results.Ok(new JsonObject { ["empresa"] = i.TenantName, ["email"] = i.Email, ["papel"] = i.Role, ["tem_conta"] = i.HasAccount });
        }).RequireRateLimiting(RateLimitPolicy);
        c.MapPost("/convite/aceitar", AcceptInviteAsync).RequireRateLimiting(RateLimitPolicy);
        c.MapGet("/membros", MembersAsync);
        c.MapPost("/membros/convites", InviteAsync);
        c.MapDelete("/membros/convites", RevokeInviteAsync);
        c.MapPatch("/membros/{userId:guid}", ChangeRoleAsync);
        c.MapDelete("/membros/{userId:guid}", RemoveMemberAsync);

        if (includeOperators)
        {
            var o = app.MapGroup("/admin/v1/conta").AddEndpointFilter<CsrfFilter>().WithTags("Conta de operador");
            MapCommon(o, "operator");
        }
    }

    private static void MapCommon(RouteGroupBuilder g, string realm)
    {
        g.MapPost("/entrar", (HttpContext h, AccountService a, SessionCookies k) => LoginAsync(h, a, k, realm)).RequireRateLimiting(RateLimitPolicy);
        g.MapGet("/sessao", (HttpContext h, AccountService a, SessionCookies k, IParamSnapshotProvider p) => SessionAsync(h, a, k, p, realm));
        g.MapPost("/sair", async (HttpContext h, AccountService a, SessionCookies k) =>
        {
            if (await Current(h, a, k, realm, requireMfa: false) is { } s)
            {
                await a.LogoutAsync(s, Ip(h), h.RequestAborted);
            }

            k.Clear(h, realm);
            return Results.NoContent();
        });
        g.MapPost("/sair-outras", async (HttpContext h, AccountService a, SessionCookies k) =>
            Results.Ok(new JsonObject { ["encerradas"] = await a.LogoutOthersAsync(await Require(h, a, k, realm), Ip(h), h.RequestAborted) }));
        g.MapGet("/sessoes", async (HttpContext h, AccountService a, SessionCookies k) =>
            Results.Ok(await a.ListSessionsAsync(await Require(h, a, k, realm), h.RequestAborted)));
        g.MapPost("/mfa/iniciar", async (HttpContext h, AccountService a, SessionCookies k) =>
        {
            var (secret, uri) = await a.MfaBeginAsync(await Require(h, a, k, realm, requireMfa: false), h.RequestAborted);
            return Results.Ok(new JsonObject { ["segredo"] = secret, ["otpauth_uri"] = uri });
        });
        g.MapPost("/mfa/ativar", async (HttpContext h, AccountService a, SessionCookies k) =>
        {
            var codes = await a.MfaEnableAsync(await Require(h, a, k, realm, requireMfa: false), Str(await Body(h), "codigo"), Ip(h), h.RequestAborted);
            return Results.Ok(new JsonObject { ["codigos_recuperacao"] = new JsonArray([.. codes.Select(x => (JsonNode)x)]) });
        });
        g.MapPost("/mfa/verificar", async (HttpContext h, AccountService a, SessionCookies k) =>
        {
            var body = await Body(h);
            await a.MfaVerifyAsync(await Require(h, a, k, realm, requireMfa: false), Opt(body, "codigo"), Opt(body, "codigo_recuperacao"), Ip(h), h.RequestAborted);
            return Results.NoContent();
        }).RequireRateLimiting(RateLimitPolicy);
        g.MapPost("/mfa/novos-codigos", async (HttpContext h, AccountService a, SessionCookies k) =>
        {
            var codes = await a.RegenerateRecoveryCodesAsync(await Require(h, a, k, realm), Str(await Body(h), "codigo"), Ip(h), h.RequestAborted);
            return Results.Ok(new JsonObject { ["codigos_recuperacao"] = new JsonArray([.. codes.Select(x => (JsonNode)x)]) });
        });
        g.MapPost("/senha/esqueci", async (HttpContext h, AccountService a) =>
        {
            await a.ForgotPasswordAsync(realm, Str(await Body(h), "email"), Ip(h), h.RequestAborted);
            return Results.Accepted();   // mesma resposta exista ou não a conta
        }).RequireRateLimiting(RateLimitPolicy);
        g.MapPost("/senha/redefinir", async (HttpContext h, AccountService a) =>
        {
            var body = await Body(h);
            await a.ResetPasswordAsync(Str(body, "token"), Str(body, "senha"), Ip(h), h.RequestAborted);
            return Results.NoContent();
        }).RequireRateLimiting(RateLimitPolicy);
        g.MapPost("/senha/alterar", async (HttpContext h, AccountService a, SessionCookies k) =>
        {
            var body = await Body(h);
            await a.ChangePasswordAsync(await Require(h, a, k, realm), Str(body, "atual"), Str(body, "nova"), Ip(h), h.RequestAborted);
            return Results.NoContent();
        });
    }

    // ---------------------------------------------------------------------------------------------------- handlers

    private static async Task<IResult> SignupAsync(HttpContext h, AccountService a)
    {
        var b = await Body(h);
        var empresa = b["empresa"] as JsonObject;
        await a.SignupAsync(Str(b, "nome"), Str(b, "email"), Str(b, "senha"), empresa is null ? "" : Str(empresa, "nome"),
            empresa is null ? "" : Str(empresa, "cnpj"), b["aceite_termos"]?.GetValue<bool>() == true, Ip(h), h.RequestAborted);
        // Mesma resposta para e-mail novo ou já cadastrado (sem enumerar contas).
        return Results.Accepted(value: new JsonObject { ["mensagem"] = "Se o e-mail puder ser usado, enviamos um link de confirmação." });
    }

    private static async Task<IResult> LoginAsync(HttpContext h, AccountService a, SessionCookies k, string realm)
    {
        var b = await Body(h);
        var (cookie, s) = await a.LoginAsync(realm, Str(b, "email"), Str(b, "senha"), Ip(h), h.Request.Headers.UserAgent.ToString(), h.RequestAborted);
        k.Write(h, realm, cookie);
        return Results.Ok(new JsonObject { ["mfa"] = s.MfaEnabled ? "verificar" : "configurar", ["nome"] = s.Name });
    }

    private static async Task<IResult> SessionAsync(HttpContext h, AccountService a, SessionCookies k, IParamSnapshotProvider p, string realm)
    {
        var s = await Current(h, a, k, realm, requireMfa: false)
            ?? throw new AccountException(401, "ERR_UNAUTHENTICATED", "Sessão expirada. Entre novamente.");
        var json = new JsonObject
        {
            ["usuario"] = new JsonObject { ["id"] = s.UserId, ["nome"] = s.Name, ["email"] = s.Email },
            ["mfa"] = s.MfaPassed ? "ok" : s.MfaEnabled ? "verificar" : "configurar",
            ["codigos_recuperacao_restantes"] = s.MfaEnabled ? await a.RemainingRecoveryCodesAsync(s.UserId, h.RequestAborted) : null,
        };
        if (realm == "operator")
        {
            json["escopos"] = new JsonArray([.. s.OperatorScopes.Select(x => (JsonNode)x)]);
            return Results.Ok(json);
        }

        var memberships = await a.MembershipsAsync(s.UserId, h.RequestAborted);
        json["empresas"] = new JsonArray([.. memberships.Select(m => (JsonNode)new JsonObject
        {
            ["tenant_id"] = m.Tenant,
            ["nome"] = m.TenantName,
            ["cnpj_raiz"] = m.RootCnpj,
            ["papel"] = m.Role,
            ["plano"] = m.PlanCode,
            ["situacao"] = m.TenantStatus,
        })]);
        if (s.MfaPassed && memberships.FirstOrDefault(m => m.Tenant == s.ActiveTenant) is { } active)
        {
            json["empresa_ativa"] = active.Tenant;
            json["papel"] = active.Role;
            json["escopos"] = new JsonArray([.. RoleScopes.For(active.Role).Select(x => (JsonNode)x)]);
            var plan = p.Current.Plans.GetValueOrDefault(active.PlanCode);
            json["plano"] = active.PlanCode;
            json["max_usuarios"] = plan?.MaxUsers;
        }

        return Results.Ok(json);
    }

    private static async Task<IResult> SetTenantAsync(HttpContext h, AccountService a, SessionCookies k)
    {
        var s = await Require(h, a, k, "customer");
        var id = Guid.TryParse(Str(await Body(h), "tenant_id"), out var g) ? g : throw new AccountException(400, "ERR_VALIDATION", "tenant_id inválido");
        var m = await a.SetActiveTenantAsync(s, id, h.RequestAborted);
        return Results.Ok(new JsonObject { ["empresa_ativa"] = m.Tenant, ["papel"] = m.Role });
    }

    private static async Task<IResult> AcceptInviteAsync(HttpContext h, AccountService a, SessionCookies k)
    {
        var b = await Body(h);
        var session = await Current(h, a, k, "customer", requireMfa: true);
        var tenant = await a.AcceptInviteAsync(Str(b, "token"), session, Opt(b, "nome"), Opt(b, "senha"), Ip(h), h.RequestAborted);
        if (session is not null)
        {
            await a.SetActiveTenantAsync(session, tenant, h.RequestAborted);
        }

        return Results.Ok(new JsonObject { ["tenant_id"] = tenant, ["proximo"] = session is null ? "entrar" : "portal" });
    }

    private static async Task<IResult> MembersAsync(HttpContext h, AccountService a, SessionCookies k)
    {
        var (_, tenant, _, _) = await RequireAdmin(h, a, k);
        var members = await a.ListMembersAsync(tenant, h.RequestAborted);
        var invites = await a.ListInvitesAsync(tenant, h.RequestAborted);
        return Results.Ok(new JsonObject
        {
            ["membros"] = new JsonArray([.. members.Select(m => (JsonNode)new JsonObject
            {
                ["user_id"] = m.UserId,
                ["nome"] = m.Name,
                ["email"] = m.Email,
                ["papel"] = m.Role,
                ["desde"] = m.Since,
                ["mfa"] = m.MfaEnabled,
                ["ultimo_acesso"] = m.LastLogin,
            })]),
            ["convites"] = new JsonArray([.. invites.Select(i => (JsonNode)new JsonObject
            {
                ["email"] = i.Email,
                ["papel"] = i.Role,
                ["expira_em"] = i.ExpiresAt,
                ["criado_em"] = i.CreatedAt,
            })]),
        });
    }

    private static async Task<IResult> InviteAsync(HttpContext h, AccountService a, SessionCookies k, IParamSnapshotProvider p)
    {
        var (s, tenant, _, m) = await RequireAdmin(h, a, k);
        var b = await Body(h);
        await a.InviteAsync(tenant, m.TenantName, s, Str(b, "email"), Str(b, "papel"), p.Current.Plans.GetValueOrDefault(m.PlanCode)?.MaxUsers, Ip(h), h.RequestAborted);
        return Results.Accepted();
    }

    private static async Task<IResult> RevokeInviteAsync(HttpContext h, string email, AccountService a, SessionCookies k)
    {
        var (s, tenant, _, _) = await RequireAdmin(h, a, k);
        await a.RevokeInviteAsync(tenant, s, email, Ip(h), h.RequestAborted);
        return Results.NoContent();
    }

    private static async Task<IResult> ChangeRoleAsync(HttpContext h, Guid userId, AccountService a, SessionCookies k)
    {
        var (s, tenant, role, _) = await RequireAdmin(h, a, k);
        await a.ChangeRoleAsync(tenant, s, role, userId, Str(await Body(h), "papel"), Ip(h), h.RequestAborted);
        return Results.NoContent();
    }

    private static async Task<IResult> RemoveMemberAsync(HttpContext h, Guid userId, AccountService a, SessionCookies k)
    {
        var (s, tenant, _, _) = await RequireAdmin(h, a, k);
        await a.RemoveMemberAsync(tenant, s, userId, Ip(h), h.RequestAborted);
        return Results.NoContent();
    }

    // ---------------------------------------------------------------------------------------------------- apoio

    private static async Task<SessionInfo?> Current(HttpContext h, AccountService a, SessionCookies k, string realm, bool requireMfa = true)
    {
        var s = await a.GetSessionAsync(k.Read(h, realm), realm, h.RequestAborted);
        return s is null || (requireMfa && !s.MfaPassed) ? null : s;
    }

    private static async Task<SessionInfo> Require(HttpContext h, AccountService a, SessionCookies k, string realm, bool requireMfa = true)
    {
        var s = await a.GetSessionAsync(k.Read(h, realm), realm, h.RequestAborted)
            ?? throw new AccountException(401, "ERR_UNAUTHENTICATED", "Sessão expirada. Entre novamente.");
        if (requireMfa && !s.MfaPassed)
        {
            throw new AccountException(403, "ERR_MFA_NECESSARIO", "Conclua a verificação em duas etapas (MFA) para continuar");
        }

        return s;
    }

    /// <summary>Gerenciar pessoas: sessão com MFA, empresa ativa e papel de dono ou admin.</summary>
    private static async Task<(SessionInfo Session, TenantId Tenant, string Role, Membership Membership)> RequireAdmin(HttpContext h, AccountService a, SessionCookies k)
    {
        var s = await Require(h, a, k, "customer");
        var m = (await a.MembershipsAsync(s.UserId, h.RequestAborted)).FirstOrDefault(x => x.Tenant == s.ActiveTenant)
            ?? throw new AccountException(403, "ERR_SEM_EMPRESA", "Escolha a empresa");
        if (m.Role is not ("owner" or "admin"))
        {
            throw new AccountException(403, "ERR_FORBIDDEN_SCOPE", "Só donos e administradores gerenciam acessos");
        }

        return (s, new TenantId(m.Tenant), m.Role, m);
    }

    private static async Task<JsonObject> Body(HttpContext h)
    {
        try
        {
            return await JsonNode.ParseAsync(h.Request.Body, cancellationToken: h.RequestAborted) as JsonObject
                ?? throw new AccountException(400, "ERR_JSON_INVALIDO", "Corpo JSON inválido");
        }
        catch (System.Text.Json.JsonException)
        {
            throw new AccountException(400, "ERR_JSON_INVALIDO", "Corpo JSON inválido");
        }
    }

    private static string Str(JsonObject b, string name) =>
        b[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : throw new AccountException(400, "ERR_VALIDATION", $"Campo obrigatório: {name}");

    private static string? Opt(JsonObject b, string name) => b[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static string? Ip(HttpContext h) => h.Connection.RemoteIpAddress?.ToString();
}

/// <summary>Anti-CSRF nas rotas de conta: todo POST/PATCH/DELETE precisa do header (inclusive login e cadastro).</summary>
public sealed class CsrfFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        return SessionCookies.CsrfOk(context.HttpContext)
            ? await next(context).ConfigureAwait(false)
            : ApiProblem.Result(403, "ERR_CSRF", "Requisição recusada", $"Envie o header {SessionCookies.CsrfHeader}: 1.");
    }
}
