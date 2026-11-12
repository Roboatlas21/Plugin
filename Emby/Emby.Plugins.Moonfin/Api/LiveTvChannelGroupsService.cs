using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Services;
using MediaBrowser.Model.Tasks;
using Moonfin.LiveTv;

namespace Emby.Plugins.Moonfin.Api
{
    public sealed class LiveTvChannelGroupsService : IService, IRequiresRequest, IHasResultFactory
    {
        private static readonly ChannelGroupCache Cache = new ChannelGroupCache();
        internal static void Invalidate() => Cache.Clear();
        private readonly ILiveTvManager _manager;
        private readonly ILibraryManager _library;
        private readonly ITaskManager _tasks;
        private readonly IAuthorizationContext _auth;
        private readonly ILogger _logger;
        public IRequest Request { get; set; } = null!;
        public IHttpResultFactory ResultFactory { get; set; }

        public LiveTvChannelGroupsService(IApplicationHost appHost)
        {
            _manager = appHost.Resolve<ILiveTvManager>();
            _library = appHost.Resolve<ILibraryManager>();
            _tasks = appHost.Resolve<ITaskManager>();
            _auth = appHost.Resolve<IAuthorizationContext>();
            _logger = appHost.Resolve<ILogManager>().GetLogger("Moonfin");
            ResultFactory = appHost.Resolve<IHttpResultFactory>();
        }

        public async Task<object> Get(GetLiveTvChannelGroups request)
        {
            var user = AuthHelpers.GetCurrentUser(Request, _auth);
            if (user == null || user.Id == Guid.Empty || user.Policy?.EnableLiveTvAccess != true)
            {
                Request.Response.StatusCode = user == null || user.Id == Guid.Empty ? 401 : 403;
                return MoonfinJson.Result(Request, ResultFactory, null);
            }

            var token = Request.CancellationToken;
            var revision = ChannelGroupResult.GetGuideRevision(_tasks);
            var query = new InternalItemsQuery(user) { EnableTotalRecordCount = false };
            var channels = _manager.GetInternalChannels(query, false, null, token).Items;
            var snapshot = await Cache.GetAsync(revision, channels.Select(c => c.ExternalId), ReadTunersAsync, token).ConfigureAwait(false);
            var result = snapshot.ForVisibleChannels(channels.Select(c => (c.GetClientId(), c.ExternalId)));
            if (result.Partial) Cache.Invalidate(snapshot);
            try
            {
                result.ReadProgrammeCategories(_tasks, revision, () => ReadCategories(user, channels.Select(c => c.InternalId).ToArray(), token));
            }
            catch (Exception) when (!token.IsCancellationRequested)
            {
                _logger.Warn("Could not read guide programme categories");
            }
            return MoonfinJson.Result(Request, ResultFactory, result);
        }

        private string[] ReadCategories(User user, long[] channelIds, CancellationToken token)
        {
            if (channelIds.Length == 0) return Array.Empty<string>();
            var found = new List<string>();
            Check("movies", q => q.IsMovie = true);
            Check("series", q => q.IsSeries = true);
            Check("sports", q => q.IsSports = true);
            Check("news", q => q.IsNews = true);
            Check("kids", q => q.IsKids = true);
            Check("premiere", q => q.IsPremiere = true);
            return found.ToArray();

            void Check(string key, Action<InternalItemsQuery> applyFilter)
            {
                var query = new InternalItemsQuery(user)
                {
                    IncludeItemTypes = new[] { nameof(LiveTvProgram) },
                    ParentIds = channelIds,
                    SkipAncestorNormalization = true,
                    Limit = 1,
                    EnableTotalRecordCount = false
                };
                applyFilter(query);
                if (_library.GetInternalItemIds(query, token).Length != 0) found.Add(key);
            }
        }

        private async Task ReadTunersAsync(ChannelGroupSnapshot result, CancellationToken token)
        {
            // Emby 4.9 exposes the registered instances on the manager implementation only.
            // Creating fresh tuner exports would skip the running tuners' caches and configuration.
            if (!(_manager.GetType().GetProperty("TunerHosts")?.GetValue(_manager) is IEnumerable<ITunerHost> tuners))
            {
                result.Partial = true;
                return;
            }

            var index = 0;
            foreach (var tuner in tuners)
            {
                foreach (var host in _manager.GetTunerHostInfos(tuner.Type))
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        var uncertain = new HashSet<string>(StringComparer.Ordinal);
                        var channels = await EmbyTunerGroupReader.GetChannelsAsync(tuner, host, token, id => uncertain.Add(id)).ConfigureAwait(false);
                        foreach (var channel in channels)
                            result.Add(index, host.Id, channel.Id, channel.Tags, uncertain.Contains(channel.Id));
                    }
                    catch (Exception) when (!token.IsCancellationRequested)
                    {
                        result.Partial = true;
                        _logger.Warn("Could not read channel groups from tuner {0}", index);
                    }
                    index++;
                }
            }
        }
    }
}
