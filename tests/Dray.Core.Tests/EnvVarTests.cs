using Dray.Core.Model;
using Xunit;

namespace Dray.Core.Tests;

/// <summary>
/// Which environment values Dray hides until asked.
/// <para>
/// The rule is name-based on purpose. Entropy scoring would catch more, and would also mask a
/// build hash and a UUID — and a user who cannot predict what will be hidden stops trusting the
/// screen and reveals everything out of habit, which is worse than not masking at all.
/// </para>
/// </summary>
public class EnvVarTests
{
    [Theory]
    [InlineData("POSTGRES_PASSWORD")]
    [InlineData("DB_PASSWD")]
    [InlineData("MYSQL_ROOT_PWD")]
    [InlineData("JWT_SECRET")]
    [InlineData("GITHUB_TOKEN")]
    [InlineData("STRIPE_API_KEY")]
    [InlineData("AWS_SECRET_ACCESS_KEY")]
    [InlineData("SESSION_KEY")]
    [InlineData("HMAC_SIGNING_SECRET")]
    [InlineData("BASIC_AUTH")]
    [InlineData("PASSWORD_SALT")]
    [InlineData("KEY")]
    public void SecretsAreMasked(string key)
        => Assert.True(new EnvVar(key, "value").IsSecret);

    [Theory]
    [InlineData("PATH")]
    [InlineData("HOME")]
    [InlineData("NGINX_VERSION")]
    [InlineData("KEYCLOAK_URL")]
    [InlineData("MONKEY_MODE")]
    [InlineData("LANG")]
    [InlineData("HOSTNAME")]
    public void OrdinaryValuesAreNot(string key)
        => Assert.False(new EnvVar(key, "value").IsSecret);

    [Theory]
    [InlineData("SSH_KEY_PATH")]
    [InlineData("TLS_KEY_FILE")]
    [InlineData("ACCESS_KEY_ID")]
    public void NamesEndingInKeyThatArePointersRatherThanSecretsAreNotMasked(string key)
    {
        // A path to a key is not a key. Masking these would train the user to reveal by reflex.
        Assert.False(new EnvVar(key, "/etc/ssl/private.pem").IsSecret);
    }

    // ---------------------------------------------------------------- by value

    [Fact]
    public void AConnectionStringWithAPasswordIsMaskedDespiteAnInnocentName()
    {
        // The single most commonly leaked variable, and nothing in the name suggests it.
        Assert.True(new EnvVar("DATABASE_URL", "postgres://admin:hunter2@db:5432/app").IsSecret);
    }

    [Fact]
    public void AUrlWithAUsernameAndNoPasswordIsNotACredential()
        => Assert.False(new EnvVar("REPO_URL", "https://git@github.com/redth/dray").IsSecret);

    [Fact]
    public void APlainUrlIsNotACredential()
        => Assert.False(new EnvVar("API_URL", "https://api.example.com/v1").IsSecret);

    [Fact]
    public void AColonInThePathDoesNotMakeAUrlACredential()
    {
        // The check must look at the authority only; a colon after the first '/' is not auth.
        Assert.False(new EnvVar("ENDPOINT", "https://example.com/a:b@c").IsSecret);
    }

    [Fact]
    public void APortIsNotAPassword()
        => Assert.False(new EnvVar("BROKER", "amqp://rabbit:5672/").IsSecret);
}
