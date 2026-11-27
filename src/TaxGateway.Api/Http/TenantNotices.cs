using TaxGateway.Infrastructure.Persistence;

namespace TaxGateway.Api.Http;

/// <summary>Avisos ao cliente gravados pela API no escopo do tenant (portal + e-mail pelo NotificationMailer).</summary>
public static class TenantNotices
{
    /// <summary>D-R32: aviso de segurança (credencial criada etc.) para dono e administradores, uma vez por fato.</summary>
    public static async Task<int> SecurityAsync(TenantScope scope, Guid tenant, string title, string body, string link, string dedupe, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(body);
        await using var cmd = scope.CreateCommand("""
            INSERT INTO tenant_notifications (tenant_id, kind, severity, title, body, link, audience, dedupe_key)
            VALUES ($1, 'SEGURANCA', 'aviso', $2, $3, $4, '{owner,admin}', $5) ON CONFLICT (tenant_id, dedupe_key) DO NOTHING
            """);
        cmd.Parameters.Add(new() { Value = tenant });
        cmd.Parameters.Add(new() { Value = title });
        cmd.Parameters.Add(new() { Value = body.Length > 2000 ? body[..2000] : body });
        cmd.Parameters.Add(new() { Value = link });
        cmd.Parameters.Add(new() { Value = dedupe });
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
