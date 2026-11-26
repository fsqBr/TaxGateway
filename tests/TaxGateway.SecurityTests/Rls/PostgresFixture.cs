using DotNet.Testcontainers.Builders;

using Npgsql;

using TaxGateway.Infrastructure.Persistence;

using Testcontainers.PostgreSql;

namespace TaxGateway.SecurityTests.Rls;

/// <summary>
/// Sobe a imagem do Postgres do projeto (infra/postgres: pg_partman + bootstrap de roles), aplica as
/// migrations como tg_owner e popula dois tenants (A e B) em TODAS as tabelas com tenant_id.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    public const string Password = "dev-only";
    private PostgreSqlContainer? _container;

    public static readonly Guid TenantA = Guid.Parse("01900000-0000-7000-8000-00000000000a");
    public static readonly Guid TenantB = Guid.Parse("01900000-0000-7000-8000-00000000000b");

    public string SuperuserConnection => _container!.GetConnectionString();

    public string ConnectionFor(string role) =>
        new NpgsqlConnectionStringBuilder(SuperuserConnection) { Username = role, Password = Password, Pooling = false }.ConnectionString;

    public async Task InitializeAsync()
    {
        var image = new ImageFromDockerfileBuilder()
            .WithDockerfileDirectory(Path.Combine(RepoRoot(), "infra", "postgres"))
            .WithDockerfile("Dockerfile")
            .WithName("taxgateway/postgres:18-test")
            .WithCleanUp(false)
            .Build();
        await image.CreateAsync();

        _container = new PostgreSqlBuilder(image.FullName)
            .WithUsername("tg_bootstrap")
            .WithPassword("bootstrap")
            .WithDatabase("taxgateway")
            .Build();
        await _container.StartAsync();

        await using (var owner = NpgsqlDataSource.Create(ConnectionFor("tg_owner")))
        {
            await new SqlMigrator(owner).MigrateAsync();
        }

        await using var conn = new NpgsqlConnection(SuperuserConnection);
        await conn.OpenAsync();
        await Seed.InsertGlobalAsync(conn);
        await Seed.InsertTenantAsync(conn, TenantA, 1);
        await Seed.InsertTenantAsync(conn, TenantB, 2);
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "global.json")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Raiz do repositório (global.json) não encontrada.");
    }
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
