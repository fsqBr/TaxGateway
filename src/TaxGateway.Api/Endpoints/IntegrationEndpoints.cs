using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Threading.Channels;

using TaxGateway.Api.Http;
using TaxGateway.Core.Tenancy;
using TaxGateway.Identity;
using TaxGateway.Infrastructure.Persistence;

namespace TaxGateway.Api.Endpoints;

/// <summary>D-R27: clientes OAuth no portal (autoatendimento) e registro das chamadas da API para quem integra.</summary>
public static class IntegrationEndpoints
{
    public static RouteGroupBuilder MapIntegration(this RouteGroupBuilder v1)
    {
        var keys = new RequiredScope(Scopes.KeysManage);
        v1.MapGet("/oauth-clients", ListClientsAsync).WithMetadata(keys).WithTags("Integração");
        v1.MapPost("/oauth-clients", CreateClientAsync).WithMetadata(keys).WithTags("Integração");
        v1.MapDelete("/oauth-clients/{clientId}", RevokeClientAsync).WithMetadata(keys).WithTags("Integração");
        v1.MapGet("/logs", LogsAsync).WithMetadata(new RequiredScope(Scopes.KeysManage, Scopes.WebhooksManage)).WithTags("Integração");
        return v1;
    }

    private static async Task<IResult> ListClientsAsync(HttpContext http, ITenantDb db)
    {
        var json = await db.InTenantAsync(http.GetCaller().Identity.Tenant, async (s, ct) =>
        {
            await using var cmd = s.CreateCommand("""
                SELECT coalesce(json_agg(json_build_object('client_id', client_id, 'nome', name, 'scopes', scopes, 'criado_em', created_at,
                         'revogado_em', revoked_at) ORDER BY created_at DESC), '[]')::text FROM oauth_applications
                """);
            return (string)(await cmd.ExecuteScalarAsync(ct))!;
        }, http.RequestAborted);
        return Results.Text($$"""{"clientes":{{json}}}""", "application/json");
    }

    private static async Task<IResult> CreateClientAsync(HttpContext http, ITenantDb db)
    {
        var caller = http.GetCaller();
        var body = await JsonNode.ParseAsync(http.Request.Body, cancellationToken: http.RequestAborted) as JsonObject;
        var name = (body?["nome"]?.GetValue<string>() ?? "").Trim();
        var scopes = body?["scopes"] is JsonArray a ? a.Select(x => x!.GetValue<string>()).Distinct(StringComparer.Ordinal).ToArray() : [Scopes.SimulacaoWrite, Scopes.AuditoriaWrite];
        // Ninguém cria credencial com mais poder do que tem (mesma regra das API keys).
        var invalid = scopes.Where(x => !Scopes.TenantDefault.Contains(x) || !caller.Identity.Scopes.Contains(x, StringComparer.Ordinal)).ToList();
        if (name.Length is < 2 or > 80 || scopes.Length == 0 || invalid.Count > 0)
        {
            return ApiProblem.Result(400, "ERR_VALIDATION", "Dados inválidos",
                invalid.Count > 0 ? $"Escopos não permitidos para você: {string.Join(", ", invalid)}" : "Informe nome (2 a 80 caracteres) e ao menos um escopo.");
        }

        var clientId = "tgc_" + ClientSecretHasher.NewSecret()[..20];
        var secret = ClientSecretHasher.NewSecret();
        var (hash, salt) = ClientSecretHasher.Hash(secret);
        await db.InTenantAsync(caller.Identity.Tenant, async (s, ct) =>
        {
            await using var cmd = s.CreateCommand("INSERT INTO oauth_applications (client_id, tenant_id, name, secret_hash, secret_salt, scopes) VALUES ($1, $2, $3, $4, $5, $6)");
            cmd.Parameters.Add(new() { Value = clientId });
            cmd.Parameters.Add(new() { Value = caller.Identity.Tenant.Value });
            cmd.Parameters.Add(new() { Value = name });
            cmd.Parameters.Add(new() { Value = hash });
            cmd.Parameters.Add(new() { Value = salt });
            cmd.Parameters.Add(new() { Value = scopes });
            await cmd.ExecuteNonQueryAsync(ct);
            return await TenantNotices.SecurityAsync(s, caller.Identity.Tenant.Value, "Novo cliente OAuth criado",
                $"O cliente \"{name}\" ({clientId}) foi criado com os escopos {string.Join(", ", scopes)}. Se ninguém da empresa reconhece, revogue em Credenciais.",
                "/portal/credenciais", "oauth:" + clientId, ct);
        }, http.RequestAborted);
        return Results.Created($"/v1/oauth-clients/{clientId}", new JsonObject
        {
            ["client_id"] = clientId,
            ["client_secret"] = secret,
            ["scopes"] = new JsonArray([.. scopes.Select(x => (JsonNode)x)]),
            ["token_url"] = "/oauth/token",
            ["aviso"] = "Guarde o client_secret agora: ele não é mostrado de novo.",
        });
    }

    private static async Task<IResult> RevokeClientAsync(HttpContext http, string clientId, ITenantDb db, RevocationService revocations)
    {
        var n = await db.InTenantAsync(http.GetCaller().Identity.Tenant, async (s, ct) =>
        {
            await using var cmd = s.CreateCommand("SELECT app.revoke_own_oauth_client($1)");
            cmd.Parameters.Add(new() { Value = clientId });
            return (int)(await cmd.ExecuteScalarAsync(ct))!;
        }, http.RequestAborted);
        if (n == 0)
        {
            return ApiProblem.Result(404, "ERR_NOT_FOUND", "Cliente não encontrado ou já revogado");
        }

        await revocations.RevokeClientAsync(clientId);   // tokens já emitidos param na hora
        return Results.NoContent();
    }

