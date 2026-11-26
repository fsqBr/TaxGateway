// Aplica as migrations SQL (D-101). Roda como tg_owner, no CI/CD ou como Job do Kubernetes antes do deploy.
// Uso: TG_OWNER_CONNECTION="Host=...;Username=tg_owner;Password=..." dotnet run --project src/TaxGateway.Migrator
using Npgsql;

using TaxGateway.Infrastructure.Persistence;

var connectionString = Environment.GetEnvironmentVariable("TG_OWNER_CONNECTION")
    ?? (args.Length > 0 ? args[0] : null);
if (string.IsNullOrWhiteSpace(connectionString))
{
    await Console.Error.WriteLineAsync("Informe a conexão do tg_owner em TG_OWNER_CONNECTION (ou como 1º argumento).");
    return 2;
}

await using var dataSource = NpgsqlDataSource.Create(
    new NpgsqlConnectionStringBuilder(connectionString) { GssEncryptionMode = GssEncryptionMode.Disable }.ConnectionString);
var migrations = SqlMigrator.LoadEmbedded();
var applied = await new SqlMigrator(dataSource).MigrateAsync(migrations);
Console.WriteLine(applied.Count == 0
    ? $"Banco atualizado (V{migrations[^1].Version:D4}); nada a aplicar."
    : $"Aplicadas: {string.Join(", ", applied.Select(v => $"V{v:D4}"))}.");
return 0;
