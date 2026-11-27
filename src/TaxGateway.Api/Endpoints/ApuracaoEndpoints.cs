using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

using TaxGateway.Api.Http;
using TaxGateway.Infrastructure.Persistence;

namespace TaxGateway.Api.Endpoints;

/// <summary>
/// Apuração mensal e fornecedores (D-R26), a partir das auditorias já feitas — a versão mais recente de cada nota
/// (registry), pela data de EMISSÃO (competência fiscal), não pela data do envio:
/// <list type="bullet">
/// <item><c>GET /v1/apuracao</c>: débitos das saídas (recalculado pelo motor oficial e declarado), créditos das
/// entradas (potencial e em risco), saldo estimado, quebra por município de incidência e por CST/cClassTrib.</item>
/// <item><c>GET /v1/apuracao/fornecedores</c>: crédito e divergências por fornecedor, do maior risco ao menor.</item>
/// <item><c>GET /v1/apuracao/exportar</c>: CSV para o contador (Excel: ponto e vírgula, vírgula decimal, BOM).</item>
/// </list>
/// Estimativa gerencial: a apuração oficial é a da Receita/CG-IBS; aqui é o que as notas auditadas mostram.
/// </summary>
public static class ApuracaoEndpoints
{
    public static RouteGroupBuilder MapApuracao(this RouteGroupBuilder v1)
    {
        var read = new RequiredScope(Scopes.AuditoriaRead, Scopes.AuditoriaWrite);
        v1.MapGet("/apuracao", ResumoAsync).WithMetadata(read).WithTags("Apuração");
        v1.MapGet("/apuracao/fornecedores", FornecedoresAsync).WithMetadata(read).WithTags("Apuração");
        v1.MapGet("/apuracao/exportar", ExportarAsync).WithMetadata(read).WithTags("Apuração");
        return v1;
    }

    /// <summary>Base comum: última auditoria de cada chave com emissão na competência.</summary>
    private const string Base = """
        WITH docs AS (
          SELECT f.*, (f.declared->>'data_emissao')::timestamptz AS emissao,
                 coalesce((f.recalculated->'resposta'->'resumo_financeiro'->'tributo_recalculado'->>'cbs')::numeric, 0) AS rcbs,
                 coalesce((f.recalculated->'resposta'->'resumo_financeiro'->'tributo_recalculado'->>'ibs')::numeric, 0) AS ribs,
                 coalesce((f.recalculated->'resposta'->'resumo_financeiro'->'tributo_recalculado'->>'is')::numeric, 0) AS ris,
                 coalesce((f.recalculated->'resposta'->'resumo_financeiro'->'tributo_declarado'->>'cbs')::numeric, 0) AS dcbs,
                 coalesce((f.recalculated->'resposta'->'resumo_financeiro'->'tributo_declarado'->>'ibs')::numeric, 0) AS dibs,
                 coalesce((f.recalculated->'resposta'->'resumo_financeiro'->'tributo_declarado'->>'is')::numeric, 0) AS dis
            FROM document_registry r
            JOIN fiscal_documents f ON f.id = r.latest_document_id AND f.access_key = r.access_key
           WHERE (f.declared->>'data_emissao')::timestamptz >= $1 AND (f.declared->>'data_emissao')::timestamptz < $2
             AND f.audit_status NOT IN ('SCHEMA_ERROR','ASSINATURA_INVALIDA'))
        """;

    private static bool TryMonth(string? competencia, out DateOnly month)
    {
        month = default;
        return competencia is { Length: 7 } && DateOnly.TryParseExact(competencia + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out month)
            && month.Year is >= 2026 and <= 2100;
    }

    private static (DateTimeOffset From, DateTimeOffset To) Range(DateOnly m) =>
        (new DateTimeOffset(m.ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(-3)).ToUniversalTime(),
         new DateTimeOffset(m.AddMonths(1).ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(-3)).ToUniversalTime());   // Npgsql só aceita UTC

    private static IResult BadMonth() => ApiProblem.Result(400, "ERR_VALIDATION", "competencia no formato AAAA-MM (2026 em diante)");

