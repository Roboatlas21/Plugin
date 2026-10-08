using System.Reflection;
using Emby.Plugins.Moonfin.Api;
using Emby.Plugins.Moonfin.Services;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Providers;
using Xunit;

namespace Emby.Plugins.Moonfin.Tests;

public sealed class CinemaMediaResolverTests
{
    private static RemoteSearchResult Result(int? id, string title, int? year = null) => new()
    {
        Name = title,
        ProductionYear = year,
        ProviderIds = id.HasValue
            ? new Dictionary<string, string> { ["Tmdb"] = id.Value.ToString() }
            : new Dictionary<string, string>(),
    };

    [Theory]
    [InlineData("Dune.Part.Two.2024.Official.Trailer.mp4", "Dune Part Two", 2024)]
    [InlineData("Show Season 5 (2026) Trailer.mp4", "Show", null)]
    [InlineData("Show S05 Official Trailer.mp4", "Show", null)]
    [InlineData("1917 Trailer.mp4", "1917", null)]
    public void ParseNameFindsSeriesTitleWithoutSeasonHints(
        string input,
        string title,
        int? year)
    {
        var parsed = CinemaMediaResolver.ParseName(input);
        Assert.NotNull(parsed);
        Assert.Equal(title, parsed.Title);
        Assert.Equal(year, parsed.Year);
    }

    [Fact]
    public void MovieMatchingRequiresYearAndRejectsAmbiguity()
    {
        var named = new CinemaMediaResolver.MediaName("Dune Part Two", 2024);
        Assert.Equal(42, CinemaMediaResolver.Match(named, [Result(42, "Dune: Part Two", 2024)], "movie"));
        Assert.Null(CinemaMediaResolver.Match(
            new CinemaMediaResolver.MediaName("Batman", null),
            [Result(1, "Batman", 1966)],
            "movie"));
        Assert.Null(CinemaMediaResolver.Match(named, [Result(42, "Dune Part Two", 2024), Result(43, "Dune Part Two", 2024)], "movie"));
        Assert.Null(CinemaMediaResolver.Match(named, [Result(null, "Dune Part Two", 2024)], "movie"));
    }

    [Fact]
    public void SeriesMatchingUsesExactTitleWithOptionalYear()
    {
        var series = new CinemaMediaResolver.MediaName("Show", null);
        Assert.Equal(42, CinemaMediaResolver.Match(series, [Result(42, "Show", 2016)], "tv"));
        Assert.Null(CinemaMediaResolver.Match(series, [Result(42, "Other Show", 2016)], "tv"));
    }

    [Fact]
    public void EndpointRequiresAuthentication()
    {
        Assert.NotNull(typeof(ResolveCinemaMediaRequest).GetCustomAttribute<AuthenticatedAttribute>());
    }
}
