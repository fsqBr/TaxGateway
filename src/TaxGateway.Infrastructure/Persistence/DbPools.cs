using System.Data.Common;

using Npgsql;

namespace TaxGateway.Infrastructure.Persistence;

/// <summary>
/// Orçamento de conexões (D-P22; revisão de arquitetura 20). O padrão do Npgsql é 100 por fonte de dados: com o HPA
/// em 30 pods e 3–4 fontes por pod (app, admin, cobrança, relay) isso passava de 10 mil conexões possíveis contra um
/// Postgres de algumas centenas. Cada fonte recebe um teto padrão pequeno, que a connection string pode sobrescrever
/// explicitamente (<c>Maximum Pool Size=…</c>). Conta: pods × soma dos tetos ≤ conexões do PgBouncer/Postgres.
/// </summary>
public static class DbPools
{
    /// <summary>API e workers, role de runtime (tg_app/tg_worker): o caminho quente.</summary>
    public const int App = 30;

    /// <summary>Fontes auxiliares (admin, cobrança, relay): poucas operações, quase sempre sequenciais.</summary>
    public const int Auxiliary = 5;

    private static readonly string[] MaxPoolKeys = ["Maximum Pool Size", "MaxPoolSize"];

    /// <summary>Connection string com GSS desligado (imagem chiseled, D-404) e teto de pool, se não houver um explícito.</summary>
    public static string Normalize(string connectionString, int defaultMaxPoolSize)
    {
        var raw = new DbConnectionStringBuilder { ConnectionString = connectionString };
        var b = new NpgsqlConnectionStringBuilder(connectionString) { GssEncryptionMode = GssEncryptionMode.Disable };
        if (!MaxPoolKeys.Any(raw.ContainsKey))
        {
            b.MaxPoolSize = defaultMaxPoolSize;
        }

        return b.ConnectionString;
    }
}
