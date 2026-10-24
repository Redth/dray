using Dray.Core.Model;
using Dray.Core.Shell;
using Dray.Ui.Components;

namespace Dray.Ui.Services;

/// <summary>
/// Adding to a compose file and editing what is in it through forms, for any editor of one — a
/// stack's file and one being written in the New stack window alike.
/// <para>
/// Every change is computed against the editor's text as it is now, made by <see cref="ComposeEdits"/>
/// or <see cref="ComposeResources"/> as line edits that leave every other byte alone, and handed to
/// the editor as one undoable edit. Nothing is saved: the file is the user's to read and save.
/// </para>
/// </summary>
public static class ComposeEditorActions
{
    /// <summary>The Add menu: what a compose file declares, and the two things most often added to a service.</summary>
    public static IReadOnlyList<RowAction> AddMenu(bool hasServices) =>
    [
        new("add:service", "Service…", IconRef.Container, "A service from an image"),
        new("add:volume", "Volume…", IconRef.Volume, "Storage that outlives the containers using it"),
        new("add:network", "Network…", IconRef.Network, "A network some of the services share"),
        new("add:port", "Port…", IconRef.Port, "Publish a port on one of the services", Disabled: !hasServices),
        new("add:variable", "Variable…", IconRef.Settings, "Set an environment variable on one of the services", Disabled: !hasServices),
    ];

    /// <summary>
    /// The lenses over a file: Add above each section, Edit above each volume and network. Drawn
    /// from the text as it stands, so they move with it as it is edited.
    /// </summary>
    public static IReadOnlyList<EditorLens> Lenses(string text)
    {
        var document = ComposeDocument.Parse(text);
        if (document.SyntaxError is not null) return [];

        var lenses = new List<EditorLens>();

        foreach (var (section, title, id) in (ReadOnlySpan<(string, string, string)>)
                 [("services", "Add service…", "add:service"), ("volumes", "Add volume…", "add:volume"), ("networks", "Add network…", "add:network")])
        {
            if (document.LineOfKey(section) is { } line) lenses.Add(new EditorLens(line, title, id));
        }

        foreach (var entry in ComposeResources.List(text))
        {
            var noun = entry.Kind == ComposeResourceKind.Volume ? "volume" : "network";
            lenses.Add(new EditorLens(entry.Line, $"Edit {noun}…", $"edit:{noun}:{entry.Name}"));
        }

        return [.. lenses.OrderBy(l => l.Line)];
    }

    /// <summary>
    /// Run an Add or Edit command against the editor. Returns what to tell the user — or null when
    /// they cancelled, which needs no sentence.
    /// </summary>
    public static async Task<(string Message, bool Ok)?> RunAsync(IShellBridge shell, CodeEditor editor, string command)
    {
        if (await editor.GetTextAsync() is not { } text) return null;

        var document = ComposeDocument.Parse(text);
        var services = document.Services.Select(s => s.Name).ToList();
        var parts = command.Split(':', 3);

        return (parts[0], parts.ElementAtOrDefault(1)) switch
        {
            ("add", "volume") => await AddResourceAsync(shell, editor, text, ComposeResourceKind.Volume, services),
            ("add", "network") => await AddResourceAsync(shell, editor, text, ComposeResourceKind.Network, services),
            ("edit", "volume") when parts.Length == 3 => await EditResourceAsync(shell, editor, text, ComposeResourceKind.Volume, parts[2], services),
            ("edit", "network") when parts.Length == 3 => await EditResourceAsync(shell, editor, text, ComposeResourceKind.Network, parts[2], services),
            ("add", "service" or "port" or "variable") => await QuickEditAsync(shell, editor, text, parts[1], services),
            _ => null,
        };
    }

