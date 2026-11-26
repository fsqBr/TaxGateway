using Microsoft.AspNetCore.Mvc;

using TaxGateway.Api.Audit;
using TaxGateway.Api.Http;
using TaxGateway.Infrastructure.Persistence;
using TaxGateway.Infrastructure.Xml;

namespace TaxGateway.Api.Endpoints;

public static class AuditEndpoints
{
    public static RouteGroupBuilder MapAudit(this RouteGroupBuilder v1)
    {
        v1.MapPost("/auditoria/xml", AuditXmlAsync)
            .WithMetadata(new RequiredScope(Scopes.AuditoriaWrite), new QuotaMetric("DOC_AUDIT"), new RequestSizeLimitAttribute(SecureXmlLoader.DefaultMaxCharacters + 1024))
            .WithName("AuditarXml")
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(409)
            .ProducesProblem(422).ProducesProblem(429).ProducesProblem(503);
        return v1;
    }

    /// <summary>
    /// Corpo application/xml. <c>Idempotency-Key</c> obrigatório (SDD §6). Parâmetros: direction (OUTBOUND),
    /// reprocess (true reaudita sem cobrar), assinatura=opcional (aceita XML sem assinatura).
    /// </summary>
    private static async Task<IResult> AuditXmlAsync(HttpContext http, AuditService service, IdempotencyStore idempotency,
        string? direction, bool? reprocess, string? assinatura, string? confirmar)
    {
        var key = http.Request.Headers["Idempotency-Key"].ToString();
        if (key.Length is 0 or > 255)
        {
            return ApiProblem.Result(400, "ERR_IDEMPOTENCY_KEY_OBRIGATORIA", "Idempotency-Key obrigatória (até 255 caracteres) em /auditoria");
        }

        byte[] body;
        using (var ms = new MemoryStream())
        {
            await http.Request.Body.CopyToAsync(ms, http.RequestAborted);
            body = ms.ToArray();
        }

        if (body.Length == 0)
        {
            return ApiProblem.Result(400, "ERR_XML_REJEITADO", "Corpo vazio");
        }

        var caller = http.GetCaller();
        var tenant = caller.Identity.Tenant;
        var begin = await idempotency.BeginAsync(tenant, key, RequestHashing.Hash(http.Request, body), http.RequestAborted);
        switch (begin.State)
        {
            case IdempotencyState.Replay:
                http.Response.Headers["Idempotent-Replayed"] = "true";
                http.Response.StatusCode = begin.StatusCode;
                return Results.Bytes(begin.Body!, begin.StatusCode >= 400 ? "application/problem+json" : "application/json");
            case IdempotencyState.Mismatch:
                return ApiProblem.Result(422, "ERR_IDEMPOTENCY_KEY_REUSED", "Idempotency-Key já usada com outro payload");
            case IdempotencyState.InProgress:
                return ApiProblem.Result(409, "ERR_IDEMPOTENCY_IN_PROGRESS", "Requisição com esta Idempotency-Key ainda em processamento");
        }

        try
        {
            var json = await service.AuditAsync(caller,
                new AuditRequest(body, (direction ?? "OUTBOUND").ToUpperInvariant(), reprocess ?? false,
                    string.Equals(assinatura, "opcional", StringComparison.OrdinalIgnoreCase),
                    // Contexto opcional (§8.3): indícios confirmados pelo cliente, ex.: confirmar=R021.
                    (confirmar ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal)),
                http.RequestAborted);
            await idempotency.CompleteAsync(tenant, key, 200, json, CancellationToken.None);
            return Results.Bytes(json, "application/json");
        }
        catch (ApiProblemException ex) when (ex.Status is >= 400 and < 500)
        {
            await idempotency.CompleteAsync(tenant, key, ex.Status, ApiProblem.ToJson(ex), CancellationToken.None);
            throw;
        }
        catch
        {
            await idempotency.ReleaseAsync(tenant, key, CancellationToken.None);
            throw;
        }
    }
}
