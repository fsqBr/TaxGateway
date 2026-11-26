using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

using Npgsql;

namespace TaxGateway.Infrastructure.Persistence;

/// <summary>Uma migration SQL versionada (arquivo <c>Vnnnn__nome.sql</c> embutido no assembly).</summary>
public sealed record SqlMigration(int Version, string Name, string Sql, byte[] Checksum);

/// <summary>
/// Migrator de SQL puro (decisão D-101): arquivos versionados, um por transação, checksum verificado,
/// advisory lock contra execução concorrente. Executado como <c>tg_owner</c> (só no CI/CD).
/// </summary>
public sealed partial class SqlMigrator(NpgsqlDataSource ownerDataSource)
{
    // "TGMIGR" em ASCII: chave fixa do advisory lock do migrator.
    private const long AdvisoryLockKey = 0x5447_4D49_4752;

    [GeneratedRegex(@"^V(?<v>\d{4})__(?<n>[a-z0-9_]+)\.sql$")]
    private static partial Regex FileNamePattern();

    public static IReadOnlyList<SqlMigration> LoadEmbedded()
    {
        var asm = typeof(SqlMigrator).Assembly;
        var list = new List<SqlMigration>();
        foreach (var resource in asm.GetManifestResourceNames().Where(r => r.StartsWith("migrations/", StringComparison.Ordinal)))
        {
            var file = resource["migrations/".Length..];
            var m = FileNamePattern().Match(file);
            if (!m.Success)
            {
                throw new InvalidOperationException($"Nome de migration inválido: {file} (esperado Vnnnn__nome.sql).");
            }

            list.Add(Create(int.Parse(m.Groups["v"].Value, System.Globalization.CultureInfo.InvariantCulture),
                m.Groups["n"].Value, ReadResource(asm, resource)));
        }

        list.Sort((a, b) => a.Version.CompareTo(b.Version));
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i].Version != i + 1)
            {
                throw new InvalidOperationException($"Sequência de migrations com lacuna ou duplicata em V{list[i].Version:D4}.");
            }
        }

        return list;
    }

    /// <summary>Checksum sobre o texto com quebras de linha normalizadas (imune a autocrlf do git).</summary>
    public static SqlMigration Create(int version, string name, string sql)
    {
        var normalized = sql.Replace("\r\n", "\n", StringComparison.Ordinal);
        return new SqlMigration(version, name, normalized, SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }

    /// <returns>Versões aplicadas nesta execução.</returns>
    public async Task<IReadOnlyList<int>> MigrateAsync(IReadOnlyList<SqlMigration>? migrations = null, CancellationToken ct = default)
    {
        migrations ??= LoadEmbedded();
        await using var conn = await ownerDataSource.OpenConnectionAsync(ct).ConfigureAwait(false);

        await ExecAsync(conn, null, $"SELECT pg_advisory_lock({AdvisoryLockKey})", ct).ConfigureAwait(false);
        try
        {
            await ExecAsync(conn, null, """
                CREATE SCHEMA IF NOT EXISTS migrations;
                CREATE TABLE IF NOT EXISTS migrations.history (
                    version     INT PRIMARY KEY,
                    name        TEXT NOT NULL,
                    checksum    BYTEA NOT NULL,
                    applied_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
                    applied_by  TEXT NOT NULL DEFAULT current_user
                );
                """, ct).ConfigureAwait(false);

            var applied = await ReadHistoryAsync(conn, ct).ConfigureAwait(false);
            var known = migrations.ToDictionary(m => m.Version);
            foreach (var (version, checksum) in applied)
            {
                if (!known.TryGetValue(version, out var m))
                {
                    throw new InvalidOperationException($"O banco tem a migration V{version:D4}, que não existe neste build (banco à frente do código).");
                }

                if (!m.Checksum.AsSpan().SequenceEqual(checksum))
                {
                    throw new InvalidOperationException($"Checksum divergente em V{version:D4}__{m.Name}: migration aplicada foi alterada. Crie uma nova versão.");
                }
            }

            var done = new List<int>();
            foreach (var m in migrations.Where(m => !applied.ContainsKey(m.Version)))
            {
                await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
                await ExecAsync(conn, tx, m.Sql, ct).ConfigureAwait(false);
                await using (var cmd = new NpgsqlCommand("INSERT INTO migrations.history (version, name, checksum) VALUES ($1, $2, $3)", conn, tx))
                {
                    cmd.Parameters.Add(new() { Value = m.Version });
                    cmd.Parameters.Add(new() { Value = m.Name });
                    cmd.Parameters.Add(new() { Value = m.Checksum });
                    await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                await tx.CommitAsync(ct).ConfigureAwait(false);
                done.Add(m.Version);
            }

            return done;
        }
        finally
        {
            await ExecAsync(conn, null, $"SELECT pg_advisory_unlock({AdvisoryLockKey})", CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static async Task<Dictionary<int, byte[]>> ReadHistoryAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        var result = new Dictionary<int, byte[]>();
        await using var cmd = new NpgsqlCommand("SELECT version, checksum FROM migrations.history", conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result[reader.GetInt32(0)] = (byte[])reader[1];
        }

        return result;
    }

    private static async Task ExecAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx) { CommandTimeout = 0 };
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static string ReadResource(Assembly asm, string name)
    {
        using var stream = asm.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Recurso não encontrado: {name}");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
