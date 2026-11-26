using Microsoft.Extensions.DependencyInjection;

using Npgsql;

namespace TaxGateway.Infrastructure.Persistence;

public static class PersistenceServiceCollectionExtensions
{
    /// <summary>
    /// Registra o acesso a dados do runtime. <paramref name="connectionString"/> é de um role de runtime
    /// (<c>tg_app</c> na API, <c>tg_worker</c> nos workers), nunca do <c>tg_owner</c>.
    /// </summary>
    public static IServiceCollection AddTaxGatewayPersistence(this IServiceCollection services, string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            // D-110: o contexto de tenant é local à transação (set_config(..., true)); não há estado de sessão
            // a limpar, então pulamos o reset na devolução ao pool e mantemos os prepared statements.
            NoResetOnClose = true,
            MaxAutoPrepare = 64,
            AutoPrepareMinUsages = 2,
            // Sem Kerberos: evita carregar libgssapi (ausente na imagem chiseled) a cada conexão (D-404).
            GssEncryptionMode = GssEncryptionMode.Disable,
        };

        services.AddSingleton(_ => NpgsqlDataSource.Create(builder.ConnectionString));
        services.AddSingleton<ITenantDb, TenantDb>();
        services.AddSingleton<ApiKeyLookup>();
        return services;
    }
}
