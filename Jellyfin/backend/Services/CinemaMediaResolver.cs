using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;

namespace Moonfin.Server.Services;

/// <summary>
/// Validates server-only trailer ownership/access and resolves identities the client
/// could not establish locally, without scanning the library or retaining search results.
/// </summary>
public sealed class CinemaMediaResolver(ILibraryManager library, IProviderManager providers)
{
    public sealed record Resolution(int? TmdbId, string? Source, string? MediaType = null);
    public sealed record MediaName(string Title, int? Year);

    public async Task<Resolution> ResolveAsync(Guid itemId, Guid userId, CancellationToken cancellationToken)
    {
        // Old clients interpret every ID as a movie. Never return a series here.
        var result = await ResolveMediaAsync(itemId, userId, "movie", cancellationToken).ConfigureAwait(false);
        return result.MediaType == "movie" ? new(result.TmdbId, result.Source) : new(null, null);
    }

    public async Task<Resolution> ResolveMediaAsync(Guid itemId, Guid userId, string? expectedMediaType,
        CancellationToken cancellationToken)
    {
        // This overload uses IsVisibleStandalone, including parental/library restrictions,
        // and can read registered private Videos that don't occur in library queries.
        // The Guid overload also avoids binding to the User type moved in Jellyfin 10.11.
        var item = library.GetItemById<Video>(itemId, userId);
        if (item == null || item is Episode) return new(null, null);

        BaseItem? owner = null;
        if (item.ExtraType == ExtraType.Trailer && item.OwnerId != Guid.Empty)
        {
            owner = library.GetItemById<BaseItem>(item.OwnerId, userId);
            if (owner is not Movie && owner is not Series) return new(null, null);
        }

        // Moonfin normally consumes trustworthy unattached typed metadata locally.
        // If the request reaches Moonbase, still parse direct metadata so an attached
        // trailer can be checked against its accessible owner and so this endpoint
        // remains safe when called directly. Feature context never supplies the type.
        var explicitType = ProviderValue(item.ProviderIds, "TmdbMediaType");
        if (explicitType != null && explicitType is not ("movie" or "tv")) return new(null, null);
        var itemType = item is Movie ? "movie" : null;
        var ownerType = owner is Movie ? "movie" : owner is Series ? "tv" : null;
        var types = new[] { explicitType, itemType, ownerType }.Where(t => t != null).Distinct().ToArray();
        if (types.Length > 1) return new(null, null);
        var type = types.FirstOrDefault();
        var direct = PositiveTmdb(item.ProviderIds);
        var owned = owner == null ? null : PositiveTmdb(owner.ProviderIds);
        if (direct.HasValue && owned.HasValue && direct != owned) return new(null, null);
        var hasDirectId = ProviderValue(item.ProviderIds, "Tmdb") != null;
        // An untyped ID is only usable after the filename confirms the same ID.
        if (hasDirectId && !direct.HasValue) return new(null, null);
        if ((direct ?? owned) is int id && type != null)
        {
            return new(id, direct.HasValue ? "direct" : "owner", type);
        }

        // Context selects a search category only; it never types a bare TMDB ID.
        if (expectedMediaType is not ("movie" or "tv") || (type != null && type != expectedMediaType))
            return new(null, null);
        Resolution? resolved = null;
        var names = new[] { ParseName(item.Path), ParseName(item.Name) }.Where(n => n != null).Distinct();
        foreach (var name in names)
        {
            if (expectedMediaType == "movie" && !name!.Year.HasValue) continue;
            IEnumerable<RemoteSearchResult> results;
            if (expectedMediaType == "movie")
            {
                results = await providers.GetRemoteSearchResults<Movie, MovieInfo>(new RemoteSearchQuery<MovieInfo>
                {
                    SearchInfo = new MovieInfo { Name = name!.Title, Year = name.Year },
                    IncludeDisabledProviders = false,
                }, cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                results = await providers.GetRemoteSearchResults<Series, SeriesInfo>(new RemoteSearchQuery<SeriesInfo>
                {
                    SearchInfo = new SeriesInfo { Name = name!.Title, Year = name.Year },
                    IncludeDisabledProviders = false,
                }, cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            var matches = Matches(name!, results, expectedMediaType).Take(2).ToArray();
            if (matches.Length > 1 || matches.Any(id => id <= 0)) return new(null, null);
            if (matches.Length == 0) continue; // e.g. a hashed cache filename
            if (direct.HasValue && direct.Value != matches[0]) return new(null, null);
            var match = new Resolution(matches[0], "filename", expectedMediaType);
            if (resolved != null && resolved.TmdbId != match.TmdbId) return new(null, null);
            resolved = match;
        }
        return resolved ?? new(null, null);
    }

    private static string? ProviderValue(IDictionary<string, string>? ids, string key) =>
        ids?.FirstOrDefault(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value;

    public static int? PositiveTmdb(IDictionary<string, string>? ids)
    {
        var value = ids?.FirstOrDefault(p => p.Key.Equals("Tmdb", StringComparison.OrdinalIgnoreCase)).Value;
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 ? id : null;
    }

    public static MediaName? ParseName(string? pathOrName)
    {
        if (string.IsNullOrWhiteSpace(pathOrName)) return null;
        // Either host's path separator can occur in provider metadata.
        var title = pathOrName.Replace('\\', '/').Split('/').Last();
        title = Regex.Replace(title, @"\.(mp4|mkv|avi|mov|webm|m4v|ts)$", "", RegexOptions.IgnoreCase);
        title = title.Replace('.', ' ').Replace('_', ' ').Trim();
        // Only remove trailing decorations; never remove words inside a movie title.
        title = Regex.Replace(title, @"\s*[\[(]?(?:720p|1080p|2160p|4k|hd|uhd)[\])]?\s*$", "", RegexOptions.IgnoreCase);
        title = Regex.Replace(title, @"(?:^|[\s\-–:])(?:official\s+|theatrical\s+|final\s+)?(?:teaser(?:\s+trailer)?|trailer)(?:\s*#?\d+)?\s*$", "", RegexOptions.IgnoreCase).Trim(' ', '-', '–', ':');
        int? year = null;
        var match = Regex.Match(title, @"^(?<title>.+?)[\s\-(\[]+(?<year>\d{4})[)\]]?\s*$");
        if (match.Success && int.TryParse(match.Groups["year"].Value, out var parsed) &&
            parsed >= 1900 && parsed <= DateTime.UtcNow.Year + 3)
        {
            year = parsed;
            title = match.Groups["title"].Value.Trim(' ', '-', '–', ':');
        }
        // Treat a trailer's season suffix as decoration, not request metadata.
        var seriesTitle = Regex.Replace(title, @"[\s\-:]+(?:season\s+|s)\d{1,3}$", "", RegexOptions.IgnoreCase);
        if (seriesTitle != title)
        {
            title = seriesTitle.Trim(' ', '-', ':');
            year = null;
        }
        // A numeric title such as 1917 remains a title, not a year. Cache hashes
        // cannot identify a work; try the readable item name instead.
        if (title.Length == 0 || title.Length > 200 || NormalizeTitle(title).Length == 0 ||
            Regex.IsMatch(title, @"\A[0-9a-f]{32,64}\z", RegexOptions.IgnoreCase)) return null;
        return new(title, year);
    }

    public static int? Match(MediaName name, IEnumerable<RemoteSearchResult> results)
    {
        var matches = Matches(name, results, "movie").Take(2).ToArray();
        // Duplicate provider results for the same movie are fine; distinct IDs are ambiguous.
        return matches.Length == 1 && matches[0] > 0 ? matches[0] : null;
    }

    private static IEnumerable<int> Matches(MediaName name, IEnumerable<RemoteSearchResult> results, string type)
    {
        if (type == "movie" && !name.Year.HasValue) return [];
        var year = name.Year;
        var title = NormalizeTitle(name.Title);
        return results.Where(r => NormalizeTitle(r.Name ?? "") == title &&
                (!year.HasValue || r.ProductionYear == year))
            // An exact candidate without an ID leaves identity uncertain too.
            .Select(r => PositiveTmdb(r.ProviderIds) ?? 0).Distinct();
    }

    private static string NormalizeTitle(string title) => string.Concat(
        title.Normalize(NormalizationForm.FormKC).ToUpperInvariant().Where(char.IsLetterOrDigit));
}
