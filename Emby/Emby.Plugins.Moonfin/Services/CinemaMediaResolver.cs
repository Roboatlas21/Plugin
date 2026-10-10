using System;
using System.Collections.Generic;
using System.Linq;
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
    /// <summary>Looks up trailers and their owners using Emby's access checks.</summary>
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

        public async Task<Resolution> ResolveMediaAsync(string itemId, User user,
            string? expectedMediaType, CancellationToken cancellationToken)
        {
            var item = _library.GetItemById(itemId) as Video;
            if (item == null || item is Episode || !item.IsVisibleStandalone(user)) return new(null);

            // Find the owning movie or series through Emby's parent hierarchy.
            BaseItem? owner = item.GetParents()
                .FirstOrDefault(parent => parent is Movie or Series or Season or Episode);
            if (owner is Season or Episode)
            {
                if (owner.SeriesId <= 0) return new(null);
                owner = _library.GetItemById(owner.SeriesId) as Series;
                if (owner == null) return new(null);
            }
            if (owner != null && !owner.IsVisibleStandalone(user)) return new(null);

            return await ResolveItemAsync(item, owner, expectedMediaType,
                name => _library.ParseName(name.AsSpan()), cancellationToken).ConfigureAwait(false);
        }

        private static Task<IEnumerable<RemoteSearchResult>> AwaitSearch(
            Task<IEnumerable<RemoteSearchResult>> search, CancellationToken cancellationToken) => search;
    }
}
