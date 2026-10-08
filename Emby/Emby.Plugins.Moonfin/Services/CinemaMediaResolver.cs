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
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;

namespace Emby.Plugins.Moonfin.Services
{
    /// <summary>
    /// Validates server-only trailer ownership/access and resolves identities the client
    /// could not establish locally, without scanning or caching the library.
    /// </summary>
    public sealed class CinemaMediaResolver
    {
        private readonly ILibraryManager _library;
        private readonly IProviderManager _providers;

        public sealed record Resolution(int? TmdbId, string? Source, string? MediaType = null, int? Season = null);
        public sealed record MediaName(string Title, int? Year, int? Season = null);

        public CinemaMediaResolver(ILibraryManager library, IProviderManager providers)
        {
            _library = library;
            _providers = providers;
        }

        public async Task<Resolution> ResolveMediaAsync(
            Guid itemId,
            User user,
            string? expectedMediaType,
            CancellationToken cancellationToken)
        {
            var item = _library.GetItemById(itemId) as Video;
            if (item == null || item is Episode || !item.IsVisibleStandalone(user)) return new Resolution(null, null);

            BaseItem? owner = null;
            if (item.ExtraType == ExtraType.Trailer && item.OwnerId != Guid.Empty)
            {
                owner = _library.GetItemById(item.OwnerId);
                if (owner == null || !owner.IsVisibleStandalone(user) || (owner is not Movie && owner is not Series))
                    return new Resolution(null, null);
            }

            // Moonfin normally consumes trustworthy unattached typed metadata locally.
            // If the request reaches Moonbase, still parse direct metadata so an attached
            // trailer can be checked against its accessible owner and so this endpoint
            // remains safe when called directly. Feature context never supplies the type.
            var explicitType = ProviderValue(item.ProviderIds, "TmdbMediaType");
            if (explicitType != null && explicitType is not ("movie" or "tv")) return new Resolution(null, null);
            var itemType = item is Movie ? "movie" : null;
            var ownerType = owner is Movie ? "movie" : owner is Series ? "tv" : null;
            var legacyType = explicitType == null && ProviderValue(item.ProviderIds, "trailers4jellyfin.trailer") != null
                ? "movie" : null;
            var types = new[] { explicitType, itemType, ownerType, legacyType }
                .Where(t => t != null).Distinct().ToArray();
            if (types.Length > 1) return new Resolution(null, null);

            var type = types.FirstOrDefault();
            var direct = PositiveTmdb(item.ProviderIds);
            var owned = owner == null ? null : PositiveTmdb(owner.ProviderIds);
            if (direct.HasValue && owned.HasValue && direct != owned) return new Resolution(null, null);

            var hasDirectId = ProviderValue(item.ProviderIds, "Tmdb") != null;
            if (hasDirectId && (!direct.HasValue || type == null)) return new Resolution(null, null);
            if ((direct ?? owned) is int id && type != null)
            {
                return new Resolution(
                    id,
                    direct.HasValue ? "direct" : "owner",
                    type);
            }

            // Feature context restricts filename search only. It never types a bare TMDB id.
            if (expectedMediaType is not ("movie" or "tv") || (type != null && type != expectedMediaType))
                return new Resolution(null, null);

            Resolution? resolved = null;
            var names = new[] { ParseName(item.Path), ParseName(item.Name) }
                .Where(n => n != null).Distinct();

            foreach (var name in names)
            {
                if (expectedMediaType == "movie" && (!name!.Year.HasValue || name.Season.HasValue)) continue;

                IEnumerable<RemoteSearchResult> results;
                if (expectedMediaType == "movie")
                {
                    results = await _providers.GetRemoteSearchResults<Movie, MovieInfo>(
                        new RemoteSearchQuery<MovieInfo>
                        {
                            SearchInfo = new MovieInfo { Name = name!.Title, Year = name.Year },
                            IncludeDisabledProviders = false,
                        },
                        cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    results = await _providers.GetRemoteSearchResults<Series, SeriesInfo>(
                        new RemoteSearchQuery<SeriesInfo>
                        {
                            SearchInfo = new SeriesInfo
                            {
                                Name = name!.Title,
                                Year = name.Season.HasValue ? null : name.Year,
                            },
                            IncludeDisabledProviders = false,
                        },
                        cancellationToken).ConfigureAwait(false);
                }

                var matchId = Match(name!, results, expectedMediaType);
                if (!matchId.HasValue)
                {
                    // Zero exact matches may fall back from a hashed/path name to the readable
                    // display name; ambiguity or a matching result without an ID may not.
                    var exact = ExactMatches(name!, results, expectedMediaType).Take(2).ToArray();
                    if (exact.Length > 0) return new Resolution(null, null);
                    continue;
                }

                var match = new Resolution(matchId.Value, "filename", expectedMediaType, name!.Season);
                if (resolved != null &&
                    (resolved.TmdbId != match.TmdbId ||
                     (resolved.Season.HasValue && match.Season.HasValue && resolved.Season != match.Season)))
                    return new Resolution(null, null);

                resolved = match with { Season = resolved?.Season ?? match.Season };
            }

            return resolved ?? new Resolution(null, null);
        }

        private static string? ProviderValue(IDictionary<string, string>? ids, string key) =>
            ids?.FirstOrDefault(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value;

        public static int? PositiveTmdb(IDictionary<string, string>? ids)
        {
            var value = ProviderValue(ids, "Tmdb");
            return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0
                ? id
                : (int?)null;
        }

        public static MediaName? ParseName(string? pathOrName)
        {
            if (string.IsNullOrWhiteSpace(pathOrName)) return null;

            var title = pathOrName.Replace('\\', '/').Split('/').Last();
            title = Regex.Replace(title, @"\.(mp4|mkv|avi|mov|webm|m4v|ts)$", "", RegexOptions.IgnoreCase);
            title = title.Replace('.', ' ').Replace('_', ' ').Trim();
            title = Regex.Replace(
                title,
                @"\s*[\[(]?(?:720p|1080p|2160p|4k|hd|uhd)[\])]?\s*$",
                "",
                RegexOptions.IgnoreCase);
            title = Regex.Replace(
                title,
                @"(?:^|[\s\-–:])(?:official\s+|theatrical\s+|final\s+)?(?:teaser(?:\s+trailer)?|trailer)(?:\s*#?\d+)?\s*$",
                "",
                RegexOptions.IgnoreCase).Trim(' ', '-', '–', ':');

            int? year = null;
            var match = Regex.Match(title, @"^(?<title>.+?)[\s\-(\[]+(?<year>\d{4})[)\]]?\s*$");
            if (match.Success &&
                int.TryParse(match.Groups["year"].Value, out var parsed) &&
                parsed >= 1900 &&
                parsed <= DateTime.UtcNow.Year + 3)
            {
                year = parsed;
                title = match.Groups["title"].Value.Trim(' ', '-', '–', ':');
            }

            int? season = null;
            var seasonMatch = Regex.Match(
                title,
                @"^(?<title>.+?)[\s\-:]+(?:season\s+|s)(?<season>\d{1,3})$",
                RegexOptions.IgnoreCase);
            if (seasonMatch.Success &&
                int.TryParse(seasonMatch.Groups["season"].Value, out var number) &&
                number > 0)
            {
                season = number;
                title = seasonMatch.Groups["title"].Value.Trim(' ', '-', ':');
            }

            if (title.Length == 0 ||
                title.Length > 200 ||
                NormalizeTitle(title).Length == 0 ||
                Regex.IsMatch(title, @"\A[0-9a-f]{32,64}\z", RegexOptions.IgnoreCase))
                return null;

            return new MediaName(title, year, season);
        }

        public static int? Match(MediaName name, IEnumerable<RemoteSearchResult> results, string mediaType)
        {
            var matches = ExactMatches(name, results, mediaType).Take(2).ToArray();
            return matches.Length == 1 && matches[0] > 0 ? matches[0] : (int?)null;
        }

        private static IEnumerable<int> ExactMatches(
            MediaName name,
            IEnumerable<RemoteSearchResult> results,
            string mediaType)
        {
            if (mediaType == "movie" && (!name.Year.HasValue || name.Season.HasValue))
                return Enumerable.Empty<int>();

            var year = mediaType == "tv" && name.Season.HasValue ? null : name.Year;
            var title = NormalizeTitle(name.Title);
            return results
                .Where(r => NormalizeTitle(r.Name ?? "") == title &&
                            (!year.HasValue || r.ProductionYear == year))
                .Select(r => PositiveTmdb(r.ProviderIds) ?? 0)
                .Distinct();
        }

        private static string NormalizeTitle(string title) => string.Concat(
            title.Normalize(NormalizationForm.FormKC)
                .ToUpperInvariant()
                .Where(char.IsLetterOrDigit));
    }
}
