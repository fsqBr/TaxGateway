using Npgsql;

using TaxGateway.Core.Tenancy;

namespace TaxGateway.Infrastructure.Persistence;

/// <summary>
/// Única porta de acesso a dados de tenant (SDD §4.1, decisão D-109).
/// Toda unidade de trabalho roda numa transação explícita com <c>app.current_tenant_id</c> definido
/// por <c>set_config(..., true)</c> (escopo da transação: some no COMMIT/ROLLBACK, seguro com pool e PgBouncer
/// em modo transação). Não existe outra forma de obter conexão para tabelas de tenant.
/// </summary>
public interface ITenantDb
{
    Task<T> InTenantAsync<T>(TenantId tenant, Func<TenantScope, CancellationToken, Task<T>> work, CancellationToken ct = default);
}

/// <summary>Conexão + transação já com o contexto do tenant. Válido só dentro de <see cref="ITenantDb.InTenantAsync{T}"/>.</summary>
public sealed class TenantScope
{
    private readonly NpgsqlConnection _connection;
    private readonly NpgsqlTransaction _transaction;
    private bool _closed;

    internal TenantScope(TenantId tenant, NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        Tenant = tenant;
        _connection = connection;
        _transaction = transaction;
    }

    public TenantId Tenant { get; }

    public NpgsqlCommand CreateCommand(string sql)
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        return new NpgsqlCommand(sql, _connection, _transaction);
    }

    public NpgsqlBatch CreateBatch()
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        return new NpgsqlBatch(_connection, _transaction);
    }

    internal void Close() => _closed = true;
}

public sealed class TenantDb(NpgsqlDataSource dataSource) : ITenantDb
{
    public async Task<T> InTenantAsync<T>(TenantId tenant, Func<TenantScope, CancellationToken, Task<T>> work, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (tenant.Value == Guid.Empty)
        {
            throw new ArgumentException("Contexto de tenant ausente.", nameof(tenant));
        }

        await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        // O Npgsql envia o BEGIN junto com o primeiro comando: set_config não custa round-trip extra.
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using (var set = new NpgsqlCommand("SELECT set_config('app.current_tenant_id', $1, true)", conn, tx))
        {
            set.Parameters.Add(new() { Value = tenant.ToString() });
            await set.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        var scope = new TenantScope(tenant, conn, tx);
        try
        {
            var result = await work(scope, ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
            return result;
        }
        finally
        {
            scope.Close();   // sem commit, o DisposeAsync da transação faz ROLLBACK
        }
    }
}
