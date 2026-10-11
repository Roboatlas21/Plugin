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
using Microsoft.Extensions.Logging;

namespace Moonfin.Server.Services;

/// <summary>Finds the movie or series advertised by a trailer.</summary>
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

    public async Task<Resolution> ResolveMediaAsync(Guid itemId, string? expectedMediaType,
        CancellationToken cancellationToken, bool allowFilenameSearch = false, ILogger? diagnostics = null)
    {
        var libraryItem = _library.GetItemById(itemId);
        var item = libraryItem as Video;
        diagnostics?.LogInformation(
            "[CinemaDiag] Item {ItemId}: Type={ItemType}, Name={Name}, Path={Path}, OwnerId={OwnerId}, ExtraType={ExtraType}, Tmdb={Tmdb}, TmdbMediaType={TmdbMediaType}",
            itemId, libraryItem?.GetType().Name ?? "missing", item?.Name, item?.Path, item?.OwnerId,
            item?.ExtraType, ProviderValue(item?.ProviderIds, "Tmdb"), ProviderValue(item?.ProviderIds, "TmdbMediaType"));
        if (item == null || item is Episode) return new(null);

        var ownerId = item.OwnerId;
        if (ownerId == Guid.Empty && !string.IsNullOrWhiteSpace(item.Path))
        {
            // Other intro providers may register the same trailer file as a separate Video.
            var matches = _library.GetItemList(new InternalItemsQuery
            {
                Path = item.Path,
                HasOwnerId = true,
                GroupByPresentationUniqueKey = false,
            });
            var ownerIds = matches
            .Where(extra => extra.ExtraType == ExtraType.Trailer)
            .OfType<Video>()
            .Select(extra => extra.OwnerId)
            .Where(id => id != Guid.Empty)
            .Distinct()
            .Take(2)
            .ToArray();
            diagnostics?.LogInformation(
                "[CinemaDiag] Exact-path lookup {ItemId}: Matches={MatchCount}, Items={Items}, UniqueOwnerIds={OwnerIds}",
                itemId, matches.Count,
                string.Join("; ", matches.Take(10).Select(m => $"{m.Id} ({m.GetType().Name}, extra={m.ExtraType}, owner={m.OwnerId})")),
                string.Join(", ", ownerIds));
            if (ownerIds.Length == 1) ownerId = ownerIds[0];
        }

        BaseItem? owner = null;
        if (ownerId != Guid.Empty)
        {
            owner = _library.GetItemById(ownerId);
            diagnostics?.LogInformation(
                "[CinemaDiag] Owner lookup {ItemId}: OwnerId={OwnerId}, Type={OwnerType}, Title={OwnerTitle}, Tmdb={OwnerTmdb}",
                itemId, ownerId, owner?.GetType().Name ?? "missing", owner?.Name, ProviderValue(owner?.ProviderIds, "Tmdb"));
            if (owner == null) return new(null);
            var seriesId = OwnerSeriesId(owner);
            if (seriesId != Guid.Empty)
            {
                owner = _library.GetItemById(seriesId) as Series;
                diagnostics?.LogInformation(
                    "[CinemaDiag] Series lookup {ItemId}: SeriesId={SeriesId}, Title={Title}, Tmdb={Tmdb}",
                    itemId, seriesId, owner?.Name, ProviderValue(owner?.ProviderIds, "Tmdb"));
            }
            if (owner is not Movie && owner is not Series) return new(null);
        }

        var resolution = await ResolveItemAsync(item, owner, expectedMediaType, _library.ParseName,
            cancellationToken, allowFilenameSearch).ConfigureAwait(false);
        diagnostics?.LogInformation(
            "[CinemaDiag] Resolved {ItemId}: OwnerId={OwnerId}, OwnerType={OwnerType}, OwnerTmdb={OwnerTmdb}, TmdbId={TmdbId}, MediaType={MediaType}",
            itemId, ownerId, owner?.GetType().Name, ProviderValue(owner?.ProviderIds, "Tmdb"),
            resolution.TmdbId, resolution.MediaType);
        return resolution;
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
