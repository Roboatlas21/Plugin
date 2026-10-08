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

            // Moonfin normally consumes trustworthy unattached typed metadata locally.
            // If the request reaches Moonbase, still parse direct metadata so an attached
            // trailer can be checked against its accessible owner and so this endpoint
            // remains safe when called directly. Feature context never supplies the type.
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
            // An untyped ID is only usable after the filename confirms the same ID.
            if (hasDirectId && !direct.HasValue) return new Resolution(null);
            if ((direct ?? owned) is int id && type != null)
            {
                return new Resolution(id, type);
            }

            // Feature context restricts filename search only. It never types a bare TMDB id.
            if (expectedMediaType is not ("movie" or "tv") || (type != null && type != expectedMediaType))
                return new Resolution(null);

            Resolution? resolved = null;
            var names = new[] { ParseName(item.Path), ParseName(item.Name) }
                .Where(n => n != null).Distinct();

            foreach (var name in names)
            {
                if (expectedMediaType == "movie" && !name!.Year.HasValue) continue;

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
                                Year = name.Year,
                            },
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

                var match = new Resolution(matches[0], expectedMediaType);
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
            const string trailerSuffixPattern = @"(?:^|[\s\-–:])(?:official\s+|theatrical\s+|final\s+)?(?:teaser(?:\s+trailer)?|trailer)(?:\s*#?\d+)?\s*$";
            title = Regex.Replace(title, trailerSuffixPattern, "", RegexOptions.IgnoreCase).Trim(' ', '-', '–', ':');

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
            // The year may follow "Trailer", so remove that suffix after extracting it.
            title = Regex.Replace(title, trailerSuffixPattern, "", RegexOptions.IgnoreCase).Trim(' ', '-', '–', ':');

            // Treat a trailer's season suffix as decoration, not request metadata.
            var seriesTitle = Regex.Replace(
                title,
                @"[\s\-:]+(?:season\s+|s)\d{1,3}$",
                "",
                RegexOptions.IgnoreCase);
            if (seriesTitle != title)
            {
                title = Regex.Replace(seriesTitle, @"[\s\-(\[]+\d{4}[)\]]?\s*$", "")
                    .Trim(' ', '-', ':');
                year = null;
            }

            if (title.Length == 0 ||
                title.Length > 200 ||
                NormalizeTitle(title).Length == 0 ||
                Regex.IsMatch(title, @"\A[0-9a-f]{32,64}\z", RegexOptions.IgnoreCase))
                return null;

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

        private static string NormalizeTitle(string title) => string.Concat(
            title.Normalize(NormalizationForm.FormKC)
                .ToUpperInvariant()
                .Where(char.IsLetterOrDigit));
    }
}
