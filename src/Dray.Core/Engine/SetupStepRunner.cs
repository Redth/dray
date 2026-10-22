using System.Diagnostics;

namespace Dray.Core.Engine;

/// <summary>How a setup step ended.</summary>
/// <param name="Succeeded">Whether the command reported success.</param>
/// <param name="RestartRequired">
/// The change is made and takes effect after a restart. A distinct outcome from plain success,
/// because a user told only "done" will try the thing and find it still missing.
/// </param>
/// <param name="Detail">A sentence for the user. Null when there is nothing to add.</param>
public sealed record SetupStepResult(bool Succeeded, bool RestartRequired, string? Detail)
{
    public static readonly SetupStepResult Cancelled =
        new(false, false, "Cancelled — nothing was changed.");
}

/// <summary>
/// Runs a <see cref="SetupStep"/>, elevated and in the open.
/// <para>
/// Elevation means <c>UseShellExecute</c>, which means the child's output cannot be redirected —
/// so the command runs in its own visible window and the user watches it happen. That is a
/// constraint of the platform rather than a choice, and it happens to be the right behaviour: the
/// command was shown before it was run, and now its output is too. Dray never changes this machine
/// behind a spinner.
/// </para>
/// </summary>
public sealed class SetupStepRunner
{
    /// <summary>
    /// What DISM returns when it has made the change and needs a restart to finish. It is a
    /// success, and reporting it as a failure would send the user to fix something that is done.
    /// </summary>
    const int RestartRequiredExitCode = 3010;

    /// <summary>What Windows returns when the user dismissed the User Account Control prompt.</summary>
    const int ElevationCancelled = 1223;

    /// <summary>What <c>pkexec</c> returns when the user dismissed polkit's password dialog.</summary>
    const int PolkitDismissed = 126;

    /// <summary>
    /// Run a step inside Dray, handing each line of its output to <paramref name="output"/> as it
    /// arrives.
    /// <para>
    /// For the steps that do not need Dray itself elevated: installing a distribution, installing an
    /// engine inside one as its root, installers that raise their own prompt. Their output is the
    /// only sign that a long step is moving, so it is shown rather than hidden behind a spinner —
    /// and stdout and stderr together, in arrival order, because these tools report progress on one
    /// and results on the other.
    /// </para>
    /// </summary>
    public async Task<SetupStepResult> RunInAppAsync(SetupStep step, Action<string> output, CancellationToken ct = default)
    {
        var info = new ProcessStartInfo(step.FileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in step.Arguments) info.ArgumentList.Add(argument);

        // wsl.exe writes UTF-16 without it. Harmless to everything else, which ignores it.
        info.Environment["WSL_UTF8"] = "1";

        foreach (var (key, value) in step.Environment ?? new Dictionary<string, string>()) info.Environment[key] = value;

        var restartDistro = false;

        void Line(string? line)
        {
            if (line is null) return;

            // Builds of wsl.exe that ignore WSL_UTF8 arrive with a NUL after every character.
            line = WslDistroReader.Decode(line);

            if (line.Trim() == WindowsSetupPlan.RestartDistroMarker)
            {
                restartDistro = true;
                return;
            }

            output(line);
        }

        try
        {
            using var process = Process.Start(info);
            if (process is null) return new SetupStepResult(false, false, $"Could not start {step.FileName}.");

            process.OutputDataReceived += (_, e) => Line(e.Data);
            process.ErrorDataReceived += (_, e) => Line(e.Data);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            try
            {
                await process.WaitForExitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Unlike a step in its own window, this one is Dray's, and nobody else can see it
                // to stop it. An install abandoned half-way is still better ended than orphaned.
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception)
                {
                    // Already exited between the cancellation and the kill.
                }

                return new SetupStepResult(false, false, "Stopped. Whatever it had finished stays done; running it again picks up from there.");
            }

            // pkexec's code for a dismissed password dialog: a decision, said as one, like a
            // declined User Account Control prompt.
            if (process.ExitCode == PolkitDismissed && step.FileName.EndsWith("pkexec", StringComparison.Ordinal))
                return SetupStepResult.Cancelled;

            if (process.ExitCode != 0)
                return new SetupStepResult(false, false, $"{Path.GetFileNameWithoutExtension(step.FileName)} exited with code {process.ExitCode}. Its output is above.");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return new SetupStepResult(false, false, $"Could not run {step.FileName}: {ex.Message}");
        }

