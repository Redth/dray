using System.Runtime.CompilerServices;
using Dray.Core.Engine;
using Dray.Core.Model;
using Dray.Core.Tests.Fakes;
using Xunit;

namespace Dray.Core.Tests;

/// <summary>
/// The engine never reads the user's credential store: the CLI hands it a credential on every
/// pull. Without one, a private image cannot be pulled from Dray at all — so a pull retries with the
/// stored credential when, and only when, the registry refuses an anonymous one.
/// </summary>
public class PullCredentialTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A registry that refuses anyone it does not know.</summary>
    sealed class PrivateRegistry : StubRuntime
    {
        public List<RegistryCredential?> Pulls { get; } = [];

        public bool Public { get; init; }

        public override async IAsyncEnumerable<PullProgress> PullImageAsync(
            string reference, RegistryCredential? credential = null, [EnumeratorCancellation] CancellationToken ct = default)
        {
            Pulls.Add(credential);
            await Task.CompletedTask;

            if (credential is null && !Public)
            {
                yield return new PullProgress(null, "error", Error: "Head \"https://ghcr.io/v2/owner/app/manifests/1.0\": unauthorized");
                yield break;
            }

            yield return new PullProgress("abc123", "Pull complete");
        }
    }

    sealed class OneRuntime(IContainerRuntime runtime) : IContainerRuntimeFactory
    {
        public bool Handles(DockerEndpoint endpoint) => true;

        public IContainerRuntime Create(DockerEndpoint endpoint) => runtime;
    }

    static async Task<(EngineManager Manager, List<PullProgress> Steps)> PullAsync(
        PrivateRegistry runtime, ScriptedRunner helper, string reference = "ghcr.io/owner/app:1.0")
    {
        var source = new FakeConfigSource()
            .WithRawConfig("""{"currentContext":"alpha","credsStore":"desktop"}""")
            .WithContext("alpha", "unix:///a.sock");

        var manager = new EngineManager(new DockerContextReader(source), new OneRuntime(runtime));
        await manager.InitializeAsync(Ct);

        var steps = new List<PullProgress>();
        await foreach (var step in manager.PullImageAsync(reference, new RegistryReader(source, helper), Ct)) steps.Add(step);

        return (manager, steps);
    }

    [Fact]
    public async Task ARefusedPullIsRetriedWithTheStoredCredential()
    {
        var runtime = new PrivateRegistry();
        var helper = new ScriptedRunner().On(["get"], 0, """{"ServerURL":"ghcr.io","Username":"redth","Secret":"ghp_s3cret"}""");

        var (manager, steps) = await PullAsync(runtime, helper);
        await using var _ = manager;

        Assert.Equal(2, runtime.Pulls.Count);
        Assert.Null(runtime.Pulls[0]);
        Assert.Equal("redth", runtime.Pulls[1]!.Username);

        // The refusal is not shown: it was a question, and the retry answered it.
        Assert.DoesNotContain(steps, s => s.IsError);
        Assert.Contains(steps, s => s.IsComplete);
    }

    [Fact]
    public async Task APublicPullNeverAsksTheHelper()
    {
        // Fetching a credential can raise a keychain prompt, and nginx is no reason for one.
        var runtime = new PrivateRegistry { Public = true };
        var helper = new ScriptedRunner();

        var (manager, _) = await PullAsync(runtime, helper, "nginx:alpine");
        await using var __ = manager;

        Assert.Single(runtime.Pulls);
        Assert.Empty(helper.Calls);
    }

    [Fact]
    public async Task WithNothingStoredTheRefusalSaysWhatToDo()
    {
        var runtime = new PrivateRegistry();

        var (manager, steps) = await PullAsync(runtime, new ScriptedRunner());
        await using var _ = manager;

        var error = Assert.Single(steps, s => s.IsError).Error!;
        Assert.Contains("Registries page", error);
        Assert.Contains("GitHub", error);

        // The engine's own words stay in it, for anyone who knows what they mean.
        Assert.Contains("unauthorized", error);
    }

    [Fact]
    public async Task DockerHubsCredentialIsAskedForUnderItsLegacyUrl()
    {
        var runtime = new PrivateRegistry();
        var helper = new ScriptedRunner().On(["get"], 0, """{"Username":"redth","Secret":"dckr_pat"}""");

        var (manager, _) = await PullAsync(runtime, helper, "redth/private:1.0");
        await using var __ = manager;

        Assert.Equal("https://index.docker.io/v1/", Assert.Single(helper.Calls).Input);
    }
}
