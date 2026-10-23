using Dray.Core.Model;

namespace Dray.Core.Engine;

/// <summary>
/// Every stack Dray knows about on a host, from three sources merged.
/// <list type="bullet">
/// <item><b>Labels</b> on the host's containers — a stack brought up from a terminal, with nothing
/// registered. This is the only source for a stack whose files are on another machine.</item>
/// <item><b>The registry</b> — stacks this host has had containers for, which is what keeps one on
/// the list after it is brought down.</item>
/// <item><b>Folders</b> — the stacks folder the user chose, scanned, and any folder they added — so a
/// stack written and never deployed is on the list too.</item>
/// </list>
/// <para>
/// Folders are watched, never polled. A change to a compose file or a <c>.env</c>, or a stack folder
/// appearing or going, rescans; anything else under the folder — a database writing its files into
/// a stack's data directory — is ignored, because it says nothing about which stacks exist.
/// </para>
/// </summary>
public sealed class StackCatalog : IDisposable
{
    readonly Lock _lock = new();
    readonly Dictionary<string, (DateTime Written, IReadOnlyList<ComposeServiceEntry> Services)> _declared = [];
    readonly List<FileSystemWatcher> _watchers = [];

    IReadOnlyList<StackDefinition> _definitions = [];
    Timer? _debounce;

    public StackCatalog(StackRegistry registry)
    {
        Registry = registry;
        Registry.Changed += OnRegistryChanged;
        Rescan();
        Watch();
    }

    public StackRegistry Registry { get; }

    /// <summary>The list may have changed: a folder appeared, a file was edited, the registry moved.</summary>
    public event Action? Changed;

    /// <summary>
    /// Scan again now rather than when the watcher next fires — after Dray itself has written a
    /// stack, so the page that opens next finds it.
    /// </summary>
    public void Refresh()
    {
        Rescan();
        Changed?.Invoke();
    }

    /// <summary>The stacks on <paramref name="host"/>, given its containers.</summary>
    /// <param name="onHost">Stacks whose files were found on the engine's host over SSH.</param>
    public IReadOnlyList<StackSummary> For(string? host, IEnumerable<ContainerSummary> containers, IReadOnlySet<string>? onHost = null)
    {
        IReadOnlyList<StackDefinition> definitions;
        lock (_lock) definitions = _definitions;

        return Merge(
            StackDiscovery.From(containers),
            definitions,
            host is null ? [] : Registry.KnownOn(host),
            Registry.StacksFolder,
            DeclaredServices,
            File.Exists,
            onHost);
    }

    readonly Dictionary<string, IReadOnlyList<ComposeServiceEntry>> _remoteDeclared = new(StringComparer.Ordinal);

    /// <summary>
    /// What a file on the engine's host declares, read over SSH by whoever probed it — so a remote
    /// stack's services not yet created are listed, as a local stack's are.
    /// </summary>
    public void SetRemoteDeclared(string path, IReadOnlyList<ComposeServiceEntry> services)
    {
        lock (_lock) _remoteDeclared[path] = services;
        Changed?.Invoke();
    }

    /// <summary>
    /// The files for a stack Dray would drive: the folder's definition, or the registry's record
    /// when the stack is down and only remembered.
    /// </summary>
    public StackDefinition? DefinitionFor(string host, string project)
    {
        lock (_lock)
        {
            if (_definitions.FirstOrDefault(d => d.Project == project) is { } definition) return definition;
        }

        return Registry.KnownOn(host).FirstOrDefault(r => r.Project == project) is { } known
            ? new StackDefinition(known.Project, known.WorkingDirectory, known.Files)
            : null;
    }

    /// <summary>
    /// Remember the stacks a host has containers for, when their files are on this machine.
    /// <para>
    /// Called as the host's containers change. A stack whose files are elsewhere is not remembered:
    /// once down there would be nothing here to bring it back up with. One the user forgot stays
    /// forgotten until they deploy it again.
    /// </para>
    /// </summary>
    public void Observe(string host, IReadOnlyList<StackSummary> observed)
    {
        foreach (var stack in observed)
        {
            if (stack.ContainerCount == 0 || stack.ConfigFiles.Count == 0) continue;
            if (!stack.ConfigFiles.All(File.Exists)) continue;
            if (Registry.IsForgotten(host, stack.Name)) continue;

            var directory = stack.WorkingDirectory ?? Path.GetDirectoryName(stack.ConfigFiles[0])!;
            Registry.Remember(new StackRecord(host, stack.Name, directory, stack.ConfigFiles));
        }
    }

