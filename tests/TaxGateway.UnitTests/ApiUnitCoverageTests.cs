using System.Text.Json.Nodes;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using TaxGateway.Api.Http;
using TaxGateway.Core.Engines;

namespace TaxGateway.UnitTests;

/// <summary>
/// ApiExceptionHandler: cada exceção conhecida vira problem+json com código estável e status certo; o resto vira 500
/// sem detalhe interno. Motor indisponível pede nova tentativa (Retry-After maior com o circuito aberto, D-P20).
/// </summary>
public sealed class ApiUnitCoverageTests
{
    private static async Task<(int Status, JsonObject Body, HttpResponse Response)> HandleAsync(Exception ex)
    {
        var ctx = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider(),
        };
        ctx.Response.Body = new MemoryStream();
        var handled = await new ApiExceptionHandler(NullLogger<ApiExceptionHandler>.Instance).TryHandleAsync(ctx, ex, CancellationToken.None);
        Assert.True(handled);
        ctx.Response.Body.Position = 0;
        var body = (await JsonNode.ParseAsync(ctx.Response.Body))!.AsObject();
        return (ctx.Response.StatusCode, body, ctx.Response);
    }

    [Fact]
    public async Task Problema_de_negocio_preserva_status_codigo_e_detalhe()
    {
        var (status, body, _) = await HandleAsync(new ApiProblemException(409, "ERR_X", "Título", "Detalhe"));
        Assert.Equal(409, status);
        Assert.Equal("ERR_X", body["code"]!.GetValue<string>());
        Assert.Equal("urn:taxgateway:erro:ERR_X", body["type"]!.GetValue<string>());
        Assert.Equal("Detalhe", body["detail"]!.GetValue<string>());
    }

    [Fact]
    public async Task Erro_de_conta_vira_problem_com_o_codigo_da_conta()
    {
        var (status, body, _) = await HandleAsync(new TaxGateway.Identity.Accounts.AccountException(423, "ERR_CONTA_BLOQUEADA", "Bloqueada", "Aguarde"));
        Assert.Equal(423, status);
        Assert.Equal("ERR_CONTA_BLOQUEADA", body["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task Capacidade_nao_suportada_pelo_motor_e_422()
    {
        var (status, body, _) = await HandleAsync(new EngineCapabilityNotSupportedException(RfbEngine.Simples, EngineCapability.CalcIs));
        Assert.Equal(422, status);
        Assert.Equal("ERR_ENGINE_CAPABILITY", body["code"]!.GetValue<string>());
        Assert.Contains("Simples", body["detail"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Recusa_do_motor_traduz_o_ponteiro_para_o_campo_da_API()
    {
        EngineError[] errors =
        [
            new("E1", "NCM inválido", "8 dígitos", "#/itens/2/ncm"),
            new("E2", "CST", null, "#/itens/0/cst"),
            new("E3", "Sem ponteiro", null, null),
            new("E4", "Campo novo", null, "#/campoNovo"),
        ];
        var (status, body, _) = await HandleAsync(new EngineRejectedException(400, errors));
        Assert.Equal(422, status);
        Assert.Equal("ERR_ENGINE_REJECTED", body["code"]!.GetValue<string>());
        var campos = body["errors"]!.AsArray().Select(e => e!["campo"]?.GetValue<string>()).ToList();
        Assert.Equal(["itens[2].ncm", "itens[0].cst_ibs_cbs", null, "campoNovo"], campos);
        Assert.Equal("NCM inválido", body["errors"]![0]!["titulo"]!.GetValue<string>());
    }

    [Fact]
    public async Task Motor_indisponivel_pede_nova_tentativa_e_circuito_aberto_espera_mais()
    {
        var (status, body, resp) = await HandleAsync(new EngineUnavailableException("timeout"));
        Assert.Equal(503, status);
        Assert.Equal("ERR_ENGINE_UNAVAILABLE", body["code"]!.GetValue<string>());
        Assert.Equal("2", resp.Headers.RetryAfter.ToString());

        var (_, _, open) = await HandleAsync(new EngineUnavailableException("circuito", new Polly.CircuitBreaker.BrokenCircuitException()));
        Assert.Equal("15", open.Headers.RetryAfter.ToString());
    }

    [Fact]
    public async Task Corpo_grande_demais_e_413_e_requisicao_invalida_e_400()
    {
        var (status, body, _) = await HandleAsync(new BadHttpRequestException("Request body too large.", 413));
        Assert.Equal(413, status);
        Assert.Equal("ERR_PAYLOAD_TOO_LARGE", body["code"]!.GetValue<string>());

        var (bad, badBody, _) = await HandleAsync(new BadHttpRequestException("Failed to read parameter \"req\" from the request body as JSON."));
        Assert.Equal(400, bad);
        Assert.Equal("ERR_BAD_REQUEST", badBody["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task Excecao_desconhecida_vira_500_sem_detalhe_interno()
    {
        var (status, body, _) = await HandleAsync(new InvalidOperationException("segredo interno: senha=123"));
        Assert.Equal(500, status);
        Assert.Equal("ERR_INTERNAL", body["code"]!.GetValue<string>());
        Assert.DoesNotContain("segredo", body.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Postgres_P0001_sem_o_prefixo_do_limite_nao_vira_ERR_LIMITE_CNPJS()
    {
        var pg = new Npgsql.PostgresException("outra regra", "ERROR", "ERROR", "P0001");
        var (status, body, _) = await HandleAsync(pg);
        Assert.Equal(500, status);
        Assert.Equal("ERR_INTERNAL", body["code"]!.GetValue<string>());

        var limit = new Npgsql.PostgresException("ERR_LIMITE_CNPJS:3", "ERROR", "ERROR", "P0001");
        var (s2, b2, _) = await HandleAsync(limit);
        Assert.Equal(402, s2);
        Assert.Contains("3 CNPJ(s)", b2["detail"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("#/itens/1/baseCalculo", "itens[1].valor_base_calculo")]
    [InlineData("#/municipio", "municipio_ibge")]
    [InlineData("#/itens/-1/ncm", "itens.ncm")]
    public void Ponteiro_do_motor_vira_campo_da_API(string? ponteiro, string? expected) =>
        Assert.Equal(expected, EngineFieldNames.Map(ponteiro));
}
