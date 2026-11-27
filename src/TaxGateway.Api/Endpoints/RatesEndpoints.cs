using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

using Npgsql;

using TaxGateway.Api.Http;
using TaxGateway.Core.Params;
using TaxGateway.Infrastructure.Params;
using TaxGateway.Infrastructure.Persistence;

namespace TaxGateway.Api.Endpoints;

/// <summary>
/// Transparência das alíquotas (D-R21) e exceções do contribuinte (D-R20).
/// <list type="bullet">
/// <item><c>/v1/publico/*</c> (anônimo): busca de município por nome e alíquotas vigentes por município e data, com
/// origem, ato legal e vigência — a mesma resolução que a simulação e a auditoria usam.</item>
/// <item><c>/v1/excecoes-aliquota</c>: liminar, regime especial ou benefício do contribuinte, com documento,
/// proposta por uma pessoa e aprovada por outra da empresa. Nunca altera o cálculo oficial.</item>
/// </list>
/// </summary>
public static class RatesEndpoints
{
    public static void MapPublicRates(this WebApplication app)
    {
        var g = app.MapGroup("/v1/publico").WithTags("Alíquotas").RequireRateLimiting(AccountEndpoints.RateLimitPolicy);
        g.MapGet("/municipios", MunicipiosAsync);
        g.MapGet("/aliquotas", AliquotasAsync);
        g.MapGet("/aliquotas/tabela", Tabela);
        g.MapGet("/atualizacoes-fiscais", AtualizacoesAsync);
    }

    /// <summary>
    /// D-R30/D-R31: linha do tempo das atualizações fiscais — alíquotas publicadas pela Receita (sincronizadas sozinhas,
    /// com a data em que passam a valer) e versões novas do motor oficial. Mais recentes primeiro.
    /// </summary>
    private static async Task<IResult> AtualizacoesAsync(int? limite, NpgsqlDataSource appDb,
        Microsoft.Extensions.Options.IOptions<TaxGateway.Infrastructure.Engines.EngineOptions> engines, CancellationToken ct)
    {
        await using var cmd = appDb.CreateCommand("""
            SELECT coalesce(json_agg(json_build_object('id', id, 'tipo', kind, 'titulo', title, 'texto', body,
                     'vigencia', effective_from, 'origem', source, 'publicado_em', created_at) ORDER BY created_at DESC), '[]')::text
              FROM (SELECT * FROM fiscal_updates ORDER BY created_at DESC LIMIT $1) f
            """);
        cmd.Parameters.Add(new() { Value = Math.Clamp(limite ?? 20, 1, 100) });
        var items = JsonNode.Parse((string)(await cmd.ExecuteScalarAsync(ct))!);
        return Results.Json(new JsonObject { ["bundle"] = engines.Value.Bundle, ["atualizacoes"] = items });
    }

    private static async Task<IResult> MunicipiosAsync(string? busca, string? uf, NpgsqlDataSource appDb, CancellationToken ct)
    {
        var termo = Normalize(busca ?? "");
        if (termo.Length < 2 && string.IsNullOrEmpty(uf))
        {
            return ApiProblem.Result(400, "ERR_VALIDATION", "Informe ao menos 2 letras do nome (busca) ou a UF");
        }

        await using var cmd = appDb.CreateCommand("""
            SELECT coalesce(json_agg(json_build_object('codigo', m.codigo, 'nome', m.nome, 'uf', u.sigla) ORDER BY
                     (m.nome_busca = $1) DESC, (m.nome_busca LIKE $1 || '%') DESC, m.nome), '[]')::text
              FROM (SELECT * FROM municipios m
                     WHERE ($1 = '' OR m.nome_busca LIKE '%' || $1 || '%')
                       AND ($2 = '' OR m.uf_codigo = (SELECT codigo FROM ufs WHERE sigla = $2))
                     ORDER BY (m.nome_busca = $1) DESC, (m.nome_busca LIKE $1 || '%') DESC, m.nome LIMIT 20) m
              JOIN ufs u ON u.codigo = m.uf_codigo
            """);
        cmd.Parameters.Add(new() { Value = termo });
        cmd.Parameters.Add(new() { Value = (uf ?? "").Trim().ToUpperInvariant() });
        return Results.Text((string)(await cmd.ExecuteScalarAsync(ct))!, "application/json");
    }

    private const string Acentos = "áàâãäéèêëíìîïóòôõöúùûüçñ";
    private const string SemAcento = "aaaaaeeeeiiiiooooouuuucn";

