using System.Reflection;
using Emby.Plugins.Moonfin.Api;
using Emby.Plugins.Moonfin.Services;
using MediaBrowser.Controller.Net;
using Xunit;

namespace Emby.Plugins.Moonfin.Tests;

public sealed class CinemaMediaResolverTests
{
    [Theory]
    [InlineData("Dune.2021.1080p.BluRay.x264.mkv", "Dune", 2021, "Dune", 2021)]
    [InlineData("Dune2021.mp4", "Dune2021", null, "Dune", 2021)]
    [InlineData("1917 Trailer.mp4", "1917", null, "1917", null)]
    [InlineData("The New 2024 Trailer.mp4", "The New", 2024, "The New", 2024)]
    [InlineData("Dune watch at home 2021 trailer djdjdj.mp4", "Dune watch at home", 2021, "Dune", 2021)]
    [InlineData("Silo Trailer ABCdefghiJK.mp4", "Silo Trailer ABCdefghiJK", null, "Silo", null)]
    [InlineData("Trailer Park Boys Official Trailer abcdefghijk.mp4", "Trailer Park Boys Official Trailer abcdefghijk", null, "Trailer Park Boys", null)]
    [InlineData("Silo Season 5 Trailer.mp4", "Silo Season 5", null, "Silo", null)]
    [InlineData("Oppenheimer (2023) On Digital Teaser Trailer 1080p [QB176lyq-GH].webm", "Oppenheimer", 2023, "Oppenheimer", 2023)]
    public void CleansTrailerSuffixesAfterHostNameParsing(
        string filename, string hostTitle, int? hostYear, string expectedTitle, int? expectedYear)
    {
        // Host name/year parsing is delegated to Emby; verify only Moonbase's cleanup.
        var called = false;
        var parsed = CinemaMediaResolver.ParseName(filename, name =>
        {
            called = true;
            Assert.NotEmpty(name);
            return new MediaBrowser.Controller.Providers.ItemLookupInfo
            {
                Name = hostTitle,
                Year = hostYear,
            };
        });
        Assert.True(called);
        Assert.NotNull(parsed);
        Assert.Equal(expectedTitle, parsed.Title);
        Assert.Equal(expectedYear, parsed.Year);
    }

    [Theory]
    [InlineData("Dune_438631_trailer.mp4", "Dune", "movie", 438631, null)]
    [InlineData(@"C:\Trailers\Dune_Part_Two_693134_trailer.MP4", "Dune Part Two", "movie", 693134, null)]
    [InlineData("Silo_tvdb403245_trailer.mp4", "Silo", "tv", null, 403245)]
    public void EmbeddedTrailerIdsAreProviderTyped(
        string filename, string title, string type, int? tmdbId, int? tvdbId)
    {
        var parsed = CinemaMediaResolver.ParseTrailerFilenameIdentity(filename);
        Assert.NotNull(parsed);
        Assert.Equal(title, parsed.Value.Title);
        Assert.Equal(type, parsed.Value.MediaType);
        Assert.Equal(tmdbId, parsed.Value.TmdbId);
        Assert.Equal(tvdbId, parsed.Value.TvdbId);
    }

    [Theory]
    [InlineData("Dune_2021_trailer.mp4")]
    [InlineData("Dune_438631_trailer.mp4.part")]
    [InlineData("Silo_tvdb0_trailer.mp4")]
    public void UnreliableFilenameNumbersAreNotAssumedToBeIds(string filename)
    {
        Assert.Null(CinemaMediaResolver.ParseTrailerFilenameIdentity(filename));
    }

    [Fact]
    public void TvdbLookupAcceptsUniqueTmdbMappingWithoutEchoedTvdbId()
    {
        // Some providers return only the mapped TMDB ID for an ID-scoped lookup.
        var mapped = new MediaBrowser.Model.Providers.RemoteSearchResult
        {
            ProviderIds = new() { ["Tmdb"] = "42" },
        };
        Assert.Equal(42, CinemaMediaResolver.UniqueTvdbTmdbId(403245, [mapped, mapped]));
        Assert.Null(CinemaMediaResolver.UniqueTvdbTmdbId(403245,
            [mapped, new() { ProviderIds = new() { ["Tmdb"] = "43" } }]));
        mapped.ProviderIds["Tvdb"] = "999";
        Assert.Null(CinemaMediaResolver.UniqueTvdbTmdbId(403245, [mapped]));
    }

