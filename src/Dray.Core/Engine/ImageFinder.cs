using System.Collections.Concurrent;
using Dray.Core.Model;

namespace Dray.Core.Engine;

/// <summary>Where a list of suggested repositories came from, which decides what the list can claim.</summary>
public enum RepositorySource
{
    /// <summary>The engine searched the registry. Ranked by the registry.</summary>
    Search,

    /// <summary>The registry's published catalog, filtered here. Every repository it has, but names only.</summary>
    Catalog,

    /// <summary>The registry offers neither, or the engine cannot search. The user has to type a name.</summary>
    None,
}

/// <summary>Repositories to suggest, and how they were found.</summary>
/// <param name="Total">How many the catalog holds in all, for a catalog; -1 otherwise.</param>
public sealed record RepositoryResults(
    IReadOnlyList<ImageSearchResult> Items,
    RepositorySource Source,
    int Total = -1);

/// <summary>
/// Finding repositories on whichever registry is chosen, by whatever that registry supports.
/// <para>
/// Registries differ in what they will tell anyone. Docker Hub has search, and Quay answers the same
/// search the engine sends Docker Hub; GHCR answers it with a 404 and Microsoft's registry with an
/// HTML page. Microsoft publishes a catalog of everything it hosts instead, and GHCR publishes
/// nothing at all. None of that is declared up front: the first question to each registry is the
/// measurement, and the answer is remembered for as long as this finder lives — one dialog — so a
/// registry without search is not asked again on every keystroke.
/// </para>
/// </summary>
public sealed class ImageFinder
{
    /// <summary>An engine search: the shape of <see cref="EngineManager.SearchImagesAsync"/>.</summary>
    public delegate Task<IReadOnlyList<ImageSearchResult>> SearchFunc(
        string term, int limit, ImageSearchFilters? filters, CancellationToken ct);

    /// <summary>Results asked of a search. The engine allows a hundred; past twenty-five nobody reads on.</summary>
    public const int SearchLimit = 25;

    /// <summary>Catalog matches shown. A catalog has thousands; the ranking puts the intended one near the top.</summary>
    public const int CatalogLimit = 50;

    readonly SearchFunc? _search;
    readonly RegistryBrowser _registries;
    readonly ConcurrentDictionary<string, bool> _searches = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<string, bool> _catalogs = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="search">The engine's search, or null when the engine has none (Apple's runtime).</param>
    public ImageFinder(SearchFunc? search, RegistryBrowser registries)
    {
        _search = search;
        _registries = registries;
    }

    /// <summary>
    /// Whether there is known to be no way to find a repository on <paramref name="host"/> — so the
    /// field can ask for a name rather than offer a search that cannot happen. Unmeasured is not "no".
    /// </summary>
    public bool CannotFind(string host)
    {
        var searches = _search is not null && _searches.GetValueOrDefault(host, true);
        var catalogs = !RegistryHosts.IsDockerHub(host) && _catalogs.GetValueOrDefault(host, true);

        return !searches && !catalogs;
    }

    /// <summary>
    /// Repositories on <paramref name="registry"/> matching what has been typed of a name.
    /// <para>
    /// A failure to search Docker Hub is thrown, because Docker Hub has search and failing to reach
    /// it is news. A failure to search anywhere else is the registry saying it has no search, and is
    /// remembered rather than reported.
    /// </para>
    /// </summary>
    public async Task<RepositoryResults> FindAsync(
        RegistryOption registry, ImageReferenceDraft draft, ImageSearchFilters? filters, CancellationToken ct = default)
    {
        var host = registry.Host;
        var hub = RegistryHosts.IsDockerHub(host);
        var term = draft.Path.Trim('/');

        if (_search is not null && _searches.GetValueOrDefault(host, true))
        {
            // One letter matches half of Docker Hub, in an order that means nothing.
            if (term.Length < 2) return new RepositoryResults([], RepositorySource.Search);

            try
            {
                var found = await _search(hub ? term : $"{host}/{term}", SearchLimit, filters, ct).ConfigureAwait(false);
                _searches[host] = true;

                return new RepositoryResults(found, RepositorySource.Search);
            }
            catch (Exception ex) when (!hub && ex is not OperationCanceledException)
            {
                _searches[host] = false;
            }
        }

        if (!hub && _catalogs.GetValueOrDefault(host, true))
        {
            try
            {
                var catalog = await _registries
                    .CatalogAsync(host, mayUseCredential: registry.SignedInAs is not null, ct)
                    .ConfigureAwait(false);

                _catalogs[host] = true;

                return new RepositoryResults(
                    [.. Rank(catalog, term).Take(CatalogLimit).Select(name => new ImageSearchResult($"{host}/{name}"))],
                    RepositorySource.Catalog,
                    catalog.Count);
            }
            catch (RegistryException ex) when (ex.Failure is not (RegistryFailure.Unreachable or RegistryFailure.RateLimited))
            {
                _catalogs[host] = false;
            }
        }

        return new RepositoryResults([], RepositorySource.None);
    }

    /// <summary>
    /// Catalog names containing the term, best first: the name itself, then a last segment that is
    /// or starts with it, then any segment that starts with it, then anywhere. Shorter first within
    /// each, since the shorter name is usually the thing and the longer one its sample or variant.
    /// <para>
    /// Substring, not subsequence: a subsequence ranker finds <c>dotnet/sdk</c> for "dns" — d, n, s
    /// all appear in it — which is why the Run dialog filters its own image list the same way.
    /// </para>
    /// </summary>
    internal static IEnumerable<string> Rank(IReadOnlyList<string> names, string term)
    {
        if (term.Length == 0) return names;

        return names
            .Select(name => (Name: name, Score: Score(name, term)))
            .Where(m => m.Score >= 0)
            .OrderBy(m => m.Score)
            .ThenBy(m => m.Name.Length)
            .ThenBy(m => m.Name, StringComparer.Ordinal)
            .Select(m => m.Name);
    }

    static int Score(string name, string term)
    {
        if (name.Equals(term, StringComparison.OrdinalIgnoreCase)) return 0;

        var last = name[(name.LastIndexOf('/') + 1)..];
        if (last.Equals(term, StringComparison.OrdinalIgnoreCase)) return 1;
        if (last.StartsWith(term, StringComparison.OrdinalIgnoreCase)) return 2;
        if (name.StartsWith(term, StringComparison.OrdinalIgnoreCase)) return 3;
        if (name.Split('/').Any(s => s.StartsWith(term, StringComparison.OrdinalIgnoreCase))) return 4;

        return name.Contains(term, StringComparison.OrdinalIgnoreCase) ? 5 : -1;
    }
}
