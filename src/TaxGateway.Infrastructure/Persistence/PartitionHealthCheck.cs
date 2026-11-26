using Microsoft.Extensions.Diagnostics.HealthChecks;

using Npgsql;

namespace TaxGateway.Infrastructure.Persistence;

/// <summary>
/// Linha em partição DEFAULT = manutenção do pg_partman falhou ou chegou dado fora da janela (SDD §9, D-106).
/// Fica <c>Degraded</c> (não derruba o pod) e o alerta é disparado pela observabilidade sobre este estado.
/// Lê só contagens via <c>app.default_partition_rows()</c>; nunca os dados.
/// </summary>
public sealed class PartitionHealthCheck(NpgsqlDataSource appDataSource) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var data = new Dictionary<string, object>();
        await using var cmd = appDataSource.CreateCommand("SELECT parent_table, row_count FROM app.default_partition_rows()");
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var rows = reader.GetInt64(1);
            if (rows > 0)
            {
                data[reader.GetString(0)] = rows;
            }
        }

        return data.Count == 0
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Degraded("Há linhas em partições DEFAULT: verificar a manutenção do pg_partman.", data: data);
    }
}
