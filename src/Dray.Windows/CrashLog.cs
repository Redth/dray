using System.Diagnostics;

namespace Dray.WinUI;

/// <summary>
/// Where a failure goes when there is nowhere to show it.
/// <para>
/// On macOS, running the binary from a terminal puts stderr in front of whoever is debugging. A
/// WinUI app is a GUI-subsystem executable: launched from a shell it detaches, and its stderr goes
/// nowhere at all. So every report also lands in a file under the user's local app data, truncated
/// once per launch so it always describes the run that just failed.
/// </para>
/// </summary>
public static class CrashLog
{
    static readonly Lock Gate = new();
    static bool _started;

    /// <summary><c>%LOCALAPPDATA%\Dray\logs\last-run.log</c>.</summary>
    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Dray", "logs", "last-run.log");

    public static void Report(string kind, object? error) => Write($"[dray:{kind}] {error}");

    public static void Write(string line)
    {
        Console.Error.WriteLine(line);
        Debug.WriteLine(line);

        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);

                var stamped = $"{DateTimeOffset.Now:O} {line}{Environment.NewLine}";
                if (_started) File.AppendAllText(FilePath, stamped);
                else File.WriteAllText(FilePath, stamped);

                _started = true;
            }
            catch (IOException)
            {
                // A log that cannot be written must not become the next unhandled exception.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
