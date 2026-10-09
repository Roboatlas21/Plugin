using System.Reflection;
using Emby.Plugins.Moonfin.Api;
using Emby.Plugins.Moonfin.Services;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Providers;
using Xunit;

namespace Emby.Plugins.Moonfin.Tests;

public sealed class CinemaMediaResolverTests
{
    [Theory]
    [InlineData("Dune_438631_trailer.mp4", "movie", 438631)]
    [InlineData("Silo_tvdb403245_trailer.mp4", "tv", 403245)]
    public void FilenameIdsPreserveProviderType(string filename, string mediaType, int id)
    {
        var parsed = CinemaMediaResolver.ParseTrailerFilenameIdentity(filename);
        Assert.NotNull(parsed);
        Assert.Equal(mediaType, parsed.Value.MediaType);
        Assert.Equal(id, parsed.Value.TmdbId ?? parsed.Value.TvdbId);
    }

    [Fact]
    public void PremiereDateSupportsYearlessAndKnownYearMatches()
    {
        var method = typeof(CinemaMediaResolver).GetMethod("Matches", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        var first = new RemoteSearchResult
        {
            Name = "Show", PremiereDate = new DateTime(1989, 1, 1),
            ProviderIds = new() { ["Tmdb"] = "42" },
        };
        var second = new RemoteSearchResult
        {
            Name = "Show", PremiereDate = new DateTime(2024, 1, 1),
            ProviderIds = new() { ["Tmdb"] = "43" },
        };
        var results = new[] { first, second };
        int[] Matches(int? year) => ((IEnumerable<int>)method.Invoke(null,
            new object[] { new CinemaMediaResolver.MediaName("Show", year), results })!).ToArray();
        Assert.Equal(new[] { 43 }, Matches(null));
        Assert.Equal(new[] { 42 }, Matches(1989));
        Assert.Empty(Matches(1990));
    }

    [Fact]
    public void EndpointRequiresAuthentication()
    {
        Assert.NotNull(typeof(ResolveCinemaMediaRequest).GetCustomAttribute<AuthenticatedAttribute>());
    }
}
