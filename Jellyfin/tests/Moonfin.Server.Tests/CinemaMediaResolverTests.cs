using System.Reflection;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Moonfin.Server.Services;
using Xunit;

namespace Moonfin.Server.Tests;

public sealed class CinemaMediaResolverTests
{
    private static RemoteSearchResult Result(int id, string title, int? year = null) => new()
    {
        Name = title, ProductionYear = year,
        ProviderIds = new() { ["Tmdb"] = id.ToString() },
    };

    private static CinemaMediaResolver Resolver(
        Video intro,
        Func<string, string, int?, IEnumerable<RemoteSearchResult>> search,
        Func<RemoteSearchQuery<SeriesInfo>, IEnumerable<RemoteSearchResult>>? tvdbSearch = null)
    {
        var library = new FakeLibraryManager { ItemForUserHandler = (_, _) => intro };
        var provider = DispatchProxy.Create<IProviderManager, SearchProvider>();
        ((SearchProvider)(object)provider).Search = search;
        ((SearchProvider)(object)provider).TvdbSearch = tvdbSearch;
        return new CinemaMediaResolver(library, provider);
    }

    [Theory]
    [InlineData("movie", "tv")]
    [InlineData("tv", "movie")]
    public async Task ExplicitTypeOverridesFeatureContext(string type, string context)
    {
        var intro = new Video { ProviderIds = new() { ["Tmdb"] = "42", ["TmdbMediaType"] = type } };
        var resolver = Resolver(intro, (_, _, _) => throw new Exception("No search expected"));
        var result = await resolver.ResolveMediaAsync(Guid.NewGuid(), Guid.NewGuid(), context, default);
        Assert.Equal(42, result.TmdbId);
        Assert.Equal(type, result.MediaType);
    }

    [Theory]
    [InlineData("movie", "tv")]
    [InlineData("tv", "movie")]
    [InlineData("movie-item", "tv")]
    public async Task KnownTrailerTypeTakesPriorityForFilenameSearch(string source, string context)
    {
        var mediaType = source == "tv" ? "tv" : "movie";
        var title = mediaType == "movie" ? "Dune" : "Show";
        int? year = mediaType == "movie" ? 2021 : null;
        Video intro = source == "movie-item"
            ? new Movie { Path = "Dune 2021 Trailer.mp4" }
            : new Video
            {
                Path = mediaType == "movie" ? "Dune 2021 Trailer.mp4" : "Show Trailer.mp4",
                ProviderIds = new() { ["TmdbMediaType"] = mediaType },
            };
        var searches = 0;
        var resolver = Resolver(intro, (kind, name, candidateYear) =>
        {
            searches++;
            Assert.Equal(mediaType, kind);
            Assert.Equal(title, name);
            Assert.Equal(year, candidateYear);
            return [Result(42, title, year ?? 2016)];
        });
        Assert.Equal(new CinemaMediaResolver.Resolution(42, mediaType),
            await resolver.ResolveMediaAsync(Guid.NewGuid(), Guid.NewGuid(), context, default));
        Assert.Equal(1, searches);
    }

    [Fact]
    public async Task UntypedIdsNeedMatchingSeriesFilenameAndConflictingTypesRemainInvalid()
    {
        var intro = new Video { Path = "Show Trailer.mp4", ProviderIds = new() { ["Tmdb"] = "42" } };
        var resolver = Resolver(intro, (type, name, year) =>
        {
            Assert.Equal("tv", type);
            Assert.Equal("Show", name);
            Assert.Null(year);
            return [Result(42, name)];
        });
        Assert.Null((await resolver.ResolveMediaAsync(Guid.NewGuid(), Guid.NewGuid(), null, default)).TmdbId);
        Assert.Null((await resolver.ResolveMediaAsync(Guid.NewGuid(), Guid.NewGuid(), "movie", default)).TmdbId);
        Assert.Equal(new CinemaMediaResolver.Resolution(42, "tv"),
            await resolver.ResolveMediaAsync(Guid.NewGuid(), Guid.NewGuid(), "tv", default));
        intro.ProviderIds["Tmdb"] = "43";
        Assert.Null((await resolver.ResolveMediaAsync(Guid.NewGuid(), Guid.NewGuid(), "tv", default)).TmdbId);

        intro = new Movie { ProviderIds = new() { ["Tmdb"] = "42", ["TmdbMediaType"] = "tv" } };
        resolver = Resolver(intro, (_, _, _) => throw new Exception("No search expected"));
        Assert.Null((await resolver.ResolveMediaAsync(Guid.NewGuid(), Guid.NewGuid(), "movie", default)).TmdbId);
    }

