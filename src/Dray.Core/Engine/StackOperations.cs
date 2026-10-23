using Dray.Core.Model;

namespace Dray.Core.Engine;

public enum OperationState
{
    Running,
    Succeeded,
    Failed,
    Cancelled,
}

/// <summary>
/// One action on one stack, from first command to last, and everything it printed.
/// <para>
/// Outlives the page that started it. A deploy that pulls for three minutes keeps going when the
/// user looks at another stack, and its output is still here when they come back — the replay
/// Dockge gives anyone who joins a terminal late.
/// </para>
/// </summary>
public sealed class StackOperation
{
    /// <summary>Lines kept. The tail is what anyone reads; a large pull prints thousands.</summary>
    public const int MaxLines = 2000;

    readonly Lock _lock = new();
    readonly List<ComposeOutput> _lines = [];
    readonly CancellationTokenSource _cts = new();

    internal StackOperation(string host, string stack, string label)
    {
        Host = host;
        Stack = stack;
        Label = label;
        StartedAt = DateTimeOffset.Now;
    }

    public string Host { get; }

    public string Stack { get; }

    /// <summary>What is being done, as a sentence fragment: "Deploy", "Restart web".</summary>
    public string Label { get; }

    public DateTimeOffset StartedAt { get; }

    public DateTimeOffset? FinishedAt { get; private set; }

    public OperationState State { get; private set; } = OperationState.Running;

    /// <summary>The exit code of the step that ended the operation. Null while running, or when unreported.</summary>
    public int? ExitCode { get; private set; }

    public bool IsRunning => State == OperationState.Running;

    /// <summary>Raised on every line and when the operation ends.</summary>
    public event Action? Changed;

    public IReadOnlyList<ComposeOutput> Lines
    {
        get { lock (_lock) return [.. _lines]; }
    }

    public void Cancel() => _cts.Cancel();

    internal CancellationToken Token => _cts.Token;

    internal void Add(ComposeOutput line)
    {
        lock (_lock)
        {
            _lines.Add(line);
            if (_lines.Count > MaxLines) _lines.RemoveRange(0, _lines.Count - MaxLines);
        }

        Changed?.Invoke();
    }

    internal void Finish(OperationState state, int? exitCode)
    {
        State = state;
        ExitCode = exitCode;
        FinishedAt = DateTimeOffset.Now;
        Changed?.Invoke();
    }
}

/// <summary>
/// Running stack actions, one at a time per stack.
/// <para>
/// One at a time because two compose commands on one project race: a down while an up is
/// half-way leaves containers neither of them meant. The second is refused with the first's name
/// rather than queued — a queued down after an up is rarely what someone who clicked twice wanted.
/// </para>
/// </summary>
public sealed class StackOperations(EngineManager engine)
{
    readonly Lock _lock = new();
    readonly Dictionary<(string Host, string Stack), StackOperation> _latest = [];

    /// <summary>An operation started, printed, or finished, on any stack.</summary>
    public event Action<StackOperation>? Changed;

    /// <summary>The running or most recent operation on a stack on the selected host.</summary>
    public StackOperation? For(string stack)
    {
        if (engine.Selected is not { } host) return null;

        lock (_lock) return _latest.GetValueOrDefault((host.Id, stack));
    }

    public bool IsBusy(string stack) => For(stack)?.IsRunning == true;

    /// <summary>
    /// Start an action. Returns the operation, or null when the stack already has one running.
    /// </summary>
    /// <param name="steps">Compose argument lists from <see cref="StackCommands.For"/>, run in order.</param>
    /// <param name="finished">Called once at the end, whatever the outcome — for the notification.</param>
    public StackOperation? Start(
        StackSummary stack,
        string label,
        IReadOnlyList<IReadOnlyList<string>> steps,
        Func<StackOperation, Task>? finished = null)
    {
        if (engine.Selected is not { } host) return null;

        StackOperation operation;

        lock (_lock)
        {
            if (_latest.TryGetValue((host.Id, stack.Name), out var current) && current.IsRunning) return null;

            operation = new StackOperation(host.Id, stack.Name, label);
            _latest[(host.Id, stack.Name)] = operation;
        }

        operation.Changed += () => Changed?.Invoke(operation);
        Changed?.Invoke(operation);

        _ = Task.Run(() => RunAsync(operation, stack, steps, finished));
        return operation;
    }

    async Task RunAsync(
        StackOperation operation,
        StackSummary stack,
        IReadOnlyList<IReadOnlyList<string>> steps,
        Func<StackOperation, Task>? finished)
    {
        int? exitCode = null;
        var state = OperationState.Succeeded;

        try
        {
            foreach (var step in steps)
            {
                // Each step announces itself as the command it is, so the output reads as a
                // terminal session would — and a failure is visibly the failure of that command.
                var shown = await engine.DescribeComposeAsync(stack, step, operation.Token).ConfigureAwait(false);
                operation.Add(new ComposeOutput("$ " + (shown ?? string.Join(' ', step)), IsError: false));

                exitCode = null;

                await foreach (var line in engine.RunComposeAsync(stack, step, operation.Token).ConfigureAwait(false))
                {
                    if (line.IsExit) exitCode = line.ExitCode;
                    else operation.Add(line);
                }

                // A reported zero and nothing else counts. The next step does not run after a
                // failure: an up after a pull that failed would deploy the images it failed to replace.
                if (exitCode != 0)
                {
                    state = OperationState.Failed;
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            state = OperationState.Cancelled;
        }
        catch (Exception ex)
        {
            operation.Add(new ComposeOutput(ex.Message, IsError: true));
            state = OperationState.Failed;
        }

        operation.Finish(state, exitCode);

        if (finished is not null)
        {
            try
            {
                await finished(operation).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The page that asked for the notification may be gone, and with it its shell.
            }
        }
    }
}
