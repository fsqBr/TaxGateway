using System.Security.Cryptography;
using System.Text;

using Microsoft.Extensions.Caching.Memory;

using TaxGateway.Core.Params;
using TaxGateway.Identity;
using TaxGateway.Identity.Accounts;
using TaxGateway.Infrastructure.Auth;
using TaxGateway.Infrastructure.Params;
using TaxGateway.Infrastructure.Persistence;
using TaxGateway.Infrastructure.RateLimiting;

namespace TaxGateway.Api.Http;

public static class Scopes
{
    public const string SimulacaoWrite = "simulacao:write";
    public const string AuditoriaWrite = "auditoria:write";

    /// <summary>Consultar documentos auditados, resumo e exportação (D-J01). auditoria:write também lê.</summary>
    public const string AuditoriaRead = "auditoria:read";
    public const string KeysManage = "keys:manage";
    public const string WebhooksManage = "webhooks:manage";
    public const string UsageRead = "usage:read";
    public const string CnpjsManage = "cnpjs:manage";
    public const string TitularesManage = "titulares:manage";
    public const string DfeManage = "dfe:manage";

    /// <summary>Gerenciar pessoas com acesso ao tenant (só sessão de dono/admin, D-U05).</summary>
    public const string MembrosManage = "membros:manage";

    /// <summary>Plano, cobrança e faturas (dono, admin, financeiro).</summary>
    public const string BillingManage = "billing:manage";
    public static readonly string[] TenantDefault = [SimulacaoWrite, AuditoriaWrite, AuditoriaRead, KeysManage, WebhooksManage, UsageRead, CnpjsManage, TitularesManage, DfeManage];
}

/// <summary>
/// Quem chama (API key ou token OAuth resolvido), os limites efetivos do plano e, se informado, o CNPJ do
/// cliente final em nome de quem o OEM chama (<c>X-On-Behalf-Of-CNPJ</c>, SDD §3.3; D-805).
/// </summary>
/// <summary>Teto de excedente definido pelo cliente e se a titularidade do CNPJ foi verificada (D-R08, D-R10).</summary>
public sealed record TenantBilling(long OverageCapCents, bool CnpjVerified);

public sealed record Caller(ApiKeyIdentity Identity, PlanLimits Limits, string? OnBehalfOf = null, TenantBilling? Billing = null)
{
    /// <summary>Com o header, o CNPJ sujeito do documento/simulação precisa ser exatamente o declarado.</summary>
    public void EnsureOnBehalfOf(string subjectCnpj)
    {
        if (OnBehalfOf is not null && !string.Equals(OnBehalfOf, subjectCnpj, StringComparison.Ordinal))
        {
            throw new ApiProblemException(403, "ERR_CNPJ_SCOPE", "CNPJ fora do escopo da requisição",
                "O CNPJ sujeito difere do informado em X-On-Behalf-Of-CNPJ.");
        }
    }
}

public static class CallerExtensions
{
    private const string Key = "tg.caller";

    public static Caller GetCaller(this HttpContext ctx) =>
        ctx.Items[Key] as Caller ?? throw new InvalidOperationException("Endpoint sem filtro de autenticação.");

    internal static void SetCaller(this HttpContext ctx, Caller caller) => ctx.Items[Key] = caller;
}

