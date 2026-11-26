using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using NpgsqlTypes;

using TaxGateway.Core.Tenancy;
using TaxGateway.Infrastructure.Persistence;
using TaxGateway.Infrastructure.Storage;

namespace TaxGateway.Infrastructure.Webhooks;

public sealed class WebhookOptions
{
    public const string Section = "Webhooks";

    /// <summary>Só testes: aceita http e loopback. Produção: false.</summary>
    public bool AllowInsecureForTests { get; set; }

    /// <summary>Só testes (com AllowInsecureForTests): thumbprint do certificado do receptor local aceito por pinning.</summary>
    public string? TestTrustedThumbprint { get; set; }

    /// <summary>Atrasos entre tentativas (s). Esgotou = DEAD (fila morta, retry manual pela API).</summary>
    public List<int> RetryDelaysSeconds { get; set; } = [60, 300, 1800, 7200, 21600, 43200, 86400];

    public int TimeoutSeconds { get; set; } = 10;

    /// <summary>Segredo antigo continua assinando por este período depois da rotação.</summary>
    public int RotationGraceHours { get; set; } = 24;
}

public sealed record WebhookInfo(Guid Id, string Url, IReadOnlyList<string> Events, bool Active, string? DisabledReason, DateTimeOffset CreatedAt);

public sealed record DeliveryInfo(Guid Id, Guid WebhookId, string Event, string Status, int Attempts, DateTimeOffset? NextAttemptAt, int? LastStatusCode, DateTimeOffset CreatedAt);

