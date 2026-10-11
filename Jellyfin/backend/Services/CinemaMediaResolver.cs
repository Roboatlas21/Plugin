using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;

namespace Moonfin.Server.Services;

/// <summary>Looks up trailers and their owners using Jellyfin's access checks.</summary>
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
        // Use the user's access permissions for both the trailer and its owner.
        var item = _library.GetItemById<Video>(itemId, userId);
        if (item == null || item is Episode) return new(null);

        var ownerId = item.OwnerId;
        if (ownerId == Guid.Empty && !string.IsNullOrWhiteSpace(item.Path))
        {
            // Other intro providers may register the same trailer file as a separate Video.
            var ownerIds = _library.GetItemList(new InternalItemsQuery
            {
                Path = item.Path,
                HasOwnerId = true,
                GroupByPresentationUniqueKey = false,
            })
            .Where(extra => extra.ExtraType == ExtraType.Trailer)
            .Select(extra => _library.GetItemById<Video>(extra.Id, userId))
            .OfType<Video>()
            .Select(extra => extra.OwnerId)
            .Where(id => id != Guid.Empty)
            .Distinct()
            .Take(2)
            .ToArray();
            if (ownerIds.Length == 1) ownerId = ownerIds[0];
        }

        BaseItem? owner = null;
        if (ownerId != Guid.Empty)
        {
            owner = _library.GetItemById<BaseItem>(ownerId, userId);
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
