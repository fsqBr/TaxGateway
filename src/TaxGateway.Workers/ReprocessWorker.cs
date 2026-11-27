using System.Text.Json.Nodes;

using Npgsql;

using TaxGateway.Api.Audit;
using TaxGateway.Core.Tenancy;
using TaxGateway.Infrastructure.Persistence;

namespace TaxGateway.Workers;

/// <summary>
/// D-R19: reprocessa sozinho as auditorias afetadas por uma mudança de alíquota retroativa aprovada no backoffice
/// (pedidos gerados por <c>app.notify_param_changes</c>): só o período e o ente afetados, sem cobrança, com aviso no
/// portal ao terminar. Antes o reprocessamento era um job manual por tenant (runbook).
/// </summary>
public sealed partial class ReprocessWorker(NpgsqlDataSource workerDb, ITenantDb db, ReprocessService reprocess, ILogger<ReprocessWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            try
            {
                await RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogError(log, ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var claimed = new List<(Guid Id, Guid Tenant, DateOnly From, DateOnly To, int? Municipio, short? Uf)>();
        await using (var cmd = workerDb.CreateCommand("SELECT id, tenant_id, from_month, to_month, municipio, uf_codigo FROM app.claim_reprocess_requests(5)"))
        await using (var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                claimed.Add((r.GetGuid(0), r.GetGuid(1), r.GetFieldValue<DateOnly>(2), r.GetFieldValue<DateOnly>(3),
                    r.IsDBNull(4) ? null : r.GetInt32(4), r.IsDBNull(5) ? null : r.GetInt16(5)));
            }
        }

        foreach (var q in claimed)
        {
            var tenant = new TenantId(q.Tenant);
            string status;
            JsonObject resumo;
            try
            {
                var s = await reprocess.RunAsync(tenant, q.From, q.To, ct, q.Municipio, q.Uf).ConfigureAwait(false);
                status = s.Falhas == 0 ? "CONCLUIDO" : "FALHOU";
                resumo = new JsonObject { ["encontrados"] = s.Encontrados, ["reprocessados"] = s.Reprocessados, ["sem_xml"] = s.SemXml, ["falhas"] = s.Falhas };
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogError(log, ex);
                status = "FALHOU";
                resumo = new JsonObject { ["erro"] = ex.GetType().Name };
            }

            await db.InTenantAsync(tenant, async (s, c) =>
            {
                await using (var upd = s.CreateCommand("UPDATE reprocess_requests SET status = $2, resumo = $3::jsonb, done_at = now(), lease_until = NULL WHERE id = $1"))
                {
                    upd.Parameters.Add(new() { Value = q.Id });
                    upd.Parameters.Add(new() { Value = status });
                    upd.Parameters.Add(new() { Value = resumo.ToJsonString() });
                    await upd.ExecuteNonQueryAsync(c).ConfigureAwait(false);
                }

                var n = resumo["reprocessados"]?.GetValue<int>() ?? 0;
                if (n > 0)
                {
                    await using var note = s.CreateCommand("""
                        INSERT INTO tenant_notifications (tenant_id, kind, severity, title, body, link, audience, dedupe_key)
                        VALUES ($1, 'MUDANCA_FISCAL', 'info', 'Auditorias recalculadas com a alíquota nova', $2, '/portal/documentos', '{owner,admin,fiscal}', $3)
                        ON CONFLICT (tenant_id, dedupe_key) DO NOTHING
                        """);
                    note.Parameters.Add(new() { Value = q.Tenant });
                    note.Parameters.Add(new() { Value = $"{n} auditoria(s) de {q.From:MM/yyyy} a {q.To:MM/yyyy} foram refeitas com a alíquota aprovada (sem cobrança). Veja as que mudaram de situação em Documentos auditados." });
                    note.Parameters.Add(new() { Value = $"reprocess:{q.Id}" });
                    await note.ExecuteNonQueryAsync(c).ConfigureAwait(false);
                }

                return 0;
            }, ct).ConfigureAwait(false);
        }

        return claimed.Count;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Falha no reprocessamento automático.")]
    private static partial void LogError(ILogger logger, Exception ex);
}
