using System.Security.Cryptography;
using System.Text;

using TaxGateway.Core.Params;
using TaxGateway.Identity;
using TaxGateway.Infrastructure.Auth;
using TaxGateway.Infrastructure.Params;
using TaxGateway.Infrastructure.Persistence;
using TaxGateway.Infrastructure.RateLimiting;

namespace TaxGateway.Api.Http;

public static class Scopes
{
    public const string SimulacaoWrite = "simulacao:write";
    public const string AuditoriaWrite = "auditoria:write";
    public const string KeysManage = "keys:manage";
    public const string WebhooksManage = "webhooks:manage";
    public const string UsageRead = "usage:read";
    public const string CnpjsManage = "cnpjs:manage";
    public const string TitularesManage = "titulares:manage";
    public const string DfeManage = "dfe:manage";
    public static readonly string[] TenantDefault = [SimulacaoWrite, AuditoriaWrite, KeysManage, WebhooksManage, UsageRead, CnpjsManage, TitularesManage, DfeManage];
}

/// <summary>
/// Quem chama (API key ou token OAuth resolvido), os limites efetivos do plano e, se informado, o CNPJ do
/// cliente final em nome de quem o OEM chama (<c>X-On-Behalf-Of-CNPJ</c>, SDD §3.3; D-805).
/// </summary>
public sealed record Caller(ApiKeyIdentity Identity, PlanLimits Limits, string? OnBehalfOf = null)
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
public sealed class ApiKeyFilter(ApiKeyAuthenticator auth, BearerAuthenticator bearer, RevocationService revocations,
    IParamSnapshotProvider snapshots) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        var http = context.HttpContext;
        var authorization = http.Request.Headers.Authorization.ToString();
        var isBearer = authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase);
        var identity = isBearer
            ? await bearer.AuthenticateAsync(http).ConfigureAwait(false)
            : await auth.AuthenticateAsync(http.Request.Headers["X-Api-Key"].ToString(), http.RequestAborted).ConfigureAwait(false);
        if (identity is null)
        {
            http.Response.Headers.WWWAuthenticate = isBearer ? "Bearer error=\"invalid_token\"" : "ApiKey header=\"X-Api-Key\", Bearer";
            return ApiProblem.Result(401, "ERR_UNAUTHENTICATED", isBearer
                ? "Token ausente, inválido, expirado ou revogado"
                : "API key ausente, inválida, revogada ou expirada");
        }

        if (identity.TenantStatus != "ACTIVE" || revocations.IsTenantSuspended(identity.Tenant))
        {
            return ApiProblem.Result(403, "ERR_TENANT_SUSPENDED", "Tenant suspenso ou encerrado");
        }

        var scope = http.GetEndpoint()?.Metadata.GetMetadata<RequiredScope>()?.Scope;
        if (scope is not null && !identity.Scopes.Contains(scope, StringComparer.Ordinal))
        {
            return ApiProblem.Result(403, "ERR_FORBIDDEN_SCOPE", "Escopo insuficiente", $"Esta operação exige o escopo '{scope}'.");
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
        http.SetCaller(new Caller(identity, limits, onBehalfOf));
        return await next(context).ConfigureAwait(false);
    }
}

public sealed record RequiredScope(string Scope);

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
}

public sealed class AdminOperator
{
    public string Name { get; set; } = "";
    public string KeyHash { get; set; } = "";   // hex do HMAC-SHA256(pepper, chave)
    public List<string> Scopes { get; set; } = [];
}

public sealed record AdminCaller(string Name, IReadOnlyList<string> Scopes);

public sealed class AdminKeyFilter(ApiKeyHasher hasher, Microsoft.Extensions.Options.IOptionsMonitor<AdminOperatorOptions> operators) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        var http = context.HttpContext;
        var key = http.Request.Headers["X-Admin-Key"].ToString();
        if (key.Length is < 32 or > 256)
        {
            return ApiProblem.Result(401, "ERR_UNAUTHENTICATED", "Chave de operador ausente ou inválida");
        }

        var hash = hasher.Hash(key);
        AdminOperator? op = null;
        foreach (var candidate in operators.CurrentValue.Operators)
        {
            if (candidate.KeyHash.Length == 64 && CryptographicOperations.FixedTimeEquals(Convert.FromHexString(candidate.KeyHash), hash))
            {
                op = candidate;
            }
        }

        if (op is null)
        {
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