    private static async Task<IResult> ResumoAsync(HttpContext http, string? competencia, ITenantDb db)
    {
        if (!TryMonth(competencia, out var m))
        {
            return BadMonth();
        }

        var (from, to) = Range(m);
        var json = await db.InTenantAsync(http.GetCaller().Identity.Tenant, async (s, ct) =>
        {
            await using var cmd = s.CreateCommand(Base + """
                SELECT json_build_object(
                  'competencia', to_char($1 + interval '3 hours', 'YYYY-MM'),
                  'saidas', (SELECT json_build_object('documentos', count(*), 'cbs', coalesce(sum(rcbs), 0), 'ibs', coalesce(sum(ribs), 0), 'is', coalesce(sum(ris), 0),
                               'declarado', json_build_object('cbs', coalesce(sum(dcbs), 0), 'ibs', coalesce(sum(dibs), 0), 'is', coalesce(sum(dis), 0)),
                               'diferenca', coalesce(sum(difference_total), 0),
                               'divergentes', count(*) FILTER (WHERE audit_status = 'DIVERGENTE')) FROM docs WHERE kind = 'OUTBOUND'),
                  'entradas', (SELECT json_build_object('documentos', count(*), 'cbs', coalesce(sum(rcbs), 0), 'ibs', coalesce(sum(ribs), 0),
                               'credito_potencial', coalesce(sum(credit_potential), 0),
                               'credito_em_risco', coalesce(sum(credit_potential) FILTER (WHERE coalesce(credit_status, '') <> 'APROPRIAVEL_POR_DESTAQUE'), 0),
                               'divergentes', count(*) FILTER (WHERE audit_status = 'DIVERGENTE')) FROM docs WHERE kind = 'INBOUND'),
                  'por_municipio', (SELECT coalesce(json_agg(x ORDER BY x.tributo DESC), '[]') FROM (
                       SELECT d.municipio_incidencia AS codigo, m.nome, u.sigla AS uf, count(*) AS documentos,
                              sum(d.rcbs) AS cbs, sum(d.ribs) AS ibs, sum(d.rcbs + d.ribs) AS tributo
                         FROM docs d LEFT JOIN municipios m ON m.codigo = d.municipio_incidencia LEFT JOIN ufs u ON u.codigo = m.uf_codigo
                        WHERE d.kind = 'OUTBOUND' GROUP BY 1, 2, 3) x),
                  'por_classificacao', (SELECT coalesce(json_agg(y ORDER BY y.valor DESC), '[]') FROM (
                       SELECT i->>'cst' AS cst, i->>'c_class_trib' AS c_class_trib, count(*) AS itens,
                              sum(coalesce((i->>'vbc')::numeric, 0)) AS base,
                              sum(coalesce((i->>'vcbs')::numeric, 0) + coalesce((i->>'vibsuf')::numeric, 0) + coalesce((i->>'vibsmun')::numeric, 0)) AS valor
                         FROM docs d, jsonb_array_elements(d.declared->'itens') i
                        WHERE d.kind = 'OUTBOUND' GROUP BY 1, 2) y)
                )::text
                """);
            cmd.Parameters.Add(new() { Value = from });
            cmd.Parameters.Add(new() { Value = to });
            return (string)(await cmd.ExecuteScalarAsync(ct))!;
        }, http.RequestAborted);

        var node = JsonNode.Parse(json)!.AsObject();
        decimal D(string a, string b) => node[a]?[b]?.GetValue<decimal>() ?? 0;
        var debitos = D("saidas", "cbs") + D("saidas", "ibs");
        var creditos = D("entradas", "credito_potencial") - D("entradas", "credito_em_risco");
        node["saldo"] = new JsonObject
        {
            ["debitos"] = debitos,
            ["creditos_aproveitaveis"] = creditos,
            ["a_recolher_estimado"] = Math.Max(0, debitos - creditos),
            ["credor_estimado"] = Math.Max(0, creditos - debitos),
        };
        node["observacao"] = "Estimativa a partir das notas auditadas (última auditoria de cada chave, pela data de emissão). A apuração oficial é a da Receita e do Comitê Gestor do IBS.";
        return Results.Ok(node);
    }

