namespace Dray.Core.Engine;

/// <summary>
/// The one <c>.wslconfig</c> setting an engine in WSL needs: keep the distribution running when
/// nothing is attached to it.
/// <para>
/// WSL stops a distribution about fifteen seconds after its last <c>wsl.exe</c> client exits, and a
/// running container is not a client — measured: a container started in Ubuntu was gone within
/// eighteen seconds of the terminal closing. So an engine in WSL ran only while Dray or a terminal
/// happened to be attached. <c>[general] instanceIdleTimeout=-1</c> turns that off, measured on WSL
/// 2.6: the distribution, and its containers, stayed up.
/// </para>
/// <para>
/// It is WSL-wide, in the user's own <c>%USERPROFILE%\.wslconfig</c>, so it is a step the user
/// chooses and is shown, never something Dray sets quietly. Everything else in the file is kept.
/// </para>
/// </summary>
public static class WslConfig
{
    public const string Section = "general";

    public const string Key = "instanceIdleTimeout";

    /// <summary>Where WSL reads it from.</summary>
    public static string PathFor(string userProfile) => Path.Combine(userProfile, ".wslconfig");

    /// <summary>Whether <paramref name="content"/> already keeps distributions running.</summary>
    public static bool KeepsDistributionsRunning(string? content)
    {
        if (content is null) return false;

        string? section = null;

        foreach (var raw in content.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('[') && line.EndsWith(']')) section = line[1..^1].Trim();
            else if (string.Equals(section, Section, StringComparison.OrdinalIgnoreCase)
                     && line.Split('=', 2) is [var key, var value]
                     && key.Trim().Equals(Key, StringComparison.OrdinalIgnoreCase))
            {
                return value.Trim() == "-1";
            }
        }

        return false;
    }

    /// <summary>
    /// <paramref name="content"/> with the setting turned on: replaced where it is already set,
    /// added to an existing <c>[general]</c> section, or appended as one. Nothing else changes.
    /// </summary>
    public static string WithDistributionsKeptRunning(string? content)
    {
        var setting = $"{Key}=-1";

        if (string.IsNullOrWhiteSpace(content)) return $"[{Section}]\n{setting}\n";

        var lines = content.Replace("\r\n", "\n").Split('\n').ToList();
        int? sectionAt = null;

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i].Trim();

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                // Leaving [general] without having found the key: add it at the end of the section.
                if (sectionAt is not null) break;
                if (line[1..^1].Trim().Equals(Section, StringComparison.OrdinalIgnoreCase)) sectionAt = i;
                continue;
            }

            if (sectionAt is not null && line.Split('=', 2) is [var key, _] && key.Trim().Equals(Key, StringComparison.OrdinalIgnoreCase))
            {
                lines[i] = setting;
                return string.Join('\n', lines);
            }
        }

        if (sectionAt is { } at)
        {
            lines.Insert(at + 1, setting);
            return string.Join('\n', lines);
        }

        var text = string.Join('\n', lines).TrimEnd('\n');
        return $"{text}\n\n[{Section}]\n{setting}\n";
    }
}
