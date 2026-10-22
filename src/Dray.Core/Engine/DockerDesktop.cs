using System.Text.Json;

namespace Dray.Core.Engine;

/// <summary>
/// Which of Docker Desktop's engines is answering. Two on Windows; on a Mac there is only ever
/// <see cref="Linux"/>.
/// </summary>
public enum DesktopEngineMode
{
    Linux,
    Windows,
}

/// <summary>What Docker Desktop is doing on this machine.</summary>
/// <param name="Installed">Whether Docker Desktop is installed at all.</param>
/// <param name="Running">Whether it reported itself running.</param>
/// <param name="Mode">
/// The engine it is running, as it reported it. Null when it is not running, or did not say.
/// </param>
public sealed record DockerDesktopState(bool Installed, bool Running, DesktopEngineMode? Mode)
{
    public static readonly DockerDesktopState NotInstalled = new(false, false, null);

    /// <summary>
    /// Whether choosing <paramref name="mode"/> means Docker Desktop has to switch first.
    /// <para>
    /// Only once it has said which mode it is in. A Docker Desktop that has not answered is not
    /// known to be in the other mode, and switching it on a guess would restart its engine for
    /// nothing.
    /// </para>
    /// </summary>
    public bool NeedsSwitchTo(DesktopEngineMode mode) => Running && Mode is { } current && current != mode;
}