/// <summary>
/// Autenticação (API key ou <c>Authorization: Bearer</c>) + checagem de escopo + rate limit (SDD §3, §5) como filtros
/// do grupo /v1. Suspensão de tenant vale para os dois fluxos via <see cref="RevocationService"/> (meta &lt; 5 s, D-803).
/// O lease de concorrência é liberado em <c>finally</c>, inclusive quando o handler lança exceção.
/// </summary>
public sealed class ApiKeyFilter(ApiKeyAuthenticator auth, BearerAuthenticator bearer, SessionAuthenticator sessions, RevocationService revocations,
    IParamSnapshotProvider snapshots, TenantProfileCache profiles) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        var http = context.HttpContext;
        var authorization = http.Request.Headers.Authorization.ToString();
        var isBearer = authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase);
        var apiKey = http.Request.Headers["X-Api-Key"].ToString();
        ApiKeyIdentity? identity;
        if (isBearer)
        {
            identity = await bearer.AuthenticateAsync(http).ConfigureAwait(false);
        }
        else if (apiKey.Length > 0)
        {
            identity = await auth.AuthenticateAsync(apiKey, http.RequestAborted).ConfigureAwait(false);
        }
        else
        {
            // Pessoa no portal (D-U01): sessão em cookie, com MFA e empresa ativa.
            var (outcome, sessionIdentity) = await sessions.AuthenticateAsync(http).ConfigureAwait(false);
            switch (outcome)
            {
                case SessionAuthenticator.Outcome.MfaPending:
                    return ApiProblem.Result(403, "ERR_MFA_NECESSARIO", "Conclua a verificação em duas etapas (MFA) para continuar");
                case SessionAuthenticator.Outcome.Csrf:
                    return ApiProblem.Result(403, "ERR_CSRF", "Requisição recusada", $"Envie o header {SessionCookies.CsrfHeader}: 1.");
                case SessionAuthenticator.Outcome.NoTenant:
                    return ApiProblem.Result(403, "ERR_SEM_EMPRESA", "Escolha a empresa", "Sua conta não tem empresa ativa nesta sessão.");
            }

            identity = sessionIdentity;
        }

        if (identity is null)
        {
            http.Response.Headers.WWWAuthenticate = isBearer ? "Bearer error=\"invalid_token\"" : "ApiKey header=\"X-Api-Key\", Bearer";
            return ApiProblem.Result(401, "ERR_UNAUTHENTICATED", isBearer
                ? "Token ausente, inválido, expirado ou revogado"
                : "API key ausente, inválida, revogada ou expirada");
        }

        // Plano, situação e overrides sempre do perfil do tenant (cache de 30 s invalidado ao mudar, D-C08) — não do cache da credencial.
        var billing = new TenantBilling(0, false);
        if (await profiles.GetAsync(identity.Tenant, http.RequestAborted).ConfigureAwait(false) is { } profile)
        {
            identity = identity with { PlanCode = profile.PlanCode, TenantStatus = profile.Status, LimitsOverrideJson = profile.LimitsOverrideJson, AutoProvisionCnpj = profile.AutoProvisionCnpj };
            billing = new TenantBilling(profile.OverageCapCents, profile.CnpjVerified);
        }

        // D-R09: RESTRICTED (inadimplência a partir do 10º dia) autentica para consultar, exportar e pagar; o consumo é
        // barrado no QuotaFilter. Suspenso ou encerrado não entra.
        if (identity.TenantStatus is not ("ACTIVE" or "RESTRICTED") || revocations.IsTenantSuspended(identity.Tenant))
        {
            return ApiProblem.Result(403, "ERR_TENANT_SUSPENDED", "Tenant suspenso ou encerrado");
        }

        var required = http.GetEndpoint()?.Metadata.GetMetadata<RequiredScope>();
        if (required is not null && !required.SatisfiedBy(identity.Scopes))
        {
            return ApiProblem.Result(403, "ERR_FORBIDDEN_SCOPE", "Escopo insuficiente", $"Esta operação exige o escopo {required.Describe()}.");
        }

        string? onBehalfOf = null;
        var obo = http.Request.Headers["X-On-Behalf-Of-CNPJ"].ToString();
        if (obo.Length > 0)
        {
            if (!TaxGateway.Core.Fiscal.Cnpj.TryNormalize(obo, out var n))
            {
                return ApiProblem.Result(400, "ERR_VALIDATION", "X-On-Behalf-Of-CNPJ inválido");
            }

            onBehalfOf = n;
        }

        var limits = ParamSnapshotService.EffectiveLimits(snapshots.Current, identity.PlanCode, identity.LimitsOverrideJson);
        http.SetCaller(new Caller(identity, limits, onBehalfOf, billing));
        return await next(context).ConfigureAwait(false);
    }
}

