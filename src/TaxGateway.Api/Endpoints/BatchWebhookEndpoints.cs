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
        v1.MapPost("/auditoria/lotes", CreateBatchAsync).WithMetadata(new RequiredScope(Scopes.AuditoriaWrite),
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
    /// </summary>
    private static async Task<IResult> CreateBatchAsync(HttpContext http, string? direction, IOptions<BatchOptions> opts, IServiceProvider sp)
    {
        var caller = http.GetCaller();
        var limits = caller.Limits;
        var dir = (direction ?? "OUTBOUND").ToUpperInvariant();
        if (dir is not ("OUTBOUND" or "INBOUND"))
        {
            return ApiProblem.Result(400, "ERR_DIRECTION_INVALIDA", "direction deve ser OUTBOUND ou INBOUND");
        }

        if (http.Request.Headers["Idempotency-Key"].ToString().Length == 0)
        {
            return ApiProblem.Result(400, "ERR_IDEMPOTENCY_KEY_OBRIGATORIA", "Idempotency-Key obrigatória em /auditoria/lotes");
        }

        var vault = sp.GetService<XmlVault>();
        if (vault is null)
        {
            return ApiProblem.Result(503, "ERR_STORAGE_INDISPONIVEL", "Armazenamento de lotes não configurado");
        }

        var ingest = sp.GetRequiredService<BatchIngestService>();
        try
        {
            BatchCreated created;
            if (http.Request.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) == true)
            {
                var body = await JsonNode.ParseAsync(http.Request.Body, cancellationToken: http.RequestAborted);
                var key = body?["storage_object_key"]?.GetValue<string>();
                if (key is null || !key.StartsWith($"t/{caller.Identity.Tenant}/uploads/", StringComparison.Ordinal))
                {
                    return ApiProblem.Result(400, "ERR_VALIDATION", "storage_object_key inválida para este tenant");
                }

                await using var s = await vault.OpenRawAsync(key, http.RequestAborted);
                created = await ingest.IngestAsync(caller.Identity.Tenant, s, limits.BatchMaxBytes, limits.BatchMaxDocs, limits.PriorityRank, dir, http.RequestAborted);
                await vault.DeleteAsync(key, http.RequestAborted);
            }
            else
            {
                var max = Math.Min(limits.BatchMaxBytes, opts.Value.ModeOneMaxBytes);
                created = await ingest.IngestAsync(caller.Identity.Tenant, http.Request.Body, max, limits.BatchMaxDocs, limits.PriorityRank, dir, http.RequestAborted);
            }

            var seconds = (int)Math.Ceiling(created.Documents / (opts.Value.DocsPerSecondPerSlot * opts.Value.PerTenantInFlight));
            return Results.Json(new JsonObject
            {
                ["batch_id"] = created.Id,
                ["status_url"] = $"/v1/auditoria/lotes/{created.Id}",
                ["documentos"] = created.Documents,
                ["chunks"] = created.Chunks,
                ["estimated_completion_seconds"] = seconds,
            }, statusCode: 202);
        }
        catch (BatchRejectedException ex)
        {
            return ApiProblem.Result(ex.Code == "BATCH_TOO_LARGE" ? 413 : 422, ex.Code, "Lote recusado", ex.Message);
        }
    }

    private static IResult UploadUrl(HttpContext http, IServiceProvider sp)
    {
        var vault = sp.GetService<XmlVault>();
        if (vault is null)
        {
            return ApiProblem.Result(503, "ERR_STORAGE_INDISPONIVEL", "Armazenamento de lotes não configurado");
        }

        var key = $"t/{http.GetCaller().Identity.Tenant}/uploads/{Guid.CreateVersion7():N}.zip";
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
        var body = await JsonNode.ParseAsync(http.Request.Body, cancellationToken: http.RequestAborted);
        var url = body?["url"]?.GetValue<string>();
        var events = body?["eventos"]?.AsArray().Select(e => e!.GetValue<string>()).ToList() ?? [];
        if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || events.Count == 0)
        {
            return ApiProblem.Result(400, "ERR_VALIDATION", "Informe url (https) e eventos (ex.: document.audited, batch.audit.completed ou *)");
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
