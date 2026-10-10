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

    [Theory]
    [InlineData("194469")]
    [InlineData("ec92a723-68c6-4a55-b035-a716eb9a60aa")]
    public void EndpointAcceptsEmbyItemIds(string itemId)
    {
        Assert.Equal(itemId, new ResolveCinemaMediaRequest { ItemId = itemId }.ItemId);
    }

    [Fact]
    public void SharedTvdbMappingAcceptsEmbyProviderIds()
    {
        var result = new RemoteSearchResult
        {
            ProviderIds = new() { ["Tvdb"] = "403245", ["Tmdb"] = "42" },
        };
        Assert.Equal(42, CinemaMediaResolver.UniqueTvdbTmdbId(403245, [result]));
    }

    [Fact]
    public void EndpointRequiresAuthentication()
    {
        Assert.NotNull(typeof(ResolveCinemaMediaRequest).GetCustomAttribute<AuthenticatedAttribute>());
    }
}
