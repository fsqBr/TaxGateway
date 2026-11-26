using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

using Npgsql;

using NpgsqlTypes;

using TaxGateway.Api.Http;
using TaxGateway.Infrastructure.Persistence;

namespace TaxGateway.Api.Endpoints;

/// <summary>
/// Histórico de documentos auditados (D-J01; revisão de produto L4 e UX C1): antes não existia GET de auditoria — o DF-e
/// auditava sozinho, mas quem não tinha webhook nunca via o resultado. Listagem paginada por cursor com filtros, detalhe
/// com a comparação por item e exportação CSV. Lê só o que já está gravado (metadados fiscais, sem XML nem CPF).
/// </summary>
public static class DocumentEndpoints
{
    public const int MaxRangeDays = 400;
    public const int MaxExportRows = 50_000;
    private static readonly string[] Statuses = ["CONFORME", "DIVERGENTE", "INDICIO", "SCHEMA_ERROR", "ASSINATURA_INVALIDA", "INDETERMINADO"];

    public static RouteGroupBuilder MapDocuments(this RouteGroupBuilder v1)
    {
        var read = new RequiredScope(Scopes.AuditoriaRead, Scopes.AuditoriaWrite);
        v1.MapGet("/auditoria/documentos", ListAsync).WithMetadata(read);
        v1.MapGet("/auditoria/documentos/exportar", ExportAsync).WithMetadata(read);
        v1.MapGet("/auditoria/documentos/{id:guid}", DetailAsync).WithMetadata(read);
        v1.MapGet("/auditoria/resumo", SummaryAsync).WithMetadata(read);
        v1.MapGet("/cnpjs/{cnpj}/dfe/documentos", DfeListAsync).WithMetadata(new RequiredScope(Scopes.DfeManage, Scopes.AuditoriaRead));
        return v1;
    }

    /// <summary>Filtros comuns da listagem e da exportação, validados (entrada inválida = 400, nunca 500).</summary>
    public sealed record Filter(DateOnly De, DateOnly Ate, string[] Status, string? Direcao, string? Cnpj, string? Chave, Guid? Lote);

    public static (Filter? Filter, IResult? Error) ParseFilter(HttpRequest req, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(req);
        var q = req.Query;
        DateOnly? D(string name) => DateOnly.TryParseExact(q[name].ToString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
        if ((q.ContainsKey("de") && D("de") is null) || (q.ContainsKey("ate") && D("ate") is null))
        {
            return (null, ApiProblem.Result(400, "ERR_VALIDATION", "Datas no formato AAAA-MM-DD", "Use de=2026-01-01&ate=2026-01-31."));
        }

        var ate = D("ate") ?? today;
        var de = D("de") ?? ate.AddDays(-90);
        if (de > ate || ate.DayNumber - de.DayNumber > MaxRangeDays)
        {
            return (null, ApiProblem.Result(400, "ERR_VALIDATION", "Período inválido", $"'de' deve ser anterior a 'ate' e o período ter até {MaxRangeDays} dias."));
        }

        var status = q["status"].ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => s.ToUpperInvariant()).Distinct(StringComparer.Ordinal).ToArray();
        if (status.Except(Statuses, StringComparer.Ordinal).FirstOrDefault() is { } bad)
        {
            return (null, ApiProblem.Result(400, "ERR_VALIDATION", "Situação desconhecida", $"'{bad}' não existe. Use: {string.Join(", ", Statuses)}."));
        }

        var direcao = q["direcao"].ToString().ToUpperInvariant() switch
        {
            "" => null,
            "SAIDA" or "OUTBOUND" => "OUTBOUND",
            "ENTRADA" or "INBOUND" => "INBOUND",
            _ => "?",
        };
        if (direcao == "?")
        {
            return (null, ApiProblem.Result(400, "ERR_VALIDATION", "direcao deve ser 'saida' ou 'entrada'"));
        }

        var cnpj = q["cnpj"].ToString();
        if (cnpj.Length > 0 && !IsAsciiDigits(cnpj, 14))
        {
            return (null, ApiProblem.Result(400, "ERR_VALIDATION", "CNPJ com 14 dígitos, sem pontuação"));
        }

        var chave = q["chave"].ToString();
        if (chave.Length > 0 && !IsAsciiDigits(chave, 44))
        {
            return (null, ApiProblem.Result(400, "ERR_VALIDATION", "Chave de acesso com 44 dígitos"));
        }

        Guid? lote = null;
        if (q["lote"].ToString() is { Length: > 0 } l)
        {
            if (!Guid.TryParse(l, out var g))
            {
                return (null, ApiProblem.Result(400, "ERR_VALIDATION", "lote deve ser o batch_id (UUID)"));
            }

            lote = g;
        }

        return (new Filter(de, ate, status, direcao, cnpj.Length > 0 ? cnpj : null, chave.Length > 0 ? chave : null, lote), null);
    }

