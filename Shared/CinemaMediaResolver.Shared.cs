using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Providers;

#if MOONFIN_JELLYFIN
namespace Moonfin.Server.Services;
#else
namespace Emby.Plugins.Moonfin.Services;
#endif

// Source-linked into both plugins; each host retains its own visibility and cancellation logic.
public sealed partial class CinemaMediaResolver
{
    public sealed record MediaName(string Title, int? Year);

    private async Task<Resolution> ResolveItemAsync(Video item, BaseItem? owner,
        string? expectedMediaType, Func<string, ItemLookupInfo> parseStandard,
        CancellationToken cancellationToken)
    {
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
        if ((direct ?? owned) is int id && type != null)
        {
            return new(id, type);
        }

        // File-based identifiers are usable regardless of which program created the trailer.
        var fromPath = owner == null ? ParseTrailerFilenameIdentity(item.Path) : null;
        var fromName = owner == null ? ParseTrailerFilenameIdentity(item.Name, isPath: false) : null;
        if (fromPath.HasValue && fromName.HasValue &&
            (fromPath.Value.MediaType != fromName.Value.MediaType ||
             fromPath.Value.TmdbId != fromName.Value.TmdbId ||
             fromPath.Value.TvdbId != fromName.Value.TvdbId))
            return new(null);
        var filenameIdentity = fromPath ?? fromName;
        if (filenameIdentity is { } named && type != null && type != named.MediaType)
            return new(null);

        var itemTvdb = PositiveId(item.ProviderIds, "Tvdb");
        var ownerTvdb = owner == null ? null : PositiveId(owner.ProviderIds, "Tvdb");
        if (itemTvdb.HasValue && ownerTvdb.HasValue && itemTvdb != ownerTvdb)
            return new(null);
        if (filenameIdentity?.TvdbId is int fileTvdb &&
            ((itemTvdb.HasValue && itemTvdb != fileTvdb) ||
             (ownerTvdb.HasValue && ownerTvdb != fileTvdb)))
            return new(null);
        var tvdbId = itemTvdb ?? ownerTvdb ?? filenameIdentity?.TvdbId;

        if (filenameIdentity?.TmdbId is int movieId)
        {
            if (tvdbId.HasValue || (direct.HasValue && direct != movieId)) return new(null);
            return new(movieId, "movie");
        }

        // Known metadata and typed filename IDs override playback context.
        var searchType = type ?? filenameIdentity?.MediaType ?? (tvdbId.HasValue ? "tv" : expectedMediaType);
        if (searchType is not ("movie" or "tv") || (tvdbId.HasValue && searchType != "tv"))
            return new(null);

        if (tvdbId is int externalId)
        {
            // Use an ID-only provider lookup. A blank Name avoids guessing by title.
            var results = await AwaitSearch(_providers.GetRemoteSearchResults<Series, SeriesInfo>(
                new RemoteSearchQuery<SeriesInfo>
                {
                    SearchInfo = new SeriesInfo
                    {
                        Name = string.Empty,
                        ProviderIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["Tvdb"] = externalId.ToString(CultureInfo.InvariantCulture),
                        },
                    },
                    SearchProviderName = "TheMovieDb",
                    IncludeDisabledProviders = false,
                },
                cancellationToken), cancellationToken).ConfigureAwait(false);
            var mappedResults = results.ToArray();
            if (mappedResults.Length > 0)
            {
                var mappedId = UniqueTvdbTmdbId(externalId, mappedResults);
                if (!mappedId.HasValue || (direct.HasValue && direct != mappedId)) return new(null);
                return new(mappedId.Value, "tv");
            }
        }
        // When attached, identify the accessible owner, never a potentially unrelated trailer filename.
        if (owner != null && (string.IsNullOrWhiteSpace(owner.Name) ||
            owner.Name.Length > 200 || NormalizeTitle(owner.Name).Length == 0)) return new(null);
        MediaName?[] candidates;
        if (owner != null)
            candidates = new[] { new MediaName(owner.Name, owner.ProductionYear) };
        else if (filenameIdentity is { } n)
            candidates = new[] { new MediaName(n.Title, null) };
        else
            candidates = new[] { ParseName(item.Path, parseStandard), ParseName(item.Name, parseStandard, isPath: false) };
        var names = candidates.OfType<MediaName>().ToArray();
        // Try generated-suffix cleanup first, then the complete title.
        var ordered = owner == null && filenameIdentity == null
            ? names.SelectMany(OrderedNames)
            : names;
        foreach (var name in ordered
            .GroupBy(n => (NormalizeTitle(n.Title), n.Year))
            .Select(group => group.First()))
        {
            // Mirror Jellyfin's TMDB title cleanup, which its remote-search API skips.
            var searchName = Regex.Replace(name.Title, @"[\W_-[·]]+", " ");
            IEnumerable<RemoteSearchResult> results;
            if (searchType == "movie")
            {
                results = await AwaitSearch(_providers.GetRemoteSearchResults<Movie, MovieInfo>(new RemoteSearchQuery<MovieInfo>
                {
                    SearchInfo = new MovieInfo { Name = searchName, Year = name.Year },
                    SearchProviderName = "TheMovieDb",
                    IncludeDisabledProviders = false,
                }, cancellationToken), cancellationToken).ConfigureAwait(false);
            }
            else
            {
                results = await AwaitSearch(_providers.GetRemoteSearchResults<Series, SeriesInfo>(new RemoteSearchQuery<SeriesInfo>
                {
                    SearchInfo = new SeriesInfo { Name = searchName, Year = name.Year },
                    SearchProviderName = "TheMovieDb",
                    IncludeDisabledProviders = false,
                }, cancellationToken), cancellationToken).ConfigureAwait(false);
            }
            // Use TMDB's ranking, filtering the known year if necessary.
            var first = results.FirstOrDefault(r => !name.Year.HasValue ||
                (r.ProductionYear ?? r.PremiereDate?.Year) == name.Year);
            var matchId = PositiveTmdb(first?.ProviderIds);
            if (!matchId.HasValue) continue;
            if (direct.HasValue && direct != matchId) return new(null);
            return new(matchId.Value, searchType);
        }
        return new(null);
    }

    // An extra attached to a season or episode advertises its series.
    private static Guid OwnerSeriesId(BaseItem owner) => owner switch
    {
        Season season => season.SeriesId,
        Episode episode => episode.SeriesId,
        _ => Guid.Empty,
    };

    private static string? ProviderValue(IDictionary<string, string>? ids, string key) =>
        ids?.FirstOrDefault(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value;

    private static int? PositiveId(IDictionary<string, string>? ids, string key)
    {
        var value = ProviderValue(ids, key);
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 ? id : null;
    }

    public static int? PositiveTmdb(IDictionary<string, string>? ids) => PositiveId(ids, "Tmdb");

    /// <summary>Accept a unique TMDB mapping from a TVDB-based series lookup.</summary>
    public static int? UniqueTvdbTmdbId(int tvdbId, IEnumerable<RemoteSearchResult> results)
    {
        int? found = null;
        foreach (var result in results)
        {
            var reportedTvdb = ProviderValue(result.ProviderIds, "Tvdb");
            if (reportedTvdb != null &&
                (!int.TryParse(reportedTvdb, NumberStyles.None, CultureInfo.InvariantCulture, out var reported) || reported != tvdbId))
                return null;
            var id = PositiveTmdb(result.ProviderIds);
            if (!id.HasValue || (found.HasValue && found != id)) return null;
            found = id;
        }
        return found;
    }

    public static MediaName? ParseName(string? pathOrName, Func<string, ItemLookupInfo> parseStandard, bool isPath = true)
    {
        if (string.IsNullOrWhiteSpace(pathOrName)) return null;
        var filename = isPath ? pathOrName.Replace('\\', '/').Split('/').Last() : pathOrName;
        filename = Regex.Replace(filename, @"\.(mp4|mkv|avi|mov|webm|m4v|ts|m2ts)$", "", RegexOptions.IgnoreCase)
            .Normalize(NormalizationForm.FormKC).Replace('⧸', '/').Replace('⁄', '/').Replace('∕', '/');
        // Channel-prefixed downloads use "Uploader - Title [video ID]".
        var prefixed = Regex.Match(filename,
            @"^.+?\s+-\s+(?<title>.+?)\s+\[[A-Za-z0-9_-]{10,12}\]$");
        if (prefixed.Success) filename = prefixed.Groups["title"].Value;
        var parsed = parseStandard(filename);
        var title = parsed.Name ?? "";
        var year = parsed.Year;

        // Once a filename says Trailer/Teaser, everything after that label is decoration.
        // Use the last trailer label to avoid truncating earlier title words.
        var trailerLabels = Regex.Matches(title,
            @"[\s._:|-]+(?:(?:official|final|theatrical)[\s._:|-]+)?(?:teaser[\s._:|-]+trailer|trailer|teaser)(?![\p{L}\p{N}])",
            RegexOptions.IgnoreCase);
        if (trailerLabels.Count > 0)
            title = title.Substring(0, trailerLabels[trailerLabels.Count - 1].Index);

        // The host may remove Trailer but leave its Official/Final prefix.
        if (Regex.IsMatch(filename, @"(?:official|final|theatrical)[\s._:|-]+(?:teaser[\s._:|-]+)?trailer\b", RegexOptions.IgnoreCase))
            title = Regex.Replace(title, @"[\s._:|-]+(?:official|final|theatrical)$", "", RegexOptions.IgnoreCase);
        title = Regex.Replace(title,
            @"[\s._:|-]+(?:watch[\s._:|-]+at[\s._:|-]+home|watch[\s._:|-]+now|on[\s._:|-]+digital)$",
            "", RegexOptions.IgnoreCase);
        // Handle release years joined directly to titles, which the host may not parse.
        if (!year.HasValue)
        {
            var compact = Regex.Match(title, @"^(?<title>.*\p{L})(?<year>19\d{2}|20\d{2})$");
            if (compact.Success && int.TryParse(compact.Groups["year"].Value, out var candidate)
                && candidate <= DateTime.UtcNow.Year + 3)
            {
                title = compact.Groups["title"].Value;
                year = candidate;
            }
        }

        var seriesTitle = Regex.Replace(title, @"[\s._:|-]+(?:season[\s._|-]*|s)\d{1,3}$", "", RegexOptions.IgnoreCase);
        if (seriesTitle != title)
        {
            title = seriesTitle;
            year = null;
        }

        title = title.Replace('.', ' ').Replace('_', ' ').Trim(' ', '-', '–', ':', '|');
        if (year is < 1900 || year > DateTime.UtcNow.Year + 3) year = null;
        if (title.Length == 0 || title.Length > 200 || NormalizeTitle(title).Length == 0
            || Regex.IsMatch(title, @"\A[0-9a-f]{32,64}\z", RegexOptions.IgnoreCase)) return null;
        return new MediaName(title, year);
    }

    // Avoid TMDB's fuzzy matches for generated filename suffixes.
    private static IEnumerable<MediaName> OrderedNames(MediaName source)
    {
        var title = source.Title.Trim(' ', '|');
        var suffix = Regex.Match(title, @"[\s._-]+(?<id>[A-Za-z0-9_-]{8,})$");
        var token = suffix.Groups["id"].Value;
        var mixedCaseId = token.Length >= 10 && token.All(char.IsLetter) &&
            Regex.IsMatch(token, @"[A-Z]{2}") && Regex.IsMatch(token, @"[a-z]{2,}[A-Z]");
        if (suffix.Success && token.Any(char.IsLetter) &&
            (token.Count(char.IsDigit) > 1 || Regex.IsMatch(token, @"\d[A-Za-z]") || mixedCaseId))
            yield return new MediaName(title.Substring(0, suffix.Index), source.Year);

        yield return source;
    }

    // Recognize embedded provider IDs by their namespace, not the downloader.
    public static (string Title, string MediaType, int? TmdbId, int? TvdbId)? ParseTrailerFilenameIdentity(
        string? pathOrName, bool isPath = true)
    {
        if (string.IsNullOrWhiteSpace(pathOrName)) return null;
        var filename = isPath ? pathOrName.Replace('\\', '/').Split('/').Last() : pathOrName;
        var match = Regex.Match(filename,
            @"^(?<title>.+)_(?<source>tvdb|tmdb)?(?<id>[1-9]\d*)_trailer(?:\.(?:mp4|mkv|avi|mov|webm|m4v|ts|m2ts))?$",
            RegexOptions.IgnoreCase);
        if (!match.Success || !int.TryParse(match.Groups["id"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
            return null;
        var source = match.Groups["source"].Value;
        // A bare four-digit year is not reliable evidence of a TMDB ID.
        if (source.Length == 0 && id >= 1900 && id <= DateTime.UtcNow.Year + 3) return null;
        var title = match.Groups["title"].Value.Replace('_', ' ').Trim();
        if (title.Length == 0 || title.Length > 200 || NormalizeTitle(title).Length == 0) return null;
        var isTv = source.Equals("tvdb", StringComparison.OrdinalIgnoreCase);
        return (title, isTv ? "tv" : "movie", isTv ? (int?)null : id, isTv ? id : (int?)null);
    }

    private static string NormalizeTitle(string title) => string.Concat(
        title.Normalize(NormalizationForm.FormKC).ToUpperInvariant().Where(char.IsLetterOrDigit));
}