    [Fact]
    public void DisplayNameWithFractionSlashIsNotParsedAsAFilePath()
    {
        var parsed = CinemaMediaResolver.ParseName(
            "Ranma1/2 Official Trailer",
            input =>
            {
                Assert.Equal("Ranma1/2 Official Trailer", input);
                return new MediaBrowser.Controller.Providers.ItemLookupInfo
                {
                    Name = "Ranma1/2 Official",
                };
            },
            isPath: false);
        Assert.Equal("Ranma1/2", parsed?.Title);
    }

    [Theory]
    [InlineData("Silo 3883jsjsjd8dj", "Silo")]
    [InlineData("Silo Bv9wTsjqpSQ", "Silo")]
    [InlineData("Silo ABCdefghiJK", "Silo")]
    [InlineData("Silo abcDEfghij", "Silo")]
    [InlineData("Silo abcdefghijk", null)]
    [InlineData("Silo SpiderMan", null)]
    [InlineData("The LordOfTheRings", null)]
    [InlineData("Alien Resurrection", null)]
    public void GeneratedSuffixCandidateDoesNotRemoveOrdinaryTitleWords(string name, string? expected)
    {
        var method = typeof(CinemaMediaResolver).GetMethod("DelimitedNames",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        var alternatives = (IEnumerable<CinemaMediaResolver.MediaName>)method.Invoke(
            null, new object[] { new CinemaMediaResolver.MediaName(name, null) })!;
        Assert.Equal(expected, alternatives.SingleOrDefault()?.Title);
    }

    [Fact]
    public void YearlessMoviePrefersDecadeNewerTitleButKeepsKnownYearsStrict()
    {
        var matchMethod = typeof(CinemaMediaResolver).GetMethod(
            "Matches", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(matchMethod);
        var results = new MediaBrowser.Model.Providers.RemoteSearchResult[]
        {
            new() { Name = "Dune", ProductionYear = 1984, ProviderIds = new() { ["Tmdb"] = "42" } },
            new() { Name = "Dune", ProductionYear = 2021, ProviderIds = new() { ["Tmdb"] = "43" } },
        };
        int[] Matches(int? year) => ((IEnumerable<int>)matchMethod.Invoke(null,
            new object[] { new CinemaMediaResolver.MediaName("Dune", year), results })!).ToArray();

        Assert.Equal(new[] { 43 }, Matches(null));
        Assert.Equal(new[] { 42 }, Matches(1984)); // Known year always wins.
        results[0].ProductionYear = 2012;
        Assert.Equal(new[] { 42, 43 }, Matches(null)); // Nine years is ambiguous.
        results[0].ProductionYear = 2011;
        Assert.Equal(new[] { 43 }, Matches(null));
        results[0].ProductionYear = null;
        Assert.Equal(new[] { 42, 43 }, Matches(null));
        results[0].PremiereDate = new DateTime(2011, 1, 1);
        Assert.Equal(new[] { 43 }, Matches(null));
        Assert.Empty(Matches(2011)); // Known-year matching stays strict.
    }

    [Fact]
    public void SeasonAndEpisodeExtrasPointToTheirSeries()
    {
        var seriesId = Guid.NewGuid();
        var method = typeof(CinemaMediaResolver).GetMethod(
            "OwnerSeriesId", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        foreach (MediaBrowser.Controller.Entities.BaseItem owner in new MediaBrowser.Controller.Entities.BaseItem[]
        {
            new MediaBrowser.Controller.Entities.TV.Season { SeriesId = seriesId },
            new MediaBrowser.Controller.Entities.TV.Episode { SeriesId = seriesId },
        })
            Assert.Equal(seriesId, method.Invoke(null, new object[] { owner }));

        Assert.Equal(Guid.Empty, method.Invoke(
            null, new object[] { new MediaBrowser.Controller.Entities.Movies.Movie() }));
    }

    [Fact]
    public void EndpointRequiresAuthentication()
    {
        Assert.NotNull(typeof(ResolveCinemaMediaRequest).GetCustomAttribute<AuthenticatedAttribute>());
    }
}
