using System.Buffers;
using System.Globalization;
using System.Net;
using System.Text.Json;

using Microsoft.Extensions.Options;

using Polly.CircuitBreaker;
using Polly.Timeout;

using TaxGateway.Core.Engines;

namespace TaxGateway.Infrastructure.Engines;

public sealed class EngineOptions
{
    public const string Section = "Engines";

    /// <summary>Bundle em uso (infra/rfb/versions.json), devolvido em toda resposta.</summary>
    public string Bundle { get; set; } = "";

    public RegularEngineOptions Regular { get; set; } = new();

    public SimplesEngineOptions Simples { get; set; } = new();
}

public sealed class RegularEngineOptions
{
    public Uri BaseUrl { get; set; } = new("http://127.0.0.1:8080/api/");

    /// <summary>A partir desta data o motor exige aliquotasNominais e antes dela as proíbe (REG-036/REG-037).</summary>
    public DateOnly AliquotasNominaisDesde { get; set; } = new(2027, 1, 1);

    public int SyncTimeoutMs { get; set; } = 1500;
    public int SyncMaxConcurrency { get; set; } = 64;
    public int SyncMaxQueue { get; set; } = 256;
}

/// <summary>
/// Adapter do motor do regime regular (SDD §8). Usa <c>/observabilidade/regime-geral</c> para devolver os erros
/// de todos os itens de uma vez. Resiliência (timeout, retry, circuit breaker, bulkhead) fica no HttpClient
/// nomeado <see cref="SyncClient"/> (D-209).
/// </summary>
public sealed class RegularEngineAdapter(IHttpClientFactory httpFactory, IOptions<EngineOptions> options) : IRfbTaxEngineAdapter
{
    public const string SyncClient = "rtc-regular-sync";
    private const string CalcPath = "calculadora/observabilidade/regime-geral";

    public RfbEngine Engine => RfbEngine.Regular;

    public EngineCapability Capabilities =>
        EngineCapability.CalcCbsIbs | EngineCapability.CalcIs | EngineCapability.XmlGenerate |
        EngineCapability.XmlValidate | EngineCapability.BaseCalculoUnificada | EngineCapability.DadosAbertos;

    public async Task<EngineCalculation> CalculateAsync(EngineOperation operation, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(operation);
        using var content = new ByteArrayContent(EngineJson.WriteOperation(operation));
        content.Headers.ContentType = new("application/json");

        HttpResponseMessage response;
        try
        {
            response = await httpFactory.CreateClient(SyncClient).PostAsync(CalcPath, content, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutRejectedException or BrokenCircuitException
                                       or Polly.RateLimiting.RateLimiterRejectedException
                                   || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            throw new EngineUnavailableException($"Motor regular indisponível: {ex.GetType().Name}.", ex);
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
                throw new EngineUnavailableException($"Motor regular respondeu HTTP {(int)response.StatusCode}.");
            }

            var version = new EngineVersion(
                options.Value.Bundle, RfbEngine.Regular,
                Header(response, "X-CALC-APP-VERSION"), Header(response, "X-CALC-DB-VERSION"));
            var warnings = response.Headers.TryGetValues("x-warning-dados-simulados", out var w) ? w.ToArray() : [];
            return EngineJson.ReadCalculation(body, operation, warnings, version);
        }
    }

    /// <summary>JSON exato enviado ao motor: base da chave do cache determinístico (SDD §16.2 item 5).</summary>
    public static byte[] CanonicalRequest(EngineOperation operation) => EngineJson.WriteOperation(operation);

    private static string Header(HttpResponseMessage r, string name) =>
        r.Headers.TryGetValues(name, out var v) ? v.FirstOrDefault() ?? "" : "";
}

