using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Npgsql;

using TaxGateway.Identity.Accounts;

namespace TaxGateway.Workers;

/// <summary>
/// Avisos por e-mail (D-J03): a cada minuto faz o fan-out das mudanças fiscais aprovadas para os clientes ativos e envia
/// os avisos pendentes aos papéis de cada aviso (só e-mails verificados). Um aviso que falha no envio fica pendente e é
/// tentado de novo; depois de 3 dias desiste (fica só no portal).
/// </summary>
public sealed partial class NotificationMailer(NpgsqlDataSource workerDb, IEmailSender email, Microsoft.Extensions.Options.IOptions<AccountOptions> options,
    ILogger<NotificationMailer> log) : BackgroundService
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
            catch (NpgsqlException ex) when (!stoppingToken.IsCancellationRequested)
            {
                LogCycleFailed(log, ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    /// <returns>Avisos enviados por e-mail neste ciclo.</returns>
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        await using (var fan = workerDb.CreateCommand("SELECT app.notify_param_changes()"))
        {
            await fan.ExecuteScalarAsync(ct).ConfigureAwait(false);
        }

        var pending = new List<(Guid Id, string Tenant, string Title, string Body, string? Link, string[] To)>();
        await using (var cmd = workerDb.CreateCommand("SELECT id, tenant_name, title, body, link, recipients FROM app.pending_notification_emails(100)"))
        await using (var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                pending.Add((r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4), r.GetFieldValue<string[]>(5)));
            }
        }

        var done = new List<Guid>();
        foreach (var n in pending)
        {
            try
            {
                var link = n.Link is null ? null : options.Value.PublicBaseUrl.TrimEnd('/') + n.Link;
                foreach (var to in n.To)
                {
                    await email.SendAsync(EmailTemplates.Notice(to, n.Tenant, n.Title, n.Body, link), ct).ConfigureAwait(false);
                }

                done.Add(n.Id);   // sem destinatário verificado também conta como resolvido: o aviso fica no portal
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogSendFailed(log, ex, n.Id);
            }
        }

        if (done.Count > 0)
        {
            await using var mark = workerDb.CreateCommand("SELECT app.mark_notifications_emailed($1)");
            mark.Parameters.Add(new() { Value = done.ToArray() });
            await mark.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        return done.Count;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Ciclo de avisos falhou; nova tentativa em 1 min.")]
    private static partial void LogCycleFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Falha ao enviar o aviso {Id} por e-mail; fica pendente.")]
    private static partial void LogSendFailed(ILogger logger, Exception ex, Guid id);
}
