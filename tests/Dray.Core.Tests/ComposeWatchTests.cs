using Dray.Core.Model;
using Xunit;

namespace Dray.Core.Tests;

/// <summary>
/// Whether a stack can be watched — which is two questions, both of which have to be yes before
/// the button appears. Getting either wrong offers a control that exits with a message.
/// </summary>
public class ComposeWatchTests
{
    [Theory]
    [InlineData("v2.39.1", true)]
    [InlineData("2.39.1", true)]
    [InlineData("2.22.0", true)]
    [InlineData("v2.22", true)]
    [InlineData("2.21.9", false)]
    [InlineData("v1.29.2", false)]
    public void TheVersionHasToHaveTheSubcommand(string version, bool supported)
        => Assert.Equal(supported, ComposeWatch.IsSupported(version));

    [Fact]
    public void ASuffixedVersionComparesOnItsNumbers()
    {
        // Docker Desktop ships versions like this, and a string compare would call it unsupported.
        Assert.True(ComposeWatch.IsSupported("v2.39.1-desktop.1"));
    }

    [Fact]
    public void AVersionThatCannotBeReadIsTreatedAsTooOld()
    {
        // Offering a button that fails is worse than hiding one that would have worked.
        Assert.False(ComposeWatch.IsSupported(null));
        Assert.False(ComposeWatch.IsSupported(""));
        Assert.False(ComposeWatch.IsSupported("unknown"));
    }

    // ---------------------------------------------------------------- the file

    [Fact]
    public void FindsTheServicesThatDeclareSomethingToWatch()
    {
        const string yaml = """
            services:
              web:
                image: nginx
                develop:
                  watch:
                    - action: sync
                      path: ./src
                      target: /app/src
              api:
                image: api
                develop:
                  watch:
                    - action: rebuild
                      path: ./api
              db:
                image: postgres
            """;

        Assert.Equal(["web", "api"], ComposeWatch.Declares(yaml));
    }

    [Fact]
    public void AFileWithNoDevelopBlockDeclaresNothing()
    {
        const string yaml = """
            services:
              web:
                image: nginx
                volumes:
                  - ./src:/app/src
            """;

        Assert.Empty(ComposeWatch.Declares(yaml));
    }

    [Fact]
    public void WatchOnlyCountsInsideDevelop()
    {
        // Not a key compose reads. Counting it would offer a button that does nothing.
        const string yaml = """
            services:
              web:
                watch:
                  - ./src
            """;

        Assert.Empty(ComposeWatch.Declares(yaml));
    }

    [Fact]
    public void DevelopOnOneServiceDoesNotLeakOntoTheNext()
    {
        const string yaml = """
            services:
              web:
                develop:
                  watch:
                    - action: sync
                      path: ./src
                      target: /app
              db:
                image: postgres
                environment:
                  POSTGRES_PASSWORD: x
            """;

        Assert.Equal(["web"], ComposeWatch.Declares(yaml));
    }

    [Fact]
    public void KeysOutsideTheServicesBlockAreNotServices()
    {
        const string yaml = """
            services:
              web:
                develop:
                  watch:
                    - action: sync
                      path: ./src
                      target: /app
            volumes:
              cache:
            """;

        Assert.Equal(["web"], ComposeWatch.Declares(yaml));
    }

    [Fact]
    public void NothingAtAllIsNotAFailure()
    {
        Assert.Empty(ComposeWatch.Declares(null));
        Assert.Empty(ComposeWatch.Declares(""));
    }
}
