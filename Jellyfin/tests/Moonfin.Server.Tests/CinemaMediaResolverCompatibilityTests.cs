using System.Reflection;
using System.Text.Json;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Authorization;
using Moonfin.Server.Api;
using Moonfin.Server.Services;
using Xunit;

namespace Moonfin.Server.Tests;

public sealed class CinemaMediaResolverCompatibilityTests
{
    [Fact]
    public void ResolutionJsonMatchesMoonfinClientContract()
    {
        var json = JsonSerializer.Serialize(new CinemaMediaResolver.Resolution(42, "tv"));
        Assert.Equal("{\"tmdbId\":42,\"mediaType\":\"tv\"}", json);
    }

    [Theory]
    [InlineData("Dune.Part.Two.2024.Official.Trailer.mp4", "Dune Part Two", 2024)]
    [InlineData("Dune2021 Trailer.mp4", "Dune", 2021)]
    [InlineData("Show Season 5 Trailer Now Streaming.mp4", "Show", null)]
    [InlineData("The New 2024 Trailer.mp4", "The New", 2024)]
    [InlineData("Oppenheimer (2023) On Digital Teaser Trailer 1080p [QB176lyq-GH].webm", "Oppenheimer", 2023)]
    [InlineData("Dune 2021 Official Trailer 1080p [abcdefghijk].mp4", "Dune", 2021)]
    [InlineData("C:\\Intros\\The Batman 2022 Trailer.mp4", "The Batman", 2022)]
    [InlineData("Trailer Park Boys Official Trailer abcdefghijk.mp4", "Trailer Park Boys", null)]
    [InlineData("The Godfather2 1974 Trailer.mp4", "The Godfather2", 1974)]
    [InlineData("1917 Trailer.mp4", "1917", null)]
    public void ExtractsOnlyTrailingDecorations(string name, string title, int? year)
    {
        var library = (ILibraryManager)new FakeLibraryManager();
        var parsed = CinemaMediaResolver.ParseName(name, library.ParseName);
        Assert.NotNull(parsed);
        Assert.Equal(title, parsed.Title);
        Assert.Equal(year, parsed.Year);
    }

    [Fact]
    public async Task DirectAndOwnerResolutionUseTheAuthenticatedUserAndNeverParentId()
    {
        var user = Guid.NewGuid();
        var movie = new Movie { Id = Guid.NewGuid(), ProviderIds = new() { ["Tmdb"] = "693134" } };
        var intro = new Video { Id = Guid.NewGuid(), ExtraType = ExtraType.Trailer, OwnerId = movie.Id };
        var items = new Dictionary<Guid, BaseItem> { [intro.Id] = intro, [movie.Id] = movie };
        var library = new FakeLibraryManager
        {
            ItemForUserHandler = (id, uid) => { Assert.Equal(user, uid); return items.GetValueOrDefault(id); },
        };
        // No provider should be consulted by either authoritative path.
        var resolver = new CinemaMediaResolver(library, null!);
        Assert.Equal(new CinemaMediaResolver.Resolution(693134, "movie"),
            await resolver.ResolveMediaAsync(intro.Id, user, "movie", CancellationToken.None));
        intro.ProviderIds["tmdb"] = "42";
        Assert.Null((await resolver.ResolveMediaAsync(intro.Id, user, "movie", CancellationToken.None)).TmdbId); // conflicting owner
        intro.ProviderIds.Clear();
        items.Remove(movie.Id); // Owner absent or filtered by user access.
        Assert.Null((await resolver.ResolveMediaAsync(intro.Id, user, "movie", CancellationToken.None)).TmdbId);
    }

    [Fact]
    public async Task PrivateVideosCanResolveButEpisodesAndInaccessibleItemsCannot()
    {
        var item = new Video { Id = Guid.NewGuid(), ProviderIds = new() { ["Tmdb"] = "42", ["TmdbMediaType"] = "movie" } };
        var library = new FakeLibraryManager { ItemForUserHandler = (_, _) => item };
        var resolver = new CinemaMediaResolver(library, null!);
        Assert.Equal(42, (await resolver.ResolveMediaAsync(item.Id, Guid.NewGuid(), "movie", CancellationToken.None)).TmdbId);
        library.ItemForUserHandler = (_, _) => new Episode { ProviderIds = new() { ["Tmdb"] = "42" } };
        Assert.Null((await resolver.ResolveMediaAsync(item.Id, Guid.NewGuid(), "movie", CancellationToken.None)).TmdbId);
        library.ItemForUserHandler = (_, _) => null;
        Assert.Null((await resolver.ResolveMediaAsync(item.Id, Guid.NewGuid(), "movie", CancellationToken.None)).TmdbId);
        Assert.NotNull(typeof(CinemaController).GetCustomAttribute<AuthorizeAttribute>());
    }

    [Fact]
    public async Task EndpointRejectsRequestsWithoutAUserClaim()
    {
        var controller = new CinemaController(null!, Microsoft.Extensions.Logging.Abstractions.NullLogger<CinemaController>.Instance)
        {
            ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext
            {
                HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext(),
            },
        };
        Assert.IsType<Microsoft.AspNetCore.Mvc.UnauthorizedResult>(
            (await controller.ResolveMedia(Guid.NewGuid(), "tv", CancellationToken.None)).Result);
    }
}