    /// <summary>Só '0'–'9' ASCII (char.IsDigit aceita dígitos de outros alfabetos, como os arábico-índicos).</summary>
    public static bool IsAsciiDigits(string s, int length) => s.Length == length && s.All(char.IsAsciiDigit);

    private static void Where(NpgsqlCommand cmd, StringBuilder sql, Filter f)
    {
        void P(object value, NpgsqlDbType? type = null)
        {
            var p = new NpgsqlParameter { Value = value };
            if (type is { } t)
            {
                p.NpgsqlDbType = t;
            }

            cmd.Parameters.Add(p);
        }

        // Poda de partição pela competência e corte exato pela data de criação (meia-noite de Brasília = 03:00 UTC).
        P(new DateOnly(f.De.Year, f.De.Month, 1));
        P(new DateOnly(f.Ate.Year, f.Ate.Month, 1));
        P(f.De);
        P(f.Ate.AddDays(1));
        sql.Append(" WHERE f.billing_month BETWEEN $1 AND $2")
           .Append(" AND f.created_at >= (($3::date)::timestamp + interval '3 hours') AT TIME ZONE 'UTC'")
           .Append(" AND f.created_at < (($4::date)::timestamp + interval '3 hours') AT TIME ZONE 'UTC'");
        if (f.Status.Length > 0)
        {
            P(f.Status, NpgsqlDbType.Array | NpgsqlDbType.Text);
            sql.Append(CultureInfo.InvariantCulture, $" AND f.audit_status = ANY(${cmd.Parameters.Count})");
        }

        if (f.Direcao is not null)
        {
            P(f.Direcao);
            sql.Append(CultureInfo.InvariantCulture, $" AND f.kind = ${cmd.Parameters.Count}");
        }

        if (f.Cnpj is not null)
        {
            P(f.Cnpj);
            sql.Append(CultureInfo.InvariantCulture, $" AND (f.cnpj_subject = ${cmd.Parameters.Count} OR f.cnpj_counterparty = ${cmd.Parameters.Count})");
        }

        if (f.Chave is not null)
        {
            P(f.Chave);
            sql.Append(CultureInfo.InvariantCulture, $" AND f.access_key = ${cmd.Parameters.Count}");
        }

        if (f.Lote is not null)
        {
            P(f.Lote.Value);
            sql.Append(CultureInfo.InvariantCulture, $" AND f.batch_id = ${cmd.Parameters.Count}");
        }
    }

    private const string Columns = """
        f.id, f.access_key, f.kind, f.model, f.audit_status, f.cnpj_subject, f.cnpj_counterparty, f.difference_total,
        f.credit_potential, f.credit_status, jsonb_array_length(f.findings), f.batch_id, f.billing_month, f.created_at,
        f.reprocess_of IS NOT NULL, f.xml_storage_key IS NOT NULL
        """;

    private static JsonObject Row(NpgsqlDataReader r) => new()
    {
        ["id"] = r.GetGuid(0),
        ["chave_acesso"] = r.IsDBNull(1) ? null : r.GetString(1),
        ["direcao"] = r.GetString(2) == "INBOUND" ? "entrada" : "saida",
        ["modelo"] = r.GetString(3),
        ["status"] = r.GetString(4),
        ["cnpj"] = r.GetString(5),
        ["cnpj_contraparte"] = r.IsDBNull(6) ? null : r.GetString(6),
        ["diferenca"] = r.GetDecimal(7),
        ["credito_potencial"] = r.GetDecimal(8),
        ["credito_status"] = r.IsDBNull(9) ? null : r.GetString(9),
        ["ocorrencias"] = r.GetInt32(10),
        ["lote_id"] = r.IsDBNull(11) ? null : r.GetGuid(11),
        ["competencia"] = r.GetFieldValue<DateOnly>(12).ToString("yyyy-MM", CultureInfo.InvariantCulture),
        ["criado_em"] = r.GetFieldValue<DateTimeOffset>(13),
        ["reprocessamento"] = r.GetBoolean(14),
        ["xml_armazenado"] = r.GetBoolean(15),
    };

