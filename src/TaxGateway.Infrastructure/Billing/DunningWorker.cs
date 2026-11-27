using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Npgsql;

namespace TaxGateway.Infrastructure.Billing;

/// <summary>
/// Régua de inadimplência (D-R09; decisão do produto de 26/11/2026). Começa quando a Stripe avisa a falha de pagamento
/// (<c>invoice.payment_failed</c> grava <c>payment_failed_at</c>) e termina quando a fatura é paga:
/// <list type="bullet">
/// <item>dia 0: aviso no portal e por e-mail (dono, administrador, financeiro);</item>
/// <item>dias 3 e 7: lembretes;</item>
/// <item>dia 10: modo restrito — consulta e exportação continuam, consumo para (402 <c>ERR_PAGAMENTO_PENDENTE</c>);</item>
/// <item>dia 15: suspensão (motivo INADIMPLENCIA).</item>
/// </list>
/// Pagou: volta a ACTIVE na hora (só as suspensões por inadimplência; suspensão manual do backoffice continua). A Stripe
/// segue retentando o cartão por conta própria (Smart Retries).
/// </summary>
public sealed partial class DunningWorker(BillingDataSource db, StackExchange.Redis.IConnectionMultiplexer valkey, TimeProvider clock,
    ILogger<DunningWorker> log) : BackgroundService
{
    public static readonly int[] Steps = [0, 3, 7, 10, 15];
    public const string ChangedChannel = "tenant:changed";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            try
            {
                await RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (NpgsqlException ex) when (!stoppingToken.IsCancellationRequested)
            {
                LogFailed(log, ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    /// <returns>Tenants que mudaram de passo neste ciclo.</returns>
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var due = new List<(Guid Tenant, DateTimeOffset FailedAt, int Stage, string Status)>();
        await using (var cmd = db.DataSource.CreateCommand("SELECT id, payment_failed_at, dunning_stage, status FROM tenants WHERE payment_failed_at IS NOT NULL"))
        await using (var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                due.Add((r.GetGuid(0), r.GetFieldValue<DateTimeOffset>(1), r.GetInt32(2), r.GetString(3)));
            }
        }

        var changed = 0;
        foreach (var t in due)
        {
            var days = (int)Math.Floor((now - t.FailedAt).TotalDays);
            var target = Steps.Where(s => s <= days).DefaultIfEmpty(0).Max();
            if (target <= t.Stage)   // a falha começa em -1: o aviso do dia 0 sai uma vez
            {
                continue;
            }

            await ApplyAsync(t.Tenant, t.FailedAt, target, t.Status, ct).ConfigureAwait(false);
            changed++;
        }

        return changed;
    }

    private async Task ApplyAsync(Guid tenant, DateTimeOffset failedAt, int stage, string status, CancellationToken ct)
    {
        var (kind, severity, title, body, newStatus, reason) = stage switch
        {
            >= 15 => ("CONTA_SUSPENSA", "critico", "Conta suspensa por falta de pagamento",
                "A fatura segue em aberto há 15 dias e o acesso à API e ao portal foi suspenso. Regularize o pagamento no portal de cobrança da Stripe (link na fatura recebida por e-mail) ou fale com o suporte: o acesso volta assim que o pagamento é confirmado.",
                "SUSPENDED", "INADIMPLENCIA"),
            >= 10 => ("ACESSO_RESTRITO", "critico", "Consumo suspenso: pagamento pendente",
                "A fatura segue em aberto há 10 dias. Consulta e exportação continuam liberadas, mas simulações, auditorias e lotes estão bloqueados até o pagamento. A suspensão completa acontece no 15º dia.",
                "RESTRICTED", (string?)null),
            _ => ("PAGAMENTO_PENDENTE", "aviso", stage == 0 ? "Falha no pagamento da fatura" : $"Fatura em aberto há {stage} dias",
                "A Stripe não conseguiu cobrar a fatura do TaxGateway e vai tentar de novo. Atualize o cartão em Plano e cobrança → Gerenciar pagamento. "
                + "Sem pagamento, o consumo para no 10º dia e a conta é suspensa no 15º.",
                (string?)null, (string?)null),
        };

        await using var batch = new NpgsqlBatch(await db.DataSource.OpenConnectionAsync(ct).ConfigureAwait(false));
        await using (batch.Connection)
        {
            var upd = new NpgsqlBatchCommand("""
                UPDATE tenants SET dunning_stage = $2,
                       status = CASE WHEN $3::text IS NULL THEN status
                                     WHEN $3 = 'RESTRICTED' AND status = 'ACTIVE' THEN 'RESTRICTED'
                                     WHEN $3 = 'SUSPENDED' AND status IN ('ACTIVE','RESTRICTED') THEN 'SUSPENDED'
                                     ELSE status END,
                       suspended_reason = CASE WHEN $3 = 'SUSPENDED' AND status IN ('ACTIVE','RESTRICTED') THEN $4 ELSE suspended_reason END
                 WHERE id = $1 AND payment_failed_at IS NOT NULL
                """);
            upd.Parameters.Add(new() { Value = tenant });
            upd.Parameters.Add(new() { Value = stage });
            upd.Parameters.Add(new() { Value = (object?)newStatus ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
            upd.Parameters.Add(new() { Value = (object?)reason ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
            batch.BatchCommands.Add(upd);
            batch.BatchCommands.Add(Notice(tenant, kind, severity, title, body, $"dunning:{failedAt.ToUnixTimeSeconds()}:{stage}"));
            await batch.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        LogStage(log, tenant, stage, status);
        await PublishAsync(tenant).ConfigureAwait(false);
    }

    internal static NpgsqlBatchCommand Notice(Guid tenant, string kind, string severity, string title, string body, string dedupe)
    {
        var cmd = new NpgsqlBatchCommand("""
            INSERT INTO tenant_notifications (tenant_id, kind, severity, title, body, link, audience, dedupe_key)
            VALUES ($1, $2, $3, $4, $5, '/portal/cobranca', '{owner,admin,finance}', $6)
            ON CONFLICT (tenant_id, dedupe_key) DO NOTHING
            """);
        cmd.Parameters.Add(new() { Value = tenant });
        cmd.Parameters.Add(new() { Value = kind });
        cmd.Parameters.Add(new() { Value = severity });
        cmd.Parameters.Add(new() { Value = title });
        cmd.Parameters.Add(new() { Value = body });
        cmd.Parameters.Add(new() { Value = dedupe });
        return cmd;
    }

    private async Task PublishAsync(Guid tenant)
    {
        try
        {
            await valkey.GetSubscriber().PublishAsync(StackExchange.Redis.RedisChannel.Literal(ChangedChannel), tenant.ToString()).ConfigureAwait(false);
        }
        catch (StackExchange.Redis.RedisException)
        {
            // o perfil expira em 30 s; a suspensão também chega pelo estado de revogação (10 s)
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Régua de inadimplência: tenant {Tenant} no dia {Stage} (situação anterior {Status}).")]
    private static partial void LogStage(ILogger logger, Guid tenant, int stage, string status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Ciclo da régua de inadimplência falhou; nova tentativa em 1 h.")]
    private static partial void LogFailed(ILogger logger, Exception ex);
}
