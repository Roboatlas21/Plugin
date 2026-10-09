using System.Reflection;
using System.Text.Json;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
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
    [InlineData("Dune 2021 1080p [abc_defghij].mp4", "Dune", 2021)]
    [InlineData("Dune 2021 Official Trailer 1080p [abcdefghijk].mp4", "Dune", 2021)]
    [InlineData("Show Season 5 Trailer 1080p [abcdefghijk].mp4", "Show", null)]
    [InlineData("Dune 2021 [abcdefghijk] 1080p.mp4", "Dune", 2021)]
    [InlineData("Dune 2021 [abc_defghij].mp4", "Dune", 2021)]
    [InlineData("Dune 2021 Trailer jdjdjsn.mp4", "Dune", 2021)]
    [InlineData("Dune official trailer 2021 jdjdjsn.mp4", "Dune", 2021)]
    [InlineData("Dune_2021_Trailer_jdjdjsn.mp4", "Dune", 2021)]
    [InlineData("Dune 2021 abcd_efghij.mp4", "Dune", 2021)]
    [InlineData("Dune 2021 [abc_defghij] Trailer.mp4", "Dune", 2021)]
    [InlineData("Dune 2021 jdidisisj.mp4", "Dune", 2021)]
    [InlineData("Dune (2021) [abcdefghijk].mp4", "Dune", 2021)]
    [InlineData("Dune 2021 Trailer [abcdefghijk].mp4", "Dune", 2021)]
    [InlineData("Dune dhjdiii3jeb 2023.mp4", "Dune", 2023)]
    [InlineData("Dune Part Two 2024 jdjdjsn.mp4", "Dune Part Two", 2024)]
    [InlineData("Pride and Prejudice 2005.mp4", "Pride and Prejudice", 2005)]
    [InlineData("Dune Official Trailer (2024).mp4", "Dune", 2024)]
    [InlineData("Dune Trailer 2 (2024).mp4", "Dune", 2024)]
    [InlineData("Show Season 5 Trailer (2026).mp4", "Show", null)]
    [InlineData("C:\\Intros\\The Batman 2022 Trailer.mp4", "The Batman", 2022)]
    [InlineData("/intros/Superman (2025).mp4", "Superman", 2025)]
    [InlineData("1917 Trailer.mp4", "1917", null)]
    [InlineData("1917 (2019) Official Trailer 2 1080p.mp4", "1917", 2019)]
    [InlineData("2001 A Space Odyssey Trailer 2160p.mp4", "2001 A Space Odyssey", null)]
    public void ExtractsOnlyTrailingDecorations(string name, string title, int? year)
    {
        var parsed = CinemaMediaResolver.ParseName(name);
        Assert.NotNull(parsed);
        Assert.Equal(title, parsed.Title);
        Assert.Equal(year, parsed.Year);
    }

    private static RemoteSearchResult Result(int id, string title = "Dune: Part Two", int? year = 2024) => new()
    {
        Name = title, ProductionYear = year,
        ProviderIds = new Dictionary<string, string> { ["Tmdb"] = id.ToString() },
    };

    [Fact]
    public async Task ExactTitleAndYearRequiredAndProvidersDeduplicated()
    {
        var item = new Video { Id = Guid.NewGuid(), Path = "/intros/Dune.Part.Two.2024.Official.Trailer.mp4" };
        var library = new FakeLibraryManager { ItemForUserHandler = (_, _) => item };
        var provider = DispatchProxy.Create<IProviderManager, SearchProvider>();
        var resolver = new CinemaMediaResolver(library, provider);
        var candidates = new (RemoteSearchResult[] Results, int? Expected)[]
        {
            ([Result(693134), Result(693134)], 693134),
            ([Result(1, year: 2021)], null),
            ([Result(1, title: "Dune")], null),
            ([Result(1), Result(2)], null),
            ([Result(0)], null),
            ([], null),
        };

        foreach (var (results, expected) in candidates)
        {
            ((SearchProvider)(object)provider).Search = _ => results;
            var resolved = await resolver.ResolveMediaAsync(item.Id, Guid.NewGuid(), "movie", CancellationToken.None);
            Assert.Equal(expected, resolved.TmdbId);
        }
    }

    [Fact]
    public async Task StandaloneIntroUsesProductionYearWhenItsNameHasNoYear()
    {
        var intro = new Video
        {
            Id = Guid.NewGuid(),
            Path = "/trailers/" + new string('a', 64) + ".mp4",
            Name = "Dune",
            ProductionYear = 2021,
        };
        var library = new FakeLibraryManager { ItemForUserHandler = (_, _) => intro };
        var provider = DispatchProxy.Create<IProviderManager, SearchProvider>();
        var searches = 0;
        ((SearchProvider)(object)provider).Search = query =>
        {
            searches++;
            Assert.Equal("Dune", query.SearchInfo.Name);
            Assert.Equal(2021, query.SearchInfo.Year);
            return [Result(42, "Dune", 2021)];
        };
        var resolver = new CinemaMediaResolver(library, provider);
        Assert.Equal(new CinemaMediaResolver.Resolution(42, "movie"),
            await resolver.ResolveMediaAsync(intro.Id, Guid.NewGuid(), "movie", default));
        Assert.Equal(1, searches);

        intro.ProductionYear = null;
        Assert.Null((await resolver.ResolveMediaAsync(intro.Id, Guid.NewGuid(), "movie", default)).TmdbId);
        Assert.Equal(1, searches);
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
    public async Task GenericVideoOwnerMustBeAccessible()
    {
        var user = Guid.NewGuid();
        var movie = new Movie { Id = Guid.NewGuid(), ProviderIds = new() { ["Tmdb"] = "42" } };
        var video = new Video
        {
            Id = Guid.NewGuid(),
            OwnerId = movie.Id,
            ProviderIds = new() { ["Tmdb"] = "42", ["TmdbMediaType"] = "movie" },
        };
        Assert.NotEqual(ExtraType.Trailer, video.ExtraType);
        var library = new FakeLibraryManager
        {
            ItemForUserHandler = (id, _) => id == video.Id ? video : null,
        };
        var resolver = new CinemaMediaResolver(library, null!);
        Assert.Null((await resolver.ResolveMediaAsync(video.Id, user, "movie", CancellationToken.None)).TmdbId);

        library.ItemForUserHandler = (id, _) => id == video.Id ? video : id == movie.Id ? movie : null;
        Assert.Equal(new CinemaMediaResolver.Resolution(42, "movie"),
            await resolver.ResolveMediaAsync(video.Id, user, "movie", CancellationToken.None));
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

    public class SearchProvider : DispatchProxy
    {
        public Func<RemoteSearchQuery<MovieInfo>, IEnumerable<RemoteSearchResult>> Search { get; set; } = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            Assert.Equal("GetRemoteSearchResults", method!.Name);
            Assert.Equal(new[] { typeof(Movie), typeof(MovieInfo) }, method.GetGenericArguments());
            return Task.FromResult(Search((RemoteSearchQuery<MovieInfo>)args![0]!));
        }
    }
}
