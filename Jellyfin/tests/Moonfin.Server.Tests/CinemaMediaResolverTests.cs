using System.Reflection;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Providers;
using Moonfin.Server.Services;
using Xunit;

namespace Moonfin.Server.Tests;

public sealed class CinemaMediaResolverTests
{
    private static RemoteSearchResult Result(int id, string title, int? year = null) =>
        new() { Name = title, ProductionYear = year, ProviderIds = new() { ["Tmdb"] = id.ToString() } };

    private static CinemaMediaResolver Resolver(
        Video intro,
        Func<string, string, int?, IEnumerable<RemoteSearchResult>> search,
        Func<RemoteSearchQuery<SeriesInfo>, IEnumerable<RemoteSearchResult>>? tvdbSearch = null)
    {
        var library = new FakeLibraryManager { ItemForUserHandler = (_, _) => intro };
        var provider = DispatchProxy.Create<IProviderManager, SearchProvider>();
        ((SearchProvider)(object)provider).Search = search;
        ((SearchProvider)(object)provider).TvdbSearch = tvdbSearch;
        return new CinemaMediaResolver(library, provider);
    }

    [Theory]
    [InlineData("movie", "tv")]
    [InlineData("tv", "movie")]
    public async Task TypedTmdbMetadataOverridesPlaybackContext(string type, string context)
    {
        var intro = new Video { ProviderIds = new() { ["Tmdb"] = "42", ["TmdbMediaType"] = type } };
        var resolver = Resolver(intro, (_, _, _) => throw new Exception("No search expected"));
        Assert.Equal(new CinemaMediaResolver.Resolution(42, type),
            await resolver.ResolveMediaAsync(Guid.NewGuid(), Guid.NewGuid(), context, default));
    }

    [Theory]
    [InlineData("movie")]
    [InlineData("season")]
    [InlineData("episode")]
    public async Task OwnedTrailersUseAccessibleMovieOrSeries(string ownerKind)
    {
        var user = Guid.NewGuid();
        var series = new Series { Id = Guid.NewGuid(), ProviderIds = new() { ["Tmdb"] = "42" } };
        BaseItem owner = ownerKind switch
        {
            "movie" => new Movie { Id = Guid.NewGuid(), ProviderIds = new() { ["Tmdb"] = "42" } },
            "season" => new Season { Id = Guid.NewGuid(), SeriesId = series.Id },
            _ => new Episode { Id = Guid.NewGuid(), SeriesId = series.Id },
        };
        var intro = new Video { Id = Guid.NewGuid(), OwnerId = owner.Id, Path = "Wrong Title Trailer.mp4" };
        var library = new FakeLibraryManager
        {
            ItemForUserHandler = (id, uid) =>
            {
                Assert.Equal(user, uid);
                return id == intro.Id ? intro : id == owner.Id ? owner : id == series.Id ? series : null;
            },
        };
        var resolver = new CinemaMediaResolver(library, null!);
        Assert.Equal(new CinemaMediaResolver.Resolution(42, ownerKind == "movie" ? "movie" : "tv"),
            await resolver.ResolveMediaAsync(intro.Id, user, "movie", default));

        intro.ProviderIds["Tmdb"] = "99";
        Assert.Null((await resolver.ResolveMediaAsync(intro.Id, user, "movie", default)).TmdbId);
        intro.ProviderIds.Clear();
        library.ItemForUserHandler = (id, _) => id == intro.Id ? intro : null;
        Assert.Null((await resolver.ResolveMediaAsync(intro.Id, user, "movie", default)).TmdbId);
    }

    [Fact]
    public async Task FilenameMovieIdAllowsRenamingButRejectsConflictingIds()
    {
        var intro = new Video { Path = "Dune_438631_trailer.mp4", Name = "Localized_Dune_438631_trailer" };
        var resolver = Resolver(intro, (_, _, _) => throw new Exception("No search expected"));
        Assert.Equal(new CinemaMediaResolver.Resolution(438631, "movie"),
            await resolver.ResolveMediaAsync(Guid.NewGuid(), Guid.NewGuid(), "tv", default));
        intro.Name = "Other_12345_trailer";
        Assert.Null((await resolver.ResolveMediaAsync(Guid.NewGuid(), Guid.NewGuid(), "movie", default)).TmdbId);
    }

