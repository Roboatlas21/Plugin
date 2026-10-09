using System.Globalization;
using System.Text.Json.Serialization;
using System.Text;
using System.Text.RegularExpressions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Providers;

namespace Moonfin.Server.Services;

/// <summary>
/// Validates server-only trailer ownership/access and resolves identities the client
/// could not establish locally, without scanning the library or retaining search results.
/// </summary>
public sealed class CinemaMediaResolver(ILibraryManager library, IProviderManager providers)
{
    public sealed record Resolution(
        [property: JsonPropertyName("tmdbId")] int? TmdbId,
        [property: JsonPropertyName("mediaType")] string? MediaType = null);
    public sealed record MediaName(string Title, int? Year);

    public async Task<Resolution> ResolveMediaAsync(Guid itemId, Guid userId, string? expectedMediaType,
        CancellationToken cancellationToken)
    {
        // This overload uses IsVisibleStandalone, including parental/library restrictions,
        // and can read registered private Videos that don't occur in library queries.
        // The Guid overload also avoids binding to the User type moved in Jellyfin 10.11.
        var item = library.GetItemById<Video>(itemId, userId);
        if (item == null || item is Episode) return new(null);

        BaseItem? owner = null;
        if (item.OwnerId != Guid.Empty)
        {
            owner = library.GetItemById<BaseItem>(item.OwnerId, userId);
            if (owner is not Movie && owner is not Series) return new(null);
        }

        // Moonfin uses a valid TMDB ID directly when it can determine the type.
        // When the fallback is called, still validate any accessible owner and
        // direct metadata; playback context does not type a bare server-side ID.
        var explicitType = ProviderValue(item.ProviderIds, "TmdbMediaType");
        if (explicitType != null && explicitType is not ("movie" or "tv")) return new(null);
        var itemType = item is Movie ? "movie" : null;
        var ownerType = owner is Movie ? "movie" : owner is Series ? "tv" : null;
        var types = new[] { explicitType, itemType, ownerType }.Where(t => t != null).Distinct().ToArray();
        if (types.Length > 1) return new(null);
        var type = types.FirstOrDefault();
        var direct = PositiveTmdb(item.ProviderIds);
        var owned = owner == null ? null : PositiveTmdb(owner.ProviderIds);
        if (direct.HasValue && owned.HasValue && direct != owned) return new(null);
        var hasDirectId = ProviderValue(item.ProviderIds, "Tmdb") != null;
        if (hasDirectId && !direct.HasValue) return new(null);
        // A Series owner establishes type, not the correctness of an untyped trailer ID.
        // If the owner has no TMDB ID, verify that ID with a strict series lookup.
        var unverifiedTvId = type == "tv" && direct.HasValue &&
            explicitType == null && !owned.HasValue;
        if ((direct ?? owned) is int id && type != null && !unverifiedTvId)
        {
            return new(id, type);
        }

        // Context selects a search category only; it never types a bare TMDB ID.
        if (expectedMediaType is not ("movie" or "tv") || (type != null && type != expectedMediaType))
            return new(null);
        Resolution? resolved = null;
        // When attached, identify the accessible owner, never a potentially unrelated trailer filename.
        if (owner != null && (string.IsNullOrWhiteSpace(owner.Name) ||
            owner.Name.Length > 200 || NormalizeTitle(owner.Name).Length == 0)) return new(null);
        var names = (owner == null
                ? new[] { ParseName(item.Path), ParseName(item.Name) }
                : new[] { new MediaName(owner.Name, owner is Movie ? owner.ProductionYear : null) })
            .Where(n => n != null)
            .Select(n => expectedMediaType == "tv"
                ? new MediaName(n!.Title, null)
                : new MediaName(n!.Title, n!.Year ?? (owner == null ? item.ProductionYear : null)))
            .Distinct();
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
                    SearchInfo = new SeriesInfo { Name = name!.Title },
                    IncludeDisabledProviders = false,
                }, cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            var matches = Matches(name!, results).Take(2).ToArray();
            if (matches.Length > 1 || matches.Any(id => id <= 0)) return new(null);
            if (matches.Length == 0) continue; // e.g. a hashed cache filename
            if (direct.HasValue && direct.Value != matches[0]) return new(null);
            var match = new Resolution(matches[0], expectedMediaType);
            if (resolved != null && resolved.TmdbId != match.TmdbId) return new(null);
            resolved = match;
        }
        return resolved ?? new(null);
    }

    private static string? ProviderValue(IDictionary<string, string>? ids, string key) =>
        ids?.FirstOrDefault(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value;

    public static int? PositiveTmdb(IDictionary<string, string>? ids)
    {
        var value = ProviderValue(ids, "Tmdb");
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 ? id : null;
    }

    public static MediaName? ParseName(string? pathOrName)
    {
        if (string.IsNullOrWhiteSpace(pathOrName)) return null;
        // Either host's path separator can occur in provider metadata.
        var title = pathOrName.Replace('\\', '/').Split('/').Last();
        title = Regex.Replace(title, @"\.(mp4|mkv|avi|mov|webm|m4v|ts)$", "", RegexOptions.IgnoreCase);
        title = title.Replace('.', ' ').Trim();
        // Only remove trailing decorations; never remove words inside a movie title.
        title = Regex.Replace(title, @"\s*[\[(]?(?:720p|1080p|2160p|4k|hd|uhd)[\])]?\s*$", "", RegexOptions.IgnoreCase);
        const string trailerSuffixPattern = @"(?:^|[\s\-–:])(?:official\s+|theatrical\s+|final\s+)?(?:teaser(?:\s+trailer)?|trailer)(?:\s*#?\d{1,2})?\s*$";
        // Keep underscores intact until embedded video IDs have been stripped.
        title = Regex.Replace(title, @"(?<label>\b(?:trailer|teaser))[\s_]+[A-Za-z0-9_-]{7,}$",
            "${label}", RegexOptions.IgnoreCase);
        // Remove video IDs and generated suffixes without losing a known release year.
        title = Regex.Replace(title, @"\s*\[[A-Za-z0-9_-]{11}\]\s*$", "");
        title = Regex.Replace(title, trailerSuffixPattern, "", RegexOptions.IgnoreCase).Trim(' ', '-', '–', ':');
        title = Regex.Replace(title, @"\s*\[[A-Za-z0-9_-]{11}\]\s*$", "");
        title = Regex.Replace(title, @"(?<year>(?:19|20)\d{2}[)\]]?)[\s_]+[A-Za-z0-9_-]{7,}$",
            "${year}", RegexOptions.IgnoreCase);
        title = title.Replace('_', ' ');
        title = Regex.Replace(title, trailerSuffixPattern, "", RegexOptions.IgnoreCase).Trim(' ', '-', '–', ':');
        int? year = null;
        var match = Regex.Match(title, @"^(?<title>.+?)[\s\-(\[]+(?<year>\d{4})[)\]]?\s*$");
        if (match.Success && int.TryParse(match.Groups["year"].Value, out var parsed) &&
            parsed >= 1900 && parsed <= DateTime.UtcNow.Year + 3)
        {
            year = parsed;
            title = match.Groups["title"].Value.Trim(' ', '-', '–', ':');
        }
        // The year may follow "Trailer", so remove that suffix after extracting it.
        title = Regex.Replace(title, trailerSuffixPattern, "", RegexOptions.IgnoreCase).Trim(' ', '-', '–', ':');
        if (year.HasValue)
            title = Regex.Replace(title, @"\s+(?=[A-Za-z0-9_-]{8,}$)(?=[A-Za-z0-9_-]*\d)[A-Za-z0-9_-]+$", "", RegexOptions.IgnoreCase);
        // Treat a trailer's season suffix as decoration, not request metadata.
        var seriesTitle = Regex.Replace(title, @"[\s\-:]+(?:season\s+|s)\d{1,3}$", "", RegexOptions.IgnoreCase);
        if (seriesTitle != title)
        {
            title = Regex.Replace(seriesTitle, @"[\s\-(\[]+\d{4}[)\]]?\s*$", "")
                .Trim(' ', '-', ':');
            year = null;
        }
        // A numeric title such as 1917 remains a title, not a year. Cache hashes
        // cannot identify a work; try the readable item name instead.
        if (title.Length == 0 || title.Length > 200 || NormalizeTitle(title).Length == 0 ||
            Regex.IsMatch(title, @"\A[0-9a-f]{32,64}\z", RegexOptions.IgnoreCase)) return null;
        return new(title, year);
    }

    private static IEnumerable<int> Matches(MediaName name, IEnumerable<RemoteSearchResult> results)
    {
        var title = NormalizeTitle(name.Title);
        return results.Where(r => NormalizeTitle(r.Name ?? "") == title &&
                (!name.Year.HasValue || r.ProductionYear == name.Year))
            // An exact candidate without an ID leaves identity uncertain too.
            .Select(r => PositiveTmdb(r.ProviderIds) ?? 0).Distinct();
    }

    private static string NormalizeTitle(string title) => string.Concat(
        title.Normalize(NormalizationForm.FormKC).ToUpperInvariant().Where(char.IsLetterOrDigit));
}
