using System.Reflection;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Moonfin.Server.Services;
using Xunit;

namespace Moonfin.Server.Tests;

public sealed class CinemaMediaResolverTests
{
    private static RemoteSearchResult Result(int id, string title, int? year = null) => new()
    {
        Name = title, ProductionYear = year,
        ProviderIds = new() { ["Tmdb"] = id.ToString() },
    };

    private static CinemaMediaResolver Resolver(Video intro, Func<string, string, int?, IEnumerable<RemoteSearchResult>> search)
    {
        var library = new FakeLibraryManager { ItemForUserHandler = (_, _) => intro };
        var provider = DispatchProxy.Create<IProviderManager, SearchProvider>();
        ((SearchProvider)(object)provider).Search = search;
        return new CinemaMediaResolver(library, provider);
    }

    [Theory]
    [InlineData("movie", "tv")]
    [InlineData("tv", "movie")]
    public async Task ExplicitTypeOverridesFeatureContext(string type, string context)
    {
        var intro = new Video { ProviderIds = new() { ["Tmdb"] = "42", ["TmdbMediaType"] = type } };
        var resolver = Resolver(intro, (_, _, _) => throw new Exception("No search expected"));
        var result = await resolver.ResolveMediaAsync(Guid.NewGuid(), Guid.NewGuid(), context, default);
        Assert.Equal(42, result.TmdbId);
        Assert.Equal(type, result.MediaType);
    }

    [Fact]
    public async Task UntypedIdsNeedMatchingSeriesFilenameAndConflictingTypesRemainInvalid()
    {
        var intro = new Video { Path = "Show Trailer.mp4", ProviderIds = new() { ["Tmdb"] = "42" } };
        var resolver = Resolver(intro, (type, name, year) =>
        {
            Assert.Equal("tv", type);
            Assert.Equal("Show", name);
            Assert.Null(year);
            return [Result(42, name)];
        });
        Assert.Null((await resolver.ResolveMediaAsync(Guid.NewGuid(), Guid.NewGuid(), null, default)).TmdbId);
        Assert.Null((await resolver.ResolveMediaAsync(Guid.NewGuid(), Guid.NewGuid(), "movie", default)).TmdbId);
        Assert.Equal(new CinemaMediaResolver.Resolution(42, "tv"),
            await resolver.ResolveMediaAsync(Guid.NewGuid(), Guid.NewGuid(), "tv", default));
        intro.ProviderIds["Tmdb"] = "43";
        Assert.Null((await resolver.ResolveMediaAsync(Guid.NewGuid(), Guid.NewGuid(), "tv", default)).TmdbId);

        intro = new Movie { ProviderIds = new() { ["Tmdb"] = "42", ["TmdbMediaType"] = "tv" } };
        resolver = Resolver(intro, (_, _, _) => throw new Exception("No search expected"));
        Assert.Null((await resolver.ResolveMediaAsync(Guid.NewGuid(), Guid.NewGuid(), "movie", default)).TmdbId);
    }

    [Fact]
    public async Task AccessibleSeriesOwnerIsAuthoritative()
    {
        var user = Guid.NewGuid();
        var series = new Series { Id = Guid.NewGuid(), ProviderIds = new() { ["Tmdb"] = "42" } };
        var intro = new Video { Id = Guid.NewGuid(), ExtraType = ExtraType.Trailer, OwnerId = series.Id };
        var library = new FakeLibraryManager { ItemForUserHandler = (id, uid) =>
        {
            Assert.Equal(user, uid);
            return id == intro.Id ? intro : id == series.Id ? series : null;
        }};
        var resolver = new CinemaMediaResolver(library, null!);
        Assert.Equal(new CinemaMediaResolver.Resolution(42, "tv"),
            await resolver.ResolveMediaAsync(intro.Id, user, "movie", default));

        library.ItemForUserHandler = (id, _) => id == intro.Id ? intro : null;
        Assert.Null((await resolver.ResolveMediaAsync(intro.Id, user, "tv", default)).TmdbId);
    }

    [Theory]
    [InlineData("Show Season 5 (2026) Trailer.mp4", "Show")]
    [InlineData("Show (2026) Season 5 Trailer.mp4", "Show")]
    [InlineData("Show S05 Official Trailer.mp4", "Show")]
    [InlineData("Show Trailer.mp4", "Show")]
    public async Task SeriesSearchUsesExactTitleWithoutSeasonHints(string filename, string title)
    {
        var intro = new Video { Path = filename };
        var resolver = Resolver(intro, (type, name, year) =>
        {
            Assert.Equal("tv", type);
            Assert.Equal(title, name);
            Assert.Null(year);
            return [Result(42, title, 2016)];
        });
        Assert.Equal(new CinemaMediaResolver.Resolution(42, "tv"),
            await resolver.ResolveMediaAsync(Guid.NewGuid(), Guid.NewGuid(), "tv", default));
    }

