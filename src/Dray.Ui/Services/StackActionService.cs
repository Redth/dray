using Dray.Core.Engine;
using Dray.Core.Model;
using Dray.Core.Shell;
using Dray.Ui.Components;

namespace Dray.Ui.Services;

/// <summary>What the Deploy dialog reads and writes: the options chosen, and how to show them as a command.</summary>
public sealed class DeployRequest(Func<DeployOptions, Task<string?>> describe)
{
    public DeployOptions Options { get; set; } = DeployOptions.OnlyChanged;

    public Func<DeployOptions, Task<string?>> Describe { get; } = describe;

    /// <summary>Compose's warnings about the files, which do not stop a deploy but are worth reading first.</summary>
    public IReadOnlyList<ComposeProblem> Warnings { get; init; } = [];

    /// <summary>Per service, what the files say compared with what is running. Null when unknown.</summary>
    public IReadOnlyDictionary<string, ServiceDrift>? Drift { get; init; }

    /// <summary>Per service, the mounted files edited since Dray last deployed it.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> ChangedFiles { get; init; } = new Dictionary<string, IReadOnlyList<string>>();
}

/// <summary>What Deploy would change, with Dray's addition: files compose's hash does not cover.</summary>
public sealed record StackChanges(
    IReadOnlyDictionary<string, ServiceDrift> Drift,
    IReadOnlyDictionary<string, IReadOnlyList<string>> ChangedFiles);

