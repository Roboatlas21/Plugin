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