    /// <summary>Minúsculas sem acento (mesma forma de <c>municipios.nome_busca</c>); independe de ICU (globalização invariante).</summary>
    public static string Normalize(string s)
    {
        ArgumentNullException.ThrowIfNull(s);
        var chars = s.Trim().ToLowerInvariant().Where(c => c is not ('%' or '_')).Select(c => Acentos.IndexOf(c, StringComparison.Ordinal) is var i and >= 0 ? SemAcento[i] : c);
        return new string([.. chars]);
    }

    private static async Task<IResult> AliquotasAsync(int? municipio, string? data, IParamSnapshotProvider snapshots, NpgsqlDataSource appDb,
        TimeProvider clock, CancellationToken ct)
    {
        if (municipio is not { } m || m is < 1000000 or > 9999999)
        {
            return ApiProblem.Result(400, "ERR_VALIDATION", "Informe o município (código IBGE de 7 dígitos; busque em /v1/publico/municipios)");
        }

        var dia = DateOnly.FromDateTime(clock.GetUtcNow().AddHours(-3).DateTime);
        if (data is not null && !DateOnly.TryParseExact(data, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out dia))
        {
            return ApiProblem.Result(400, "ERR_VALIDATION", "data no formato AAAA-MM-DD");
        }

        if (dia.Year is < 2026 or > 2100)
        {
            return ApiProblem.Result(400, "ERR_VALIDATION", "data entre 2026 e 2100");
        }

        string? nome = null, uf = null;
        await using (var cmd = appDb.CreateCommand("SELECT m.nome, u.sigla FROM municipios m JOIN ufs u ON u.codigo = m.uf_codigo WHERE m.codigo = $1"))
        {
            cmd.Parameters.Add(new() { Value = m });
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct))
            {
                (nome, uf) = (r.GetString(0), r.GetString(1));
            }
        }

        if (nome is null)
        {
            return ApiProblem.Result(404, "ERR_NOT_FOUND", "Município não encontrado no cadastro do IBGE");
        }

        var snapshot = snapshots.Current;
        var tributos = new JsonArray();
        decimal total = 0;
        var completo = true;
        foreach (var (t, nomeT) in new[] { (Tributo.Cbs, "CBS"), (Tributo.IbsUf, "IBS estadual"), (Tributo.IbsMun, "IBS municipal") })
        {
            var res = snapshot.Rates.Resolve(t, m, dia);
            if (res is null)
            {
                completo = false;
                tributos.Add(new JsonObject { ["tributo"] = Code(t), ["nome"] = nomeT, ["aliquota"] = null, ["mensagem"] = "Sem alíquota publicada para esta data." });
                continue;
            }

            total += res.Aliquota;
            var row = res.Propria ?? res.Referencia;
            tributos.Add(new JsonObject
            {
                ["tributo"] = Code(t),
                ["nome"] = nomeT,
                ["aliquota"] = res.Aliquota,
                ["origem"] = res.Propria is null ? "REFERENCIA" : "PROPRIA_DO_ENTE",
                ["forma_aplicacao"] = res.Propria?.Forma?.ToString().ToUpperInvariant(),
                ["referencia"] = res.Referencia.Aliquota,
                ["ato_legal"] = row.AtoLegal,
                ["fonte"] = row.Fonte,
                ["provisoria"] = res.Provisoria,
                ["vigencia_inicio"] = row.ValidFrom.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["vigencia_fim"] = row.ValidTo?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["catalogo_id"] = row.Id,
            });
        }

        return Results.Ok(new JsonObject
        {
            ["municipio"] = new JsonObject { ["codigo"] = m, ["nome"] = nome, ["uf"] = uf },
            ["data"] = dia.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["tributos"] = tributos,
            ["total"] = completo ? total : null,
            ["observacao"] = dia.Year < 2027
                ? "2026 é o ano de teste: CBS 0,9% e IBS 0,1%, compensáveis com PIS/Cofins (LC 214/2025, art. 346)."
                : "Alíquotas nominais; reduções por cClassTrib (ex.: 60% na cesta básica) são aplicadas pelo motor oficial no cálculo.",
            ["parametros_snapshot"] = snapshot.Id,
        });
    }

    /// <summary>Tabela pública: referências nacionais (todas as vigências), IS por produto e quantos entes têm alíquota própria.</summary>
    private static IResult Tabela(IParamSnapshotProvider snapshots)
    {
        var all = snapshots.Current.Rates.All.ToList();
        static JsonObject Row(RateRow r) => new()
        {
            ["tributo"] = Code(r.Tributo),
            ["aliquota"] = r.Aliquota,
            ["produto"] = r.Produto,
            ["vigencia_inicio"] = r.ValidFrom.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["vigencia_fim"] = r.ValidTo?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["ato_legal"] = r.AtoLegal,
            ["fonte"] = r.Fonte,
            ["provisoria"] = r.Provisoria,
        };
        return Results.Ok(new JsonObject
        {
            ["referencias"] = new JsonArray([.. all.Where(r => r.EnteCodigo is null && r.Produto is null).OrderBy(r => r.Tributo).ThenBy(r => r.ValidFrom).Select(r => (JsonNode)Row(r))]),
            ["imposto_seletivo"] = new JsonArray([.. all.Where(r => r.Produto is not null).OrderBy(r => r.Produto).ThenBy(r => r.ValidFrom).Select(r => (JsonNode)Row(r))]),
            ["entes_com_aliquota_propria"] = new JsonObject
            {
                ["ufs"] = all.Where(r => r.EnteCodigo is { Length: 2 }).Select(r => r.EnteCodigo).Distinct().Count(),
                ["municipios"] = all.Where(r => r.EnteCodigo is { Length: 7 }).Select(r => r.EnteCodigo).Distinct().Count(),
            },
            ["parametros_snapshot"] = snapshots.Current.Id,
        });
    }

    /// <summary>D-R22: dias de cobertura da referência de cada tributo a partir de hoje (36.500 = sem fim publicado).</summary>
    public static IEnumerable<(string Tributo, int Dias, DateOnly? Ate)> Coverage(ParamSnapshot snapshot, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(clock);
        var today = DateOnly.FromDateTime(clock.GetUtcNow().AddHours(-3).DateTime);
        foreach (var t in new[] { Tributo.Cbs, Tributo.IbsUf, Tributo.IbsMun })
        {
            var until = snapshot.Rates.CoveredUntil(t, today);
            yield return until switch
            {
                null => (Code(t), 0, today),
                { } u when u == DateOnly.MaxValue => (Code(t), 36500, null),
                { } u => (Code(t), u.DayNumber - today.DayNumber, u),
            };
        }
    }

    public static void MapAdminCoverage(this RouteGroupBuilder admin) =>
        admin.MapGet("/params/cobertura", (IParamSnapshotProvider snapshots, TimeProvider clock) => Results.Ok(new JsonObject
        {
            ["tributos"] = new JsonArray([.. Coverage(snapshots.Current, clock).Select(c => (JsonNode)new JsonObject
            {
                ["tributo"] = c.Tributo, ["dias"] = c.Dias, ["coberto_ate"] = c.Ate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["alerta"] = c.Dias < 90,
            })]),
        })).WithMetadata(new RequiredScope(AdminEndpoints.Fiscal));

    private static string Code(Tributo t) => t switch
    {
        Tributo.Cbs => "CBS",
        Tributo.IbsUf => "IBS_UF",
        Tributo.IbsMun => "IBS_MUN",
        _ => "IS",
    };

    // ------------------------------------------------------------------------------------------- exceções (D-R20)

    private static readonly string[] Motivos = ["LIMINAR", "DECISAO_JUDICIAL", "REGIME_ESPECIAL", "BENEFICIO_FISCAL", "OUTRO"];

    public static RouteGroupBuilder MapRateExceptions(this RouteGroupBuilder v1)
    {
        var read = new RequiredScope(Scopes.AuditoriaRead);
        var write = new RequiredScope(Scopes.CnpjsManage);
        v1.MapGet("/excecoes-aliquota", ListAsync).WithMetadata(read).ExcludeFromDescription();
        v1.MapGet("/excecoes-aliquota/{id:guid}/documento", DocumentAsync).WithMetadata(read).ExcludeFromDescription();
        v1.MapPost("/excecoes-aliquota", ProposeAsync).WithMetadata(write).ExcludeFromDescription();
        v1.MapPost("/excecoes-aliquota/{id:guid}/{acao}", DecideAsync).WithMetadata(write).ExcludeFromDescription();
        return v1;
    }

    private static async Task<IResult> ListAsync(HttpContext http, ITenantDb db)
    {
        var json = await db.InTenantAsync(http.GetCaller().Identity.Tenant, async (s, ct) =>
        {
            await using var cmd = s.CreateCommand("""
                SELECT coalesce(json_agg(json_build_object('id', e.id, 'cnpj', e.cnpj, 'tributo', e.tributo, 'ente_codigo', e.ente_codigo,
                         'produto', e.produto, 'aliquota', e.aliquota, 'vigencia_inicio', e.valid_from, 'vigencia_fim', e.valid_to,
                         'motivo', e.motivo, 'processo', e.processo, 'documento', e.documento_nome, 'situacao', e.status,
                         'proposta_por', p.name, 'decidida_por', d.name, 'decidida_em', e.decided_at, 'criada_em', e.created_at)
                         ORDER BY e.created_at DESC), '[]')::text
                  FROM tenant_rate_exceptions e
                  LEFT JOIN users p ON p.id = e.proposed_by LEFT JOIN users d ON d.id = e.decided_by
                """);
            return (string)(await cmd.ExecuteScalarAsync(ct))!;
        }, http.RequestAborted);
        return Results.Text($$"""{"excecoes":{{json}}}""", "application/json");
    }

    private static async Task<IResult> DocumentAsync(HttpContext http, Guid id, ITenantDb db)
    {
        var doc = await db.InTenantAsync(http.GetCaller().Identity.Tenant, async (s, ct) =>
        {
            await using var cmd = s.CreateCommand("SELECT documento_nome, documento FROM tenant_rate_exceptions WHERE id = $1");
            cmd.Parameters.Add(new() { Value = id });
            await using var r = await cmd.ExecuteReaderAsync(ct);
            return await r.ReadAsync(ct) ? (r.GetString(0), (byte[])r[1]) : default((string, byte[])?);
        }, http.RequestAborted);
        return doc is { } d ? Results.File(d.Item2, "application/pdf", d.Item1) : ApiProblem.Result(404, "ERR_NOT_FOUND", "Exceção não encontrada");
    }

    private static async Task<IResult> ProposeAsync(HttpContext http, ITenantDb db)
    {
        if (SessionAuthenticator.User(http) is not { } user)
        {
            return ApiProblem.Result(403, "ERR_SESSAO_NECESSARIA", "Exceções são cadastradas por pessoas no portal", "Entre no portal: a proposta e a aprovação ficam na trilha com o nome de quem fez.");
        }

        var b = await JsonNode.ParseAsync(http.Request.Body, cancellationToken: http.RequestAborted) as JsonObject;
        var errors = new List<string>();
        string? S(string k) => b?[k] is JsonValue v && v.TryGetValue<string>(out var x) ? x.Trim() : null;
        var tributo = S("tributo");
        var ente = S("ente_codigo");
        var produto = S("produto");
        var motivo = S("motivo");
        var processo = S("processo");
        var nomeDoc = S("documento_nome") ?? "documento.pdf";
        decimal? aliquota = b?["aliquota"] is JsonValue av && av.TryGetValue<decimal>(out var a) ? a : null;
        DateOnly? from = DateOnly.TryParseExact(S("vigencia_inicio"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var f) ? f : null;
        DateOnly? to = DateOnly.TryParseExact(S("vigencia_fim"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : null;
        string? cnpj = S("cnpj") is { Length: > 0 } rawCnpj ? (TaxGateway.Core.Fiscal.Cnpj.TryNormalize(rawCnpj, out var nc) ? nc : "") : null;
        byte[]? pdf = null;
        try
        {
            pdf = S("documento_base64") is { } b64 ? Convert.FromBase64String(b64) : null;
        }
        catch (FormatException)
        {
            errors.Add("documento_base64 inválido");
        }

        if (tributo is not ("CBS" or "IBS_UF" or "IBS_MUN" or "IS")) errors.Add("tributo: CBS, IBS_UF, IBS_MUN ou IS");
        if (aliquota is not (>= 0 and <= 100)) errors.Add("aliquota entre 0 e 100");
        if (from is null) errors.Add("vigencia_inicio (AAAA-MM-DD)");
        if (to is { } tt && from is { } ff && tt <= ff) errors.Add("vigencia_fim depois do início");
        if (motivo is null || !Motivos.Contains(motivo)) errors.Add($"motivo: {string.Join(", ", Motivos)}");
        if (processo is not { Length: >= 3 and <= 200 }) errors.Add("processo ou ato (3 a 200 caracteres)");
        if (cnpj == "") errors.Add("cnpj inválido");
        if (ente is not null && (tributo == "IBS_UF" ? ente.Length != 2 : tributo == "IBS_MUN" ? ente.Length != 7 : true)) errors.Add("ente_codigo: 2 dígitos (UF) para IBS_UF, 7 (município) para IBS_MUN; CBS e IS sem ente");
        if (produto is not null && !System.Text.RegularExpressions.Regex.IsMatch(produto, "^(NCM:[0-9]{2,8}|CCLASS:[0-9]{6})$")) errors.Add("produto: NCM:<prefixo> ou CCLASS:<cClassTrib>");
        if (pdf is null || pdf.Length is 0 or > 5 * 1024 * 1024 || !pdf.AsSpan().StartsWith("%PDF"u8)) errors.Add("documento_base64: PDF de até 5 MB (decisão, ato ou termo do regime)");
        if (errors.Count > 0)
        {
            return ApiProblem.Result(400, "ERR_VALIDATION", "Exceção inválida", string.Join("; ", errors));
        }

        var id = Guid.CreateVersion7();
        await db.InTenantAsync(http.GetCaller().Identity.Tenant, async (s, ct) =>
        {
            await using var cmd = s.CreateCommand("""
                INSERT INTO tenant_rate_exceptions (id, tenant_id, cnpj, tributo, ente_codigo, produto, aliquota, valid_from, valid_to, motivo, processo,
                                                    documento_nome, documento, documento_sha256, proposed_by)
                VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15)
                """);
            cmd.Parameters.Add(new() { Value = id });
            cmd.Parameters.Add(new() { Value = http.GetCaller().Identity.Tenant.Value });
            cmd.Parameters.Add(new() { Value = (object?)cnpj ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
            cmd.Parameters.Add(new() { Value = tributo! });
            cmd.Parameters.Add(new() { Value = (object?)ente ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
            cmd.Parameters.Add(new() { Value = (object?)produto ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
            cmd.Parameters.Add(new() { Value = aliquota!.Value });
            cmd.Parameters.Add(new() { Value = from!.Value });
            cmd.Parameters.Add(new() { Value = (object?)to ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Date });
            cmd.Parameters.Add(new() { Value = motivo! });
            cmd.Parameters.Add(new() { Value = processo! });
            cmd.Parameters.Add(new() { Value = Path.GetFileName(nomeDoc)[..Math.Min(120, Path.GetFileName(nomeDoc).Length)] });
            cmd.Parameters.Add(new() { Value = pdf! });
            cmd.Parameters.Add(new() { Value = SHA256.HashData(pdf!) });
            cmd.Parameters.Add(new() { Value = user.Session.UserId });
            return await cmd.ExecuteNonQueryAsync(ct);
        }, http.RequestAborted);
        return Results.Created($"/v1/excecoes-aliquota/{id}", new JsonObject
        {
            ["id"] = id,
            ["situacao"] = "PROPOSTA",
            ["mensagem"] = "Proposta registrada. Outra pessoa da empresa (dono, administrador ou fiscal) precisa aprovar para ela passar a valer.",
        });
    }

    private static async Task<IResult> DecideAsync(HttpContext http, Guid id, string acao, ITenantDb db, RateExceptionService exceptions)
    {
        if (SessionAuthenticator.User(http) is not { } user)
        {
            return ApiProblem.Result(403, "ERR_SESSAO_NECESSARIA", "A decisão é de uma pessoa no portal");
        }

        var (from, to) = acao switch
        {
            "aprovar" => ("PROPOSTA", "ATIVA"),
            "recusar" => ("PROPOSTA", "RECUSADA"),
            "revogar" => ("ATIVA", "REVOGADA"),
            _ => (null, null),
        };
        if (from is null)
        {
            return ApiProblem.Result(404, "ERR_NOT_FOUND", "Ação inválida (aprovar, recusar ou revogar)");
        }

        var tenant = http.GetCaller().Identity.Tenant;
        var result = await db.InTenantAsync(tenant, async (s, ct) =>
        {
            await using var cmd = s.CreateCommand("""
                UPDATE tenant_rate_exceptions SET status = $3, decided_by = CASE WHEN $3 = 'REVOGADA' THEN decided_by ELSE $2 END,
                       decided_at = now()
                 WHERE id = $1 AND status = $4 AND ($3 = 'REVOGADA' OR proposed_by <> $2)
                RETURNING status
                """);
            cmd.Parameters.Add(new() { Value = id });
            cmd.Parameters.Add(new() { Value = user.Session.UserId });
            cmd.Parameters.Add(new() { Value = to! });
            cmd.Parameters.Add(new() { Value = from });
            return await cmd.ExecuteScalarAsync(ct) as string;
        }, http.RequestAborted);
        if (result is null)
        {
            return ApiProblem.Result(409, "ERR_CONFLICT", "Não foi possível",
                acao == "revogar" ? "Só exceções ativas podem ser revogadas." : "Só propostas pendentes, e por outra pessoa que não quem propôs (duas pessoas).");
        }

        exceptions.Invalidate(tenant);
        return Results.Ok(new JsonObject { ["id"] = id, ["situacao"] = result });
    }
}