    [Fact]
    public async Task TvdbFilenameMapsThroughHostTmdbProvider()
    {
        var intro = new Video { Path = "Silo_tvdb403245_trailer.mp4" };
        var resolver = Resolver(intro, (_, _, _) => throw new Exception("No title search expected"),
            query =>
            {
                Assert.Equal("403245", query.SearchInfo.ProviderIds["Tvdb"]);
                return [new RemoteSearchResult { ProviderIds = new() { ["Tmdb"] = "42", ["Tvdb"] = "403245" } }];
            });
        Assert.Equal(new CinemaMediaResolver.Resolution(42, "tv"),
            await resolver.ResolveMediaAsync(Guid.NewGuid(), Guid.NewGuid(), "movie", default));
    }

    [Fact]
    public async Task UnmappedTvdbIdFallsBackToRankedSeriesSearch()
    {
        var lookedUp = false;
        var resolver = Resolver(new Video { Path = "Silo_tvdb403245_trailer.mp4" },
            (type, name, year) => lookedUp && type == "tv" && name == "Silo" && year == null
                ? [Result(42, "Silo")] : [],
            _ => { lookedUp = true; return []; });
        Assert.Equal(new CinemaMediaResolver.Resolution(42, "tv"),
            await resolver.ResolveMediaAsync(Guid.NewGuid(), Guid.NewGuid(), "movie", default));
    }

    [Fact]
    public async Task YearlessMovieUsesTmdbRankingWithLocalizedTitles()
    {
        RemoteSearchResult[] matches = [Result(43, "Duna", 2021), Result(42, "Dune", 1984)];
        var resolver = Resolver(new Video { Path = "Dune Trailer.mp4", Name = "Other Movie Trailer" },
            (_, title, _) =>
            {
                Assert.Equal("Dune", title);
                return matches;
            });
        Assert.Equal(43, (await resolver.ResolveMediaAsync(Guid.NewGuid(), Guid.NewGuid(), "movie", default)).TmdbId);
        matches = [Result(42, "Dune", 1984), Result(43, "Duna", 2021)];
        Assert.Equal(42, (await resolver.ResolveMediaAsync(Guid.NewGuid(), Guid.NewGuid(), "movie", default)).TmdbId);
    }

    [Theory]
    [InlineData("Show Trailer.mp4", null, 43)]
    [InlineData("Show (1989) Trailer.mp4", 1989, 42)]
    [InlineData("Show (2024) Trailer.mp4", 2024, 43)]
    public async Task TvSearchRespectsTmdbRankingAndKnownPremiereYear(
        string filename, int? expectedYear, int expectedId)
    {
        var older = Result(42, "Show");
        older.PremiereDate = new DateTime(1989, 1, 1);
        var newer = Result(43, "Show");
        newer.PremiereDate = new DateTime(2024, 1, 1);
        var resolver = Resolver(new Video { Path = filename }, (type, title, year) =>
        {
            Assert.Equal("tv", type);
            Assert.Equal("Show", title);
            Assert.Equal(expectedYear, year);
            return [newer, older];
        });
        Assert.Equal(new CinemaMediaResolver.Resolution(expectedId, "tv"),
            await resolver.ResolveMediaAsync(Guid.NewGuid(), Guid.NewGuid(), "tv", default));
    }

