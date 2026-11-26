using System.Buffers;
using System.Globalization;
using System.Net;
using System.Text.Json;

using Microsoft.Extensions.Options;

using Polly.CircuitBreaker;
using Polly.Timeout;

using TaxGateway.Core.Engines;

namespace TaxGateway.Infrastructure.Engines;

public sealed class SimplesEngineOptions
{
    public Uri BaseUrl { get; set; } = new("http://127.0.0.1:8082/api/");
    public int SyncTimeoutMs { get; set; } = 1500;
}

/// <summary>
/// Adapter do motor do Simples Nacional (SDD §8, §8.1; D-501). Capacidade declarada: só CBS/IBS (sem IS, sem
/// XML). O motor recebe a alíquota efetiva (não calcula RBT12) e a base sem IS; devolve vIBSSN/vCBSSN.
/// </summary>
public sealed class SimplesEngineAdapter(IHttpClientFactory httpFactory, IOptions<EngineOptions> options) : ISimplesEngine, IRfbTaxEngineAdapter
{
    public const string SyncClient = "rtc-simples-sync";

    public RfbEngine Engine => RfbEngine.Simples;

    public EngineCapability Capabilities => EngineCapability.CalcCbsIbs;

    public Task<EngineCalculation> CalculateAsync(EngineOperation operation, CancellationToken ct) =>
        throw new EngineCapabilityNotSupportedException(RfbEngine.Simples, EngineCapability.CalcIs);

    public async Task<SimplesCalculation> CalculateAsync(SimplesOperation operation, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(operation);
        using var content = new ByteArrayContent(Write(operation));
        content.Headers.ContentType = new("application/json");
        HttpResponseMessage response;
        try
        {
            response = await httpFactory.CreateClient(SyncClient).PostAsync("calculadora/observabilidade/simples-nacional", content, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutRejectedException or BrokenCircuitException
                                       or Polly.RateLimiting.RateLimiterRejectedException
                                   || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            throw new EngineUnavailableException($"Motor do Simples indisponível: {ex.GetType().Name}.", ex);
        }

        using (response)
        {
            var body = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity)
            {
                throw new EngineRejectedException((int)response.StatusCode, EngineJson.ReadProblemErrors(body));
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new EngineUnavailableException($"Motor do Simples respondeu HTTP {(int)response.StatusCode}.");
            }

            var version = new EngineVersion(options.Value.Bundle, RfbEngine.Simples,
                response.Headers.TryGetValues("X-CALC-APP-VERSION", out var a) ? a.FirstOrDefault() ?? "" : "",
                response.Headers.TryGetValues("X-CALC-DB-VERSION", out var d) ? d.FirstOrDefault() ?? "" : "");
            return Read(body, version);
        }
    }

    public static byte[] Write(SimplesOperation op)
    {
        ArgumentNullException.ThrowIfNull(op);
        var buffer = new ArrayBufferWriter<byte>(256 + (op.Itens.Count * 200));
        using (var w = new Utf8JsonWriter(buffer))
        {
            static string M(decimal v) => v.ToString("0.00", CultureInfo.InvariantCulture);
            w.WriteStartObject();
            w.WriteString("id", "tg");
            w.WriteString("versao", "1.0.0");
            w.WriteString("data", op.Data.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            w.WriteString("tpDoc", op.TipoDocumento.ToString(CultureInfo.InvariantCulture));
            w.WriteString("CRT", op.Crt);
            if (op.TpRegimeIbsCbs is not null)
            {
                w.WriteString("tpRegimeIBSCBS", op.TpRegimeIbsCbs);
            }

            if (op.TipoEnteGov is { } te)
            {
                w.WriteString("tpEnteGov", te.ToString(CultureInfo.InvariantCulture));
            }

            w.WriteStartArray("itens");
            foreach (var i in op.Itens)
            {
                w.WriteStartObject();
                w.WriteNumber("numero", i.Numero);
                if (i.CodigoAtividade is not null) { w.WriteString("cAtivSN", i.CodigoAtividade); }
                w.WriteString("cClassTrib", i.CClassTrib);
                if (i.Ncm is not null) { w.WriteString("ncm", i.Ncm); }
                if (i.Nbs is not null) { w.WriteString("nbs", i.Nbs); }
                w.WriteString("baseCalculo", M(i.BaseCalculo));
                w.WriteString("valorImpostoSeletivo", M(i.ValorImpostoSeletivo));
                if (i.AliquotaIbs is { } pi) { w.WriteString("pIBSSN", pi.ToString("0.00##", CultureInfo.InvariantCulture)); }
                if (i.AliquotaCbs is { } pc) { w.WriteString("pCBSSN", pc.ToString("0.00##", CultureInfo.InvariantCulture)); }
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    private static SimplesCalculation Read(byte[] body, EngineVersion version)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var itens = new List<SimplesItemResult>();
        if (root.TryGetProperty("objetos", out var objs))
        {
            foreach (var o in objs.EnumerateArray())
            {
                var erros = new List<EngineError>();
                if (o.TryGetProperty("erros", out var errs))
                {
                    foreach (var e in errs.EnumerateArray())
                    {
                        erros.Add(new EngineError(Str(e, "code") ?? "", Str(e, "title") ?? "", Str(e, "detail"), Str(e, "pointer")));
                    }
                }

                o.TryGetProperty("tribCalc", out var t);
                var has = t.ValueKind == JsonValueKind.Object;
                itens.Add(new SimplesItemResult(
                    o.GetProperty("nObj").GetInt32(), Str(o, "estadoItem") ?? "",
                    has ? Dec(t, "vBC") : null, has ? Dec(t, "pIBSSN") : null, has ? Dec(t, "vIBSSN") : null,
                    has ? Dec(t, "pCBSSN") : null, has ? Dec(t, "vCBSSN") : null, erros,
                    o.TryGetProperty("motivoEstado", out var m) && m.ValueKind == JsonValueKind.Object ? Str(m, "descricao") : null,
                    has ? t.Clone() : null));
            }
        }

        var totais = new SimplesTotals(0, 0, 0, 0);
        if (root.TryGetProperty("total", out var tot))
        {
            totais = new SimplesTotals(Dec(tot, "vBC") ?? 0, Dec(tot, "vIBSSNtot") ?? 0, Dec(tot, "vCBSSNtot") ?? 0, Dec(tot, "totalIs") ?? 0);
        }

        return new SimplesCalculation(itens, totais, version);
    }

    private static string? Str(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static decimal? Dec(JsonElement e, string n) =>
        e.TryGetProperty(n, out var v) ? v.ValueKind switch
        {
            JsonValueKind.String => decimal.Parse(v.GetString()!, NumberStyles.Number, CultureInfo.InvariantCulture),
            JsonValueKind.Number => v.GetDecimal(),
            _ => null,
        } : null;
}
