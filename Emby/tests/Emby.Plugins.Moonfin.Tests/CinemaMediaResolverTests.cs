using System.Reflection;
using Emby.Plugins.Moonfin.Api;
using Emby.Plugins.Moonfin.Services;
using MediaBrowser.Controller.Net;
using Xunit;

namespace Emby.Plugins.Moonfin.Tests;

public sealed class CinemaMediaResolverTests
{
    [Theory]
    [InlineData("Dune.Part.Two.2024.Official.Trailer.mp4", "Dune Part Two", 2024)]
    [InlineData("Dune 2021 [abc_defghij].mp4", "Dune", 2021)]
    [InlineData("Dune 2021 Trailer jdjdjsn.mp4", "Dune", 2021)]
    [InlineData("Dune official trailer 2021 jdjdjsn.mp4", "Dune", 2021)]
    [InlineData("Dune_2021_Trailer_jdjdjsn.mp4", "Dune", 2021)]
    [InlineData("Dune 2021 abcd_efghij.mp4", "Dune", 2021)]
    [InlineData("Dune 2021 [abc_defghij] Trailer.mp4", "Dune", 2021)]
    [InlineData("Dune 2021 jdidisisj.mp4", "Dune", 2021)]
    [InlineData("Dune official trailer 2021 jdjdjsn.mp4", "Dune", 2021)]
    [InlineData("Dune (2021) [abcdefghijk].mp4", "Dune", 2021)]
    [InlineData("Dune 2021 Trailer [abcdefghijk].mp4", "Dune", 2021)]
    [InlineData("Dune dhjdiii3jeb 2023.mp4", "Dune", 2023)]
    [InlineData("Dune Part Two 2024 jdjdjsn.mp4", "Dune Part Two", 2024)]
    [InlineData("Pride and Prejudice 2005.mp4", "Pride and Prejudice", 2005)]
    [InlineData("Dune Official Trailer (2024).mp4", "Dune", 2024)]
    [InlineData("Show Season 5 Trailer (2026).mp4", "Show", null)]
    [InlineData("Show Season 5 (2026) Trailer.mp4", "Show", null)]
    [InlineData("Show (2026) Season 5 Trailer.mp4", "Show", null)]
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
    public void EndpointRequiresAuthentication()
    {
        Assert.NotNull(typeof(ResolveCinemaMediaRequest).GetCustomAttribute<AuthenticatedAttribute>());
    }
}