/// <summary>
/// What can be done to a stack and to its services, and doing it.
/// <para>
/// Shared by the list and the stack's own page because the rules have to be identical on both: which
/// actions a stack in this state offers, which ones are typed rather than clicked, and what a
/// delete may touch. A page that assembled its own would eventually assemble a weaker one — the
/// reason <see cref="PruneService"/> exists too.
/// </para>
/// </summary>
public sealed class StackActionService(
    EngineManager engine,
    StackOperations operations,
    IShellBridge shell,
    Microsoft.AspNetCore.Components.NavigationManager nav)
{
    // ---------------------------------------------------------------- what is offered

    /// <summary>
    /// The actions for a stack, the one its state invites first.
    /// <para>
    /// Offered by what is true of this stack rather than all at once. A stack whose files are on
    /// another machine has no Deploy, because there is nothing here to deploy it from; one with no
    /// containers has no Stop. Delete exists only for a stack in the stacks folder, and Forget only
    /// for one adopted from outside it.
    /// </para>
    /// </summary>
    public IReadOnlyList<RowAction> ActionsFor(StackSummary stack)
    {
        if (engine.Selected?.Capabilities.SupportsCompose != true) return [];

        var busy = operations.IsBusy(stack.Name);
        var files = stack.Provenance != StackProvenance.Observed;
        var name = stack.Name;

        var deploy = new RowAction("deploy", $"Deploy {name}…", IconRef.Push, "Deploy — create what is missing and apply what changed", Disabled: busy);
        var start = new RowAction("start", $"Start {name}", IconRef.Play, "Start the stack's containers", Disabled: busy);
        var stop = new RowAction("stop", $"Stop {name}", IconRef.Stop, "Stop every container, keeping them", Disabled: busy);
        var restart = new RowAction("restart", $"Restart {name}", IconRef.Restart, "Restart every container", Disabled: busy);
        var update = new RowAction("update", $"Pull and update {name}", IconRef.Pull, "Pull newer images, then apply them if the stack is up", Disabled: busy);
        var down = new RowAction("down", $"Bring {name} down", IconRef.Stop, "Stop and remove the containers and networks; volumes are kept", Danger: true, Disabled: busy);
        var downVolumes = new RowAction("down-volumes", $"Down {name} and delete its volumes…", IconRef.Trash, "Remove containers, networks and named volumes", Danger: true, Disabled: busy);
        var forget = new RowAction("forget", $"Forget {name}", IconRef.Close, "Stop listing this stack. Its files and containers are left alone", Disabled: busy);
        var delete = new RowAction("delete", $"Delete {name}…", IconRef.Trash, "Bring it down and delete its folder", Danger: true, Disabled: busy);

        var actions = new List<RowAction>();
        var up = stack.ContainerCount > 0;

        // The first is the one the row shows: what this stack's state invites.
        switch (stack.State)
        {
            case StackState.Draft or StackState.Down:
                if (files) actions.Add(deploy);
                break;

            case StackState.Stopped:
                actions.Add(start);
                if (files) actions.Add(deploy);
                break;

            case StackState.Partial:
                if (files) actions.Add(deploy);
                actions.AddRange([stop, restart]);
                break;

            default:
                actions.AddRange([stop, restart]);
                if (files) actions.Add(deploy);
                break;
        }

        if (files) actions.Add(update);

        if (files && stack.Builds)
            actions.Add(new RowAction("build", $"Build {name}", IconRef.Build, "Build the images for services with a build: section", Disabled: busy));

        if (up) actions.AddRange([down, downVolumes]);

        if (stack.Provenance is StackProvenance.Adopted or StackProvenance.Remote) actions.Add(forget);
        if (stack.Provenance == StackProvenance.Managed) actions.Add(delete);

        return actions;
    }

    /// <summary>The actions for one service of a stack.</summary>
    public IReadOnlyList<RowAction> ServiceActionsFor(StackSummary stack, StackService service)
    {
        var actions = new List<RowAction> { new("logs", $"Logs for {service.Name}", IconRef.Logs, "Read this service's output") };

        var running = service.Replicas.FirstOrDefault(r => r.State == DockerState.Running);

        if (running is not null && engine.Selected?.Capabilities.SupportsShell != false)
            actions.Add(new RowAction("shell", $"Shell in {service.Name}", IconRef.Terminal, "Open a terminal in a running container of this service"));

        if (engine.Selected?.Capabilities.SupportsCompose != true) return actions;

        var busy = operations.IsBusy(stack.Name);
        var files = stack.Provenance != StackProvenance.Observed;

        if (service.RunningCount == 0 && (files || service.Replicas.Count > 0))
            actions.Add(new RowAction("start", $"Start {service.Name}", IconRef.Play, "Start this service, creating it if it has no container", Disabled: busy));

        if (service.RunningCount > 0)
            actions.Add(new RowAction("stop", $"Stop {service.Name}", IconRef.Stop, "Stop this service", Disabled: busy));

        if (service.Replicas.Count > 0)
            actions.Add(new RowAction("restart", $"Restart {service.Name}", IconRef.Restart, "Restart this service's containers", Disabled: busy));

        if (files)
        {
            actions.Add(new RowAction("recreate", $"Recreate {service.Name}", IconRef.Refresh, "Recreate from its configuration, without touching what it depends on", Disabled: busy));
            actions.Add(new RowAction("update", $"Pull and update {service.Name}", IconRef.Pull, "Pull its image, then recreate it if it is running", Disabled: busy));

            if (stack.Declared.GetValueOrDefault(service.Name)?.Builds == true)
                actions.Add(new RowAction("build", $"Build {service.Name}", IconRef.Build, "Build this service's image from its build: section", Disabled: busy));
        }

        return actions;
    }

    // ---------------------------------------------------------------- doing it

    /// <summary>
    /// Run a stack action, asking first where it has to.
    /// </summary>
    /// <returns>A sentence to show where the user is, or null when there is nothing to say.</returns>
    public async Task<string?> RunAsync(StackSummary stack, string action)
    {
        switch (action)
        {
            case "deploy":
                return await DeployAsync(stack);

            case "start":
                return Start(stack, "Start", StackCommands.For(StackVerb.Start, stack));

            case "stop":
                return Start(stack, "Stop", StackCommands.For(StackVerb.Stop, stack));

            case "restart":
                return Start(stack, "Restart", StackCommands.For(StackVerb.Restart, stack));

            case "update":
                return Start(stack, "Pull and update", StackCommands.For(StackVerb.Update, stack), RecordMounts(stack));

            case "build":
                return Start(stack, "Build", StackCommands.For(StackVerb.Build, stack));

            case "down":
            {
                var answer = await shell.ConfirmDestructiveAsync(new DestructiveConfirm(
                    $"Bring {stack.Name} down?",
                    "Every container in the stack is stopped and removed, and so are its networks. Named volumes "
                    + "are kept, and Deploy brings it back. Anything written inside a container rather than to a "
                    + "volume is lost.",
                    "Bring down"));

                return answer == ConfirmResult.Confirm ? Start(stack, "Down", StackCommands.For(StackVerb.Down, stack)) : null;
            }

            case "down-volumes":
                return await DownWithVolumesAsync(stack);

            case "forget":
                if (engine.Selected is not { } host || engine.Catalog is not { } catalog) return null;

                catalog.Registry.Forget(host.Id, stack.Name, stack.Provenance == StackProvenance.Remote ? null : stack.WorkingDirectory);
                return stack.ContainerCount > 0
                    ? $"Dray stopped tracking {stack.Name}. Its containers are still running, so it stays listed until they are gone; its files are untouched."
                    : $"Dray stopped tracking {stack.Name}. Its files were not touched.";

            case "delete":
                return await DeleteAsync(stack);

            default:
                return null;
        }
    }

    /// <summary>Run a service action. Logs and Shell are navigation, and the page handles them.</summary>
    public string? RunService(StackSummary stack, StackService service, string action)
    {
        var verb = action switch
        {
            "build" => StackVerb.Build,
            "start" => StackVerb.Start,
            "stop" => StackVerb.Stop,
            "restart" => StackVerb.Restart,
            "recreate" => StackVerb.Recreate,
            "update" => StackVerb.Update,
            _ => (StackVerb?)null,
        };

        if (verb is not { } chosen) return null;

        var label = action switch
        {
            "update" => $"Pull and update {service.Name}",
            _ => $"{char.ToUpperInvariant(action[0])}{action[1..]} {service.Name}",
        };

        var recreates = chosen is StackVerb.Recreate or StackVerb.Update;
        return Start(stack, label, StackCommands.For(chosen, stack, service: service.Name), recreates ? RecordMounts(stack, service.Name) : null);
    }

    /// <summary>
    /// What Deploy would change: compose's own drift, and the services whose mounted files were
    /// edited since Dray last deployed them, which compose's hash does not cover.
    /// </summary>
    public async Task<StackChanges?> ChangesAsync(StackSummary stack, CancellationToken ct = default)
    {
        if (await engine.DriftAsync(stack, ct) is not { } drift) return null;

        // Mounted files are hashed on this machine only; on an SSH host that would mean reading
        // every one over the connection on every visit.
        var changed = engine.Selected is { } host && operations.History is { } history && stack.Provenance is StackProvenance.Managed or StackProvenance.Adopted
            ? StackMounts.Changed(StackMounts.Snapshot(stack.ConfigFiles, stack.WorkingDirectory), history.MountsAtDeploy(host.Id, stack.Name))
            : new Dictionary<string, IReadOnlyList<string>>();

        return new StackChanges(StackDrift.WithMounts(drift, changed), changed);
    }

    /// <summary>
    /// After a run that brought containers up, remember what their mounted files were — the
    /// baseline the next "edited since deploy" is measured from.
    /// </summary>
    Func<StackOperation, Task> RecordMounts(StackSummary stack, string? service = null) => operation =>
    {
        if (operation.State == OperationState.Succeeded && operations.History is { } history && stack.Provenance is StackProvenance.Managed or StackProvenance.Adopted)
            history.RecordMounts(operation.Host, stack.Name, StackMounts.Snapshot(stack.ConfigFiles, stack.WorkingDirectory), service);

        return Task.CompletedTask;
    };

    /// <summary>
    /// Deploy, after compose has looked at the files.
    /// <para>
    /// Checked before the dialog rather than left to <c>up</c>: a file compose refuses is caught
    /// here, with its problems listed and a way to the line, instead of as a failed run halfway
    /// down an output panel. What compose merely warns about is shown in the dialog and does not
    /// stop anything — it is compose's call that those are not errors, not Dray's.
    /// </para>
    /// </summary>
    async Task<string?> DeployAsync(StackSummary stack)
    {
        var config = await engine.ValidateComposeAsync(stack);

        if (!config.IsValid)
        {
            var open = await shell.ShowDialogAsync(new DialogRequest(
                $"{stack.Name} cannot be deployed yet",
                typeof(ComposeProblemList),
                new Dictionary<string, object?>
                {
                    [nameof(ComposeProblemList.Problems)] = config.Problems,
                    [nameof(ComposeProblemList.Note)] = "Compose refused the files, so Deploy would fail before it changed anything. Fix these and deploy again.",
                },
                [DialogButton.Cancel("Close"), DialogButton.Confirm("open", "Open compose file")],
                Subtitle: "What docker compose config reported"));

            if (open == "open") nav.NavigateTo($"/stacks/{Uri.EscapeDataString(stack.Name)}?tab=compose");
            return null;
        }

        var changes = await ChangesAsync(stack);

        var request = new DeployRequest(options => engine.DescribeComposeAsync(stack, StackCommands.For(StackVerb.Deploy, stack, options)[0]))
        {
            Warnings = [.. config.Problems.Where(p => p.Severity == ProblemSeverity.Warning)],
            Drift = changes?.Drift,
            ChangedFiles = changes?.ChangedFiles ?? new Dictionary<string, IReadOnlyList<string>>(),
        };

        var choice = await shell.ShowDialogAsync(new DialogRequest(
            $"Deploy {stack.Name}",
            typeof(DeployDialog),
            new Dictionary<string, object?> { [nameof(DeployDialog.Request)] = request },
            [DialogButton.Cancel(), DialogButton.Confirm("deploy", "Deploy")],
            Subtitle: engine.Selected is { } host ? $"To {host.Name}" : null));

        if (choice != "deploy") return null;

        // Deploying a stack is asking Dray to track it, whatever it was told before.
        if (engine.Selected is { } selected) engine.Catalog?.Registry.Unforget(selected.Id, stack.Name);

        var steps = StackCommands.For(StackVerb.Deploy, stack, request.Options).ToList();

        // Compose would leave these running the old file; recreating them is what "only what
        // changed" means once a mounted file counts as a change.
        if (!request.Options.ForceRecreate && request.ChangedFiles.Count > 0)
            steps.Add(["up", "-d", "--no-deps", "--force-recreate", .. request.ChangedFiles.Keys.Order(StringComparer.Ordinal)]);

        return Start(stack, "Deploy", steps, RecordMounts(stack));
    }

    async Task<string?> DownWithVolumesAsync(StackSummary stack)
    {
        // Named for real: the confirmation says which volumes, not "its volumes". A list read now,
        // from the engine, of the volumes compose labelled as this project's.
        IReadOnlyList<VolumeSummary> volumes;

        try
        {
            volumes = [.. (await engine.ListVolumesAsync()).Where(v => v.Stack == stack.Name)];
        }
        catch (Exception ex)
        {
            return $"Could not list {stack.Name}'s volumes: {ex.Message}";
        }

        var named = volumes.Count == 0
            ? "It has no named volumes right now, so this removes only what Down would."
            : $"These volumes and everything in them are deleted: {string.Join(", ", volumes.Select(v => v.Name))}. This cannot be undone.";

        var answer = await shell.ConfirmDestructiveAsync(new DestructiveConfirm(
            $"Bring {stack.Name} down and delete its volumes?",
            $"Every container and network in the stack is removed. {named}",
            "Delete volumes",
            TypeToConfirm: stack.Name));

        return answer == ConfirmResult.Confirm
            ? Start(stack, "Down and delete volumes", StackCommands.For(StackVerb.DownWithVolumes, stack))
            : null;
    }

    /// <summary>
    /// Bring a stack down and delete its folder.
    /// <para>
    /// Only ever a folder in a stacks folder, checked again here rather than trusted from the list,
    /// and only after compose has taken the stack down: deleting the files of a stack that is still
    /// running leaves containers nothing can manage. If the down fails the files stay, and the
    /// output says why — the stranded half-deleted stack Dockge leaves behind is the other outcome.
    /// </para>
    /// </summary>
    async Task<string?> DeleteAsync(StackSummary stack)
    {
        var stacksFolders = engine.Catalog?.Registry.StacksFolders ?? [];

        if (stack.Provenance != StackProvenance.Managed || stack.WorkingDirectory is not { } folder || StackCatalog.FolderOf(folder, stacksFolders) is null)
            return $"{stack.Name} is not in a stacks folder, so Dray will not delete its files. Forget it instead.";

        var answer = await shell.ConfirmDestructiveAsync(new DestructiveConfirm(
            $"Delete {stack.Name}?",
            $"The stack is brought down — its containers and networks removed, its named volumes kept — and then "
            + $"the folder {folder} is deleted, with every file in it. This cannot be undone.",
            "Delete stack",
            TypeToConfirm: stack.Name));

        if (answer != ConfirmResult.Confirm) return null;

        var host = engine.Selected?.Id;

        return Start(stack, "Delete", StackCommands.For(StackVerb.Down, stack), async operation =>
        {
            if (operation.State != OperationState.Succeeded)
            {
                await shell.NotifyAsync($"{stack.Name} was not deleted", "Compose could not bring it down, so its files were kept.", NoticeKind.Error);
                return;
            }

            try
            {
                Directory.Delete(folder, recursive: true);
                if (host is not null) engine.Catalog?.Registry.Forget(host, stack.Name, folder);
            }
            catch (Exception ex)
            {
                await shell.NotifyAsync($"{stack.Name} is down, but its folder remains", ex.Message, NoticeKind.Error);
            }
        });
    }

    /// <returns>Null on starting, or why it could not.</returns>
    string? Start(
        StackSummary stack,
        string label,
        IReadOnlyList<IReadOnlyList<string>> steps,
        Func<StackOperation, Task>? then = null)
    {
        var host = engine.Selected?.Name;

        var operation = operations.Start(stack, label, steps, async finished =>
        {
            if (then is not null) await then(finished);

            // Lands only when the window is not in front, so watching it work is not reported twice.
            if (finished.State == OperationState.Succeeded)
                await shell.NotifyAsync($"{label} finished", $"{stack.Name} on {host}.", NoticeKind.Success);
            else if (finished.State == OperationState.Failed)
                await shell.NotifyAsync($"{label} failed", $"{stack.Name}: compose exited with {finished.ExitCode?.ToString() ?? "no code"}.", NoticeKind.Error);
        });

        return operation is null ? $"{stack.Name} is already busy with {operations.For(stack.Name)?.Label}." : null;
    }
}
