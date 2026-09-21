using FluentAssertions;
using HatidSuki.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;

namespace HatidSuki.IntegrationTests;

public class DatabaseConnectionTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => v.Value)).Build();

    [Fact]
    public void Converts_a_railway_style_url_into_an_npgsql_connection_string()
    {
        var cs = DatabaseConnection.FromUrl("postgresql://app:s3cret@db.internal:5433/orders");

        cs.Should().Contain("Host=db.internal").And.Contain("Port=5433").And.Contain("Database=orders")
            .And.Contain("Username=app").And.Contain("Password=s3cret");
    }

    [Fact]
    public void Decodes_special_characters_in_the_password_and_defaults_the_port()
    {
        var cs = DatabaseConnection.FromUrl("postgres://app:p%40ss%2Fword@db.example.com/orders");

        cs.Should().Contain("Password=p@ss/word").And.Contain("Port=5432");
    }

    [Fact]
    public void An_explicit_connection_string_wins_over_database_url()
    {
        var cs = DatabaseConnection.Resolve(Config(("ConnectionStrings:Default", "Host=local"), ("DATABASE_URL", "postgresql://a:b@c/d")));

        cs.Should().Be("Host=local");
    }

    [Fact]
    public void Falls_back_to_database_url()
    {
        DatabaseConnection.Resolve(Config(("DATABASE_URL", "postgresql://a:b@host/d"))).Should().Contain("Host=host");
    }

    [Fact]
    public void Complains_clearly_when_nothing_is_configured_or_the_url_is_wrong()
    {
        FluentActions.Invoking(() => DatabaseConnection.Resolve(Config())).Should().Throw<InvalidOperationException>().WithMessage("*DATABASE_URL*");
        FluentActions.Invoking(() => DatabaseConnection.FromUrl("mysql://a:b@c/d")).Should().Throw<InvalidOperationException>();
    }
}