/// <summary>
/// Cadastro de webhooks e entregas (SDD §7; D-603/D-605). Segredos de assinatura cifrados por envelope com a KEK
/// do tenant (o banco só guarda texto cifrado); rotação mantém o segredo anterior válido por um período de graça.
/// </summary>
public sealed class WebhookRepository(ITenantDb db, IKeyVault vault, TenantSettingsService settings, IOptions<WebhookOptions> options,
    Microsoft.Extensions.Caching.Memory.IMemoryCache cache)
{
    private sealed record Secret(string S, DateTimeOffset? Exp);

    public async Task<(Guid Id, string Secret)> CreateAsync(TenantId tenant, Uri url, IReadOnlyList<string> events, CancellationToken ct)
    {
        SsrfGuard.ValidateUrl(url, options.Value.AllowInsecureForTests);
        if (!options.Value.AllowInsecureForTests)
        {
            await SsrfGuard.ResolveAllowedAsync(url.Host, ct).ConfigureAwait(false);
        }

        var id = Guid.CreateVersion7();
        var secret = "whsec_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var (cipher, dek) = await SealAsync(tenant, id, [new Secret(secret, null)], ct).ConfigureAwait(false);
        await db.InTenantAsync(tenant, async (s, c) =>
        {
            await using var cmd = s.CreateCommand("""
                INSERT INTO tenant_webhooks (id, tenant_id, target_url, secrets_ciphertext, dek_wrapped, subscribed_events)
                VALUES ($1, $2, $3, $4, $5, $6)
                """);
            cmd.Parameters.Add(new() { Value = id });
            cmd.Parameters.Add(new() { Value = tenant.Value });
            cmd.Parameters.Add(new() { Value = url.ToString() });
            cmd.Parameters.Add(new() { Value = cipher });
            cmd.Parameters.Add(new() { Value = dek });
            cmd.Parameters.Add(new() { Value = events.ToArray() });
            return await cmd.ExecuteNonQueryAsync(c).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
        return (id, secret);
    }

    public async Task<string?> RotateSecretAsync(TenantId tenant, Guid id, CancellationToken ct)
    {
        var current = await LoadSecretsAsync(tenant, id, ct).ConfigureAwait(false);
        if (current is null)
        {
            return null;
        }

        var secret = "whsec_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var graceUntil = DateTimeOffset.UtcNow.AddHours(options.Value.RotationGraceHours);
        var list = current.Where(x => x.Exp is null || x.Exp > DateTimeOffset.UtcNow).Select(x => x with { Exp = x.Exp ?? graceUntil }).Prepend(new Secret(secret, null)).ToList();
        var (cipher, dek) = await SealAsync(tenant, id, list, ct).ConfigureAwait(false);
        await db.InTenantAsync(tenant, async (s, c) =>
        {
            await using var cmd = s.CreateCommand("UPDATE tenant_webhooks SET secrets_ciphertext = $2, dek_wrapped = $3 WHERE id = $1");
            cmd.Parameters.Add(new() { Value = id });
            cmd.Parameters.Add(new() { Value = cipher });
            cmd.Parameters.Add(new() { Value = dek });
            return await cmd.ExecuteNonQueryAsync(c).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
        return secret;
    }

    public Task<List<WebhookInfo>> ListAsync(TenantId tenant, CancellationToken ct) =>
        db.InTenantAsync(tenant, async (s, c) =>
        {
            await using var cmd = s.CreateCommand("SELECT id, target_url, subscribed_events, is_active, disabled_reason, created_at FROM tenant_webhooks ORDER BY created_at");
            await using var r = await cmd.ExecuteReaderAsync(c).ConfigureAwait(false);
            var list = new List<WebhookInfo>();
            while (await r.ReadAsync(c).ConfigureAwait(false))
            {
                list.Add(new WebhookInfo(r.GetGuid(0), r.GetString(1), r.GetFieldValue<string[]>(2), r.GetBoolean(3),
                    r.IsDBNull(4) ? null : r.GetString(4), r.GetFieldValue<DateTimeOffset>(5)));
            }

            return list;
        }, ct);

    public Task<int> DeleteAsync(TenantId tenant, Guid id, CancellationToken ct) =>
        db.InTenantAsync(tenant, async (s, c) =>
        {
            await using var cmd = s.CreateCommand("DELETE FROM tenant_webhooks WHERE id = $1");
            cmd.Parameters.Add(new() { Value = id });
            return await cmd.ExecuteNonQueryAsync(c).ConfigureAwait(false);
        }, ct);

    public Task<List<DeliveryInfo>> ListDeliveriesAsync(TenantId tenant, string? status, CancellationToken ct) =>
        db.InTenantAsync(tenant, async (s, c) =>
        {
            await using var cmd = s.CreateCommand("""
                SELECT id, webhook_id, event, status, attempts, next_attempt_at, last_status_code, created_at
                  FROM webhook_deliveries WHERE ($1::text IS NULL OR status = $1) ORDER BY created_at DESC LIMIT 200
                """);
            cmd.Parameters.Add(new() { Value = (object?)status ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
            await using var r = await cmd.ExecuteReaderAsync(c).ConfigureAwait(false);
            var list = new List<DeliveryInfo>();
            while (await r.ReadAsync(c).ConfigureAwait(false))
            {
                list.Add(new DeliveryInfo(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetString(3), r.GetInt32(4),
                    r.IsDBNull(5) ? null : r.GetFieldValue<DateTimeOffset>(5), r.IsDBNull(6) ? null : r.GetInt32(6), r.GetFieldValue<DateTimeOffset>(7)));
            }

            return list;
        }, ct);

    /// <summary>Retry manual (fila morta incluída): volta para a fila com tentativa imediata.</summary>
    public Task<int> RetryAsync(TenantId tenant, Guid deliveryId, CancellationToken ct) =>
        db.InTenantAsync(tenant, async (s, c) =>
        {
            await using var cmd = s.CreateCommand("UPDATE webhook_deliveries SET status = 'RETRYING', next_attempt_at = now() WHERE id = $1 AND status <> 'DELIVERED'");
            cmd.Parameters.Add(new() { Value = deliveryId });
            return await cmd.ExecuteNonQueryAsync(c).ConfigureAwait(false);
        }, ct);

    /// <summary>Fan-out de um evento do outbox: uma entrega por webhook ativo inscrito (id determinístico = sem duplicar em redelivery).</summary>
    public Task<int> FanOutAsync(TenantId tenant, long eventId, string topic, string payloadJson, CancellationToken ct) =>
        db.InTenantAsync(tenant, async (s, c) =>
        {
            await using var cmd = s.CreateCommand("""
                INSERT INTO webhook_deliveries (id, tenant_id, webhook_id, event, payload, status, next_attempt_at)
                SELECT md5($2::text || ':' || w.id::text)::uuid, $1, w.id, $3, $4, 'PENDING', now()
                  FROM tenant_webhooks w
                 WHERE w.is_active AND ($3 = ANY(w.subscribed_events) OR '*' = ANY(w.subscribed_events))
                ON CONFLICT (id) DO NOTHING
                """);
            cmd.Parameters.Add(new() { Value = tenant.Value });
            cmd.Parameters.Add(new() { Value = eventId });
            cmd.Parameters.Add(new() { Value = topic });
            cmd.Parameters.Add(new() { Value = payloadJson, NpgsqlDbType = NpgsqlDbType.Jsonb });
            return await cmd.ExecuteNonQueryAsync(c).ConfigureAwait(false);
        }, ct);

    /// <summary>
    /// Segredos ativos para assinar (D-P21): cache de 5 min por webhook — antes cada entrega fazia uma chamada ao KMS. Depois
    /// de uma rotação, o segredo anterior segue válido por 24 h, então assinar com ele por até 5 min não quebra o cliente.
    /// </summary>
    internal async Task<IReadOnlyList<string>?> ActiveSecretsAsync(TenantId tenant, Guid webhookId, CancellationToken ct)
    {
        var key = $"whsecrets:{webhookId}";
        if (!cache.TryGetValue(key, out List<Secret>? list))
        {
            list = await LoadSecretsAsync(tenant, webhookId, ct).ConfigureAwait(false);
            if (list is not null)
            {
                cache.Set(key, list, new Microsoft.Extensions.Caching.Memory.MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5), Size = 1 });
            }
        }

        return list?.Where(x => x.Exp is null || x.Exp > DateTimeOffset.UtcNow).Select(x => x.S).ToList();
    }

    private async Task<List<Secret>?> LoadSecretsAsync(TenantId tenant, Guid id, CancellationToken ct)
    {
        var row = await db.InTenantAsync(tenant, async (s, c) =>
        {
            await using var cmd = s.CreateCommand("SELECT secrets_ciphertext, dek_wrapped FROM tenant_webhooks WHERE id = $1");
            cmd.Parameters.Add(new() { Value = id });
            await using var r = await cmd.ExecuteReaderAsync(c).ConfigureAwait(false);
            return await r.ReadAsync(c).ConfigureAwait(false) ? (r.GetFieldValue<byte[]>(0), r.GetFieldValue<byte[]>(1)) : ((byte[], byte[])?)null;
        }, ct).ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        var kek = (await settings.GetAsync(tenant, ct).ConfigureAwait(false)).KmsKeyRef;
        var aad = Encoding.UTF8.GetBytes($"webhook:{id}");
        var dek = await vault.UnwrapAsync(kek, row.Value.Item2, aad, ct).ConfigureAwait(false);
        try
        {
            var plain = LocalKeyVault.Open(dek, row.Value.Item1, aad);
            return JsonSerializer.Deserialize<List<Secret>>(plain)!;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
        }
    }

    private async Task<(byte[] Cipher, byte[] WrappedDek)> SealAsync(TenantId tenant, Guid id, List<Secret> secrets, CancellationToken ct)
    {
        var kek = (await settings.GetAsync(tenant, ct).ConfigureAwait(false)).KmsKeyRef;
        var aad = Encoding.UTF8.GetBytes($"webhook:{id}");
        var dek = RandomNumberGenerator.GetBytes(32);
        try
        {
            var cipher = LocalKeyVault.Seal(dek, JsonSerializer.SerializeToUtf8Bytes(secrets), aad);
            return (cipher, await vault.WrapAsync(kek, dek, aad, ct).ConfigureAwait(false));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
        }
    }
}

/// <summary>
/// Entrega de webhooks (SDD §7; D-605): POST com assinatura HMAC-SHA256 (`TaxGateway-Signature: t=..,v1=..[,v1=..]`,
/// um v1 por segredo ativo durante a rotação), sem redirect, IP validado na conexão, timeout curto. 2xx = entregue;
/// 410 = endpoint desativado; outros = nova tentativa pela agenda; esgotou = DEAD.
/// </summary>
public sealed partial class WebhookSender(ITenantDb db, WebhookRepository repo, IHttpClientFactory http, IOptions<WebhookOptions> options, ILogger<WebhookSender> log)
{
    public const string Client = "webhooks";

    public async Task<string> DeliverAsync(TenantId tenant, Guid deliveryId, CancellationToken ct)
    {
        var row = await db.InTenantAsync(tenant, async (s, c) =>
        {
            await using var cmd = s.CreateCommand("""
                SELECT d.webhook_id, d.event, d.payload::text, d.attempts, d.status, w.target_url, w.is_active, d.created_at
                  FROM webhook_deliveries d JOIN tenant_webhooks w ON w.id = d.webhook_id WHERE d.id = $1
                """);
            cmd.Parameters.Add(new() { Value = deliveryId });
            await using var r = await cmd.ExecuteReaderAsync(c).ConfigureAwait(false);
            return await r.ReadAsync(c).ConfigureAwait(false)
                ? new DeliveryRow(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetInt32(3), r.GetString(4), r.GetString(5), r.GetBoolean(6),
                    r.GetFieldValue<DateTimeOffset>(7))
                : null;
        }, ct).ConfigureAwait(false);
        if (row is not { } d || d.Status == "DELIVERED")
        {
            return "SKIPPED";
        }

        int? code = null;
        string? error = null;
        if (!d.Active)
        {
            error = "webhook inativo";
        }
        else
        {
            try
            {
                var url = new Uri(d.Url);
                SsrfGuard.ValidateUrl(url, options.Value.AllowInsecureForTests);
                var body = Encoding.UTF8.GetBytes(new JsonObject
                {
                    ["id"] = deliveryId.ToString(),
                    ["event"] = d.Event,
                    ["created_at"] = d.Created.ToString("O", CultureInfo.InvariantCulture),
                    ["data"] = JsonNode.Parse(d.Payload),
                }.ToJsonString());
                var secrets = await repo.ActiveSecretsAsync(tenant, d.Webhook, ct).ConfigureAwait(false) ?? [];
                var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
                var signed = Encoding.UTF8.GetBytes(ts + ".").Concat(body).ToArray();
                var sig = $"t={ts}," + string.Join(',', secrets.Select(s => "v1=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(s), signed))));

                using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new ByteArrayContent(body) };
                req.Content.Headers.ContentType = new("application/json");
                req.Headers.Add("TaxGateway-Signature", sig);
                req.Headers.Add("TaxGateway-Event", d.Event);
                req.Headers.Add("TaxGateway-Delivery", deliveryId.ToString());
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(options.Value.TimeoutSeconds));
                using var resp = await http.CreateClient(Client).SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
                code = (int)resp.StatusCode;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or WebhookUrlRejectedException or System.Net.Sockets.SocketException)
            {
                error = ex is WebhookUrlRejectedException ? $"bloqueado: {ex.Message}" : ex.GetType().Name;
                LogFailed(log, deliveryId, error);
            }
        }

        var attempts = d.Attempts + 1;
        var delays = options.Value.RetryDelaysSeconds;
        var (status, next, disable) = code switch
        {
            >= 200 and < 300 => ("DELIVERED", (DateTimeOffset?)null, (string?)null),
            410 => ("DEAD", null, "410 Gone do destino"),
            _ when error?.StartsWith("bloqueado", StringComparison.Ordinal) == true => ("DEAD", null, error),
            _ when attempts > delays.Count => ("DEAD", null, null),
            _ => ("RETRYING", DateTimeOffset.UtcNow.AddSeconds(delays[attempts - 1]), null),
        };

        // Disjuntor por endpoint (D-P21): 5 falhas seguidas pausam o endpoint (5 min, dobrando até 1 h); um sucesso zera.
        var delivered = status == "DELIVERED";
        await db.InTenantAsync(tenant, async (s, c) =>
        {
            await using var batch = s.CreateBatch();
            var breaker = new Npgsql.NpgsqlBatchCommand("""
                UPDATE tenant_webhooks SET
                       consecutive_failures = CASE WHEN $2 THEN 0 ELSE consecutive_failures + 1 END,
                       paused_until = CASE WHEN $2 THEN NULL
                                           WHEN consecutive_failures + 1 >= 5
                                           THEN now() + least(interval '1 hour', interval '5 minutes' * power(2, least(consecutive_failures + 1 - 5, 4)))
                                           ELSE paused_until END
                 WHERE id = $1
                """);
            breaker.Parameters.Add(new() { Value = d.Webhook });
            breaker.Parameters.Add(new() { Value = delivered });
            batch.BatchCommands.Add(breaker);
            // Ao abrir o disjuntor (5ª falha seguida), avisa no portal e por e-mail (D-J03) — um aviso por endpoint por dia.
            var paused = new Npgsql.NpgsqlBatchCommand("""
                INSERT INTO tenant_notifications (tenant_id, kind, severity, title, body, link, audience, dedupe_key)
                SELECT w.tenant_id, 'WEBHOOK_PAUSADO', 'critico', 'Webhook pausado por falhas seguidas',
                       'O endpoint ' || left(w.target_url, 200) || ' falhou 5 vezes seguidas e as entregas estão pausadas; elas retomam '
                       || 'sozinhas (de 5 min até 1 h) e nada se perde. Confira o endpoint e as entregas em Webhooks.',
                       '/portal/webhooks', '{owner,admin,developer}', 'webhook:' || w.id::text || ':' || to_char(now(), 'YYYYMMDD')
                  FROM tenant_webhooks w WHERE w.id = $1 AND w.consecutive_failures = 5
                ON CONFLICT (tenant_id, dedupe_key) DO NOTHING
                """);
            paused.Parameters.Add(new() { Value = d.Webhook });
            batch.BatchCommands.Add(paused);
            var upd = new Npgsql.NpgsqlBatchCommand("UPDATE webhook_deliveries SET status = $2, attempts = $3, next_attempt_at = $4, last_status_code = $5 WHERE id = $1");
            upd.Parameters.Add(new() { Value = deliveryId });
            upd.Parameters.Add(new() { Value = status });
            upd.Parameters.Add(new() { Value = attempts });
            upd.Parameters.Add(new() { Value = (object?)next?.UtcDateTime ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.TimestampTz });
            upd.Parameters.Add(new() { Value = (object?)code ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Integer });
            batch.BatchCommands.Add(upd);
            if (disable is not null)
            {
                var off = new Npgsql.NpgsqlBatchCommand("UPDATE tenant_webhooks SET is_active = false, disabled_reason = $2 WHERE id = $1");
                off.Parameters.Add(new() { Value = d.Webhook });
                off.Parameters.Add(new() { Value = disable });
                batch.BatchCommands.Add(off);
            }

            return await batch.ExecuteNonQueryAsync(c).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
        return status;
    }

    private sealed record DeliveryRow(Guid Webhook, string Event, string Payload, int Attempts, string Status, string Url, bool Active, DateTimeOffset Created);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Falha na entrega do webhook {DeliveryId}: {Error}.")]
    private static partial void LogFailed(ILogger logger, Guid deliveryId, string error);
}

/// <summary>Verificação da assinatura (para documentação/SDK do cliente e testes).</summary>
public static class WebhookSignature
{
    public static bool Verify(string header, byte[] body, string secret, TimeSpan tolerance, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(header);
        var parts = header.Split(',').Select(p => p.Split('=', 2)).Where(p => p.Length == 2).ToList();
        var t = parts.FirstOrDefault(p => p[0] == "t")?[1];
        if (t is null || !long.TryParse(t, out var unix) || (now - DateTimeOffset.FromUnixTimeSeconds(unix)).Duration() > tolerance)
        {
            return false;
        }

        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(t + ".").Concat(body).ToArray());
        return parts.Where(p => p[0] == "v1").Any(p => CryptographicOperations.FixedTimeEquals(Convert.FromHexString(p[1]), expected));
    }
}
