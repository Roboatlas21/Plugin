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

/// <summary>Resolves one intro, without scanning the library or retaining search results.</summary>
public sealed class CinemaMovieResolver(ILibraryManager library, IProviderManager providers)
{
    public sealed record Resolution(int? TmdbId, string? Source);
    public sealed record MovieName(string Title, int? Year);

    public async Task<Resolution> ResolveAsync(Guid itemId, Guid userId, CancellationToken cancellationToken)
    {
        // This overload uses IsVisibleStandalone, including parental/library restrictions,
        // and can read registered private Videos that don't occur in library queries.
        // The Guid overload also avoids binding to the User type moved in Jellyfin 10.11.
        var item = library.GetItemById<Video>(itemId, userId);
        if (item == null || item is Episode) return new(null, null);

        Movie? owner = null;
        if (item.ExtraType == ExtraType.Trailer && item.OwnerId != Guid.Empty)
        {
            owner = library.GetItemById<Movie>(item.OwnerId, userId);
            // A trailer for an inaccessible item or a series must not fall through
            // to a movie search, where a different work can have the same title.
            if (owner == null) return new(null, null);
        }
        if (PositiveTmdb(item.ProviderIds) is int direct) return new(direct, "direct");
        if (owner != null && PositiveTmdb(owner.ProviderIds) is int owned) return new(owned, "owner");

        var name = ParseName(item.Path) ?? ParseName(item.Name);
        if (name == null) return new(null, null);
        var results = await providers.GetRemoteSearchResults<Movie, MovieInfo>(
            new RemoteSearchQuery<MovieInfo>
            {
                SearchInfo = new MovieInfo { Name = name.Title, Year = name.Year },
                IncludeDisabledProviders = false,
            }, cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);
        var match = Match(name, results);
        return new(match, match.HasValue ? "filename" : null);
    }

    public static int? PositiveTmdb(IDictionary<string, string>? ids)
    {
        var value = ids?.FirstOrDefault(p => p.Key.Equals("Tmdb", StringComparison.OrdinalIgnoreCase)).Value;
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 ? id : null;
    }

    public static MovieName? ParseName(string? pathOrName)
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
        // A numeric title such as 1917 remains a title, not a year.
        if (title.Length == 0 || title.Length > 200 || NormalizeTitle(title).Length == 0) return null;
        return new(title, year);
    }

    public static int? Match(MovieName name, IEnumerable<RemoteSearchResult> results)
    {
        var title = NormalizeTitle(name.Title);
        var matches = results.Where(r => NormalizeTitle(r.Name ?? "") == title &&
                (!name.Year.HasValue || r.ProductionYear == name.Year))
            .Select(r => PositiveTmdb(r.ProviderIds)).Where(id => id.HasValue)
            .Distinct().Take(2).ToArray();
        // Duplicate provider results for the same movie are fine; distinct IDs are ambiguous.
        return matches.Length == 1 ? matches[0] : null;
    }

    private static string NormalizeTitle(string title) => string.Concat(
        title.Normalize(NormalizationForm.FormKC).ToUpperInvariant().Where(char.IsLetterOrDigit));
}
