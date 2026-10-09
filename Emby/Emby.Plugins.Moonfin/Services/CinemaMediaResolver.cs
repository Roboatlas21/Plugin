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

        public sealed record Resolution(int? TmdbId, string? MediaType = null);
        public sealed record MediaName(string Title, int? Year);

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
            if (item == null || item is Episode || !item.IsVisibleStandalone(user)) return new Resolution(null);

            BaseItem? owner = null;
            if (item.OwnerId != Guid.Empty)
            {
                owner = _library.GetItemById(item.OwnerId);
                if (owner == null || !owner.IsVisibleStandalone(user) || (owner is not Movie && owner is not Series))
                    return new Resolution(null);
            }

            // Moonfin uses a valid TMDB ID directly when it can determine the type.
            // When the fallback is called, still validate any accessible owner and
            // direct metadata; playback context does not type a bare server-side ID.
            var explicitType = ProviderValue(item.ProviderIds, "TmdbMediaType");
            if (explicitType != null && explicitType is not ("movie" or "tv")) return new Resolution(null);
            var itemType = item is Movie ? "movie" : null;
            var ownerType = owner is Movie ? "movie" : owner is Series ? "tv" : null;
            var types = new[] { explicitType, itemType, ownerType }
                .Where(t => t != null).Distinct().ToArray();
            if (types.Length > 1) return new Resolution(null);

            var type = types.FirstOrDefault();
            var direct = PositiveTmdb(item.ProviderIds);
            var owned = owner == null ? null : PositiveTmdb(owner.ProviderIds);
            if (direct.HasValue && owned.HasValue && direct != owned) return new Resolution(null);

            var hasDirectId = ProviderValue(item.ProviderIds, "Tmdb") != null;
            if (hasDirectId && !direct.HasValue) return new Resolution(null);
            if ((direct ?? owned) is int id && type != null)
            {
                return new Resolution(id, type);
            }

            // NeXroll names movies Title_tmdbId_trailer and shows Title_tvdbId_trailer.
            // An unmarked number is a movie TMDB ID; a TVDB number only types a TV search.
            var fromPath = owner == null ? ParseNeXrollName(item.Path) : null;
            var fromName = owner == null ? ParseNeXrollName(item.Name) : null;
            if (fromPath.HasValue && fromName.HasValue &&
                (fromPath.Value.MediaType != fromName.Value.MediaType ||
                 fromPath.Value.TmdbId != fromName.Value.TmdbId ||
                 NormalizeTitle(fromPath.Value.Title) != NormalizeTitle(fromName.Value.Title)))
                return new Resolution(null);
            var nexroll = fromPath ?? fromName;
            if (nexroll is { } named)
            {
                if (type != null && type != named.MediaType) return new Resolution(null);
                if (named.TmdbId is int movieId)
                {
                    if (direct.HasValue && direct.Value != movieId) return new Resolution(null);
                    return new(movieId, "movie");
                }
                if (direct.HasValue) return new(direct.Value, "tv");
            }

            // Known type or the NeXroll TVDB label determines category; context is last resort.
            var searchType = type ?? nexroll?.MediaType ?? expectedMediaType;
            if (searchType is not ("movie" or "tv"))
                return new Resolution(null);

            Resolution? resolved = null;
            // When attached, identify the accessible owner, never a potentially unrelated trailer filename.
            if (owner != null && (string.IsNullOrWhiteSpace(owner.Name) ||
                owner.Name.Length > 200 || NormalizeTitle(owner.Name).Length == 0)) return new Resolution(null);
            Func<string, ItemLookupInfo> parseStandard = name => _library.ParseName(name.AsSpan());
            MediaName?[] candidates;
            if (owner != null)
                candidates = new[] { new MediaName(owner.Name, owner is Movie ? owner.ProductionYear : null) };
            else if (nexroll is { } n)
                candidates = new[] { new MediaName(n.Title, null) };
            else
                candidates = new[] { ParseName(item.Path, parseStandard), ParseName(item.Name, parseStandard) };
            var names = candidates
                .Where(n => n != null)
                .Select(n => searchType == "tv"
                    ? new MediaName(n!.Title, null)
                    : new MediaName(n!.Title, n!.Year ?? (owner == null ? item.ProductionYear : null)))
                .GroupBy(n => (NormalizeTitle(n.Title), n.Year))
                .Select(group => group.First());

            foreach (var name in names)
            {
                if (searchType == "movie" && !name!.Year.HasValue) continue;

                IEnumerable<RemoteSearchResult> results;
                if (searchType == "movie")
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
                            SearchInfo = new SeriesInfo { Name = name!.Title },
                            IncludeDisabledProviders = false,
                        },
                        cancellationToken).ConfigureAwait(false);
                }

                var matches = Matches(name!, results).Take(2).ToArray();
                if (matches.Length > 1 || matches.Any(id => id <= 0))
                    return new Resolution(null);
                if (matches.Length == 0) continue;
                if (direct.HasValue && direct.Value != matches[0])
                    return new Resolution(null);

                var match = new Resolution(matches[0], searchType);
                if (resolved != null && resolved.TmdbId != match.TmdbId)
                    return new Resolution(null);

                resolved = match;
            }

            return resolved ?? new Resolution(null);
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

        public static MediaName? ParseName(string? pathOrName, Func<string, ItemLookupInfo> parseStandard)
        {
            if (string.IsNullOrWhiteSpace(pathOrName)) return null;
            var filename = pathOrName.Replace('\\', '/').Split('/').Last();
            filename = Regex.Replace(filename, @"\.(mp4|mkv|avi|mov|webm|m4v|ts)$", "", RegexOptions.IgnoreCase);
            var parsed = parseStandard(filename);
            var title = parsed.Name ?? "";
            var year = parsed.Year;

            // Once a filename says Trailer/Teaser, everything after that label is decoration.
            // Use its last occurrence to preserve titles such as Trailer Park Boys.
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
            // The host expects a delimiter before a release year; also accept Dune2021.
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

        private static IEnumerable<int> Matches(MediaName name, IEnumerable<RemoteSearchResult> results)
        {
            var title = NormalizeTitle(name.Title);
            return results
                .Where(r => NormalizeTitle(r.Name ?? "") == title &&
                            (!name.Year.HasValue || r.ProductionYear == name.Year))
                .Select(r => PositiveTmdb(r.ProviderIds) ?? 0)
                .Distinct();
        }

        // NeXroll's TVDB ID marks a series, but is not itself a TMDB ID.
        public static (string Title, string MediaType, int? TmdbId)? ParseNeXrollName(string? pathOrName)
        {
            if (string.IsNullOrWhiteSpace(pathOrName)) return null;
            var filename = pathOrName.Replace('\\', '/').Split('/').Last();
            var match = Regex.Match(filename,
                @"^(?<title>.+)_(?<tvdb>tvdb)?(?<id>[1-9]\d*)_trailer(?:\.(?:mp4|mkv|avi|mov|webm|m4v|ts))?$",
                RegexOptions.IgnoreCase);
            if (!match.Success || !int.TryParse(match.Groups["id"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
                return null;
            var title = match.Groups["title"].Value.Replace('_', ' ').Trim();
            if (title.Length == 0 || title.Length > 200 || NormalizeTitle(title).Length == 0) return null;
            var tv = match.Groups["tvdb"].Success;
            return (title, tv ? "tv" : "movie", tv ? (int?)null : id);
        }

        private static string NormalizeTitle(string title) => string.Concat(
            title.Normalize(NormalizationForm.FormKC)
                .ToUpperInvariant()
                .Where(char.IsLetterOrDigit));
    }
}