    [Fact]
    public async Task YearlessMovieNeverFallsThroughToSeriesAndUnknownContextNeverSearches()
    {
        var intro = new Video { Path = "Show Trailer.mp4" };
        var resolver = Resolver(intro, (_, _, _) => throw new Exception("No search expected"));
        Assert.Null((await resolver.ResolveMediaAsync(Guid.NewGuid(), Guid.NewGuid(), "movie", default)).TmdbId);
        Assert.Null((await resolver.ResolveMediaAsync(Guid.NewGuid(), Guid.NewGuid(), null, default)).TmdbId);
    }

    [Fact]
    public async Task HashAndUnmatchedFilenameFallBackToReadableName()
    {
        var intro = new Video { Path = new string('a', 64) + ".mp4", Name = "Show Season 5 Trailer" };
        var searches = 0;
        var resolver = Resolver(intro, (type, title, _) =>
        {
            searches++;
            Assert.Equal("tv", type);
            return title == "Show" ? [Result(42, "Show")] : [];
        });
        Assert.Equal(42, (await resolver.ResolveMediaAsync(Guid.NewGuid(), Guid.NewGuid(), "tv", default)).TmdbId);
        Assert.Equal(1, searches);
        intro.Path = "unreadable-cache.mp4";
        Assert.Equal(42, (await resolver.ResolveMediaAsync(Guid.NewGuid(), Guid.NewGuid(), "tv", default)).TmdbId);
        Assert.Equal(3, searches);
    }

    [Fact]
    public async Task AmbiguityAndConflictingDisplayNamesHideTheAction()
    {
        var intro = new Video { Path = "Show Trailer.mp4", Name = "Other Trailer" };
        var resolver = Resolver(intro, (_, title, _) => [Result(title == "Show" ? 42 : 43, title)]);
        Assert.Null((await resolver.ResolveMediaAsync(Guid.NewGuid(), Guid.NewGuid(), "tv", default)).TmdbId);
        resolver = Resolver(intro, (_, title, _) => [Result(42, title), Result(43, title)]);
        Assert.Null((await resolver.ResolveMediaAsync(Guid.NewGuid(), Guid.NewGuid(), "tv", default)).TmdbId);
        resolver = Resolver(intro, (_, title, _) => [Result(42, title), new RemoteSearchResult { Name = title }]);
        Assert.Null((await resolver.ResolveMediaAsync(Guid.NewGuid(), Guid.NewGuid(), "tv", default)).TmdbId);
    }

    [Fact]
    public async Task SeasonDecorationsDoNotChangeSeriesIdentity()
    {
        var intro = new Video { Path = "Show Season 5 Trailer.mp4", Name = "Show" };
        var resolver = Resolver(intro, (_, title, _) => [Result(42, title)]);
        Assert.Equal(new CinemaMediaResolver.Resolution(42, "tv"),
            await resolver.ResolveMediaAsync(Guid.NewGuid(), Guid.NewGuid(), "tv", default));
    }

    [Fact]
    public async Task FailedMovieSearchNeverTriesTheSeriesNamespace()
    {
        var intro = new Video { Path = "Show (2024) Trailer.mp4" };
        var resolver = Resolver(intro, (type, _, _) =>
        {
            Assert.Equal("movie", type);
            return [];
        });
        Assert.Null((await resolver.ResolveMediaAsync(Guid.NewGuid(), Guid.NewGuid(), "movie", default)).TmdbId);
    }

    public class SearchProvider : DispatchProxy
    {
        public Func<string, string, int?, IEnumerable<RemoteSearchResult>> Search { get; set; } = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            Assert.Equal("GetRemoteSearchResults", method!.Name);
            if (args![0] is RemoteSearchQuery<MovieInfo> movie)
            {
                Assert.Equal(typeof(Movie), method.GetGenericArguments()[0]);
                Assert.False(movie.IncludeDisabledProviders);
                return Task.FromResult(Search("movie", movie.SearchInfo.Name, movie.SearchInfo.Year));
            }
            var series = Assert.IsType<RemoteSearchQuery<SeriesInfo>>(args[0]);
            Assert.Equal(typeof(Series), method.GetGenericArguments()[0]);
            Assert.False(series.IncludeDisabledProviders);
            return Task.FromResult(Search("tv", series.SearchInfo.Name, series.SearchInfo.Year));
        }
    }
}
