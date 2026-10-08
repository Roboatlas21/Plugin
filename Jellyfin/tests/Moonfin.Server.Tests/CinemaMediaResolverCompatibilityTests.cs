using System.Reflection;
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
    [Theory]
    [InlineData("Dune.Part.Two.2024.Official.Trailer.mp4", "Dune Part Two", 2024)]
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
    public async Task YearlessMoviesNeverSearch()
    {
        var item = new Video { Id = Guid.NewGuid(), Path = "Batman Trailer.mp4" };
        var library = new FakeLibraryManager { ItemForUserHandler = (_, _) => item };
        var resolver = new CinemaMediaResolver(library, null!);
        Assert.Null((await resolver.ResolveMediaAsync(item.Id, Guid.NewGuid(), "movie", CancellationToken.None)).TmdbId);
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
    public async Task FilenameFallbackUsesOnlyTheNativeMovieSearchPipeline()
    {
        var intro = new Video { Id = Guid.NewGuid(), Path = "/intros/Dune.Part.Two.2024.Official.Trailer.mp4" };
        var library = new FakeLibraryManager { ItemForUserHandler = (_, _) => intro };
        var provider = DispatchProxy.Create<IProviderManager, SearchProvider>();
        ((SearchProvider)(object)provider).Search = query =>
        {
            Assert.Equal("Dune Part Two", query.SearchInfo.Name);
            Assert.Equal(2024, query.SearchInfo.Year);
            Assert.False(query.IncludeDisabledProviders);
            return [Result(693134), Result(693134)];
        };
        var resolver = new CinemaMediaResolver(library, provider);
        Assert.Equal(new CinemaMediaResolver.Resolution(693134, "movie"),
            await resolver.ResolveMediaAsync(intro.Id, Guid.NewGuid(), "movie", CancellationToken.None));
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
