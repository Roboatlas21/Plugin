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
    [InlineData("Dune dhjdiii3jeb 2023.mp4", "Dune dhjdiii3jeb", 2023, "Dune", 2023)]
    [InlineData("Dune Official Trailer (2024).mp4", "Dune Official", 2024, "Dune", 2024)]
    [InlineData("Dune Trailer 2 (2024).mp4", "Dune Trailer 2", 2024, "Dune", 2024)]
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

    [Fact]
    public void EndpointRequiresAuthentication()
    {
        Assert.NotNull(typeof(ResolveCinemaMediaRequest).GetCustomAttribute<AuthenticatedAttribute>());
    }
}