/// <summary>Serialização do contrato do motor sem reflexão (caminho quente; compatível com trimming/AOT).</summary>
internal static class EngineJson
{
    public static byte[] WriteOperation(EngineOperation op)
    {
        var buffer = new ArrayBufferWriter<byte>(512 + (op.Itens.Count * 256));
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("id", op.Id);
            w.WriteString("versao", "1.0.0");
            // Data/hora com o offset do próprio documento: a data do fato gerador é a local (SDD §8.6).
            w.WriteString("dhFatoGerador", op.FatoGerador.ToString("yyyy-MM-dd'T'HH:mm:ssK", CultureInfo.InvariantCulture));
            w.WriteNumber("municipio", op.Municipio);
            w.WriteString("uf", op.Uf);
            if (op.TipoDocumento is { } tp)
            {
                w.WriteNumber("tpDoc", tp);
            }

            if (op.CompraGovernamental is { } gov)
            {
                w.WriteStartObject("gCompraGov");
                w.WriteNumber("tpEnteGov", gov.TipoEnte);
                if (gov.TipoOperacao is { } to)
                {
                    w.WriteNumber("tpOperGov", to);
                }

                w.WriteEndObject();
            }

            w.WriteStartArray("itens");
            foreach (var i in op.Itens)
            {
                w.WriteStartObject();
                w.WriteNumber("numero", i.Numero);
                WriteOpt(w, "ncm", i.Ncm);
                WriteOpt(w, "nbs", i.Nbs);
                w.WriteString("cst", i.Cst);
                w.WriteString("cClassTrib", i.CClassTrib);
                WriteOpt(w, "baseCalculo", i.BaseCalculo);
                WriteOpt(w, "quantidade", i.Quantidade);
                WriteOpt(w, "unidade", i.Unidade);
                if (i.ImpostoSeletivo is { } s)
                {
                    w.WriteStartObject("impostoSeletivo");
                    w.WriteString("cst", s.Cst);
                    w.WriteString("cClassTrib", s.CClassTrib);
                    w.WriteNumber("baseCalculo", s.BaseCalculo);
                    WriteOpt(w, "quantidade", s.Quantidade);
                    WriteOpt(w, "unidade", s.Unidade);
                    w.WriteEndObject();
                }

                if (i.TributacaoRegular is { } reg)
                {
                    w.WriteStartObject("tributacaoRegular");
                    w.WriteString("cst", reg.Cst);
                    w.WriteString("cClassTrib", reg.CClassTrib);
                    w.WriteEndObject();
                }

                if (i.Componentes is { } c)
                {
                    w.WriteStartObject("gComponentesBC");
                    w.WriteNumber("vProd", c.VProd);
                    WriteOpt(w, "vFrete", c.VFrete);
                    WriteOpt(w, "vSeg", c.VSeg);
                    WriteOpt(w, "vOutro", c.VOutro);
                    WriteOpt(w, "vII", c.VII);
                    WriteOpt(w, "vIS", c.VIS);
                    WriteOpt(w, "vDesc", c.VDesc);
                    WriteOpt(w, "vPIS", c.VPIS);
                    WriteOpt(w, "vCOFINS", c.VCOFINS);
                    WriteOpt(w, "vICMS", c.VICMS);
                    WriteOpt(w, "vICMSUFDest", c.VICMSUFDest);
                    WriteOpt(w, "vFCP", c.VFCP);
                    WriteOpt(w, "vFCPUFDest", c.VFCPUFDest);
                    WriteOpt(w, "vICMSMono", c.VICMSMono);
                    WriteOpt(w, "vISSQN", c.VISSQN);
                    w.WriteEndObject();
                }

                if (i.AliquotasNominais is { } a)
                {
                    w.WriteStartObject("aliquotasNominais");
                    WriteOpt(w, "cbs", a.Cbs);
                    WriteOpt(w, "ibsEstadual", a.IbsUf);
                    WriteOpt(w, "ibsMunicipal", a.IbsMun);
                    if (a.IsAdValorem is not null || a.IsAdRem is not null)
                    {
                        w.WriteStartObject("impostoSeletivo");
                        WriteOpt(w, "adValorem", a.IsAdValorem);
                        WriteOpt(w, "adRem", a.IsAdRem);
                        w.WriteEndObject();
                    }

                    w.WriteEndObject();
                }

                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    public static EngineCalculation ReadCalculation(byte[] body, EngineOperation op, IReadOnlyList<string> warnings, EngineVersion version)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var itens = new List<EngineItemResult>(op.Itens.Count);
        if (root.TryGetProperty("objetos", out var objetos))
        {
            foreach (var o in objetos.EnumerateArray())
            {
                itens.Add(ReadItem(o));
            }
        }

        var totais = new EngineTotals(0, 0, 0, 0, 0, 0);
        if (root.TryGetProperty("total", out var total) && total.TryGetProperty("tribCalc", out var tc))
        {
            decimal vbc = 0, cbs = 0, ibsUf = 0, ibsMun = 0, ibs = 0, vis = 0;
            if (tc.TryGetProperty("IBSCBSTot", out var t))
            {
                vbc = Dec(t, "vBCIBSCBS") ?? 0;
                if (t.TryGetProperty("gCBS", out var g))
                {
                    cbs = Dec(g, "vCBS") ?? 0;
                }

                if (t.TryGetProperty("gIBS", out var gi))
                {
                    ibs = Dec(gi, "vIBS") ?? 0;
                    ibsUf = gi.TryGetProperty("gIBSUF", out var u) ? Dec(u, "vIBSUF") ?? 0 : 0;
                    ibsMun = gi.TryGetProperty("gIBSMun", out var m) ? Dec(m, "vIBSMun") ?? 0 : 0;
                }
            }

            if (tc.TryGetProperty("ISTot", out var ist))
            {
                vis = Dec(ist, "vIS") ?? 0;
            }

            totais = new EngineTotals(vbc, cbs, ibsUf, ibsMun, ibs, vis);
        }

        JsonElement? oper = root.TryGetProperty("oper", out var operGroup) ? operGroup.Clone() : null;
        return new EngineCalculation(itens, totais, warnings, version, oper);
    }

    private static EngineItemResult ReadItem(JsonElement o)
    {
        var numero = o.GetProperty("nObj").GetInt32();
        var estado = o.TryGetProperty("estadoItem", out var e) ? e.GetString() ?? "" : "";
        var erros = o.TryGetProperty("erros", out var errs) ? ReadErrors(errs) : [];
        if (!o.TryGetProperty("tribCalc", out var tc))
        {
            return new EngineItemResult(numero, estado, null, null, null, null, null, null, null, null, erros, null);
        }

        string? cst = null, cClass = null;
        decimal? vbc = null, vibs = null;
        TaxLine? cbs = null, ibsUf = null, ibsMun = null;
        if (tc.TryGetProperty("IBSCBS", out var ibscbs))
        {
            cst = Str(ibscbs, "CST");
            cClass = Str(ibscbs, "cClassTrib");
            if (ibscbs.TryGetProperty("gIBSCBS", out var g))
            {
                vbc = Dec(g, "vBC");
                vibs = Dec(g, "vIBS");
                cbs = Line(g, "gCBS", "pCBS", "vCBS");
                ibsUf = Line(g, "gIBSUF", "pIBSUF", "vIBSUF");
                ibsMun = Line(g, "gIBSMun", "pIBSMun", "vIBSMun");
            }
        }

        IsLine? vis = null;
        if (tc.TryGetProperty("IS", out var isg))
        {
            vis = new IsLine(Dec(isg, "vBCIS") ?? 0, Dec(isg, "pIS"), Dec(isg, "pISEspec"), Dec(isg, "vIS") ?? 0);
        }

        return new EngineItemResult(numero, estado, cst, cClass, vbc, cbs, ibsUf, ibsMun, vibs, vis, erros, tc.Clone());
    }

    private static TaxLine? Line(JsonElement g, string group, string pName, string vName)
    {
        if (!g.TryGetProperty(group, out var x))
        {
            return null;
        }

        decimal? red = null, efet = null;
        if (x.TryGetProperty("gRed", out var r))
        {
            red = Dec(r, "pRedAliq");
            efet = Dec(r, "pAliqEfet");
        }

        return new TaxLine(Dec(x, pName) ?? 0, Dec(x, vName) ?? 0, red, efet);
    }

    public static IReadOnlyList<EngineError> ReadProblemErrors(byte[] body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("errors", out var errs) ? ReadErrors(errs) : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static List<EngineError> ReadErrors(JsonElement errs)
    {
        var list = new List<EngineError>();
        foreach (var x in errs.EnumerateArray())
        {
            list.Add(new EngineError(Str(x, "code") ?? "", Str(x, "title") ?? "", Str(x, "detail"), Str(x, "pointer")));
        }

        return list;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>O motor devolve valores como string ("100.00"); nunca arredondamos de novo (SDD §8.3).</summary>
    private static decimal? Dec(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v))
        {
            return null;
        }

        return v.ValueKind switch
        {
            JsonValueKind.String => decimal.Parse(v.GetString()!, NumberStyles.Number, CultureInfo.InvariantCulture),
            JsonValueKind.Number => v.GetDecimal(),
            _ => null,
        };
    }

    private static void WriteOpt(Utf8JsonWriter w, string name, string? value)
    {
        if (value is not null)
        {
            w.WriteString(name, value);
        }
    }

    private static void WriteOpt(Utf8JsonWriter w, string name, decimal? value)
    {
        if (value is { } v)
        {
            w.WriteNumber(name, v);
        }
    }
}
