using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Providers;

namespace Moonfin.Server.Services;

/// <summary>Jellyfin access checks for the shared Cinema Mode resolver.</summary>
public sealed partial class CinemaMediaResolver
{
    private readonly ILibraryManager _library;
    private readonly IProviderManager _providers;

    public CinemaMediaResolver(ILibraryManager library, IProviderManager providers)
    {
        _library = library;
        _providers = providers;
    }

    public sealed record Resolution(
        [property: JsonPropertyName("tmdbId")] int? TmdbId,
        [property: JsonPropertyName("mediaType")] string? MediaType = null);

    public async Task<Resolution> ResolveMediaAsync(Guid itemId, Guid userId, string? expectedMediaType,
        CancellationToken cancellationToken)
    {
        // User-aware lookups enforce visibility, including private registered trailer videos.
        var item = _library.GetItemById<Video>(itemId, userId);
        if (item == null || item is Episode) return new(null);

        BaseItem? owner = null;
        if (item.OwnerId != Guid.Empty)
        {
            owner = _library.GetItemById<BaseItem>(item.OwnerId, userId);
            if (owner == null) return new(null);
            var seriesId = OwnerSeriesId(owner);
            if (seriesId != Guid.Empty)
                owner = _library.GetItemById<Series>(seriesId, userId);
            if (owner is not Movie && owner is not Series) return new(null);
        }

        return await ResolveItemAsync(item, owner, expectedMediaType, _library.ParseName, cancellationToken)
            .ConfigureAwait(false);
    }

    // An extra attached to a season or episode advertises its series.
    private static Guid OwnerSeriesId(BaseItem owner) => owner switch
    {
        Season season => season.SeriesId,
        Episode episode => episode.SeriesId,
        _ => Guid.Empty,
    };

    private static Task<IEnumerable<RemoteSearchResult>> AwaitSearch(
        Task<IEnumerable<RemoteSearchResult>> search, CancellationToken cancellationToken) =>
        search.WaitAsync(cancellationToken);
}
