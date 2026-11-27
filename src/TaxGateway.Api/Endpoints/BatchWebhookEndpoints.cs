using System.Text;
using System.Text.Json.Nodes;

using Microsoft.Extensions.Options;

using TaxGateway.Api.Http;
using TaxGateway.Infrastructure.Batches;
using TaxGateway.Infrastructure.Persistence;
using TaxGateway.Infrastructure.Storage;
using TaxGateway.Infrastructure.Webhooks;

namespace TaxGateway.Api.Endpoints;

/// <summary>Lotes (SDD §6.3; D-606) e webhooks (§7; D-603 a D-605).</summary>
public static class BatchWebhookEndpoints
{
    public const string WebhooksManage = "webhooks:manage";

    public static RouteGroupBuilder MapBatchesAndWebhooks(this RouteGroupBuilder v1)
    {
        v1.MapPost("/auditoria/lotes", CreateBatchAsync).WithMetadata(new RequiredScope(Scopes.AuditoriaWrite), new QuotaMetric("DOC_AUDIT"), new RateCost(10, 900),
            new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(2L * 1024 * 1024 * 1024));
        v1.MapPost("/auditoria/lotes/upload-url", UploadUrl).WithMetadata(new RequiredScope(Scopes.AuditoriaWrite));
        v1.MapGet("/auditoria/lotes/{id:guid}", GetBatchAsync).WithMetadata(new RequiredScope(Scopes.AuditoriaWrite));
        v1.MapGet("/auditoria/lotes/{id:guid}/relatorio", ReportAsync).WithMetadata(new RequiredScope(Scopes.AuditoriaWrite));

        v1.MapPost("/webhooks", CreateWebhookAsync).WithMetadata(new RequiredScope(WebhooksManage));
        v1.MapGet("/webhooks", async (HttpContext h, WebhookRepository r) => Results.Ok(await r.ListAsync(h.GetCaller().Identity.Tenant, h.RequestAborted)))
            .WithMetadata(new RequiredScope(WebhooksManage));
        v1.MapDelete("/webhooks/{id:guid}", async (HttpContext h, Guid id, WebhookRepository r) =>
                await r.DeleteAsync(h.GetCaller().Identity.Tenant, id, h.RequestAborted) == 1 ? Results.NoContent() : ApiProblem.Result(404, "ERR_NOT_FOUND", "Webhook não encontrado"))
            .WithMetadata(new RequiredScope(WebhooksManage));
        v1.MapPost("/webhooks/{id:guid}/rotate-secret", async (HttpContext h, Guid id, WebhookRepository r) =>
                await r.RotateSecretAsync(h.GetCaller().Identity.Tenant, id, h.RequestAborted) is { } s
                    ? Results.Ok(new JsonObject { ["secret"] = s, ["aviso"] = "O segredo anterior continua assinando por 24 h." })
                    : ApiProblem.Result(404, "ERR_NOT_FOUND", "Webhook não encontrado"))
            .WithMetadata(new RequiredScope(WebhooksManage));
        v1.MapGet("/webhooks/deliveries", async (HttpContext h, string? status, WebhookRepository r) =>
                Results.Ok(await r.ListDeliveriesAsync(h.GetCaller().Identity.Tenant, status, h.RequestAborted)))
            .WithMetadata(new RequiredScope(WebhooksManage));
        v1.MapPost("/webhooks/deliveries/{id:guid}/retry", async (HttpContext h, Guid id, WebhookRepository r) =>
                await r.RetryAsync(h.GetCaller().Identity.Tenant, id, h.RequestAborted) == 1 ? Results.Accepted() : ApiProblem.Result(404, "ERR_NOT_FOUND", "Entrega não encontrada ou já entregue"))
            .WithMetadata(new RequiredScope(WebhooksManage));
        return v1;
    }