    private static async Task<IResult> FornecedoresAsync(HttpContext http, string? competencia, ITenantDb db)
    {
        if (!TryMonth(competencia, out var m))
        {
            return BadMonth();
        }

        var (from, to) = Range(m);
        var json = await db.InTenantAsync(http.GetCaller().Identity.Tenant, async (s, ct) =>
        {
            await using var cmd = s.CreateCommand(Base + """
                SELECT coalesce(json_agg(x ORDER BY x.credito_em_risco DESC, x.divergentes DESC, x.diferenca DESC), '[]')::text FROM (
                  SELECT cnpj_counterparty AS cnpj, count(*) AS documentos,
                         count(*) FILTER (WHERE audit_status = 'DIVERGENTE') AS divergentes,
                         count(*) FILTER (WHERE audit_status = 'INDICIO') AS indicios,
                         coalesce(sum(credit_potential), 0) AS credito_potencial,
                         coalesce(sum(credit_potential) FILTER (WHERE coalesce(credit_status, '') <> 'APROPRIAVEL_POR_DESTAQUE'), 0) AS credito_em_risco,
                         coalesce(sum(abs(difference_total)), 0) AS diferenca
                    FROM docs WHERE kind = 'INBOUND' AND cnpj_counterparty IS NOT NULL
                   GROUP BY cnpj_counterparty LIMIT 500) x
                """);
            cmd.Parameters.Add(new() { Value = from });
            cmd.Parameters.Add(new() { Value = to });
            return (string)(await cmd.ExecuteScalarAsync(ct))!;
        }, http.RequestAborted);
        return Results.Text($$"""{"competencia":"{{m:yyyy-MM}}","fornecedores":{{json}}}""", "application/json");
    }

    private static async Task<IResult> ExportarAsync(HttpContext http, string? competencia, ITenantDb db)
    {
        if (!TryMonth(competencia, out var m))
        {
            return BadMonth();
        }

        var (from, to) = Range(m);
        var rows = await db.InTenantAsync(http.GetCaller().Identity.Tenant, async (s, ct) =>
        {
            await using var cmd = s.CreateCommand(Base + """
                SELECT kind, access_key, emissao, cnpj_subject, coalesce(cnpj_counterparty, ''), audit_status, coalesce(municipio_incidencia, 0),
                       dcbs, dibs, dis, rcbs, ribs, ris, difference_total, credit_potential, coalesce(credit_status, '')
                  FROM docs ORDER BY kind DESC, emissao
                """);
            cmd.Parameters.Add(new() { Value = from });
            cmd.Parameters.Add(new() { Value = to });
            await using var r = await cmd.ExecuteReaderAsync(ct);
            var list = new List<string>();
            static string N(decimal v) => v.ToString("0.00", CultureInfo.InvariantCulture).Replace('.', ',');   // vírgula decimal (Excel pt-BR); sem ICU
            while (await r.ReadAsync(ct))
            {
                list.Add(string.Join(';',
                    r.GetString(0) == "OUTBOUND" ? "saida" : "entrada", r.GetString(1), r.GetFieldValue<DateTimeOffset>(2).ToOffset(TimeSpan.FromHours(-3)).ToString("dd/MM/yyyy", CultureInfo.InvariantCulture),
                    r.GetString(3), r.GetString(4), r.GetString(5), r.GetInt32(6).ToString(CultureInfo.InvariantCulture),
                    N(r.GetDecimal(7)), N(r.GetDecimal(8)), N(r.GetDecimal(9)), N(r.GetDecimal(10)), N(r.GetDecimal(11)), N(r.GetDecimal(12)),
                    N(r.GetDecimal(13)), N(r.GetDecimal(14)), r.GetString(15)));
            }

            return list;
        }, http.RequestAborted);
        var csv = new StringBuilder("﻿tipo;chave_acesso;emissao;cnpj;cnpj_contraparte;situacao;municipio_ibge;cbs_declarada;ibs_declarado;is_declarado;cbs_recalculada;ibs_recalculado;is_recalculado;diferenca;credito_potencial;situacao_credito\r\n");
        foreach (var line in rows)
        {
            csv.Append(line).Append("\r\n");
        }

        http.Response.Headers.ContentDisposition = $"attachment; filename=\"apuracao-{m:yyyy-MM}.csv\"";
        return Results.Text(csv.ToString(), "text/csv; charset=utf-8");
    }
}