/// <summary>
/// Docker Desktop's own command line: whether it is running, which engine it has on, switching it.
/// <para>
/// docs/ARCHITECTURE.md section 2.4b. On Windows, Docker Desktop runs <b>one engine at a time</b>.
/// Both pipes exist whichever mode it is in, but only the current one answers — the other returns
/// a 500 or does not answer at all, and <c>docker_engine</c> follows whichever is current. Measured
/// on Docker Desktop with the WSL2 backend, not assumed: an earlier design said both ran at once
/// and built the host picker on that, and it left the inactive engine looking broken.
/// </para>
/// <para>
/// <c>docker desktop</c> (4.37 and later) is the documented, scriptable route to all of it, and its
/// <c>--format json</c> output is what is parsed — the table form is for people.
/// </para>
/// <para>
/// On a Mac the same CLI starts it and says whether it is running, and that is all there is to
/// drive: Docker Desktop for Mac has one engine, so it has no <c>engine</c> command and nothing to
/// switch. Starting it from Dray matters for the same reason it does on Windows — a stopped Docker
/// Desktop leaves its context in the picker as an engine that does not answer, and the one action
/// that fixes it is Dray's to take.
/// </para>
/// </summary>
public sealed class DockerDesktop(
    IProcessRunner? runner = null,
    IDockerConfigSource? source = null,
    string? installRoot = null)
{
    readonly IProcessRunner _runner = runner ?? new SystemProcessRunner();
    readonly IDockerConfigSource _source = source ?? new SystemDockerConfigSource();

    /// <summary>
    /// Where Docker Desktop is installed: its folder in Program Files on Windows, its application
    /// bundle on a Mac. A parameter so a test is not answered by the real install on the machine
    /// running it.
    /// </summary>
    readonly string _installRoot = installRoot
        ?? ((source ?? new SystemDockerConfigSource()).IsMacOS
            ? MacApplication
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Docker", "Docker"));

    /// <summary>Where Docker Desktop for Mac installs. Its installer offers nowhere else.</summary>
    public const string MacApplication = "/Applications/Docker.app";

    /// <summary>
    /// Long enough for a mode switch, which took eleven seconds when measured, and for a cold
    /// start, which can take a minute on a machine that has just booted.
    /// </summary>
    public static readonly TimeSpan SwitchTimeout = TimeSpan.FromMinutes(3);

    /// <summary>Whether the application itself is there, which is what "installed" means.</summary>
    bool IsInstalled => _source.IsMacOS
        ? _source.DirectoryExists(_installRoot)
        : _source.IsWindows && _source.FileExists(Path.Combine(_installRoot, "Docker Desktop.exe"));

    /// <summary>
    /// Docker Desktop's own copy of the CLI. Preferred to whatever <c>docker</c> is on PATH,
    /// because the <c>desktop</c> plugin ships with Docker Desktop and a different CLI earlier on
    /// PATH — a native engine's, or Homebrew's beside Colima — would not have it.
    /// </summary>
    string Cli
    {
        get
        {
            // Joined with '/' on a Mac for the reason discovery gives: Path.Combine takes its
            // separator from the machine running the code, and the tests run on Windows too.
            var own = _source.IsMacOS
                ? _installRoot.TrimEnd('/') + "/Contents/Resources/bin/docker"
                : Path.Combine(_installRoot, "resources", "bin", "docker.exe");

            return _source.FileExists(own) ? own : "docker";
        }
    }

    /// <summary>
    /// The socket Docker Desktop for Mac serves its engine on, under the user's home. It is the
    /// endpoint of the <c>desktop-linux</c> context, and <c>/var/run/docker.sock</c> links to it.
    /// </summary>
    const string MacSocket = "/.docker/run/docker.sock";

    /// <summary>
    /// The pipe each engine publishes under, which is how a host is matched to a mode.
    /// <para>
    /// These names are Docker Desktop's own and each belongs to one engine. That is a fact about the
    /// pipe rather than an inference about the containers behind it — what kind of container an
    /// engine runs still comes from <c>/info</c>, and nowhere else.
    /// </para>
    /// </summary>
    public static DesktopEngineMode? ModeOf(DockerEndpoint endpoint) => endpoint switch
    {
        // A Mac's one engine. Only Docker Desktop serves this socket — OrbStack, Colima and Rancher
        // Desktop each have their own — so it is as much a fact about the socket as a pipe name is.
        { Scheme: EndpointScheme.Unix, Path: { } path }
            when path.EndsWith(MacSocket, StringComparison.Ordinal) => DesktopEngineMode.Linux,
        { Scheme: EndpointScheme.NamedPipe, Path: { } path }
            when path.EndsWith("/dockerDesktopLinuxEngine", StringComparison.OrdinalIgnoreCase) => DesktopEngineMode.Linux,
        { Scheme: EndpointScheme.NamedPipe, Path: { } path }
            when path.EndsWith("/dockerDesktopWindowsEngine", StringComparison.OrdinalIgnoreCase) => DesktopEngineMode.Windows,
        _ => null,
    };

    /// <summary>The word the CLI uses for a mode.</summary>
    public static string Word(DesktopEngineMode mode) => mode == DesktopEngineMode.Linux ? "linux" : "windows";

    /// <summary>
    /// What Docker Desktop is doing. Never throws: a Docker Desktop that cannot be asked is reported
    /// as installed and not running, which is what the user can act on.
    /// </summary>
    public async Task<DockerDesktopState> ReadAsync(CancellationToken ct = default)
    {
        if (!IsInstalled) return DockerDesktopState.NotInstalled;

        try
        {
            var status = await _runner.RunAsync(Cli, ["desktop", "status", "--format", "json"], null, ct).ConfigureAwait(false);
            if (status.ExitCode != 0 || !ParseRunning(status.StandardOutput)) return new(true, false, null);

            // One engine, so nothing to ask: it runs Linux containers or it is not running. The
            // kind of container a host runs is still read from /info; this is which engine of
            // Docker Desktop's is up, and a Mac's has only the one.
            if (_source.IsMacOS) return new(true, true, DesktopEngineMode.Linux);

            var engines = await _runner.RunAsync(Cli, ["desktop", "engine", "ls", "--format", "json"], null, ct).ConfigureAwait(false);
            return new(true, true, engines.ExitCode == 0 ? ParseCurrentMode(engines.StandardOutput) : null);
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return new(true, false, null);
        }
    }

    /// <summary>
    /// Switch to <paramref name="mode"/>. Null on success, or a sentence saying why not.
    /// <para>
    /// Waits for the switch to finish, which the CLI does by itself: it returns once the new engine
    /// answers. Measured at eleven seconds from Windows to Linux and three back.
    /// </para>
    /// </summary>
    public async Task<string?> UseAsync(DesktopEngineMode mode, CancellationToken ct = default)
    {
        try
        {
            var result = await _runner
                .RunLongAsync(Cli, ["desktop", "engine", "use", Word(mode)], SwitchTimeout, null, ct)
                .ConfigureAwait(false);

            return result.ExitCode == 0
                ? null
                : Explain(result, $"Docker Desktop did not switch to {Label(mode)}.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return $"Docker Desktop was still switching to {Label(mode)} after {SwitchTimeout.TotalMinutes:0} minutes.";
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return $"Could not ask Docker Desktop to switch: {e.Message}";
        }
    }

    /// <summary>Start Docker Desktop and wait until its engine answers. Null on success.</summary>
    public async Task<string?> StartAsync(CancellationToken ct = default)
    {
        try
        {
            var result = await _runner
                .RunLongAsync(Cli, ["desktop", "start", "--timeout", ((int)SwitchTimeout.TotalSeconds).ToString()], SwitchTimeout + TimeSpan.FromSeconds(10), null, ct)
                .ConfigureAwait(false);

            return result.ExitCode == 0 ? null : Explain(result, "Docker Desktop did not start.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return "Docker Desktop was still starting after three minutes.";
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return $"Could not start Docker Desktop: {e.Message}";
        }
    }

    /// <summary>What a mode is called in a sentence.</summary>
    public static string Label(DesktopEngineMode mode)
        => mode == DesktopEngineMode.Linux ? "Linux containers" : "Windows containers";

    /// <summary>
    /// The CLI's own reason, after Dray's sentence. Its words are better than a guess at them, and
    /// they are short — "unknown engine mode", not a stack trace.
    /// </summary>
    static string Explain(ProcessResult result, string what)
    {
        var said = (result.StandardError.Length > 0 ? result.StandardError : result.StandardOutput).Trim();
        return said.Length == 0 ? what : $"{what} It said: {said.Split('\n')[0].Trim()}";
    }

    /// <summary>
    /// <c>docker desktop status --format json</c>: <c>{"Status":"running", …}</c>.
    /// <para>
    /// Read as a document, the way discovery reads <c>meta.json</c>, rather than deserialised into a
    /// type: two fields do not need a model, and a document needs no reflection — an app built with
    /// it switched off, which a trimmed head is, would otherwise read every Docker Desktop as stopped.
    /// </para>
    /// </summary>
    internal static bool ParseRunning(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);

            // "starting" counts. It is what Docker Desktop says for the second after a mode switch
            // returns — measured — and reading it as stopped labelled the engine just switched away
            // from as "Docker Desktop is not running", which it plainly was.
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && Property(doc.RootElement, "Status") is { ValueKind: JsonValueKind.String } status
                && status.GetString()?.Trim().ToLowerInvariant() is "running" or "starting";
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// <c>docker desktop engine ls --format json</c>:
    /// <c>[{"Name":"linux","Current":false},{"Name":"windows","Current":true}]</c>.
    /// </summary>
    internal static DesktopEngineMode? ParseCurrentMode(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;

            foreach (var engine in doc.RootElement.EnumerateArray())
            {
                if (engine.ValueKind != JsonValueKind.Object) continue;
                if (Property(engine, "Current") is not { ValueKind: JsonValueKind.True }) continue;

                return (Property(engine, "Name") is { ValueKind: JsonValueKind.String } name ? name.GetString() : null)?.Trim().ToLowerInvariant() switch
                {
                    "linux" => DesktopEngineMode.Linux,
                    "windows" => DesktopEngineMode.Windows,
                    _ => null,
                };
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>A property by name, ignoring case — the CLI capitalises them today, and need not tomorrow.</summary>
    static JsonElement? Property(JsonElement element, string name)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return property.Value;
        }

        return null;
    }
}
