using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Npgsql;

using StackExchange.Redis;

using TaxGateway.Core.Params;

namespace TaxGateway.Infrastructure.Params;

/// <summary>
/// Snapshot imutável dos parâmetros globais em memória (SDD §15.1, itens 4 e 5; D-205):
/// carrega catálogo, planos e roteamento; calcula o SHA-256 do conteúdo canônico; registra o id em
/// <c>param_snapshots</c>; e troca a referência de forma atômica quando algo muda (pub/sub
/// <c>params:changed</c> + verificação periódica de 30 s como rede de segurança).
/// </summary>
public sealed partial class ParamSnapshotService(NpgsqlDataSource db, IConnectionMultiplexer valkey, ILogger<ParamSnapshotService> log)
    : BackgroundService, IParamSnapshotProvider
{
    public const string ChangedChannel = "params:changed";
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);
    private ParamSnapshot? _current;
    private readonly SemaphoreSlim _reloadGate = new(1, 1);

    public ParamSnapshot Current => Volatile.Read(ref _current) ?? throw new InvalidOperationException("Parâmetros ainda não carregados.");

    public bool IsLoaded => Volatile.Read(ref _current) is not null;

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await ReloadAsync(cancellationToken).ConfigureAwait(false);   // a API não sobe sem parâmetros
        await base.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await valkey.GetSubscriber().SubscribeAsync(RedisChannel.Literal(ChangedChannel),
                (_, _) => _ = ReloadSafeAsync(CancellationToken.None)).ConfigureAwait(false);
        }
        catch (RedisException ex)
        {
            LogReloadFailed(log, ex);   // segue só com a verificação periódica
        }

        using var timer = new PeriodicTimer(PollInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            await ReloadSafeAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    public async Task<ParamSnapshot> ReloadAsync(CancellationToken ct)
    {
        await _reloadGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var (canonical, rates, plans, routing, credit) = await LoadAsync(ct).ConfigureAwait(false);
            var sha = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
            var current = Volatile.Read(ref _current);
            if (current?.ContentSha256 == sha)
            {
                return current;
            }

            await using var cmd = db.CreateCommand("SELECT app.register_param_snapshot($1)");
            cmd.Parameters.Add(new() { Value = sha });
            var id = (long)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
            var snapshot = new ParamSnapshot(id, sha, new RateCatalog(rates), plans, routing, DateTimeOffset.UtcNow, credit);
            Volatile.Write(ref _current, snapshot);
            LogLoaded(log, id, rates.Count, plans.Count);
            return snapshot;
        }
        finally
        {
            _reloadGate.Release();
        }
    }

    private async Task ReloadSafeAsync(CancellationToken ct)
    {
        try
        {
            await ReloadAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or TimeoutException)
        {
            LogReloadFailed(log, ex);   // mantém o snapshot anterior
        }
    }

    private async Task<(string Canonical, List<RateRow> Rates, Dictionary<string, PlanLimits> Plans, List<EngineRoute> Routing, List<CreditRule> Credit)> LoadAsync(CancellationToken ct)
    {
        var sb = new StringBuilder();
        var rates = new List<RateRow>();
        var plans = new Dictionary<string, PlanLimits>(StringComparer.Ordinal);
        var routing = new List<EngineRoute>();
        var credit = new List<CreditRule>();

        await using var conn = await db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var batch = new NpgsqlBatch(conn)
        {
            BatchCommands =
            {
                new("""
                    SELECT id, tributo, ente_codigo, forma_aplicacao, aliquota, valid_from, valid_to, ato_legal, fonte, provisoria
                      FROM rate_catalog WHERE superseded_at IS NULL
                     ORDER BY tributo, COALESCE(ente_codigo, ''), valid_from
                    """),
                new("SELECT code, rpm, burst, concurrency, batch_max_docs, batch_max_bytes, queue_priority, bill_schema_errors, max_users, included_docs, included_simulations, hard_quota, dfe_enabled FROM plans ORDER BY code"),
                new("SELECT crt, tp_regime_ibscbs, engines, simples_mode FROM engine_routing ORDER BY crt, tp_regime_ibscbs"),
                new("SELECT valid_from, default_status FROM credit_rules ORDER BY valid_from"),
            },
        };
        await using var r = await batch.ExecuteReaderAsync(ct).ConfigureAwait(false);

        sb.Append("rates\n");
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            var row = new RateRow(
                r.GetGuid(0), ParseTributo(r.GetString(1)), r.IsDBNull(2) ? null : r.GetString(2),
                r.IsDBNull(3) ? null : ParseForma(r.GetString(3)), r.GetDecimal(4),
                r.GetFieldValue<DateOnly>(5), r.IsDBNull(6) ? null : r.GetFieldValue<DateOnly>(6),
                r.GetString(7), r.GetString(8), r.GetBoolean(9));
            rates.Add(row);
            sb.Append(CultureInfo.InvariantCulture,
                $"{row.Id}|{row.Tributo}|{row.EnteCodigo}|{row.Forma}|{row.Aliquota:0.0000}|{row.ValidFrom:O}|{row.ValidTo:O}|{row.Fonte}|{row.Provisoria}\n");
        }

        await r.NextResultAsync(ct).ConfigureAwait(false);
        sb.Append("plans\n");
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            var p = new PlanLimits(r.GetString(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4), r.GetInt64(5), r.GetString(6), r.GetBoolean(7),
                r.IsDBNull(8) ? null : r.GetInt32(8), r.GetInt64(9), r.GetInt64(10), r.GetBoolean(11), r.GetBoolean(12));
            plans[p.PlanCode] = p;
            sb.Append(CultureInfo.InvariantCulture, $"{p.PlanCode}|{p.Rpm}|{p.Burst}|{p.Concurrency}|{p.BatchMaxDocs}|{p.BatchMaxBytes}|{p.QueuePriority}|{p.BillSchemaErrors}|{p.MaxUsers}|{p.IncludedDocs}|{p.IncludedSimulations}|{p.HardQuota}|{p.DfeEnabled}\n");
        }

        await r.NextResultAsync(ct).ConfigureAwait(false);
        sb.Append("routing\n");
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            var e = new EngineRoute(r.GetString(0), r.GetString(1), r.GetFieldValue<string[]>(2), r.IsDBNull(3) ? null : r.GetString(3));
            routing.Add(e);
            sb.Append(CultureInfo.InvariantCulture, $"{e.Crt}|{e.TpRegimeIbsCbs}|{string.Join(',', e.Engines)}|{e.SimplesMode}\n");
        }

        await r.NextResultAsync(ct).ConfigureAwait(false);
        sb.Append("credit\n");
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            var c = new CreditRule(r.GetFieldValue<DateOnly>(0), r.GetString(1));
            credit.Add(c);
            sb.Append(CultureInfo.InvariantCulture, $"{c.ValidFrom:O}|{c.DefaultStatus}\n");
        }

        return (sb.ToString(), rates, plans, routing, credit);
    }

    /// <summary>Limites efetivos: plano do snapshot + <c>limits_override</c> do tenant (rpm, burst, concurrency).</summary>
    public static PlanLimits EffectiveLimits(ParamSnapshot snapshot, string planCode, string overrideJson)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!snapshot.Plans.TryGetValue(planCode, out var plan))
        {
            throw new InvalidOperationException($"Plano '{planCode}' não existe no snapshot {snapshot.Id}.");
        }

        if (string.IsNullOrEmpty(overrideJson) || overrideJson == "{}")
        {
            return plan;
        }

        using var doc = JsonDocument.Parse(overrideJson);
        var o = doc.RootElement;
        int Get(string name, int fallback) => o.TryGetProperty(name, out var v) && v.TryGetInt32(out var i) && i > 0 ? i : fallback;
        return plan with { Rpm = Get("rpm", plan.Rpm), Burst = Get("burst", plan.Burst), Concurrency = Get("concurrency", plan.Concurrency) };
    }

    private static Tributo ParseTributo(string s) => s switch
    {
        "CBS" => Tributo.Cbs,
        "IBS_UF" => Tributo.IbsUf,
        "IBS_MUN" => Tributo.IbsMun,
        "IS" => Tributo.Is,
        _ => throw new InvalidOperationException($"Tributo desconhecido: {s}"),
    };

    private static FormaAplicacao ParseForma(string s) => s switch
    {
        "SUBSTITUICAO" => FormaAplicacao.Substituicao,
        "ACRESCIMO" => FormaAplicacao.Acrescimo,
        "DECRESCIMO" => FormaAplicacao.Decrescimo,
        _ => throw new InvalidOperationException($"Forma de aplicação desconhecida: {s}"),
    };

    public override void Dispose()
    {
        _reloadGate.Dispose();
        base.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Parâmetros carregados: snapshot {SnapshotId} ({Rates} alíquotas, {Plans} planos).")]
    private static partial void LogLoaded(ILogger logger, long snapshotId, int rates, int plans);

    [LoggerMessage(Level = LogLevel.Error, Message = "Falha ao recarregar parâmetros; mantendo o snapshot anterior.")]
    private static partial void LogReloadFailed(ILogger logger, Exception ex);
}
