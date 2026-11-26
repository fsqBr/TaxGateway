using Npgsql;

using TaxGateway.Infrastructure.Persistence;

namespace TaxGateway.UnitTests;

/// <summary>D-P22: teto de pool padrão por fonte; o valor explícito na connection string prevalece.</summary>
public sealed class DbPoolsTests
{
    [Fact]
    public void Applies_the_default_ceiling_and_disables_gss()
    {
        var b = new NpgsqlConnectionStringBuilder(DbPools.Normalize("Host=db;Username=tg_app;Password=x", DbPools.App));
        Assert.Equal(DbPools.App, b.MaxPoolSize);
        Assert.Equal(GssEncryptionMode.Disable, b.GssEncryptionMode);
    }

    [Theory]
    [InlineData("Host=db;Maximum Pool Size=7")]
    [InlineData("Host=db;MaxPoolSize=7")]
    public void Explicit_value_wins(string cs) =>
        Assert.Equal(7, new NpgsqlConnectionStringBuilder(DbPools.Normalize(cs, DbPools.App)).MaxPoolSize);
}