        // Switching on systemd takes effect when the distribution next starts, and the engine is
        // started by systemd. Stopping it now means the next connection starts it properly.
        if (restartDistro && step.Distro is { } distro)
        {
            output($"Restarting {distro} so the engine starts with it…");

            using var terminate = Process.Start(new ProcessStartInfo("wsl.exe", ["--terminate", distro]) { CreateNoWindow = true, UseShellExecute = false });
            if (terminate is not null) await terminate.WaitForExitAsync(ct).ConfigureAwait(false);
        }

        return new SetupStepResult(true, step.NeedsRestart, step.NeedsRestart ? "Done. Restart Windows to finish it." : "Done.");
    }

    /// <summary>
    /// Open a new Terminal window on macOS running <see cref="SetupStep.Script"/>.
    /// <para>
    /// For an installer that prompts for a password. The prompt is the installer's and the system's,
    /// in a terminal the user can see — never a field in Dray. Terminal gives no exit code back to
    /// the app that asked, so this cannot report success; it says where the step is running, and
    /// Look again finds out what it did.
    /// </para>
    /// </summary>
    public async Task<SetupStepResult> RunInTerminalAsync(SetupStep step, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsMacOS() || step.Script is not { } script)
            return new SetupStepResult(false, false, "This step runs in macOS's Terminal.");

        var info = new ProcessStartInfo("/usr/bin/osascript") { UseShellExecute = false, CreateNoWindow = true };

        // AppleScript string literal: backslashes and quotes escaped, nothing else interpreted.
        var quoted = script.Replace("\\", "\\\\").Replace("\"", "\\\"");
        info.ArgumentList.Add("-e");
        info.ArgumentList.Add("tell application \"Terminal\" to activate");
        info.ArgumentList.Add("-e");
        info.ArgumentList.Add($"tell application \"Terminal\" to do script \"{quoted}\"");

        try
        {
            using var process = Process.Start(info);
            if (process is null) return new SetupStepResult(false, false, "Could not open Terminal.");

            await process.WaitForExitAsync(ct).ConfigureAwait(false);

            return process.ExitCode == 0
                ? new SetupStepResult(true, false, "Running in Terminal. Follow its prompts, then press Look again when it has finished.")
                : new SetupStepResult(false, false, "Could not open Terminal. The command is shown above to run yourself.");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return new SetupStepResult(false, false, $"Could not open Terminal: {ex.Message}");
        }
    }

    public async Task<SetupStepResult> RunAsync(SetupStep step, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
            return new SetupStepResult(false, false, "This step only applies to Windows.");

        var info = new ProcessStartInfo(step.FileName)
        {
            // Required for elevation, and it is what makes the window visible. Both are wanted.
            UseShellExecute = true,
            Verb = step.NeedsElevation ? "runas" : string.Empty,
        };

        foreach (var argument in step.Arguments) info.ArgumentList.Add(argument);

        try
        {
            using var process = Process.Start(info);
            if (process is null) return new SetupStepResult(false, false, $"Could not start {step.FileName}.");

            await process.WaitForExitAsync(ct).ConfigureAwait(false);

            return Interpret(process.ExitCode, step);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == ElevationCancelled)
        {
            // Declining the prompt is a decision, not a fault. Said plainly and without alarm.
            return SetupStepResult.Cancelled;
        }
        catch (OperationCanceledException)
        {
            // The page went away. The command may well still be running in its own window, which
            // is exactly why it is not claimed to have been stopped.
            return new SetupStepResult(false, false, "Stopped waiting. The command may still be running in its own window.");
        }
        catch (Exception ex)
        {
            return new SetupStepResult(false, false, $"Could not run {step.FileName}: {ex.Message}");
        }
    }

    internal static SetupStepResult Interpret(int exitCode, SetupStep step) => exitCode switch
    {
        0 => new SetupStepResult(
            true,
            step.NeedsRestart,
            step.NeedsRestart ? "Done. Restart Windows to finish it." : "Done."),

        RestartRequiredExitCode => new SetupStepResult(true, true, "Done. Restart Windows to finish it."),

        ElevationCancelled => SetupStepResult.Cancelled,

        // The command's own output is on screen in its own window, so the number is enough here
        // and inventing an explanation for it would be guessing over the top of the real one.
        _ => new SetupStepResult(false, false, $"{step.FileName} exited with code {exitCode}. Its output is in the window it opened."),
    };
}