    /// <summary>"Hoje" em Brasília: UTC−03:00 fixo (sem horário de verão desde 2019; a imagem chiseled pode não ter tzdata).</summary>
    private static DateOnly Today(TimeProvider clock) => DateOnly.FromDateTime(clock.GetUtcNow().ToOffset(TimeSpan.FromHours(-3)).DateTime);

    private static async Task<IResult> ListAsync(HttpContext http, ITenantDb db, TimeProvider clock, string? cursor, int? limite)
    {
        var (f, error) = ParseFilter(http.Request, Today(clock));
        if (error is not null)
        {
            return error;
        }

        var limit = Math.Clamp(limite ?? 50, 1, 200);
        (DateTimeOffset At, Guid Id)? after = null;
        if (!string.IsNullOrEmpty(cursor))
        {
            after = DecodeCursor(cursor);
            if (after is null)
            {
                return ApiProblem.Result(400, "ERR_VALIDATION", "cursor inválido", "Use o valor de 'proximo_cursor' da página anterior.");
            }
        }

        var caller = http.GetCaller();
        var page = await db.InTenantAsync(caller.Identity.Tenant, async (s, ct) =>
        {
            await using var cmd = s.CreateCommand("");
            var sql = new StringBuilder($"SELECT {Columns} FROM fiscal_documents f");
            Where(cmd, sql, f!);
            if (after is { } a)
            {
                cmd.Parameters.Add(new() { Value = a.At });
                cmd.Parameters.Add(new() { Value = a.Id });
                sql.Append(CultureInfo.InvariantCulture, $" AND (f.created_at, f.id) < (${cmd.Parameters.Count - 1}, ${cmd.Parameters.Count})");
            }

            cmd.Parameters.Add(new() { Value = limit + 1 });
            sql.Append(CultureInfo.InvariantCulture, $" ORDER BY f.created_at DESC, f.id DESC LIMIT ${cmd.Parameters.Count}");
            cmd.CommandText = sql.ToString();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            var rows = new List<(JsonObject Json, DateTimeOffset At, Guid Id)>();
            while (await r.ReadAsync(ct))
            {
                rows.Add((Row(r), r.GetFieldValue<DateTimeOffset>(13), r.GetGuid(0)));
            }

            return rows;
        }, http.RequestAborted);

        var next = page.Count > limit ? EncodeCursor(page[limit - 1].At, page[limit - 1].Id) : null;
        return Results.Ok(new JsonObject
        {
            ["documentos"] = new JsonArray([.. page.Take(limit).Select(x => (JsonNode)x.Json)]),
            ["proximo_cursor"] = next,
            ["periodo"] = new JsonObject { ["de"] = f!.De.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), ["ate"] = f.Ate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) },
        });
    }

    private static string EncodeCursor(DateTimeOffset at, Guid id) =>
        Convert.ToBase64String(Encoding.ASCII.GetBytes($"{at.UtcTicks}|{id:N}")).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static (DateTimeOffset, Guid)? DecodeCursor(string cursor)
    {
        try
        {
            var b64 = cursor.Replace('-', '+').Replace('_', '/');
            b64 += new string('=', (4 - (b64.Length % 4)) % 4);
            var parts = Encoding.ASCII.GetString(Convert.FromBase64String(b64)).Split('|');
            return parts.Length == 2 && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
                   && ticks > 0 && ticks < DateTimeOffset.MaxValue.UtcTicks && Guid.TryParseExact(parts[1], "N", out var id)
                ? (new DateTimeOffset(ticks, TimeSpan.Zero), id)
                : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static async Task<IResult> DetailAsync(HttpContext http, Guid id, ITenantDb db)
    {
        var caller = http.GetCaller();
        var found = await db.InTenantAsync(caller.Identity.Tenant, async (s, ct) =>
        {
            await using var cmd = s.CreateCommand($"SELECT {Columns}, f.recalculated->>'resposta', f.declared::text FROM fiscal_documents f WHERE f.id = $1");
            cmd.Parameters.Add(new() { Value = id });
            await using var r = await cmd.ExecuteReaderAsync(ct);
            return await r.ReadAsync(ct)
                ? (Row(r), r.IsDBNull(16) ? null : r.GetString(16), r.GetString(17))
                : default((JsonObject, string?, string)?);
        }, http.RequestAborted);
        if (found is not { } d)
        {
            return ApiProblem.Result(404, "ERR_NOT_FOUND", "Documento não encontrado", "Confira o id na listagem /v1/auditoria/documentos.");
        }

        var declared = JsonNode.Parse(d.Item3)!.AsObject();
        var resposta = d.Item2 is null ? null : JsonNode.Parse(d.Item2);
        return Results.Ok(new JsonObject
        {
            ["documento"] = d.Item1,
            ["itens_declarados"] = declared["itens"]?.DeepClone(),
            ["data_emissao"] = declared["data_emissao"]?.DeepClone(),
            ["municipio_incidencia"] = declared["municipio_incidencia"]?.DeepClone(),
            ["auditoria"] = resposta,
        });
    }

    private static async Task<IResult> SummaryAsync(HttpContext http, ITenantDb db, TimeProvider clock)
    {
        var (f, error) = ParseFilter(http.Request, Today(clock));
        if (error is not null)
        {
            return error;
        }

        var caller = http.GetCaller();
        var json = await db.InTenantAsync(caller.Identity.Tenant, async (s, ct) =>
        {
            await using var cmd = s.CreateCommand("");
            var sql = new StringBuilder("SELECT f.audit_status, count(*), COALESCE(sum(f.difference_total), 0), COALESCE(sum(f.credit_potential), 0) FROM fiscal_documents f");
            Where(cmd, sql, f!);
            sql.Append(" GROUP BY f.audit_status");
            cmd.CommandText = sql.ToString();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            var porStatus = new JsonObject();
            long total = 0;
            decimal dif = 0, cred = 0;
            while (await r.ReadAsync(ct))
            {
                porStatus[r.GetString(0)] = r.GetInt64(1);
                total += r.GetInt64(1);
                dif += r.GetDecimal(2);
                cred += r.GetDecimal(3);
            }

            return new JsonObject { ["total"] = total, ["por_status"] = porStatus, ["diferenca_total"] = dif, ["credito_potencial_total"] = cred };
        }, http.RequestAborted);
        json["periodo"] = new JsonObject { ["de"] = f!.De.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), ["ate"] = f.Ate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) };
        return Results.Ok(json);
    }

    /// <summary>
    /// CSV (separador ';' e vírgula decimal, como o Excel em pt-BR abre direto; UTF-8 com BOM). nivel=documento (padrão)
    /// ou nivel=ocorrencia (uma linha por achado: regra, item, campo, declarado, referência, fundamento).
    /// </summary>
    private static async Task ExportAsync(HttpContext http, ITenantDb db, TimeProvider clock, string? nivel)
    {
        var (f, error) = ParseFilter(http.Request, Today(clock));
        if (error is null && nivel is not (null or "documento" or "ocorrencia"))
        {
            error = ApiProblem.Result(400, "ERR_VALIDATION", "nivel deve ser 'documento' ou 'ocorrencia'");
        }

        if (error is not null)
        {
            await error.ExecuteAsync(http);
            return;
        }

        var porOcorrencia = nivel == "ocorrencia";
        http.Response.ContentType = "text/csv; charset=utf-8";
        http.Response.Headers.ContentDisposition = $"attachment; filename=\"auditorias-{f!.De:yyyyMMdd}-{f.Ate:yyyyMMdd}{(porOcorrencia ? "-ocorrencias" : "")}.csv\"";
        var caller = http.GetCaller();
        await using var writer = new StreamWriter(http.Response.Body, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        await writer.WriteLineAsync(porOcorrencia
            ? "chave_acesso;direcao;status;criado_em;regra;natureza;severidade;item;campo_xml;declarado;referencia;impacto_estimado;mensagem;fundamentacao"
            : "chave_acesso;direcao;modelo;status;cnpj;cnpj_contraparte;diferenca;credito_potencial;ocorrencias;competencia;criado_em;lote_id");
        await db.InTenantAsync(caller.Identity.Tenant, async (s, ct) =>
        {
            await using var cmd = s.CreateCommand("");
            var sql = new StringBuilder(porOcorrencia
                ? "SELECT f.access_key, f.kind, f.audit_status, f.created_at, o FROM fiscal_documents f CROSS JOIN LATERAL jsonb_array_elements(f.findings) o"
                : $"SELECT {Columns} FROM fiscal_documents f");
            Where(cmd, sql, f);
            cmd.Parameters.Add(new() { Value = MaxExportRows });
            sql.Append(CultureInfo.InvariantCulture, $" ORDER BY f.created_at DESC, f.id DESC LIMIT ${cmd.Parameters.Count}");
            cmd.CommandText = sql.ToString();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                string line;
                if (porOcorrencia)
                {
                    var o = JsonNode.Parse(r.GetString(4))!;
                    string S(string k) => o[k]?.ToString() ?? "";
                    line = string.Join(';', Csv(r.IsDBNull(0) ? "" : r.GetString(0)), r.GetString(1) == "INBOUND" ? "entrada" : "saida", r.GetString(2),
                        r.GetFieldValue<DateTimeOffset>(3).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                        Csv(S("regra")), Csv(S("natureza")), Csv(S("severidade")), Csv(S("numero_item")), Csv(S("campo_xml")),
                        Csv(S("valor_declarado")), Csv(S("valor_referencia")), Csv(Dec(o["impacto_financeiro_estimado"] ?? o["impacto_financeiro"])),
                        Csv(S("mensagem")), Csv(S("fundamentacao")));
                }
                else
                {
                    line = string.Join(';', Csv(r.IsDBNull(1) ? "" : r.GetString(1)), r.GetString(2) == "INBOUND" ? "entrada" : "saida", r.GetString(3),
                        r.GetString(4), r.GetString(5), r.IsDBNull(6) ? "" : r.GetString(6), Br(r.GetDecimal(7)), Br(r.GetDecimal(8)),
                        r.GetInt32(10).ToString(CultureInfo.InvariantCulture), r.GetFieldValue<DateOnly>(12).ToString("yyyy-MM", CultureInfo.InvariantCulture),
                        r.GetFieldValue<DateTimeOffset>(13).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), r.IsDBNull(11) ? "" : r.GetGuid(11).ToString());
                }

                await writer.WriteLineAsync(line.AsMemory(), ct);
            }

            return 0;
        }, http.RequestAborted);
    }

    private static string Br(decimal v) => v.ToString("0.00", CultureInfo.InvariantCulture).Replace('.', ',');

    private static string Dec(JsonNode? n) => n is null ? "" : decimal.TryParse(n.ToString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? Br(d) : "";

    /// <summary>Campo CSV seguro: aspas quando preciso e sem injeção de fórmula (=, +, -, @ no início viram texto).</summary>
    public static string Csv(string v)
    {
        if (v.Length > 0 && v[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            v = "'" + v;
        }

        return v.IndexOfAny([';', '"', '\n', '\r']) >= 0 ? "\"" + v.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : v;
    }

    private static async Task<IResult> DfeListAsync(HttpContext http, string cnpj, ITenantDb db, string? status, int? limite)
    {
        if (!IsAsciiDigits(cnpj, 14))
        {
            return ApiProblem.Result(400, "ERR_VALIDATION", "CNPJ com 14 dígitos, sem pontuação");
        }

        var caller = http.GetCaller();
        var limit = Math.Clamp(limite ?? 100, 1, 500);
        var list = await db.InTenantAsync(caller.Identity.Tenant, async (s, ct) =>
        {
            await using var cmd = s.CreateCommand("""
                SELECT nsu, schema, kind, access_key, status, audit_status, detail, received_at FROM dfe_documents
                 WHERE cnpj = $1 AND ($2::text IS NULL OR status = $2) ORDER BY received_at DESC, nsu DESC LIMIT $3
                """);
            cmd.Parameters.Add(new() { Value = cnpj });
            cmd.Parameters.Add(new() { Value = (object?)status?.ToUpperInvariant() ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
            cmd.Parameters.Add(new() { Value = limit });
            await using var r = await cmd.ExecuteReaderAsync(ct);
            var arr = new JsonArray();
            while (await r.ReadAsync(ct))
            {
                arr.Add(new JsonObject
                {
                    ["nsu"] = r.GetInt64(0),
                    ["schema"] = r.GetString(1),
                    ["tipo"] = r.GetString(2),
                    ["chave_acesso"] = r.IsDBNull(3) ? null : r.GetString(3),
                    ["situacao"] = r.GetString(4),
                    ["status_auditoria"] = r.IsDBNull(5) ? null : r.GetString(5),
                    ["detalhe"] = r.IsDBNull(6) ? null : r.GetString(6),
                    ["recebido_em"] = r.GetFieldValue<DateTimeOffset>(7),
                });
            }

            return arr;
        }, http.RequestAborted);
        return Results.Ok(new JsonObject { ["documentos"] = list });
    }
}