/// <summary>Escopo exigido; com <paramref name="OrScope"/>, qualquer um dos dois basta (ex.: leitura OU escrita).</summary>
public sealed record RequiredScope(string Scope, string? OrScope = null)
{
    public bool SatisfiedBy(IEnumerable<string> scopes) =>
        scopes.Contains(Scope, StringComparer.Ordinal) || (OrScope is not null && scopes.Contains(OrScope, StringComparer.Ordinal));

    public string Describe() => OrScope is null ? $"'{Scope}'" : $"'{Scope}' ou '{OrScope}'";
}

public sealed class RateLimitFilter(TenantRateLimiter limiter) : IEndpointFilter
{
    /// <summary>TTL do lease = teto do endpoint + 5 s (SDD §5.2): uma requisição que morreu não prende o slot.</summary>
    public static readonly TimeSpan LeaseTtl = TimeSpan.FromSeconds(10);

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        var http = context.HttpContext;
        var caller = http.GetCaller();
        var lease = await limiter.AcquireAsync(caller.Identity.Tenant, caller.Limits, LeaseTtl).ConfigureAwait(false);
        await using (lease.ConfigureAwait(false))
        {
            RateLimitHeaderWriter.Write(http.Response.Headers, lease);
            if (!lease.Acquired)
            {
                var what = lease.Reason == RateLimitReason.Rate ? "requisições por minuto" : "requisições simultâneas";
                return ApiProblem.Result(429, "ERR_RATE_LIMITED", "Limite do plano excedido", $"Limite de {what} do plano {caller.Limits.PlanCode}.");
            }

            return await next(context).ConfigureAwait(false);
        }
    }
}

/// <summary>Operadores da Admin API (provisório até o OIDC da Fase 8; D-214). Chave em hash HMAC com o mesmo pepper.</summary>
public sealed class AdminOperatorOptions
{
    public const string Section = "Admin";
    public List<AdminOperator> Operators { get; set; } = [];

    /// <summary>
    /// Chaves estáticas X-Admin-Key (automação, dev, testes). Pessoas usam conta com MFA (D-U08). Em produção desligue
    /// (Admin:AllowStaticKeys=false) assim que houver operadores com conta.
    /// </summary>
    public bool AllowStaticKeys { get; set; } = true;
}

public sealed class AdminOperator
{
    public string Name { get; set; } = "";
    public string KeyHash { get; set; } = "";   // hex do HMAC-SHA256(pepper, chave)
    public List<string> Scopes { get; set; } = [];
}

public sealed record AdminCaller(string Name, IReadOnlyList<string> Scopes);