    [Fact]
    public async Task AccessibleSeriesOwnerIsAuthoritative()
    {
        var user = Guid.NewGuid();
        var series = new Series { Id = Guid.NewGuid(), ProviderIds = new() { ["Tmdb"] = "42" } };
        var intro = new Video { Id = Guid.NewGuid(), ExtraType = ExtraType.Trailer, OwnerId = series.Id };
        var library = new FakeLibraryManager { ItemForUserHandler = (id, uid) =>
        {
            Assert.Equal(user, uid);
            return id == intro.Id ? intro : id == series.Id ? series : null;
        }};
        var resolver = new CinemaMediaResolver(library, null!);
        Assert.Equal(new CinemaMediaResolver.Resolution(42, "tv"),
            await resolver.ResolveMediaAsync(intro.Id, user, "movie", default));

        library.ItemForUserHandler = (id, _) => id == intro.Id ? intro : null;
        Assert.Null((await resolver.ResolveMediaAsync(intro.Id, user, "tv", default)).TmdbId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SeasonAndEpisodeOwnedTrailersUseAccessibleSeriesIdentity(bool episodeOwner)
    {
        var user = Guid.NewGuid();
        var series = new Series
        {
            Id = Guid.NewGuid(), Name = "Silo", ProviderIds = new() { ["Tmdb"] = "42" },
        };
        BaseItem owner = episodeOwner
            ? new Episode { Id = Guid.NewGuid(), SeriesId = series.Id, Name = "Pilot",
                ProviderIds = new() { ["Tmdb"] = "999" } }
            : new Season { Id = Guid.NewGuid(), SeriesId = series.Id, Name = "Season 1",
                ProviderIds = new() { ["Tmdb"] = "999" } };
        var trailer = new Video
        {
            Id = Guid.NewGuid(), OwnerId = owner.Id, Path = "Wrong Show Trailer.mp4",
        };
        var items = new Dictionary<Guid, BaseItem>
        {
            [trailer.Id] = trailer, [owner.Id] = owner, [series.Id] = series,
        };
        var library = new FakeLibraryManager
        {
            ItemForUserHandler = (id, uid) =>
            {
                Assert.Equal(user, uid);
                return items.GetValueOrDefault(id);
            },
        };
        var resolver = new CinemaMediaResolver(library, null!);

        Assert.Equal(new CinemaMediaResolver.Resolution(42, "tv"),
            await resolver.ResolveMediaAsync(trailer.Id, user, "movie", default));

        items.Remove(series.Id);
        Assert.Null((await resolver.ResolveMediaAsync(trailer.Id, user, "tv", default)).TmdbId);

        items[series.Id] = series;
        items.Remove(owner.Id);
        Assert.Null((await resolver.ResolveMediaAsync(trailer.Id, user, "tv", default)).TmdbId);
    }

    [Fact]
    public async Task EpisodeOwnedTrailerUsesSeriesNameForRemoteSearch()
    {
        var user = Guid.NewGuid();
        var series = new Series { Id = Guid.NewGuid(), Name = "Silo" };
        var episode = new Episode { Id = Guid.NewGuid(), Name = "Pilot", SeriesId = series.Id };
        var trailer = new Video
        {
            Id = Guid.NewGuid(), OwnerId = episode.Id, Path = "Other Movie Trailer.mp4",
        };
        var library = new FakeLibraryManager
        {
            ItemForUserHandler = (id, uid) =>
            {
                Assert.Equal(user, uid);
                return id == trailer.Id ? trailer : id == episode.Id ? episode : id == series.Id ? series : null;
            },
        };
        var provider = DispatchProxy.Create<IProviderManager, SearchProvider>();
        ((SearchProvider)(object)provider).Search = (type, title, year) =>
        {
            Assert.Equal("tv", type);
            Assert.Equal("Silo", title);
            Assert.Null(year);
            return [Result(42, "Silo", 2023)];
        };
        var resolver = new CinemaMediaResolver(library, provider);

        Assert.Equal(new CinemaMediaResolver.Resolution(42, "tv"),
            await resolver.ResolveMediaAsync(trailer.Id, user, "movie", default));
    }

    [Theory]
    [InlineData("Show Season 5 (2026) Trailer.mp4", "Show")]
    [InlineData("Show (2026) Season 5 Trailer.mp4", "Show")]
    [InlineData("Show S05 Official Trailer.mp4", "Show")]
    [InlineData("Show Trailer.mp4", "Show")]
    public async Task SeriesSearchUsesExactTitleWithoutSeasonHints(string filename, string title)
    {
        var intro = new Video { Path = filename };
        var resolver = Resolver(intro, (type, name, year) =>
        {
            Assert.Equal("tv", type);
            Assert.Equal(title, name);
            Assert.Null(year);
            return [Result(42, title, 2016)];
        });
        Assert.Equal(new CinemaMediaResolver.Resolution(42, "tv"),
            await resolver.ResolveMediaAsync(Guid.NewGuid(), Guid.NewGuid(), "tv", default));
    }


    [Fact]
    public async Task SeriesOwnerTypesUntypedTrailerIdWithoutRemoteSearch()
    {
        var user = Guid.NewGuid();
        var series = new Series { Id = Guid.NewGuid(), Name = "Show" };
        var intro = new Video
        {
            Id = Guid.NewGuid(),
            OwnerId = series.Id,
            Path = "Wrong Show Trailer.mp4",
            ProviderIds = new() { ["Tmdb"] = "42" },
        };
        var library = new FakeLibraryManager
        {
            ItemForUserHandler = (id, uid) =>
            {
                Assert.Equal(user, uid);
                return id == intro.Id ? intro : id == series.Id ? series : null;
            },
        };
        // The owner's Series type is sufficient; a remote lookup is never necessary.
        var resolver = new CinemaMediaResolver(library, null!);

        Assert.Equal(new CinemaMediaResolver.Resolution(42, "tv"),
            await resolver.ResolveMediaAsync(intro.Id, user, "movie", default));

        intro.ProviderIds["Tmdb"] = "43";
        Assert.Equal(new CinemaMediaResolver.Resolution(43, "tv"),
            await resolver.ResolveMediaAsync(intro.Id, user, "tv", default));

        series.ProviderIds = new() { ["Tmdb"] = "42" };
        Assert.Null((await resolver.ResolveMediaAsync(intro.Id, user, "tv", default)).TmdbId);

        series.ProviderIds["Tmdb"] = "43";
        Assert.Equal(new CinemaMediaResolver.Resolution(43, "tv"),
            await resolver.ResolveMediaAsync(intro.Id, user, "tv", default));

        intro.ProviderIds["TmdbMediaType"] = "movie";
        Assert.Null((await resolver.ResolveMediaAsync(intro.Id, user, "tv", default)).TmdbId);
    }


    [Fact]
    public async Task OwnedMovieSearchUsesOwnerTitleAndYearNotTrailerFilename()
    {
        var user = Guid.NewGuid();
        var movie = new Movie
        {
            Id = Guid.NewGuid(), Name = "The Batman", ProductionYear = 2022,
        };
        var intro = new Video
        {
            Id = Guid.NewGuid(),
            OwnerId = movie.Id,
            Path = "/trailers/Other.Movie.2025.Trailer.mp4",
            Name = "Other Movie Trailer",
        };
        var library = new FakeLibraryManager
        {
            ItemForUserHandler = (id, uid) =>
            {
                Assert.Equal(user, uid);
                return id == intro.Id ? intro : id == movie.Id ? movie : null;
            },
        };
        var provider = DispatchProxy.Create<IProviderManager, SearchProvider>();
        var searches = 0;
        ((SearchProvider)(object)provider).Search = (kind, title, year) =>
        {
            searches++;
            Assert.Equal("movie", kind);
            Assert.Equal("The Batman", title);
            Assert.Equal(2022, year);
            return [Result(42, "The Batman", 2022)];
        };
        var resolver = new CinemaMediaResolver(library, provider);

        Assert.Equal(new CinemaMediaResolver.Resolution(42, "movie"),
            await resolver.ResolveMediaAsync(intro.Id, user, "tv", default));
        Assert.Equal(1, searches);

        // A known owner with no year must not fall back to a misleading year
        // in the trailer's filename; movie matching remains year-strict.
        movie.ProductionYear = null;
        Assert.Null((await resolver.ResolveMediaAsync(intro.Id, user, "movie", default)).TmdbId);
        Assert.Equal(1, searches);

        intro.ProviderIds["Tmdb"] = "42";
        Assert.Equal(new CinemaMediaResolver.Resolution(42, "movie"),
            await resolver.ResolveMediaAsync(intro.Id, user, "tv", default));
        Assert.Equal(1, searches);
    }

    [Fact]
    public async Task DelimitedDisplayNameResolvesSeriesWithoutGuessingChannelNames()
    {
        var intro = new Video
        {
            Path = "/cache/" + new string('a', 64) + ".mp4",
            Name = "Netflix Anime - Ranma1/2 | Official Trailer | Netflix - Bv9wTsjqpSQ",
        };
        var searched = new List<string>();
        var resolver = Resolver(intro, (type, title, year) =>
        {
            Assert.Equal("tv", type);
            Assert.Null(year);
            searched.Add(title);
            return title == "Ranma1/2" ? [Result(777, "Ranma ½", 2024)] : [];
        });

        Assert.Equal(new CinemaMediaResolver.Resolution(777, "tv"),
            await resolver.ResolveMediaAsync(Guid.NewGuid(), Guid.NewGuid(), "tv", default));
        Assert.Contains("Ranma1/2", searched);
    }

    [Theory]
    [InlineData("Silo 3883jsjsjd8dj.mp4", "")]
    [InlineData("Silo_3883jsjsjd8dj.mp4", "")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.mp4", "Silo 3883jsjsjd8dj")]
    public async Task GeneratedSuffixUsesExactSeriesTitleAsFallback(string path, string displayName)
    {
        var intro = new Video { Path = path, Name = displayName };
        var searches = new List<string>();
        var resolver = Resolver(intro, (type, title, year) =>
        {
            Assert.Equal("tv", type);
            Assert.Null(year);
            searches.Add(title);
            return title == "Silo" ? [Result(42, "Silo")] : [];
        });

        Assert.Equal(new CinemaMediaResolver.Resolution(42, "tv"),
            await resolver.ResolveMediaAsync(Guid.NewGuid(), Guid.NewGuid(), "tv", default));
        Assert.Equal(new[] { "Silo 3883jsjsjd8dj", "Silo" }, searches);
    }

    [Fact]
    public async Task FullAlphanumericTitleMatchTakesPriorityOverSuffixFallback()
    {
        var intro = new Video { Path = "Silo 3883jsjsjd8dj.mp4" };
        var calls = 0;
        var resolver = Resolver(intro, (type, title, year) =>
        {
            calls++;
            Assert.Equal("Silo 3883jsjsjd8dj", title);
            return [Result(99, title)];
        });

        Assert.Equal(new CinemaMediaResolver.Resolution(99, "tv"),
            await resolver.ResolveMediaAsync(Guid.NewGuid(), Guid.NewGuid(), "tv", default));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ExactFullTitleWinsBeforeDelimitedFallback()
    {
        var intro = new Video { Path = "Spider-Man - No Way Home 2021 Official Trailer.mp4" };
        var count = 0;
        var resolver = Resolver(intro, (type, title, year) =>
        {
            count++;
            Assert.Equal("movie", type);
            Assert.Equal("Spider-Man - No Way Home", title);
            Assert.Equal(2021, year);
            return [Result(42, "Spider-Man: No Way Home", 2021)];
        });

        Assert.Equal(new CinemaMediaResolver.Resolution(42, "movie"),
            await resolver.ResolveMediaAsync(Guid.NewGuid(), Guid.NewGuid(), "movie", default));
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task DifferentMatchingDelimitedTitlesAreAmbiguous()
    {
        var intro = new Video { Path = "Show - Another Show Official Trailer.mp4" };
        var resolver = Resolver(intro, (type, title, year) =>
        {
            Assert.Equal("tv", type);
            Assert.Null(year);
            return title switch
            {
                "Show" => [Result(42, title)],
                "Another Show" => [Result(43, title)],
                _ => [],
            };
        });

        Assert.Null((await resolver.ResolveMediaAsync(
            Guid.NewGuid(), Guid.NewGuid(), "tv", default)).TmdbId);
    }

    [Fact]
    public async Task EmbeddedMovieTmdbIdResolvesWithoutYearOrSearch()
    {
        var intro = new Video { Path = "/trailers/Dune_438631_trailer.mp4" };
        var resolver = Resolver(intro, (_, _, _) => throw new Exception("No search expected"));
        Assert.Equal(new CinemaMediaResolver.Resolution(438631, "movie"),
            await resolver.ResolveMediaAsync(Guid.NewGuid(), Guid.NewGuid(), "tv", default));
    }

    [Theory]
    [InlineData("/trailers/Silo_tvdb403245_trailer.mp4", "", false)]
    [InlineData("/cache/abcdef1234567890.mp4", "Silo_tvdb403245_trailer", false)]
    [InlineData("/cache/abcdef1234567890.mp4", "Silo", true)]
    public async Task TvdbIdUsesHostProviderRatherThanAmbiguousTitle(
        string path, string name, bool metadataId)
    {
        var intro = new Video { Path = path, Name = name };
        if (metadataId) intro.ProviderIds["Tvdb"] = "403245";
        var lookups = 0;
        var resolver = Resolver(intro,
            (_, _, _) => throw new Exception("Title search should not run"),
            query =>
            {
                lookups++;
                Assert.Equal("403245", query.SearchInfo.ProviderIds["Tvdb"]);
                return [new RemoteSearchResult
                {
                    Name = "Silo",
                    ProviderIds = new() { ["Tmdb"] = "42", ["Tvdb"] = "403245" },
                }];
            });

        Assert.Equal(new CinemaMediaResolver.Resolution(42, "tv"),
            await resolver.ResolveMediaAsync(Guid.NewGuid(), Guid.NewGuid(), "movie", default));
        Assert.Equal(1, lookups);
    }

    [Fact]
    public async Task UnmappedTvdbIdFallsBackToExactSeriesName()
    {
        var intro = new Video { Path = "Silo_tvdb403245_trailer.mp4" };
        var titleLookups = 0;
        var resolver = Resolver(intro, (type, name, year) =>
        {
            titleLookups++;
            Assert.Equal("tv", type);
            Assert.Equal("Silo", name);
            Assert.Null(year);
            return [Result(42, "Silo", 2023)];
        }, _ => []);

        Assert.Equal(new CinemaMediaResolver.Resolution(42, "tv"),
            await resolver.ResolveMediaAsync(Guid.NewGuid(), Guid.NewGuid(), "movie", default));
        Assert.Equal(1, titleLookups);
    }

    [Fact]
    public async Task ConflictingTvdbMappingsDoNotFallBackToName()
    {
        var intro = new Video { Path = "Silo_tvdb403245_trailer.mp4" };
        var resolver = Resolver(intro,
            (_, _, _) => throw new Exception("Title search should not run"),
            _ => [Result(42, "Silo"), Result(43, "Silo")]);
        Assert.Null((await resolver.ResolveMediaAsync(
            Guid.NewGuid(), Guid.NewGuid(), "tv", default)).TmdbId);
    }

    [Fact]
    public async Task OwnedSeriesTvdbMetadataCanResolveWithoutFilename()
    {
        var user = Guid.NewGuid();
        var series = new Series
        {
            Id = Guid.NewGuid(),
            Name = "Silo",
            ProviderIds = new() { ["Tvdb"] = "403245" },
        };
        var intro = new Video { Id = Guid.NewGuid(), OwnerId = series.Id, Path = "trailer.mp4" };
        var library = new FakeLibraryManager
        {
            ItemForUserHandler = (id, uid) =>
            {
                Assert.Equal(user, uid);
                return id == intro.Id ? intro : id == series.Id ? series : null;
            },
        };
        var provider = DispatchProxy.Create<IProviderManager, SearchProvider>();
        ((SearchProvider)(object)provider).Search = (_, _, _) => throw new Exception("No title search expected");
        ((SearchProvider)(object)provider).TvdbSearch = _ =>
            [new RemoteSearchResult { ProviderIds = new() { ["Tmdb"] = "42" } }];
        var resolver = new CinemaMediaResolver(library, provider);

        Assert.Equal(new CinemaMediaResolver.Resolution(42, "tv"),
            await resolver.ResolveMediaAsync(intro.Id, user, "movie", default));
    }

    [Fact]
    public async Task FilenameIdentityConflictsStayUnresolved()
    {
        var intro = new Video
        {
            Path = "Dune_438631_trailer.mp4",
            ProviderIds = new() { ["Tmdb"] = "42" },
        };
        var resolver = Resolver(intro, (_, _, _) => throw new Exception("No search expected"));
        Assert.Null((await resolver.ResolveMediaAsync(Guid.NewGuid(), Guid.NewGuid(), "movie", default)).TmdbId);

        intro.ProviderIds.Clear();
        intro.ProviderIds["TmdbMediaType"] = "tv";
        Assert.Null((await resolver.ResolveMediaAsync(Guid.NewGuid(), Guid.NewGuid(), "tv", default)).TmdbId);

        intro.ProviderIds.Clear();
        intro.Path = "Silo_tvdb403245_trailer.mp4";
        intro.ProviderIds["TmdbMediaType"] = "movie";
        Assert.Null((await resolver.ResolveMediaAsync(Guid.NewGuid(), Guid.NewGuid(), "movie", default)).TmdbId);

        intro.ProviderIds.Clear();
        intro.ProviderIds["Tvdb"] = "99";
        Assert.Null((await resolver.ResolveMediaAsync(Guid.NewGuid(), Guid.NewGuid(), "tv", default)).TmdbId);

        intro.ProviderIds.Clear();
        intro.Path = "Dune_438631_trailer.mp4";
        intro.Name = "Other_12345_trailer";
        Assert.Null((await resolver.ResolveMediaAsync(Guid.NewGuid(), Guid.NewGuid(), "movie", default)).TmdbId);
    }

    [Fact]
    public async Task EquivalentPathAndDisplayNameSearchOnlyOnce()
    {
        var intro = new Video { Path = "Silo Official Trailer.mp4", Name = "SILO Trailer" };
        var searches = 0;
        var resolver = Resolver(intro, (type, title, year) =>
        {
            searches++;
            Assert.Equal("tv", type);
            Assert.Equal("Silo", title);
            Assert.Null(year);
            return [Result(42, title)];
        });

        Assert.Equal(new CinemaMediaResolver.Resolution(42, "tv"),
            await resolver.ResolveMediaAsync(Guid.NewGuid(), Guid.NewGuid(), "tv", default));
        Assert.Equal(1, searches);
    }

    [Fact]
    public async Task TrailerReleaseYearCannotDisambiguateSameTitleSeries()
    {
        var intro = new Video { Path = "Show Trailer (2026).mp4" };
        var resolver = Resolver(intro, (_, name, year) =>
        {
            Assert.Null(year);
            return [Result(42, name, 2016), Result(43, name, 2026)];
        });
        Assert.Null((await resolver.ResolveMediaAsync(Guid.NewGuid(), Guid.NewGuid(), "tv", default)).TmdbId);
    }

    [Fact]
    public async Task YearlessMovieResolvesUniqueExactTitleWithoutSearchingTv()
    {
        var intro = new Video { Path = "Interstellar Trailer.mp4" };
        var searches = 0;
        var resolver = Resolver(intro, (type, title, year) =>
        {
            searches++;
            Assert.Equal("movie", type);
            Assert.Equal("Interstellar", title);
            Assert.Null(year);
            return [Result(42, "Interstellar", 2014), Result(42, "Interstellar", 2014),
                Result(90, "Interstellar 2", 2027)];
        });

        Assert.Equal(new CinemaMediaResolver.Resolution(42, "movie"),
            await resolver.ResolveMediaAsync(Guid.NewGuid(), Guid.NewGuid(), "movie", default));
        Assert.Equal(1, searches);
        Assert.Null((await resolver.ResolveMediaAsync(Guid.NewGuid(), Guid.NewGuid(), null, default)).TmdbId);
        Assert.Equal(1, searches);
    }

    [Fact]
    public async Task YearlessMovieRejectsAmbiguousOrMissingTmdbIds()
    {
        var intro = new Video { Path = "Dune Trailer.mp4" };
        var results = new RemoteSearchResult[]
        {
            Result(42, "Dune", 1984),
            Result(43, "Dune", 2021),
        };
        var resolver = Resolver(intro, (type, title, year) =>
        {
            Assert.Equal("movie", type);
            Assert.Equal("Dune", title);
            Assert.Null(year);
            return results;
        });

        Assert.Null((await resolver.ResolveMediaAsync(Guid.NewGuid(), Guid.NewGuid(), "movie", default)).TmdbId);
        results = [Result(42, "Dune", 2021), new RemoteSearchResult { Name = "Dune" }];
        Assert.Null((await resolver.ResolveMediaAsync(Guid.NewGuid(), Guid.NewGuid(), "movie", default)).TmdbId);
    }

    [Fact]
    public async Task HashAndUnmatchedFilenameFallBackToReadableName()
    {
        var intro = new Video { Path = new string('a', 64) + ".mp4", Name = "Show Season 5 Trailer" };
        var searches = 0;
        var resolver = Resolver(intro, (type, title, _) =>
        {
            searches++;
            Assert.Equal("tv", type);
            return title == "Show" ? [Result(42, "Show")] : [];
        });
        Assert.Equal(42, (await resolver.ResolveMediaAsync(Guid.NewGuid(), Guid.NewGuid(), "tv", default)).TmdbId);
        Assert.Equal(1, searches);
        intro.Path = "unreadable-cache.mp4";
        Assert.Equal(42, (await resolver.ResolveMediaAsync(Guid.NewGuid(), Guid.NewGuid(), "tv", default)).TmdbId);
        Assert.Equal(3, searches);
    }

    [Fact]
    public async Task AmbiguityAndConflictingDisplayNamesHideTheAction()
    {
        var intro = new Video { Path = "Show Trailer.mp4", Name = "Other Trailer" };
        var resolver = Resolver(intro, (_, title, _) => [Result(title == "Show" ? 42 : 43, title)]);
        Assert.Null((await resolver.ResolveMediaAsync(Guid.NewGuid(), Guid.NewGuid(), "tv", default)).TmdbId);
        resolver = Resolver(intro, (_, title, _) => [Result(42, title), Result(43, title)]);
        Assert.Null((await resolver.ResolveMediaAsync(Guid.NewGuid(), Guid.NewGuid(), "tv", default)).TmdbId);
        resolver = Resolver(intro, (_, title, _) => [Result(42, title), new RemoteSearchResult { Name = title }]);
        Assert.Null((await resolver.ResolveMediaAsync(Guid.NewGuid(), Guid.NewGuid(), "tv", default)).TmdbId);
    }

    public class SearchProvider : DispatchProxy
    {
        public Func<string, string, int?, IEnumerable<RemoteSearchResult>> Search { get; set; } = null!;
        public Func<RemoteSearchQuery<SeriesInfo>, IEnumerable<RemoteSearchResult>>? TvdbSearch { get; set; }
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            Assert.Equal("GetRemoteSearchResults", method!.Name);
            if (args![0] is RemoteSearchQuery<MovieInfo> movie)
            {
                Assert.Equal(typeof(Movie), method.GetGenericArguments()[0]);
                Assert.False(movie.IncludeDisabledProviders);
                return Task.FromResult(Search("movie", movie.SearchInfo.Name, movie.SearchInfo.Year));
            }
            var series = Assert.IsType<RemoteSearchQuery<SeriesInfo>>(args[0]);
            Assert.Equal(typeof(Series), method.GetGenericArguments()[0]);
            Assert.False(series.IncludeDisabledProviders);
            if (series.SearchInfo.ProviderIds.ContainsKey("Tvdb"))
            {
                Assert.Equal("TheMovieDb", series.SearchProviderName);
                Assert.Equal(string.Empty, series.SearchInfo.Name);
                Assert.NotNull(TvdbSearch);
                return Task.FromResult(TvdbSearch!(series));
            }
            return Task.FromResult(Search("tv", series.SearchInfo.Name, series.SearchInfo.Year));
        }
    }
}
