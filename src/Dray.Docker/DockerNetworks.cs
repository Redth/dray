using Docker.DotNet;
using Docker.DotNet.Models;
using Dray.Core.Engine;
using Dray.Core.Model;

namespace Dray.Docker;

/// <summary>Networks, over the Engine API.</summary>
public static class DockerNetworks
{
    public static async Task<IReadOnlyList<NetworkSummary>> ListAsync(
        DockerClient client, CancellationToken ct = default)
    {
        var responses = await client.Networks.ListNetworksAsync(new NetworksListParameters(), ct)
            .ConfigureAwait(false);

        // The list endpoint omits attached containers; only inspect carries them, and "which
        // containers share this network" is the question the page exists to answer.
        var detailed = new List<NetworkSummary>(responses.Count);

        foreach (var network in responses)
        {
            detailed.Add(await DescribeAsync(client, network, ct).ConfigureAwait(false));
        }

        return
        [
            .. detailed
                // The engine's own networks last: they are always there, always the same, and
                // never what the user came to look at.
                .OrderBy(n => n.IsPredefined)
                .ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase),
        ];
    }

    static async Task<NetworkSummary> DescribeAsync(
        DockerClient client, NetworkResponse listed, CancellationToken ct)
    {
        try
        {
            return Map(await client.Networks.InspectNetworkAsync(listed.ID, ct).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is DockerApiException or HttpRequestException)
        {
            // Removed between the list and the inspect, or an engine that will not inspect it.
            // The list entry is still worth showing, just without its members.
            return Map(listed);
        }
    }

    internal static NetworkSummary Map(NetworkResponse n) => new()
    {
        Id = n.ID,
        Name = n.Name,
        Driver = string.IsNullOrWhiteSpace(n.Driver) ? "bridge" : n.Driver,
        Scope = string.IsNullOrWhiteSpace(n.Scope) ? null : n.Scope,
        Created = DockerTime.From(n.Created),
        IsInternal = n.Internal,

        Subnets =
        [
            .. (n.IPAM?.Config ?? [])
                .Select(c => c.Subnet)
                .Where(subnet => !string.IsNullOrWhiteSpace(subnet))
                .Select(subnet => subnet!),
        ],

        Members =
        [
            .. (n.Containers ?? new Dictionary<string, EndpointResource>())
                .Select(c => new NetworkMember(
                    c.Key,
                    string.IsNullOrWhiteSpace(c.Value?.Name) ? c.Key[..Math.Min(12, c.Key.Length)] : c.Value.Name,
                    string.IsNullOrWhiteSpace(c.Value?.IPv4Address) ? null : c.Value.IPv4Address,
                    string.IsNullOrWhiteSpace(c.Value?.MacAddress) ? null : c.Value.MacAddress))
                .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase),
        ],

        Labels = n.Labels is { Count: > 0 } labels
            ? new Dictionary<string, string>(labels, StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal),
    };

    public static async Task CreateAsync(
        DockerClient client, NetworkRequest request, CancellationToken ct = default)
    {
        var parameters = new NetworksCreateParameters
        {
            Name = request.Name,
            Driver = request.Driver,
            Internal = request.Internal,
        };

        // Only sent when the user asked for one. An empty IPAM config makes some engines reject
        // the request outright rather than defaulting.
        if (!string.IsNullOrWhiteSpace(request.Subnet))
        {
            parameters.IPAM = new IPAM
            {
                Config =
                [
                    new IPAMConfig
                    {
                        Subnet = request.Subnet!,
                        Gateway = string.IsNullOrWhiteSpace(request.Gateway) ? string.Empty : request.Gateway,
                    },
                ],
            };
        }

        try
        {
            await client.Networks.CreateNetworkAsync(parameters, ct).ConfigureAwait(false);
        }
        catch (DockerApiException ex) when ((int)ex.StatusCode == 409)
        {
            throw new RuntimeConnectionException($"A network called \"{request.Name}\" already exists.", ex);
        }
        catch (DockerApiException ex)
        {
            throw new RuntimeConnectionException($"Could not create the network: {ex.Message}", ex);
        }
    }

    public static async Task RemoveAsync(DockerClient client, string networkId, CancellationToken ct = default)
    {
        try
        {
            await client.Networks.DeleteNetworkAsync(networkId, ct).ConfigureAwait(false);
        }
        catch (DockerApiException ex) when ((int)ex.StatusCode == 403)
        {
            throw new RuntimeConnectionException(
                "This is one of the engine's own networks and cannot be removed.", ex);
        }
        catch (DockerApiException ex) when ((int)ex.StatusCode == 409)
        {
            throw new RuntimeConnectionException(
                "Containers are still attached to this network. Disconnect them first.", ex);
        }
        catch (DockerApiException ex) when ((int)ex.StatusCode == 404)
        {
            throw new RuntimeConnectionException("That network no longer exists.", ex);
        }
    }

    public static async Task ConnectAsync(
        DockerClient client, string networkId, string containerId, CancellationToken ct = default)
    {
        try
        {
            await client.Networks
                .ConnectNetworkAsync(networkId, new NetworkConnectParameters { Container = containerId }, ct)
                .ConfigureAwait(false);
        }
        catch (DockerApiException ex) when ((int)ex.StatusCode == 403)
        {
            throw new RuntimeConnectionException(
                "This network does not accept connections — host and none cannot be joined.", ex);
        }
        catch (DockerApiException ex)
        {
            throw new RuntimeConnectionException($"Could not connect: {ex.Message}", ex);
        }
    }

    public static async Task DisconnectAsync(
        DockerClient client, string networkId, string containerId, bool force, CancellationToken ct = default)
    {
        try
        {
            await client.Networks
                .DisconnectNetworkAsync(
                    networkId,
                    new NetworkDisconnectParameters { Container = containerId, Force = force },
                    ct)
                .ConfigureAwait(false);
        }
        catch (DockerApiException ex)
        {
            throw new RuntimeConnectionException($"Could not disconnect: {ex.Message}", ex);
        }
    }
}
