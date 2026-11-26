using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

using Npgsql;

using TaxGateway.Core.Tenancy;
using TaxGateway.Identity.Accounts;
using TaxGateway.Infrastructure.Auth;
using TaxGateway.Infrastructure.Persistence;

namespace TaxGateway.Api.Http;

/// <summary>
/// Sessão de pessoa (D-U01): cookie HttpOnly opaco, SameSite=Strict, com prefixo __Host- em produção. Pedidos que mudam
/// estado com sessão exigem o header <see cref="CsrfHeader"/> (um site de terceiros não consegue enviá-lo sem CORS, que a
/// API não habilita) — proteção contra CSRF em camada dupla com o SameSite.
/// </summary>
public sealed class SessionCookies(IOptions<AccountOptions> options)
{
    public const string CsrfHeader = "X-TaxGateway-Csrf";

    public string Name(string realm) => (options.Value.CookieSecure ? "__Host-" : "") + (realm == "operator" ? "tg_op" : "tg_sess");

    public string? Read(HttpContext http, string realm) => http.Request.Cookies[Name(realm)];

    public void Write(HttpContext http, string realm, string value) =>
        http.Response.Cookies.Append(Name(realm), value, new CookieOptions
        {
            HttpOnly = true,
            Secure = options.Value.CookieSecure,
            SameSite = SameSiteMode.Strict,
            Path = "/",
            MaxAge = TimeSpan.FromHours(options.Value.SessionHours),
            IsEssential = true,
        });

    public void Clear(HttpContext http, string realm) =>
        http.Response.Cookies.Delete(Name(realm), new CookieOptions { Path = "/", Secure = options.Value.CookieSecure, SameSite = SameSiteMode.Strict, HttpOnly = true });

    /// <summary>Métodos que mudam estado precisam do header anti-CSRF.</summary>
    public static bool CsrfOk(HttpContext http) =>
        HttpMethods.IsGet(http.Request.Method) || HttpMethods.IsHead(http.Request.Method) || HttpMethods.IsOptions(http.Request.Method)
        || http.Request.Headers[CsrfHeader].ToString() == "1";
}

/// <summary>Papéis de pessoa → escopos da API (D-U05). Dono e admin gerenciam acessos; financeiro vê consumo e cobrança.</summary>
public static class RoleScopes
{
    public static string[] For(string role) => role switch
    {
        "owner" or "admin" => [.. Scopes.TenantDefault, Scopes.MembrosManage, Scopes.BillingManage],
        "fiscal" => [Scopes.SimulacaoWrite, Scopes.AuditoriaWrite, Scopes.AuditoriaRead, Scopes.UsageRead, Scopes.DfeManage, Scopes.TitularesManage, Scopes.CnpjsManage],
        "developer" => [Scopes.SimulacaoWrite, Scopes.AuditoriaWrite, Scopes.AuditoriaRead, Scopes.KeysManage, Scopes.WebhooksManage, Scopes.UsageRead],
        "finance" => [Scopes.UsageRead, Scopes.BillingManage, Scopes.AuditoriaRead],
        _ => [Scopes.UsageRead, Scopes.AuditoriaRead],   // leitura: vê documentos e resultados, não envia nem altera
    };
}

/// <summary>Pessoa por trás da chamada (quando é sessão e não API key/OAuth): para trilhas e regras de papel.</summary>
public sealed record SessionUser(SessionInfo Session, string Role, string TenantName);

/// <summary>Resolve a sessão de cliente para a mesma identidade do fluxo de API key (o resto do pipeline não muda).</summary>
public sealed class SessionAuthenticator(AccountService accounts, SessionCookies cookies, NpgsqlDataSource appDb, IMemoryCache cache)
{
    public const string UserItem = "tg.session.user";
    private static readonly TimeSpan ProfileTtl = TimeSpan.FromSeconds(30);

    public enum Outcome { None, Ok, MfaPending, NoTenant, Csrf }

    public async Task<(Outcome Outcome, ApiKeyIdentity? Identity)> AuthenticateAsync(HttpContext http)
    {
        var cookie = cookies.Read(http, "customer");
        if (cookie is null)
        {
            return (Outcome.None, null);
        }

        var s = await accounts.GetSessionAsync(cookie, "customer", http.RequestAborted).ConfigureAwait(false);
        if (s is null)
        {
            return (Outcome.None, null);
        }

        if (!s.MfaPassed)
        {
            return (Outcome.MfaPending, null);
        }

        if (!SessionCookies.CsrfOk(http))
        {
            return (Outcome.Csrf, null);
        }

        if (s.ActiveTenant is not { } tid)
        {
            return (Outcome.NoTenant, null);
        }

        var membership = (await accounts.MembershipsAsync(s.UserId, http.RequestAborted).ConfigureAwait(false)).FirstOrDefault(m => m.Tenant == tid);
        if (membership is null)
        {
            return (Outcome.NoTenant, null);
        }

        var key = $"tprofile:{tid}";
        if (!cache.TryGetValue(key, out ApiKeyIdentity? profile))
        {
            await using var cmd = appDb.CreateCommand(
                "SELECT plan_code, tenant_status, limits_override::text, auto_provision_cnpj FROM app.tenant_profile($1)");
            cmd.Parameters.Add(new() { Value = tid });
            await using var r = await cmd.ExecuteReaderAsync(http.RequestAborted).ConfigureAwait(false);
            if (!await r.ReadAsync(http.RequestAborted).ConfigureAwait(false))
            {
                return (Outcome.NoTenant, null);
            }

            profile = new ApiKeyIdentity(new TenantId(tid), Guid.Empty, [], r.GetString(0), r.GetString(1), r.GetString(2), r.GetBoolean(3));
            cache.Set(key, profile, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = ProfileTtl, Size = 1 });
        }

        http.Items[UserItem] = new SessionUser(s, membership.Role, membership.TenantName);
        return (Outcome.Ok, profile! with { Scopes = RoleScopes.For(membership.Role) });
    }

    public static SessionUser? User(HttpContext http) => http.Items[UserItem] as SessionUser;

    /// <summary>Quem fez a ação, para trilhas: "user:{id}", "api-key:{id}" ou "oauth:{client}".</summary>
    public static string ActorOf(HttpContext http, ApiKeyIdentity identity) =>
        User(http) is { } u ? $"user:{u.Session.UserId}"
        : identity.KeyId != Guid.Empty ? $"api-key:{identity.KeyId}"
        : $"oauth:{http.Items[BearerAuthenticator.ClientItem]}";
}
