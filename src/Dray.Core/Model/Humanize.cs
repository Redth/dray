using System.Globalization;

namespace Dray.Core.Model;

/// <summary>
/// Compact, honest formatting for the dense columns. Nothing here rounds in a way that would let a
/// user misread a size or a duration.
/// </summary>
public static class Humanize
{
    static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    /// <summary>Byte counts, base 1000 — the unit Docker itself reports.</summary>
    public static string Bytes(long bytes)
    {
        if (bytes < 0) return "—";
        if (bytes < 1000) return $"{bytes} B";

        double value = bytes;
        var unit = 0;
        while (value >= 1000 && unit < Units.Length - 1)
        {
            value /= 1000;
            unit++;
        }

        // One decimal below 10 so "1.4 GB" and "14 GB" both read at a glance.
        var text = value < 10
            ? value.ToString("0.#", CultureInfo.InvariantCulture)
            : value.ToString("0", CultureInfo.InvariantCulture);

        return $"{text} {Units[unit]}";
    }

    /// <summary>
    /// Elapsed time in the coarsest unit that is still true: "3 days", not "3 days 4 hours".
    /// A container that has been up for weeks does not need minute precision.
    /// </summary>
    public static string Since(DateTimeOffset? then, DateTimeOffset? now = null)
    {
        if (then is null) return "—";

        var span = (now ?? DateTimeOffset.UtcNow) - then.Value;
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;

        if (span.TotalSeconds < 60) return $"{(int)span.TotalSeconds}s";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes}m";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours}h";
        if (span.TotalDays < 30) return $"{(int)span.TotalDays}d";

        var months = (int)(span.TotalDays / 30);
        return months < 12 ? $"{months}mo" : $"{(int)(span.TotalDays / 365)}y";
    }

    /// <summary>A CPU percentage. Null renders as an em dash, never "0%", which would be a lie.</summary>
    public static string Percent(double? value)
        => value is null ? "—" : value.Value.ToString("0.#", CultureInfo.InvariantCulture) + "%";

    /// <summary>
    /// Strips a registry host and tag so a list of images stays scannable.
    /// <c>ghcr.io/redth/dray:1.2</c> becomes <c>redth/dray</c>.
    /// </summary>
    /// <summary>
    /// A list of names as a sentence: "a", "a and b", "a, b and c".
    /// <para>
    /// Used wherever a confirmation names what it is about to affect. Naming two volumes beats
    /// saying "2 volumes", because the second is a number and the first is a decision.
    /// </para>
    /// </summary>
    public static string Names(IReadOnlyList<string> names) => names.Count switch
    {
        0 => string.Empty,
        1 => names[0],
        2 => $"{names[0]} and {names[1]}",
        _ => $"{string.Join(", ", names.Take(names.Count - 1))} and {names[^1]}",
    };

    public static string ImageName(string image)
    {
        if (string.IsNullOrWhiteSpace(image)) return image;

        var withoutDigest = image.Split('@')[0];
        var lastColon = withoutDigest.LastIndexOf(':');
        var lastSlash = withoutDigest.LastIndexOf('/');
        var repo = lastColon > lastSlash ? withoutDigest[..lastColon] : withoutDigest;

        var firstSlash = repo.IndexOf('/');
        if (firstSlash > 0)
        {
            var head = repo[..firstSlash];
            // A registry host has a dot or a port, or is literally localhost.
            if (head.Contains('.') || head.Contains(':') || head == "localhost")
                repo = repo[(firstSlash + 1)..];
        }

        return repo;
    }

    /// <summary>The tag portion, or "latest" when implicit.</summary>
    public static string ImageTag(string image)
    {
        var withoutDigest = image.Split('@')[0];
        var lastColon = withoutDigest.LastIndexOf(':');
        var lastSlash = withoutDigest.LastIndexOf('/');
        return lastColon > lastSlash ? withoutDigest[(lastColon + 1)..] : "latest";
    }
}