public sealed class AdminKeyFilter(ApiKeyHasher hasher, Microsoft.Extensions.Options.IOptionsMonitor<AdminOperatorOptions> operators,
    AccountService accounts, SessionCookies cookies, Microsoft.Extensions.Caching.Memory.IMemoryCache cache) : IEndpointFilter
{
    /// <summary>D-P31 (revisão de segurança B2): 10 chaves de operador erradas por IP em 15 min = 429 até a janela vencer.</summary>
    public const int MaxFailures = 10;

    private static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(15);

    private bool AllowStaticKeys => operators.CurrentValue.AllowStaticKeys;

    private sealed class Failures
    {
        public int Count;
    }

    private Failures FailuresOf(HttpContext http) =>
        cache.GetOrCreate($"adminkey-fail:{http.Connection.RemoteIpAddress}", e =>
        {
            e.AbsoluteExpirationRelativeToNow = FailureWindow;
            e.Size = 1;
            return new Failures();
        })!;

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        var http = context.HttpContext;
        var key = http.Request.Headers["X-Admin-Key"].ToString();
        if (key.Length == 0 && cookies.Read(http, "operator") is { } cookie)
        {
            // Operador com conta (D-U08): e-mail, senha e MFA; escopos da conta.
            var s = await accounts.GetSessionAsync(cookie, "operator", http.RequestAborted).ConfigureAwait(false);
            if (s is null)
            {
                return ApiProblem.Result(401, "ERR_UNAUTHENTICATED", "Sessão expirada. Entre novamente.");
            }

            if (!s.MfaPassed)
            {
                return ApiProblem.Result(403, "ERR_MFA_NECESSARIO", "Conclua a verificação em duas etapas (MFA) para continuar");
            }

            if (!SessionCookies.CsrfOk(http))
            {
                return ApiProblem.Result(403, "ERR_CSRF", "Requisição recusada", $"Envie o header {SessionCookies.CsrfHeader}: 1.");
            }

            var required = http.GetEndpoint()?.Metadata.GetMetadata<RequiredScope>()?.Scope;
            if (required is not null && !s.OperatorScopes.Contains(required, StringComparer.Ordinal))
            {
                return ApiProblem.Result(403, "ERR_FORBIDDEN_SCOPE", "Escopo insuficiente", $"Exige '{required}'.");
            }

            http.Items["tg.admin"] = new AdminCaller(s.Email, s.OperatorScopes);
            return await next(context).ConfigureAwait(false);
        }

        var failures = FailuresOf(http);
        if (Volatile.Read(ref failures.Count) >= MaxFailures)
        {
            http.Response.Headers.RetryAfter = ((int)FailureWindow.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
            return ApiProblem.Result(429, "ERR_RATE_LIMITED", "Muitas tentativas com chave de operador inválida", "Aguarde 15 minutos.");
        }

        if (key.Length is < 32 or > 256)
        {
            if (key.Length > 0)
            {
                Interlocked.Increment(ref failures.Count);   // requisição sem chave não conta (não bloqueia a automação legítima)
            }

            return ApiProblem.Result(401, "ERR_UNAUTHENTICATED", "Chave de operador ausente ou inválida");
        }

        var hash = hasher.Hash(key);
        AdminOperator? op = null;
        if (!AllowStaticKeys)
        {
            return ApiProblem.Result(401, "ERR_UNAUTHENTICATED", "Entre no backoffice com sua conta (e-mail, senha e MFA)");
        }

        foreach (var candidate in operators.CurrentValue.Operators)
        {
            if (candidate.KeyHash.Length == 64 && CryptographicOperations.FixedTimeEquals(Convert.FromHexString(candidate.KeyHash), hash))
            {
                op = candidate;
            }
        }

        if (op is null)
        {
            Interlocked.Increment(ref failures.Count);
            return ApiProblem.Result(401, "ERR_UNAUTHENTICATED", "Chave de operador ausente ou inválida");
        }

        var scope = http.GetEndpoint()?.Metadata.GetMetadata<RequiredScope>()?.Scope;
        if (scope is not null && !op.Scopes.Contains(scope, StringComparer.Ordinal))
        {
            return ApiProblem.Result(403, "ERR_FORBIDDEN_SCOPE", "Escopo insuficiente", $"Exige '{scope}'.");
        }

        http.Items["tg.admin"] = new AdminCaller(op.Name, op.Scopes);
        return await next(context).ConfigureAwait(false);
    }

    public static AdminCaller Get(HttpContext ctx) => (AdminCaller)ctx.Items["tg.admin"]!;
}

public static class RequestHashing
{
    /// <summary>Hash da requisição para Idempotency-Key: método + rota + corpo exato.</summary>
    public static byte[] Hash(HttpRequest request, byte[] body)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        h.AppendData(Encoding.UTF8.GetBytes($"{request.Method} {request.Path}\n"));
        h.AppendData(body);
        return h.GetHashAndReset();
    }
}
