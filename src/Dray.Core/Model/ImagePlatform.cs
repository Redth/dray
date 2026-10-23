namespace Dray.Core.Model;

/// <summary>
/// An OS and a CPU an image is built for — <c>linux/arm64</c>.
/// <para>
/// The question someone on an ARM laptop is really asking when they pick a tag. A tag with no build
/// for the engine's platform is either refused at pull time or runs under emulation, and neither is
/// something to find out after the download.
/// </para>
/// </summary>
public sealed record ImagePlatform(string Os, string Architecture, string? Variant = null)
{
    /// <summary>The OCI spelling, <c>linux/arm/v7</c>, which is what the engine and the CLI print.</summary>
    public override string ToString()
        => Variant is { Length: > 0 } variant ? $"{Os}/{Architecture}/{variant}" : $"{Os}/{Architecture}";

    /// <summary>
    /// The architecture as people say it: <c>arm64</c>, <c>armv7</c>, <c>amd64</c>.
    /// <para>
    /// The variant is kept only for 32-bit ARM, where v6 and v7 are different machines. arm64 has one
    /// variant, and registries write <c>v8</c> or leave it out at random.
    /// </para>
    /// </summary>
    public string Label => Architecture == "arm" && Variant is { Length: > 0 } variant ? $"arm{variant}" : Architecture;

    /// <summary>
    /// Not a platform. A registry lists build attestations beside the images in an index as
    /// <c>unknown/unknown</c>; offering one would offer something that cannot be run.
    /// </summary>
    public bool IsAttestation => Os == "unknown" || Architecture == "unknown";

    /// <summary>Whether an image built for this platform runs natively on <paramref name="engine"/>.</summary>
    public bool RunsOn(ImagePlatform engine)
    {
        if (!string.Equals(Os, engine.Os, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.Equals(Architecture, engine.Architecture, StringComparison.OrdinalIgnoreCase)) return false;

        // Only 32-bit ARM's variant decides anything. A missing variant on either side is not a claim
        // that they differ, so it is not treated as one.
        return Architecture != "arm"
            || Variant is null || engine.Variant is null
            || string.Equals(Variant, engine.Variant, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The platform an engine runs, from what the engine reported — never from this machine.
    /// <para>
    /// A remote host is a different computer, and the architecture Dray itself was built for says
    /// nothing about either. An engine whose container OS is unknown gets no platform at all: the
    /// alternative is a Linux guess that is wrong on a Windows engine, and every "runs here" claim
    /// would inherit it (AGENTS.md, "a container's OS is measured, never inferred").
    /// </para>
    /// </summary>
    /// <param name="architecture">
    /// <c>/info</c>'s <c>Architecture</c>, which is the kernel's word — <c>aarch64</c>,
    /// <c>x86_64</c> — rather than the one registries use.
    /// </param>
    public static ImagePlatform? OfEngine(ContainerOs os, string? architecture)
    {
        var name = os switch
        {
            ContainerOs.Linux => "linux",
            ContainerOs.Windows => "windows",
            _ => null,
        };

        if (name is null || string.IsNullOrWhiteSpace(architecture)) return null;

        var (arch, variant) = Normalize(architecture);
        return new ImagePlatform(name, arch, variant);
    }

    /// <summary>
    /// An architecture as the kernel or Go names it, in the OCI vocabulary registries use.
    /// <para>
    /// Docker's <c>/info</c> reports <c>uname -m</c>; manifests use GOARCH. The same machine is
    /// <c>aarch64</c> to one and <c>arm64</c> to the other, and comparing them unconverted would
    /// report that no image on any registry runs on an ARM Mac.
    /// </para>
    /// </summary>
    internal static (string Architecture, string? Variant) Normalize(string raw)
    {
        var text = raw.Trim().ToLowerInvariant();

        return text switch
        {
            "x86_64" or "x86-64" or "amd64" or "x64" => ("amd64", null),
            "aarch64" or "arm64" or "armv8" or "arm64/v8" => ("arm64", null),
            "armv7l" or "armv7" or "armhf" or "arm/v7" => ("arm", "v7"),
            "armv6l" or "armv6" or "armel" or "arm/v6" => ("arm", "v6"),
            "armv5l" or "armv5" or "armv5tel" or "arm/v5" => ("arm", "v5"),
            "i386" or "i686" or "x86" or "386" => ("386", null),
            _ => (text, null),
        };
    }

    /// <summary>A platform from a registry's own fields, or null when it gave neither half.</summary>
    public static ImagePlatform? From(string? os, string? architecture, string? variant)
    {
        if (string.IsNullOrWhiteSpace(os) || string.IsNullOrWhiteSpace(architecture)) return null;

        return new ImagePlatform(
            os.Trim().ToLowerInvariant(),
            architecture.Trim().ToLowerInvariant(),
            string.IsNullOrWhiteSpace(variant) ? null : variant.Trim().ToLowerInvariant());
    }
}
