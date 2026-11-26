using System.Reflection;
using System.Text.Json.Nodes;

using TaxGateway.Infrastructure.Engines;
using TaxGateway.Infrastructure.Params;

namespace TaxGateway.Api.Http;

/// <summary>
/// Versão do produto (D-W11): vem do arquivo VERSION na raiz (MSBuild) e do commit (BUILD_COMMIT). Exposta em
/// <c>GET /version</c> (sem autenticação) e no header <c>X-TaxGateway-Version</c> de toda resposta, para clientes e
/// para o front avisarem quando a versão muda.
/// </summary>
public static class AppVersion
{
    public const string Header = "X-TaxGateway-Version";

    private static readonly string Informational =
        typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    /// <summary>Versão semântica, ex.: 0.10.0.</summary>
    public static string Version { get; } = Informational.Split('+')[0];

    /// <summary>Commit curto do build (12 caracteres) ou null em build local sem BUILD_COMMIT.</summary>
    public static string? Commit { get; } = Informational.Contains('+', StringComparison.Ordinal)
        ? Informational.Split('+')[1] is { Length: > 12 } c ? c[..12] : Informational.Split('+')[1]
        : null;

    public static IApplicationBuilder UseVersionHeader(this IApplicationBuilder app) =>
        app.Use((ctx, next) =>
        {
            ctx.Response.Headers[Header] = Version;
            return next(ctx);
        });

    public static IEndpointRouteBuilder MapVersion(this IEndpointRouteBuilder app)
    {
        app.MapGet("/version", (RfbReferenceData engines, ParamSnapshotService parameters) =>
        {
            return Results.Ok(new JsonObject
            {
                ["versao"] = Version,
                ["commit"] = Commit,
                ["api"] = "v1",
                ["bundle_fiscal"] = engines.Bundle,
                ["parametros_snapshot"] = parameters.IsLoaded ? parameters.Current.Id : null,
                ["novidades"] = "/ajuda/novidades",
            });
        });
        return app;
    }
}