    /// <summary>
    /// The merge, as a pure function so its rules can be tested without a disk or an engine.
    /// <para>
    /// A stack with containers always wins its name: what is running is the truth about it, and the
    /// files only add what the engine cannot say — a service with no containers yet, scaled to zero
    /// or never started, is still part of the stack and is listed.
    /// </para>
    /// </summary>
    internal static IReadOnlyList<StackSummary> Merge(
        IReadOnlyList<StackSummary> observed,
        IReadOnlyList<StackDefinition> definitions,
        IReadOnlyList<StackRecord> known,
        string? stacksFolder,
        Func<IReadOnlyList<string>, IReadOnlyList<ComposeServiceEntry>> declaredServices,
        Func<string, bool> exists,
        IReadOnlySet<string>? onHost = null)
    {
        var stacks = new Dictionary<string, StackSummary>(StringComparer.Ordinal);

        StackProvenance ProvenanceOf(string? directory, IReadOnlyList<string> files, string project, bool recordedOnHost = false)
        {
            if (recordedOnHost || onHost?.Contains(project) == true) return StackProvenance.Remote;
            if (files.Count == 0 || !files.All(exists)) return StackProvenance.Observed;

            var folder = directory ?? Path.GetDirectoryName(files[0]);
            return IsUnder(folder, stacksFolder) ? StackProvenance.Managed : StackProvenance.Adopted;
        }

        IReadOnlyList<StackService> WithDeclared(IReadOnlyList<StackService> services, IReadOnlyList<ComposeServiceEntry> declared)
        {
            var present = services.Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
            var missing = declared.Where(d => !present.Contains(d.Name)).Select(d => new StackService(d.Name, []));

            return [.. services.Concat(missing).OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)];
        }

        IReadOnlyList<ComposeServiceEntry> Declared(IReadOnlyList<string> files, StackProvenance provenance)
            => provenance == StackProvenance.Observed ? [] : declaredServices(files);

        static Dictionary<string, ComposeServiceEntry> Map(IReadOnlyList<ComposeServiceEntry> declared)
            => declared.GroupBy(d => d.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);

        foreach (var stack in observed)
        {
            var provenance = ProvenanceOf(stack.WorkingDirectory, stack.ConfigFiles, stack.Name);
            var declared = Declared(stack.ConfigFiles, provenance);

            stacks[stack.Name] = stack with
            {
                Provenance = provenance,
                Services = WithDeclared(stack.Services, declared),
                Declared = Map(declared),
            };
        }

        foreach (var definition in definitions)
        {
            if (stacks.ContainsKey(definition.Project)) continue;

            var provenance = ProvenanceOf(definition.WorkingDirectory, definition.Files, definition.Project);
            var declared = Declared(definition.Files, provenance);

            stacks[definition.Project] = new StackSummary
            {
                Name = definition.Project,
                ConfigFiles = definition.Files,
                WorkingDirectory = definition.WorkingDirectory,
                Provenance = provenance,
                WasDeployed = known.Any(k => k.Project == definition.Project),
                Services = WithDeclared([], declared),
                Declared = Map(declared),
            };
        }

        foreach (var record in known)
        {
            if (stacks.ContainsKey(record.Project)) continue;

            var provenance = ProvenanceOf(record.WorkingDirectory, record.Files, record.Project, record.OnHost);
            var declared = Declared(record.Files, provenance);

            stacks[record.Project] = new StackSummary
            {
                Name = record.Project,
                ConfigFiles = record.Files,
                WorkingDirectory = record.WorkingDirectory,
                Provenance = provenance,
                WasDeployed = true,
                Services = WithDeclared([], declared),
                Declared = Map(declared),
            };
        }