    [Theory]
    [InlineData("Silo ABCdefghiJK.mp4", "", "Silo", "tv", null)]
    [InlineData("Netflix Anime - Ranma1⧸2 ｜ Official Trailer ｜ Netflix [Bv9wTsjqpSQ].mp4", "", "Ranma1 2", "tv", null)]
    [InlineData("Netflix Anime - Ranma1⧸2｜Official Trailer｜Netflix [Bv9wTsjqpSQ].mp4", "", "Ranma1 2", "tv", null)]
    [InlineData("Netflix Anime - Ranma1⧸2｜Official｜Trailer｜Netflix [Bv9wTsjqpSQ].mp4", "", "Ranma1 2", "tv", null)]
    [InlineData("Silo｜Official Trailer.mp4", "", "Silo", "tv", null)]
    [InlineData("Silo｜Final｜Teaser｜Trailer.mp4", "", "Silo", "tv", null)]
    [InlineData("Unrelated Channel - Ranma1⧸2 ｜ Official Trailer ｜ Netflix [Bv9wTsjqpSQ].mp4", "", "Ranma1 2", "tv", null)]
    [InlineData("Warner Bros - Dune - Part Two ｜ Official Trailer ｜ Warner Bros [Bv9wTsjqpSQ].mp4", "", "Dune Part Two", "movie", null)]
    [InlineData("Dune - Part Two ｜ Official Trailer ｜ Warner Bros.mp4", "", "Dune Part Two", "movie", null)]
    [InlineData("Dune: Part Two Official Trailer.mp4", "", "Dune Part Two", "movie", null)]
    [InlineData("Silo Season 5 (2026) Trailer.mp4", "", "Silo", "tv", null)]
    [InlineData("Silo｜Season 5｜Official Trailer.mp4", "", "Silo", "tv", null)]
    [InlineData("Silo｜Season｜5｜Official Trailer.mp4", "", "Silo", "tv", null)]
    [InlineData("Silo｜Watch Now.mp4", "", "Silo", "tv", null)]
    [InlineData("Silo｜Watch｜Now.mp4", "", "Silo", "tv", null)]
    [InlineData("Dune2021 Trailer.mp4", "", "Dune", "movie", 2021)]
    [InlineData("Trailer Park Boys Official Trailer abcdefghijk.mp4", "", "Trailer Park Boys", "tv", null)]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.mp4", "Show Season 5 Trailer", "Show", "tv", null)]
    public async Task TrailerNamesProduceCleanTmdbSearchQueries(
        string path, string displayName, string title, string type, int? year)
    {
        var intro = new Video { Path = path, Name = displayName };
        var searches = new List<string>();
        var resolver = Resolver(intro, (kind, candidate, candidateYear) =>
        {
            searches.Add(candidate);
            return kind == type && candidate == title && candidateYear == year
                ? [Result(42, title, year)] : [Result(99, "Unrelated")];
        });
        Assert.Equal(new CinemaMediaResolver.Resolution(42, type),
            await resolver.ResolveMediaAsync(Guid.NewGuid(), Guid.NewGuid(), type, default));
        Assert.Equal(new[] { title }, searches);
    }

    public class SearchProvider : DispatchProxy
    {
        public Func<string, string, int?, IEnumerable<RemoteSearchResult>> Search { get; set; } = null!;
        public Func<RemoteSearchQuery<SeriesInfo>, IEnumerable<RemoteSearchResult>>? TvdbSearch { get; set; }
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            Assert.Equal("GetRemoteSearchResults", method!.Name);
            if (args![0] is RemoteSearchQuery<MovieInfo> movie)
            {
                Assert.Equal(typeof(Movie), method.GetGenericArguments()[0]);
                Assert.False(movie.IncludeDisabledProviders);
                Assert.Equal("TheMovieDb", movie.SearchProviderName);
                return Task.FromResult(Search("movie", movie.SearchInfo.Name, movie.SearchInfo.Year));
            }
            var series = Assert.IsType<RemoteSearchQuery<SeriesInfo>>(args[0]);
            Assert.Equal(typeof(Series), method.GetGenericArguments()[0]);
            Assert.False(series.IncludeDisabledProviders);
            Assert.Equal("TheMovieDb", series.SearchProviderName);
            if (series.SearchInfo.ProviderIds.ContainsKey("Tvdb"))
            {
                Assert.Equal(string.Empty, series.SearchInfo.Name);
                Assert.NotNull(TvdbSearch);
                return Task.FromResult(TvdbSearch!(series));
            }
            return Task.FromResult(Search("tv", series.SearchInfo.Name, series.SearchInfo.Year));
        }
    }
}