    /// <summary>
    /// Modo 1: corpo application/zip em stream (até 100 MB). Modo 2: JSON {"storage_object_key"} de um upload feito
    /// pela URL pré-assinada. Responde 202 com batch_id, status_url e estimativa.
    /// Idempotência (D-P03): o corpo vai para um arquivo temporário com SHA-256 calculado no caminho; a mesma
    /// Idempotency-Key com o mesmo conteúdo devolve o mesmo lote (Idempotent-Replayed), com outro conteúdo é 422.
    /// </summary>
    private static async Task<IResult> CreateBatchAsync(HttpContext http, string? direction, IOptions<BatchOptions> opts, IServiceProvider sp, IdempotencyStore idempotency)
    {
        var caller = http.GetCaller();
        var tenant = caller.Identity.Tenant;
        var limits = caller.Limits;
        var dir = (direction ?? "OUTBOUND").ToUpperInvariant();
        if (dir is not ("OUTBOUND" or "INBOUND"))
        {
            return ApiProblem.Result(400, "ERR_DIRECTION_INVALIDA", "direction deve ser OUTBOUND ou INBOUND");
        }

        var idemKey = http.Request.Headers["Idempotency-Key"].ToString();
        if (idemKey.Length is 0 or > 255)
        {
            return ApiProblem.Result(400, "ERR_IDEMPOTENCY_KEY_OBRIGATORIA", "Idempotency-Key obrigatória (até 255 caracteres) em /auditoria/lotes");
        }

        var vault = sp.GetService<XmlVault>();
        if (vault is null)
        {
            return ApiProblem.Result(503, "ERR_STORAGE_INDISPONIVEL", "Armazenamento de lotes não configurado");
        }

        var ingest = sp.GetRequiredService<BatchIngestService>();
        var isJson = http.Request.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) == true;
        string? objectKey = null;
        var ownsObject = false;   // só apaga o upload do modo 2 quando esta requisição o processou (ou recusou)
        StagedUpload? staged = null;
        try
        {
            byte[] contentHash;
            if (isJson)
            {
                JsonNode? body;
                try
                {
                    body = await JsonNode.ParseAsync(http.Request.Body, cancellationToken: http.RequestAborted);
                }
                catch (System.Text.Json.JsonException)
                {
                    return ApiProblem.Result(400, "ERR_JSON_INVALIDO", "Corpo JSON inválido", "Envie {\"storage_object_key\": \"...\"} devolvido por /v1/auditoria/lotes/upload-url.");
                }

                objectKey = (body as JsonObject)?["storage_object_key"] is JsonValue v && v.TryGetValue<string>(out var k) ? k : null;
                if (objectKey is null || !UploadKeyPattern(tenant).IsMatch(objectKey))
                {
                    return ApiProblem.Result(400, "ERR_VALIDATION", "storage_object_key inválida para este tenant",
                        "Use exatamente a chave devolvida por /v1/auditoria/lotes/upload-url.");
                }

                contentHash = Encoding.UTF8.GetBytes(objectKey);
            }
            else
            {
                var max = Math.Min(limits.BatchMaxBytes, opts.Value.ModeOneMaxBytes);
                staged = await BatchIngestService.StageAsync(http.Request.Body, max, http.RequestAborted);
                contentHash = staged.Sha256;
            }

            var hash = RequestHashing.Hash(http.Request, contentHash);
            var begin = await idempotency.BeginAsync(tenant, idemKey, hash, http.RequestAborted);
            switch (begin.State)
            {
                case IdempotencyState.Replay:
                    http.Response.Headers["Idempotent-Replayed"] = "true";
                    http.Response.StatusCode = begin.StatusCode;
                    return Results.Bytes(begin.Body!, begin.StatusCode >= 400 ? "application/problem+json" : "application/json");
                case IdempotencyState.Mismatch:
                    return ApiProblem.Result(422, "ERR_IDEMPOTENCY_KEY_REUSED", "Idempotency-Key já usada com outro conteúdo");
                case IdempotencyState.InProgress:
                    return ApiProblem.Result(409, "ERR_IDEMPOTENCY_IN_PROGRESS", "Lote com esta Idempotency-Key ainda em processamento");
            }

            ownsObject = true;
            try
            {
                BatchCreated created;
                if (objectKey is not null)
                {
                    Stream raw;
                    try
                    {
                        raw = await vault.OpenRawAsync(objectKey, http.RequestAborted);
                    }
                    catch (Exception ex) when (XmlVault.IsNotFound(ex))
                    {
                        throw new ApiProblemException(404, "ERR_NOT_FOUND", "Upload não encontrado",
                            "Nada foi enviado para esta storage_object_key, ou a URL de upload expirou (15 min).");
                    }

                    await using (raw)
                    {
                        staged = await BatchIngestService.StageAsync(raw, limits.BatchMaxBytes, http.RequestAborted);
                    }
                }

                var quotaDb = sp.GetRequiredService<ITenantDb>();
                var clock = sp.GetRequiredService<TimeProvider>();
                created = await ingest.IngestAsync(tenant, staged!, limits.BatchMaxBytes, limits.BatchMaxDocs, limits.PriorityRank, dir, http.RequestAborted,
                    async (docs, c) =>
                    {
                        var usage = await TaxGateway.Infrastructure.Billing.PlanQuota.LoadAsync(quotaDb, tenant, TaxGateway.Api.Simulation.SimulationService.BillingMonth(clock.GetUtcNow()), includeReserved: true, c);
                        if (TaxGateway.Infrastructure.Billing.PlanQuota.Check(limits, caller.Identity.TenantStatus, caller.Billing?.OverageCapCents ?? 0, usage,
                                TaxGateway.Infrastructure.Billing.PlanQuota.DocMetric, docs) is { } denial)
                        {
                            throw new BatchRejectedException(denial.Code, denial.Detail, denial.Status);
                        }
                    });
                var seconds = (int)Math.Ceiling(created.Documents / (opts.Value.DocsPerSecondPerSlot * opts.Value.PerTenantInFlight));
                var json = new JsonObject
                {
                    ["batch_id"] = created.Id,
                    ["status_url"] = $"/v1/auditoria/lotes/{created.Id}",
                    ["documentos"] = created.Documents,
                    ["chunks"] = created.Chunks,
                    ["estimated_completion_seconds"] = seconds,
                };
                var bytes = Encoding.UTF8.GetBytes(json.ToJsonString());
                await idempotency.CompleteAsync(tenant, idemKey, 202, bytes, CancellationToken.None);
                http.Response.StatusCode = 202;
                return Results.Bytes(bytes, "application/json");
            }
            catch (BatchRejectedException ex)
            {
                var problem = new ApiProblemException(ex.Status, ex.Code, "Lote recusado", ex.Message);
                await idempotency.CompleteAsync(tenant, idemKey, problem.Status, ApiProblem.ToJson(problem), CancellationToken.None);
                throw problem;
            }
            catch (ApiProblemException ex) when (ex.Status is >= 400 and < 500)
            {
                await idempotency.CompleteAsync(tenant, idemKey, ex.Status, ApiProblem.ToJson(ex), CancellationToken.None);
                throw;
            }
            catch
            {
                await idempotency.ReleaseAsync(tenant, idemKey, CancellationToken.None);
                throw;
            }
        }
        catch (BatchRejectedException ex)
        {
            // Recusa antes da reserva da chave (ex.: upload acima do limite durante a leitura).
            return ApiProblem.Result(ex.Status, ex.Code, "Lote recusado", ex.Message);
        }
        finally
        {
            if (staged is not null)
            {
                await staged.DisposeAsync();
            }

            if (objectKey is not null && ownsObject)
            {
                // O upload do modo 2 nunca fica para trás — nem quando o lote é recusado (LGPD: o ZIP traz dados pessoais).
                try
                {
                    await vault.DeleteAsync(objectKey, CancellationToken.None);
                }
                catch (Exception ex) when (XmlVault.IsNotFound(ex))
                {
                    // já não existia
                }
            }
        }
    }

    /// <summary>Eventos que o TaxGateway publica (e "*" = todos).</summary>
    public static readonly string[] WebhookEvents = ["*", "document.audited", "batch.audit.completed", "dfe.document.audited", "certificate.expiring"];

    private static System.Text.RegularExpressions.Regex UploadKeyPattern(Core.Tenancy.TenantId tenant) =>
        new($"^uploads/{tenant}/[0-9a-f]{{32}}\\.zip$", System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    private static IResult UploadUrl(HttpContext http, IServiceProvider sp)
    {
        var vault = sp.GetService<XmlVault>();
        if (vault is null)
        {
            return ApiProblem.Result(503, "ERR_STORAGE_INDISPONIVEL", "Armazenamento de lotes não configurado");
        }

        var key = $"{XmlVault.UploadPrefix}{http.GetCaller().Identity.Tenant}/{Guid.CreateVersion7():N}.zip";   // D-P24
        return Results.Ok(new JsonObject
        {
            ["upload_url"] = vault.PresignUpload(key, TimeSpan.FromMinutes(15)),
            ["storage_object_key"] = key,
            ["expira_em_segundos"] = 900,
            ["metodo"] = "PUT",
            ["content_type"] = "application/zip",
        });
    }

    private static async Task<IResult> GetBatchAsync(HttpContext http, Guid id, ITenantDb db)
    {
        var row = await db.InTenantAsync(http.GetCaller().Identity.Tenant, async (s, ct) =>
        {
            await using var cmd = s.CreateCommand("""
                SELECT status, direction, total_docs, processed_docs, failed_docs, conforme_docs, total_chunks, done_chunks, report_key IS NOT NULL, created_at, completed_at
                  FROM batches WHERE id = $1
                """);
            cmd.Parameters.Add(new() { Value = id });
            await using var r = await cmd.ExecuteReaderAsync(ct);
            return await r.ReadAsync(ct)
                ? new JsonObject
                {
                    ["batch_id"] = id,
                    ["status"] = r.GetString(0),
                    ["direction"] = r.GetString(1),
                    ["total_documentos"] = r.GetInt32(2),
                    ["processados"] = r.GetInt32(3),
                    ["falhas"] = r.GetInt32(4),
                    ["conformes"] = r.GetInt32(5),
                    ["chunks"] = r.GetInt32(6),
                    ["chunks_concluidos"] = r.GetInt32(7),
                    ["relatorio_url"] = r.GetBoolean(8) ? $"/v1/auditoria/lotes/{id}/relatorio" : null,
                    ["criado_em"] = r.GetFieldValue<DateTimeOffset>(9),
                    ["concluido_em"] = r.IsDBNull(10) ? null : r.GetFieldValue<DateTimeOffset>(10),
                }
                : null;
        }, http.RequestAborted);
        return row is null ? ApiProblem.Result(404, "ERR_NOT_FOUND", "Lote não encontrado") : Results.Ok(row);
    }

    private static async Task<IResult> ReportAsync(HttpContext http, Guid id, ITenantDb db, TenantSettingsService settings, IServiceProvider sp)
    {
        var tenant = http.GetCaller().Identity.Tenant;
        var key = await db.InTenantAsync(tenant, async (s, ct) =>
        {
            await using var cmd = s.CreateCommand("SELECT report_key FROM batches WHERE id = $1");
            cmd.Parameters.Add(new() { Value = id });
            return await cmd.ExecuteScalarAsync(ct) as string;
        }, http.RequestAborted);
        var vault = sp.GetService<XmlVault>();
        if (key is null || vault is null)
        {
            return ApiProblem.Result(404, "ERR_NOT_FOUND", "Relatório ainda não disponível");
        }

        var kek = (await settings.GetAsync(tenant, http.RequestAborted)).KmsKeyRef;
        return Results.Bytes(await vault.GetAsync(kek, key, http.RequestAborted), "text/csv; charset=utf-8", $"lote-{id}.csv");
    }

    private static async Task<IResult> CreateWebhookAsync(HttpContext http, WebhookRepository repo)
    {
        string? url;
        List<string> events;
        try
        {
            var body = await JsonNode.ParseAsync(http.Request.Body, cancellationToken: http.RequestAborted);
            url = body?["url"]?.GetValue<string>();
            events = body?["eventos"]?.AsArray().Select(e => e!.GetValue<string>()).Distinct(StringComparer.Ordinal).ToList() ?? [];
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or NullReferenceException or FormatException)
        {
            return ApiProblem.Result(400, "ERR_JSON_INVALIDO", "JSON inválido", "Esperado {\"url\": \"https://...\", \"eventos\": [\"document.audited\"]}.");
        }

        if (url is null || url.Length > 2048 || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || events.Count == 0)
        {
            return ApiProblem.Result(400, "ERR_VALIDATION", "Informe url (https) e eventos (ex.: document.audited, batch.audit.completed ou *)");
        }

        // D-J05: evento com erro de digitação criava uma assinatura que nunca disparava.
        if (events.Except(WebhookEvents, StringComparer.Ordinal).FirstOrDefault() is { } unknown)
        {
            return ApiProblem.Result(400, "ERR_VALIDATION", $"Evento desconhecido: {unknown}", $"Use: {string.Join(", ", WebhookEvents)}.");
        }

        try
        {
            var (id, secret) = await repo.CreateAsync(http.GetCaller().Identity.Tenant, uri, events, http.RequestAborted);
            return Results.Created($"/v1/webhooks/{id}", new JsonObject
            {
                ["id"] = id,
                ["secret"] = secret,
                ["aviso"] = "Guarde o segredo: ele é exibido uma única vez. Assinatura em TaxGateway-Signature (t=..,v1=HMAC-SHA256).",
            });
        }
        catch (WebhookUrlRejectedException ex)
        {
            return ApiProblem.Result(422, "ERR_WEBHOOK_URL_BLOQUEADA", "URL de webhook recusada", ex.Message);
        }
    }
}
