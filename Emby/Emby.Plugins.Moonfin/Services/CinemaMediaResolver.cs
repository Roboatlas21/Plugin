using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Providers;

namespace Emby.Plugins.Moonfin.Services
{
    /// <summary>Emby access checks for the shared Cinema Mode resolver.</summary>
    public sealed partial class CinemaMediaResolver
    {
        private readonly ILibraryManager _library;
        private readonly IProviderManager _providers;

        public CinemaMediaResolver(ILibraryManager library, IProviderManager providers)
        {
            _library = library;
            _providers = providers;
        }

        public sealed record Resolution(int? TmdbId, string? MediaType = null);

        public async Task<Resolution> ResolveMediaAsync(Guid itemId, User user,
            string? expectedMediaType, CancellationToken cancellationToken)
        {
            var item = _library.GetItemById(itemId) as Video;
            if (item == null || item is Episode || !item.IsVisibleStandalone(user)) return new(null);

            BaseItem? owner = null;
            if (item.OwnerId != Guid.Empty)
            {
                owner = _library.GetItemById(item.OwnerId);
                if (owner == null || !owner.IsVisibleStandalone(user)) return new(null);
                var seriesId = OwnerSeriesId(owner);
                if (seriesId != Guid.Empty)
                    owner = _library.GetItemById(seriesId) as Series;
                if ((owner is not Movie && owner is not Series) || !owner.IsVisibleStandalone(user))
                    return new(null);
            }

            return await ResolveItemAsync(item, owner, expectedMediaType,
                name => _library.ParseName(name.AsSpan()), cancellationToken).ConfigureAwait(false);
        }

        private static Task<IEnumerable<RemoteSearchResult>> AwaitSearch(
            Task<IEnumerable<RemoteSearchResult>> search, CancellationToken cancellationToken) => search;
    }
}
