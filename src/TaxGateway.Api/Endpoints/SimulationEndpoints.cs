using System.Text.Json;

using Microsoft.AspNetCore.Mvc;

using TaxGateway.Api.Http;
using TaxGateway.Api.Simulation;
using TaxGateway.Contracts;
using TaxGateway.Infrastructure.Persistence;

namespace TaxGateway.Api.Endpoints;

public static class SimulationEndpoints
{
    private const int MaxBodyBytes = 2 * 1024 * 1024;

    public static RouteGroupBuilder MapSimulation(this RouteGroupBuilder v1)
    {
        v1.MapPost("/calculo/simular", SimulateAsync)
            .WithMetadata(new RequiredScope(Scopes.SimulacaoWrite), new RequestSizeLimitAttribute(MaxBodyBytes))
            .WithName("SimularCalculo")
            .Produces<SimulacaoResponse>(200)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(409)
            .ProducesProblem(422).ProducesProblem(429).ProducesProblem(503);
        return v1;
    }

    /// <summary>
    /// Idempotency-Key opcional (SDD §6): replay devolve a resposta original sem nova cobrança; mesma chave com
    /// payload diferente = 422; chave em processamento = 409.
    /// </summary>
    private static async Task<IResult> SimulateAsync(HttpContext http, SimulationService service, IdempotencyStore idempotency)
    {
        var caller = http.GetCaller();
        byte[] body;
        using (var ms = new MemoryStream())
        {
            await http.Request.Body.CopyToAsync(ms, http.RequestAborted);
            body = ms.ToArray();
        }

        SimulacaoRequest? req;
        try
        {
            req = JsonSerializer.Deserialize(body, ContractsJsonContext.Default.SimulacaoRequest);
        }
        catch (JsonException ex)
        {
            return ApiProblem.Result(400, "ERR_JSON_INVALIDO", "JSON inválido", ex.Path is null ? ex.Message : $"Erro em {ex.Path}.");
        }

        if (req is null)
        {
            return ApiProblem.Result(400, "ERR_JSON_INVALIDO", "Corpo vazio");
        }

        var key = http.Request.Headers["Idempotency-Key"].ToString();
        if (key.Length == 0)
        {
            var outcome = await service.SimulateAsync(caller, req, System.Text.Encoding.UTF8.GetString(body), http.RequestAborted);
            return Results.Bytes(outcome.Json, "application/json");
        }

        if (key.Length > 255)
        {
            return ApiProblem.Result(400, "ERR_IDEMPOTENCY_KEY_INVALIDA", "Idempotency-Key com mais de 255 caracteres");
        }

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
            var outcome = await service.SimulateAsync(caller, req, System.Text.Encoding.UTF8.GetString(body), http.RequestAborted);
            await idempotency.CompleteAsync(tenant, key, 200, outcome.Json, CancellationToken.None);
            return Results.Bytes(outcome.Json, "application/json");
        }
        catch (ApiProblemException ex) when (ex.Status is >= 400 and < 500)
        {
            // Erro determinístico do cliente: guardado, o replay devolve o mesmo erro.
            await idempotency.CompleteAsync(tenant, key, ex.Status, ApiProblem.ToJson(ex), CancellationToken.None);
            throw;
        }
        catch
        {
            // Falha transitória (motor, banco): libera a chave para o cliente tentar de novo.
            await idempotency.ReleaseAsync(tenant, key, CancellationToken.None);
            throw;
        }
    }
}
