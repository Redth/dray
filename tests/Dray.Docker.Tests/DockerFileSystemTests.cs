using Xunit;

namespace Dray.Docker.Tests;

/// <summary>
/// How an engine names the entries in a directory tar.
/// <para>
/// There is no standard here and the two engines genuinely differ, which is worth stating plainly
/// because assuming either shape silently produces an empty directory rather than an error.
/// </para>
/// </summary>
public class TarEntryNamingTests
{
    // Docker roots the archive at the requested directory's own name.
    const string DockerRoot = "etc";

    // Podman returns "/" for the directory and absolute-looking names for its children, so once
    // the root entry is consumed there is no prefix left to strip.
    const string PodmanRoot = "";

    [Fact]
    public void DockerStyleEntriesHaveTheirRootStripped()
        => Assert.Equal("hosts", DockerFileSystem.Relative("etc/hosts", DockerRoot));

    [Fact]
    public void PodmanStyleEntriesAreAlreadyRelative()
        => Assert.Equal("hosts", DockerFileSystem.Relative("/hosts", PodmanRoot));

    [Fact]
    public void ADeeperEntryKeepsItsRemainingPathSoItCanBeFilteredOut()
    {
        // The tar is recursive; the listing only wants depth one, and it decides that by looking
        // for a '/' in what comes back.
        Assert.Equal("nginx/nginx.conf", DockerFileSystem.Relative("etc/nginx/nginx.conf", DockerRoot));
        Assert.Equal("nginx/nginx.conf", DockerFileSystem.Relative("/nginx/nginx.conf", PodmanRoot));
    }

    [Fact]
    public void TheRootEntryItselfResolvesToNothing()
        => Assert.Equal(string.Empty, DockerFileSystem.Relative("etc", DockerRoot));

    [Fact]
    public void AnEntryOutsideTheRootIsRejectedRatherThanMisattributed()
    {
        // "etcetera" starts with "etc" as a string but is not inside it.
        Assert.Null(DockerFileSystem.Relative("etcetera/file", DockerRoot));
    }

    [Fact]
    public void AChildSharingTheRootsNameIsNotMistakenForTheRoot()
    {
        // Requesting /etc on a container that has /etc/etc. Under the Docker shape the entry is
        // "etc/etc", which must resolve to the child rather than to the empty root.
        Assert.Equal("etc", DockerFileSystem.Relative("etc/etc", DockerRoot));
    }
}
