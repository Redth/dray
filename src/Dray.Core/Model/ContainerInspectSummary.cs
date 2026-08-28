namespace Dray.Core.Model;

/// <summary>
/// A container's inspect record as plain text.
/// <para>
/// This exists to be pasted — into an issue, a message, a terminal. That is why the id is full
/// rather than shortened, why the field names are padded into a column, and why it says nothing
/// the engine did not: someone reading it is trying to work out what happened, and a summary that
/// filled a gap would send them the wrong way.
/// </para>
/// </summary>
public static class ContainerInspectSummary
{
    public static string ToText(ContainerInspect inspect)
    {
        var lines = new List<string>
        {
            $"name:     {inspect.Name}",

            // The full id, not the short one: this text may be pasted into a command.
            $"id:       {inspect.Id}",
            $"image:    {inspect.Image}",
            $"status:   {inspect.Status.Label}",
            $"command:  {inspect.CommandLine}",
        };

        // Only when true. A line reading "killed: no" is noise in something being skimmed for the
        // reason a container stopped.
        if (inspect.OomKilled) lines.Add("killed:   out of memory");
        if (inspect.Error is { Length: > 0 } error) lines.Add($"error:    {error}");

        foreach (var network in inspect.Networks)
            lines.Add($"network:  {network.Name} {network.IpAddress}");

        foreach (var mount in inspect.Mounts)
            lines.Add($"mount:    {mount.Source} -> {mount.Destination}");

        return string.Join('\n', lines);
    }
}