    static async Task<(string, bool)?> AddResourceAsync(IShellBridge shell, CodeEditor editor, string text, ComposeResourceKind kind, IReadOnlyList<string> services)
    {
        var request = new ComposeResourceRequest
        {
            Kind = kind,
            Taken = [.. ComposeResources.List(text).Where(e => e.Kind == kind).Select(e => e.Name)],
            Services = services,
            MountInto = "",
        };

        if (!await ComposeResourceDialog.ShowAsync(shell, request) || !request.IsComplete) return null;

        var resource = request.ToResource();
        var made = ComposeResources.Add(text, resource);
        if (made.Declined is { } declined) return (declined, false);

        var next = ComposeChange.Apply(text, made.Changes);
        var notes = new List<string>();

        // What uses it, in the same edit: a volume nothing mounts and a network nothing joins do nothing.
        if (kind == ComposeResourceKind.Volume && request.MountInto.Length > 0)
            next = Then(next, ComposeEdits.AddVolumeMount(next, request.MountInto, resource.Name, request.MountPath.Trim()), notes);

        if (kind == ComposeResourceKind.Network)
        {
            foreach (var service in services.Where(request.Join.Contains))
                next = Then(next, ComposeEdits.JoinNetwork(next, service, resource.Name), notes);
        }

        await editor.ReplaceTextAsync(next);

        var noun = kind == ComposeResourceKind.Volume ? "volume" : "network";
        return notes.Count == 0
            ? ($"Added the {noun} {resource.Name} to the file. Nothing is saved until you save it.", true)
            : ($"Added the {noun} {resource.Name}, but not everywhere: {string.Join(" ", notes)}", false);
    }

    static async Task<(string, bool)?> EditResourceAsync(IShellBridge shell, CodeEditor editor, string text, ComposeResourceKind kind, string name, IReadOnlyList<string> services)
    {
        var noun = kind == ComposeResourceKind.Volume ? "volume" : "network";
        if (ComposeResources.Read(text, kind, name) is not { } resource) return ($"There is no {noun} called {name} in the file any more.", false);

        var request = ComposeResourceRequest.From(resource, services);
        if (!await ComposeResourceDialog.ShowAsync(shell, request) || !request.IsComplete) return null;

        // Read again: the file may have been typed into while the form was open.
        if (await editor.GetTextAsync() is not { } now) return null;

        var made = ComposeResources.Update(now, request.ToResource());
        if (made.Declined is { } declined) return (declined, false);
        if (made.Changes.Count == 0) return null;

        await editor.ReplaceTextAsync(ComposeChange.Apply(now, made.Changes));
        return ($"Changed the {noun} {name} in the file. Nothing is saved until you save it.", true);
    }

    /// <summary>A service, a port or a variable: the three small forms of <see cref="QuickEditDialog"/>.</summary>
    static async Task<(string, bool)?> QuickEditAsync(IShellBridge shell, CodeEditor editor, string text, string kind, IReadOnlyList<string> services)
    {
        var request = new QuickEditRequest
        {
            Kind = kind switch { "port" => QuickEditKind.Port, "variable" => QuickEditKind.Variable, _ => QuickEditKind.Service },
            Services = services,
            Service = services.FirstOrDefault() ?? "",
        };

        var title = request.Kind switch
        {
            QuickEditKind.Service => "Add a service",
            QuickEditKind.Port => "Publish a port",
            _ => "Set a variable",
        };

        var choice = await shell.ShowDialogAsync(new DialogRequest(
            title,
            typeof(QuickEditDialog),
            new Dictionary<string, object?> { [nameof(QuickEditDialog.Request)] = request },
            [DialogButton.Cancel(), DialogButton.Confirm("add", "Add")],
            Size: DialogSize.Small));

        if (choice != "add") return null;
        if (!request.IsComplete) return (request.Problem ?? "Nothing was added: a field it needs was left empty.", false);

        var made = request.Kind switch
        {
            QuickEditKind.Service => ComposeEdits.AddService(text, request.Name.Trim(), request.Image.Trim(), request.Port.Length > 0 ? [request.Port.Trim()] : null),
            QuickEditKind.Port => ComposeEdits.AddPort(text, request.Service, request.Port.Trim()),
            _ => ComposeEdits.AddEnvironment(text, request.Service, request.Key.Trim(), request.Value),
        };

        if (made.Declined is { } declined) return (declined, false);

        await editor.ReplaceTextAsync(ComposeChange.Apply(text, made.Changes));
        return ("Added to the file. Nothing is saved until you save it.", true);
    }

    static string Then(string text, ComposeEdits.Result result, List<string> notes)
    {
        if (result.Declined is { } declined)
        {
            notes.Add(declined);
            return text;
        }

        return ComposeChange.Apply(text, result.Changes);
    }
}
