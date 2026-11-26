using Microsoft.AspNetCore.Diagnostics;

using TaxGateway.Contracts;
using TaxGateway.Core.Engines;

namespace TaxGateway.Api.Http;

/// <summary>Erro de negócio com código estável (ERR_*), devolvido como application/problem+json (RFC 9457).</summary>
public sealed class ApiProblemException(int status, string code, string title, string? detail = null, IReadOnlyList<CampoErroDto>? errors = null)
    : Exception(detail ?? title)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
    public string Title { get; } = title;
    public string? Detail { get; } = detail;
    public IReadOnlyList<CampoErroDto>? Errors { get; } = errors;
}

public static class ApiProblem
{
    public static IResult Result(int status, string code, string title, string? detail = null, object? errors = null)
    {
        var ext = new Dictionary<string, object?> { ["code"] = code };
        if (errors is not null)
        {
            ext["errors"] = errors;
        }

        return Results.Problem(detail: detail, statusCode: status, title: title, type: $"urn:taxgateway:erro:{code}", extensions: ext);
    }

    public static IResult From(ApiProblemException ex) => Result(ex.Status, ex.Code, ex.Title, ex.Detail, ex.Errors);

    /// <summary>Mesmo problem+json, serializado para guardar no replay de Idempotency-Key.</summary>
    public static byte[] ToJson(ApiProblemException ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        using var ms = new MemoryStream();
        using (var w = new System.Text.Json.Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("type", $"urn:taxgateway:erro:{ex.Code}");
            w.WriteString("title", ex.Title);
            w.WriteNumber("status", ex.Status);
            if (ex.Detail is not null)
            {
                w.WriteString("detail", ex.Detail);
            }

            w.WriteString("code", ex.Code);
            if (ex.Errors is not null)
            {
                w.WritePropertyName("errors");
                System.Text.Json.JsonSerializer.Serialize(w, ex.Errors.ToList(), ContractsJsonContext.Default.ListCampoErroDto);
            }

            w.WriteEndObject();
        }

        return ms.ToArray();
    }
}

/// <summary>Mapeia exceções conhecidas para problem+json com código; o resto vira 500 sem detalhes internos.</summary>
public sealed partial class ApiExceptionHandler(ILogger<ApiExceptionHandler> log) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        var result = exception switch
        {
            ApiProblemException p => ApiProblem.From(p),
            EngineCapabilityNotSupportedException c => ApiProblem.Result(422, "ERR_ENGINE_CAPABILITY",
                "Capacidade não suportada pelo motor", c.Message),
            EngineRejectedException r => ApiProblem.Result(422, "ERR_ENGINE_REJECTED",
                "O motor da RFB recusou a operação", "Ver errors.", r.Errors.Select(e => new ErroItemDto
                {
                    Codigo = e.Codigo,
                    Titulo = e.Titulo,
                    Detalhe = e.Detalhe,
                    Campo = EngineFieldNames.Map(e.Ponteiro),
                }).ToList()),
            EngineUnavailableException => ApiProblem.Result(503, "ERR_ENGINE_UNAVAILABLE",
                "Motor fiscal indisponível", "Tente novamente em instantes."),
            BadHttpRequestException b => ApiProblem.Result(b.StatusCode, "ERR_BAD_REQUEST", "Requisição inválida", b.Message),
            _ => null,
        };

        if (result is null)
        {
            LogUnhandled(log, exception);
            result = ApiProblem.Result(500, "ERR_INTERNAL", "Erro interno", "Registrado para análise.");
        }
        else if (exception is EngineUnavailableException)
        {
            httpContext.Response.Headers.RetryAfter = "1";
        }

        await result.ExecuteAsync(httpContext).ConfigureAwait(false);
        return true;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Erro não tratado.")]
    private static partial void LogUnhandled(ILogger logger, Exception ex);
}

/// <summary>Traduz o ponteiro JSON do motor (#/itens/2/ncm) para o campo da API pública (itens[2].ncm).</summary>
public static class EngineFieldNames
{
    private static readonly Dictionary<string, string> Fields = new(StringComparer.Ordinal)
    {
        ["cst"] = "cst_ibs_cbs",
        ["cClassTrib"] = "c_class_trib",
        ["baseCalculo"] = "valor_base_calculo",
        ["aliquotasNominais"] = "aliquotas_nominais",
        ["impostoSeletivo"] = "imposto_seletivo",
        ["municipio"] = "municipio_ibge",
        ["dhFatoGerador"] = "data_fato_gerador",
        ["ncm"] = "ncm",
        ["nbs"] = "nbs",
        ["quantidade"] = "quantidade",
        ["unidade"] = "unidade",
        ["itens"] = "itens",
        ["uf"] = "uf",
    };

    public static string? Map(string? jsonPointer)
    {
        if (string.IsNullOrEmpty(jsonPointer))
        {
            return null;
        }

        var parts = jsonPointer.TrimStart('#', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var sb = new System.Text.StringBuilder();
        foreach (var p in parts)
        {
            if (int.TryParse(p, System.Globalization.CultureInfo.InvariantCulture, out var idx))
            {
                if (idx >= 0)
                {
                    sb.Append('[').Append(idx).Append(']');
                }

                continue;
            }

            if (sb.Length > 0)
            {
                sb.Append('.');
            }

            sb.Append(Fields.GetValueOrDefault(p, p));
        }

        return sb.ToString();
    }
}
