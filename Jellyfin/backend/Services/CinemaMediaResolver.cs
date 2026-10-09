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
            if (owner == null) return new(null);
            var seriesId = OwnerSeriesId(owner);
            if (seriesId != Guid.Empty)
                owner = library.GetItemById<Series>(seriesId, userId);
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
             fromPath.Value.TvdbId != fromName.Value.TvdbId ||
             NormalizeTitle(fromPath.Value.Title) != NormalizeTitle(fromName.Value.Title)))
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
            var results = await providers.GetRemoteSearchResults<Series, SeriesInfo>(
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
                cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);
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
        Func<string, ItemLookupInfo> parseStandard = library.ParseName;
        MediaName?[] candidates;
        if (owner != null)
            candidates = new[] { new MediaName(owner.Name, owner is Movie ? owner.ProductionYear : null) };
        else if (filenameIdentity is { } n)
            candidates = new[] { new MediaName(n.Title, null) };
        else
            candidates = new[] { ParseName(item.Path, parseStandard), ParseName(item.Name, parseStandard, isPath: false) };
        var names = candidates
            .Where(n => n != null)
            .Select(n => searchType == "tv"
                ? new MediaName(n!.Title, null)
                : new MediaName(n!.Title, n!.Year ?? (owner == null ? item.ProductionYear : null)))
            .GroupBy(n => (NormalizeTitle(n.Title), n.Year))
            .Select(group => group.First()).ToArray();
        // Only try delimited alternatives when the complete title yields no exact match.
        var alternatives = owner == null && filenameIdentity == null
            ? names.SelectMany(DelimitedNames)
                .GroupBy(n => (NormalizeTitle(n.Title), n.Year))
                .Select(group => group.First()).ToArray()
            : Array.Empty<MediaName>();
        foreach (var batch in new[] { names, alternatives })
        {
            Resolution? resolved = null;
            foreach (var name in batch)
            {
                IEnumerable<RemoteSearchResult> results;
                if (searchType == "movie")
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
                var match = new Resolution(matches[0], searchType);
                if (resolved != null && resolved.TmdbId != match.TmdbId) return new(null);
                resolved = match;
            }
            if (resolved != null) return resolved;
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
        filename = Regex.Replace(filename, @"\.(mp4|mkv|avi|mov|webm|m4v|ts)$", "", RegexOptions.IgnoreCase);
        var parsed = parseStandard(filename);
        var title = parsed.Name ?? "";
        var year = parsed.Year;

        // Once a filename says Trailer/Teaser, everything after that label is decoration.
        // Use the last trailer label to avoid truncating earlier title words.
        var trailerLabels = Regex.Matches(title,
            @"[\s._:-]+(?:(?:official|final|theatrical)[\s._:-]+)?(?:teaser[\s._:-]+trailer|trailer|teaser)(?![\p{L}\p{N}])",
            RegexOptions.IgnoreCase);
        if (trailerLabels.Count > 0)
            title = title.Substring(0, trailerLabels[trailerLabels.Count - 1].Index);

        // The host may remove Trailer but leave its Official/Final prefix.
        if (Regex.IsMatch(filename, @"(?:official|final|theatrical)[\s._:-]+(?:teaser[\s._:-]+)?trailer\b", RegexOptions.IgnoreCase))
            title = Regex.Replace(title, @"[\s._:-]+(?:official|final|theatrical)$", "", RegexOptions.IgnoreCase);
        title = Regex.Replace(title,
            @"[\s._:-]+(?:watch[\s._:-]+at[\s._:-]+home|watch[\s._:-]+now|on[\s._:-]+digital)$",
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

        var seriesTitle = Regex.Replace(title, @"[\s._:-]+(?:season[\s._-]*|s)\d{1,3}$", "", RegexOptions.IgnoreCase);
        if (seriesTitle != title)
        {
            title = seriesTitle;
            year = null;
        }

        title = title.Replace('.', ' ').Replace('_', ' ').Trim(' ', '-', '–', ':');
        if (year is < 1900 || year > DateTime.UtcNow.Year + 3) year = null;
        if (title.Length == 0 || title.Length > 200 || NormalizeTitle(title).Length == 0
            || Regex.IsMatch(title, @"\A[0-9a-f]{32,64}\z", RegexOptions.IgnoreCase)) return null;
        return new MediaName(title, year);
    }

    // Delimited titles may include channel names or descriptions.
    // Verify each part with an exact remote match rather than guessing keywords.
    private static IEnumerable<MediaName> DelimitedNames(MediaName source)
    {
        var title = source.Title.Trim(' ', '|');
        foreach (Match separator in Regex.Matches(title, @"\s*\|\s*|\s+[-–—]\s+"))
        {
            var before = title.Substring(0, separator.Index).Trim(' ', '|');
            var after = title.Substring(separator.Index + separator.Length).Trim(' ', '|');
            if (after.Length > 0) yield return new MediaName(after, source.Year);
            if (before.Length > 0) yield return new MediaName(before, source.Year);
        }

        // Try generated-looking suffixes only if the full title has no exact match.
        var suffix = Regex.Match(title, @"[\s._-]+(?<id>[A-Za-z0-9_-]{8,})$");
        var token = suffix.Groups["id"].Value;
        // Long letter-only IDs need stronger case patterns than ordinary title words.
        var mixedCaseId = token.Length >= 10 && token.All(char.IsLetter) &&
            Regex.IsMatch(token, @"[A-Z]{2}") && Regex.IsMatch(token, @"[a-z]{2,}[A-Z]");
        if (suffix.Success && token.Any(char.IsLetter) &&
            (token.Count(char.IsDigit) > 1 || Regex.IsMatch(token, @"\d[A-Za-z]") || mixedCaseId))
            yield return new MediaName(title.Substring(0, suffix.Index), source.Year);
    }

    private static IEnumerable<int> Matches(MediaName name, IEnumerable<RemoteSearchResult> results)
    {
        var title = NormalizeTitle(name.Title);
        return results.Where(r => NormalizeTitle(r.Name ?? "") == title &&
                (!name.Year.HasValue || r.ProductionYear == name.Year))
            // An exact candidate without an ID leaves identity uncertain too.
            .Select(r => PositiveTmdb(r.ProviderIds) ?? 0).Distinct();
    }

    // Recognize embedded provider IDs by their namespace, not the downloader.
    public static (string Title, string MediaType, int? TmdbId, int? TvdbId)? ParseTrailerFilenameIdentity(
        string? pathOrName, bool isPath = true)
    {
        if (string.IsNullOrWhiteSpace(pathOrName)) return null;
        var filename = isPath ? pathOrName.Replace('\\', '/').Split('/').Last() : pathOrName;
        var match = Regex.Match(filename,
            @"^(?<title>.+)_(?<source>tvdb|tmdb)?(?<id>[1-9]\d*)_trailer(?:\.(?:mp4|mkv|avi|mov|webm|m4v|ts))?$",
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
