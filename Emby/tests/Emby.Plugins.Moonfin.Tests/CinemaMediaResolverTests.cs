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
    [InlineData("Dune2021 Trailer.mp4", "Dune2021", null, "Dune", 2021)]
    [InlineData("Dune2021 Official Trailer 1080p.mp4", "Dune2021 Official", null, "Dune", 2021)]
    [InlineData("The Batman2022 Trailer.mp4", "The Batman2022", null, "The Batman", 2022)]
    [InlineData("Amélie2001 Trailer.mp4", "Amélie2001", null, "Amélie", 2001)]
    [InlineData("1917 Trailer.mp4", "1917", null, "1917", null)]
    [InlineData("Blade Runner2049 Trailer.mp4", "Blade Runner2049", null, "Blade Runner2049", null)]
    [InlineData("19172019 Trailer.mp4", "19172019", null, "19172019", null)]
    [InlineData("The New 2024 Trailer.mp4", "The New", 2024, "The New", 2024)]
    [InlineData("The Coming Soon 2024 Trailer.mp4", "The Coming Soon", 2024, "The Coming Soon", 2024)]
    [InlineData("Dune 2021 Trailer Now Streaming.mp4", "Dune", 2021, "Dune", 2021)]
    [InlineData("Dune 2021 Trailer HDR10.mp4", "Dune", 2021, "Dune", 2021)]
    [InlineData("Dune watch at home 2021 trailer djdjdj.mp4", "Dune watch at home", 2021, "Dune", 2021)]
    [InlineData("The Batman Watch Now 2022 Teaser Trailer W3FFSjR.mp4", "The Batman Watch Now", 2022, "The Batman", 2022)]
    [InlineData("Dune official trailer 2021 jdjdjsn.mp4", "Dune official", 2021, "Dune", 2021)]
    [InlineData("Dune dhjdiii3jeb 2023.mp4", "Dune dhjdiii3jeb", 2023, "Dune dhjdiii3jeb", 2023)]
    [InlineData("Dune Official Trailer (2024).mp4", "Dune Official", 2024, "Dune", 2024)]
    [InlineData("Dune Trailer 2 (2024).mp4", "Dune Trailer 2", 2024, "Dune", 2024)]
    [InlineData("Silo Trailer ABCdefghiJK.mp4", "Silo Trailer ABCdefghiJK", null, "Silo", null)]
    [InlineData("Silo Teaser Trailer short.mp4", "Silo Teaser Trailer short", null, "Silo", null)]
    [InlineData("Silo Season 5 Official Trailer abcdefghijk.mp4", "Silo Season 5 Official Trailer abcdefghijk", null, "Silo", null)]
    [InlineData("Silo.Trailer.Now.Streaming.mp4", "Silo.Trailer.Now.Streaming", null, "Silo", null)]
    [InlineData("Silo_Teaser_Trailer_more.mp4", "Silo_Teaser_Trailer_more", null, "Silo", null)]
    [InlineData("Trailer Park Boys Official Trailer abcdefghijk.mp4", "Trailer Park Boys Official Trailer abcdefghijk", null, "Trailer Park Boys", null)]
    [InlineData("The Godfather2 1974 Trailer.mp4", "The Godfather2", 1974, "The Godfather2", 1974)]
    [InlineData("The Terminator3 2003 Trailer.mp4", "The Terminator3", 2003, "The Terminator3", 2003)]
    [InlineData("Silo Season 5 Trailer.mp4", "Silo Season 5", null, "Silo", null)]
    [InlineData("Silo S05 Official Trailer (2026).mp4", "Silo S05 Official", 2026, "Silo", null)]
    [InlineData("Show Season 5 Trailer Now Streaming.mp4", "Show Season 5 Trailer Now Streaming", null, "Show", null)]
    [InlineData("Show S05 Official Trailer.mp4", "Show S05 Official", null, "Show", null)]
    [InlineData("Dune Part Two.2024.2160p.BluRay.mkv", "Dune.Part.Two", 2024, "Dune Part Two", 2024)]
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
    [InlineData("Dune_tmdb438631_trailer.mp4", "Dune", "movie", 438631, null)]
    [InlineData("Silo_tvdb403245_trailer.mp4", "Silo", "tv", null, 403245)]
    [InlineData("The_Last_of_Us_tvdb116602_trailer.webm", "The Last of Us", "tv", null, 116602)]
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
    [InlineData("Silo_trailer.mp4")]
    [InlineData("Dune_2021_trailer.mp4")]
    [InlineData("Dune_438631_trailer.mp4.part")]
    [InlineData("Silo_tvdb0_trailer.mp4")]
    [InlineData("Dune_999999999999999999999_trailer.mp4")]
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
        mapped.ProviderIds["Tvdb"] = "999";
        Assert.Null(CinemaMediaResolver.UniqueTvdbTmdbId(403245, [mapped]));
    }

    [Fact]
    public void TvdbLookupRejectsAmbiguousOrMissingTmdbIds()
    {
        var first = new MediaBrowser.Model.Providers.RemoteSearchResult
        {
            ProviderIds = new() { ["Tmdb"] = "42", ["Tvdb"] = "403245" },
        };
        var other = new MediaBrowser.Model.Providers.RemoteSearchResult
        {
            ProviderIds = new() { ["Tmdb"] = "43", ["Tvdb"] = "403245" },
        };
        Assert.Null(CinemaMediaResolver.UniqueTvdbTmdbId(403245, [first, other]));
        Assert.Null(CinemaMediaResolver.UniqueTvdbTmdbId(403245, []));
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
    [InlineData("Silo_3883jsjsjd8dj", "Silo")]
    [InlineData("Silo Bv9wTsjqpSQ", "Silo")]
    [InlineData("Silo ABCdefghiJK", "Silo")]
    [InlineData("Silo ABcdefghiJK", "Silo")]
    [InlineData("Silo abcDEfghij", "Silo")]
    [InlineData("The Godfather2", null)]
    [InlineData("Blade Runner 2049", null)]
    [InlineData("Silo abcdefghi", null)]
    [InlineData("Silo abcdefghijk", null)]
    [InlineData("Silo ABCDEFGHIJK", null)]
    [InlineData("Silo SpiderMan", null)]
    [InlineData("The LordOfTheRings", null)]
    [InlineData("Alien Resurrection", null)]
    [InlineData("The Matrix Resurrections", null)]
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
    public void YearlessMovieMatchingStillRejectsAmbiguousTmdbIds()
    {
        var matchMethod = typeof(CinemaMediaResolver).GetMethod(
            "Matches", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(matchMethod);
        var results = new MediaBrowser.Model.Providers.RemoteSearchResult[]
        {
            new() { Name = "Dune", ProductionYear = 1984, ProviderIds = new() { ["Tmdb"] = "42" } },
            new() { Name = "Dune", ProductionYear = 2021, ProviderIds = new() { ["Tmdb"] = "43" } },
            new() { Name = "Other Movie", ProductionYear = 2021, ProviderIds = new() { ["Tmdb"] = "90" } },
        };

        var yearless = (IEnumerable<int>)matchMethod.Invoke(null,
            new object[] { new CinemaMediaResolver.MediaName("Dune", null), results })!;
        Assert.Equal(new[] { 42, 43 }, yearless);

        var withYear = (IEnumerable<int>)matchMethod.Invoke(null,
            new object[] { new CinemaMediaResolver.MediaName("Dune", 2021), results })!;
        Assert.Equal(new[] { 43 }, withYear);
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