    private static async Task<IResult> LogsAsync(HttpContext http, string? status, ITenantDb db)
    {
        var onlyErrors = status == "erros";
        var json = await db.InTenantAsync(http.GetCaller().Identity.Tenant, async (s, ct) =>
        {
            await using var cmd = s.CreateCommand("""
                SELECT coalesce(json_agg(json_build_object('em', at, 'metodo', method, 'rota', route, 'status', status, 'duracao_ms', duration_ms,
                         'credencial', credential, 'erro', error_code, 'trace_id', trace_id) ORDER BY at DESC), '[]')::text
                  FROM (SELECT * FROM api_call_logs WHERE (NOT $1 OR status >= 400) ORDER BY at DESC LIMIT 200) l
                """);
            cmd.Parameters.Add(new() { Value = onlyErrors });
            return (string)(await cmd.ExecuteScalarAsync(ct))!;
        }, http.RequestAborted);
        return Results.Text($$"""{"chamadas":{{json}},"retencao_dias":30,"observacao":"Erros e escritas; leituras com sucesso não são registradas."}""", "application/json");
    }
}

/// <summary>Uma chamada da API para o registro (D-R27).</summary>
public sealed record ApiCallLog(TenantId Tenant, DateTimeOffset At, string Method, string Route, int Status, int DurationMs, string Credential, string? ErrorCode, string? TraceId);

/// <summary>Último filtro do grupo /v1: mede e enfileira erros e escritas; a gravação é em lote, fora da requisição.</summary>
public sealed class CallLogFilter(ApiCallLogWriter writer) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        var started = Stopwatch.GetTimestamp();
        var result = await next(context).ConfigureAwait(false);
        var http = context.HttpContext;
        if (http.Items["tg.caller"] is not Caller caller)
        {
            return result;
        }

        var status = result is IStatusCodeHttpResult { StatusCode: { } sc } ? sc : http.Response.StatusCode;
        if (status < 400 && HttpMethods.IsGet(http.Request.Method))
        {
            return result;
        }

        var code = result is Microsoft.AspNetCore.Http.HttpResults.ProblemHttpResult pr && pr.ProblemDetails.Extensions.TryGetValue("code", out var c) ? c?.ToString() : null;
        var route = (http.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? http.Request.Path.Value ?? "";
        var credential = SessionAuthenticator.User(http) is { } u ? $"pessoa:{u.Session.Name}"
            : caller.Identity.KeyId != Guid.Empty ? $"api-key:{caller.Identity.KeyId.ToString()[..8]}"
            : $"oauth:{http.Items[BearerAuthenticator.ClientItem]}";
        writer.Enqueue(new ApiCallLog(caller.Identity.Tenant, DateTimeOffset.UtcNow, http.Request.Method, route, status,
            (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds, credential, code, Activity.Current?.TraceId.ToString()));
        return result;
    }
}

/// <summary>Grava o registro de chamadas em lote (canal limitado: sob pressão, descarta em vez de atrasar a API).</summary>
public sealed partial class ApiCallLogWriter(ITenantDb db, ILogger<ApiCallLogWriter> log) : BackgroundService
{
    private readonly Channel<ApiCallLog> _channel = Channel.CreateBounded<ApiCallLog>(new BoundedChannelOptions(10_000) { FullMode = BoundedChannelFullMode.DropWrite });

    public void Enqueue(ApiCallLog entry) => _channel.Writer.TryWrite(entry);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var batch = new List<ApiCallLog>(256);
        while (await _channel.Reader.WaitToReadAsync(stoppingToken).ConfigureAwait(false))
        {
            while (batch.Count < 256 && _channel.Reader.TryRead(out var e))
            {
                batch.Add(e);
            }

            foreach (var group in batch.GroupBy(b => b.Tenant))
            {
                try
                {
                    await db.InTenantAsync(group.Key, async (s, ct) =>
                    {
                        await using var cmd = s.CreateCommand("""
                            INSERT INTO api_call_logs (tenant_id, at, method, route, status, duration_ms, credential, error_code, trace_id)
                            SELECT $1, * FROM unnest($2::timestamptz[], $3::text[], $4::text[], $5::smallint[], $6::int[], $7::text[], $8::text[], $9::text[])
                            """);
                        var g = group.ToList();
                        cmd.Parameters.Add(new() { Value = group.Key.Value });
                        cmd.Parameters.Add(new() { Value = g.Select(x => x.At).ToArray() });
                        cmd.Parameters.Add(new() { Value = g.Select(x => x.Method).ToArray() });
                        cmd.Parameters.Add(new() { Value = g.Select(x => x.Route).ToArray() });
                        cmd.Parameters.Add(new() { Value = g.Select(x => (short)x.Status).ToArray() });
                        cmd.Parameters.Add(new() { Value = g.Select(x => x.DurationMs).ToArray() });
                        cmd.Parameters.Add(new() { Value = g.Select(x => x.Credential).ToArray() });
                        cmd.Parameters.Add(new() { Value = g.Select(x => x.ErrorCode).ToArray() });
                        cmd.Parameters.Add(new() { Value = g.Select(x => x.TraceId).ToArray() });
                        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                    }, stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogFailure(log, ex);
                }
            }

            batch.Clear();
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Falha ao gravar o registro de chamadas da API (descartado).")]
    private static partial void LogFailure(ILogger logger, Exception ex);
}