        return [.. stacks.Values.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Whether <paramref name="path"/> is inside <paramref name="folder"/> — strictly, never the folder itself.</summary>
    public static bool IsUnder(string? path, string? folder)
    {
        if (path is null || folder is null) return false;

        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(root, comparison);
    }

    /// <summary>
    /// The services a stack's files declare, across every file, read once per edit of each. A
    /// service named in two files — the base and its override — is the later file's entry, as
    /// compose merges it, so a profile or a build added in an override is not lost.
    /// </summary>
    IReadOnlyList<ComposeServiceEntry> DeclaredServices(IReadOnlyList<string> files)
    {
        var services = new List<ComposeServiceEntry>();

        foreach (var file in files)
        {
            // A file on the engine's host: whatever was last read of it over SSH, or nothing yet.
            if (!File.Exists(file))
            {
                IReadOnlyList<ComposeServiceEntry>? remote;
                lock (_lock) remote = _remoteDeclared.GetValueOrDefault(file);

                foreach (var entry in remote ?? []) Fold(services, entry);
                continue;
            }

            try
            {
                var written = File.GetLastWriteTimeUtc(file);
                IReadOnlyList<ComposeServiceEntry> entries;

                lock (_lock)
                {
                    if (_declared.TryGetValue(file, out var cached) && cached.Written == written)
                    {
                        entries = cached.Services;
                    }
                    else
                    {
                        entries = ComposeDocument.Parse(File.ReadAllText(file)).Services;
                        _declared[file] = (written, entries);
                    }
                }

                foreach (var entry in entries) Fold(services, entry);
            }
            catch (IOException)
            {
            }
        }

        return services;
    }

    /// <summary>Merge one file's entry for a service into the stack's, as compose merges an override.</summary>
    static void Fold(List<ComposeServiceEntry> services, ComposeServiceEntry entry)
    {
        var existing = services.FindIndex(s => s.Name == entry.Name);
        if (existing < 0) services.Add(entry);
        else services[existing] = services[existing] with
        {
            Builds = services[existing].Builds || entry.Builds,
            Profiles = entry.Profiles.Count > 0 ? entry.Profiles : services[existing].Profiles,
        };
    }

    // ---------------------------------------------------------------- folders

    void OnRegistryChanged()
    {
        // The registry changes when the folder setting changes, or a folder is adopted or
        // forgotten — and also every time a stack is remembered, which needs no rescan. Rescanning
        // anyway is bounded and cheap; watching the wrong folders is not.
        Rescan();
        Watch();
        Changed?.Invoke();
    }

    void Rescan()
    {
        var definitions = new List<StackDefinition>();

        if (Registry.StacksFolder is { } folder) definitions.AddRange(StackFolder.Scan(folder));

        foreach (var adopted in Registry.AdoptedFolders)
        {
            if (StackFolder.Read(adopted) is { } definition && definitions.All(d => d.Project != definition.Project))
                definitions.Add(definition);
        }

        lock (_lock) _definitions = definitions;
    }

    void Watch()
    {
        lock (_lock)
        {
            foreach (var watcher in _watchers) watcher.Dispose();
            _watchers.Clear();

            if (Registry.StacksFolder is { } folder && Directory.Exists(folder)) _watchers.Add(WatcherFor(folder, recursive: true));

            foreach (var adopted in Registry.AdoptedFolders.Where(Directory.Exists)) _watchers.Add(WatcherFor(adopted, recursive: false));
        }
    }

    FileSystemWatcher WatcherFor(string folder, bool recursive)
    {
        var watcher = new FileSystemWatcher(folder)
        {
            IncludeSubdirectories = recursive,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
        };

        watcher.Created += (_, e) => OnFileEvent(folder, e.FullPath);
        watcher.Deleted += (_, e) => OnFileEvent(folder, e.FullPath);
        watcher.Changed += (_, e) => OnFileEvent(folder, e.FullPath);
        watcher.Renamed += (_, e) => OnFileEvent(folder, e.FullPath);

        try
        {
            watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or PlatformNotSupportedException)
        {
            // Unwatchable — a network share, a folder that went away. The list still updates when
            // the registry does; it just will not notice a file dropped in by another program.
        }

        return watcher;
    }

    void OnFileEvent(string root, string path)
    {
        if (!Matters(root, path)) return;

        // Debounced: an editor saving a file raises three events, and a git checkout raises hundreds.
        lock (_lock)
        {
            _debounce?.Dispose();
            _debounce = new Timer(_ =>
            {
                Rescan();
                Changed?.Invoke();
            }, null, TimeSpan.FromMilliseconds(300), Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// Whether a change under a watched folder could change which stacks exist or what they declare.
    /// </summary>
    internal static bool Matters(string root, string path)
    {
        var name = Path.GetFileName(path);

        if (StackFolder.ComposeFileNames.Contains(name) || StackFolder.OverrideFileNames.Contains(name) || name == DotEnvFile.FileName)
            return true;

        // A folder appearing or going near the top is a stack appearing or going. Deeper than the
        // scan reaches is a stack's own data, and the reason this filter exists.
        var relative = Path.GetRelativePath(root, path);
        var depth = relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries).Length;

        return depth <= 1 && !name.StartsWith('.');
    }

    public void Dispose()
    {
        Registry.Changed -= OnRegistryChanged;

        lock (_lock)
        {
            _debounce?.Dispose();
            foreach (var watcher in _watchers) watcher.Dispose();
            _watchers.Clear();
        }
    }
}
