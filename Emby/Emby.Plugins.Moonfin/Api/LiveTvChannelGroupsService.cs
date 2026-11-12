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
using Moonfin.LiveTv;

namespace Emby.Plugins.Moonfin.Api
{
    public sealed class LiveTvChannelGroupsService : IService, IRequiresRequest, IHasResultFactory
    {
        private static readonly ChannelGroupReader Reader = new ChannelGroupReader();
        private readonly ILiveTvManager _manager;
        private readonly IAuthorizationContext _auth;
        private readonly ILogger _logger;
        public IRequest Request { get; set; } = null!;
        public IHttpResultFactory ResultFactory { get; set; }

        public LiveTvChannelGroupsService(IApplicationHost appHost)
        {
            _manager = appHost.Resolve<ILiveTvManager>();
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
            var query = new InternalItemsQuery(user) { EnableTotalRecordCount = false };
            var channels = _manager.GetInternalChannels(query, false, null, token).Items;
            if (channels.Length == 0)
                return MoonfinJson.Result(Request, ResultFactory, new ChannelGroupResult());
            var snapshot = await Reader.GetAsync(channels.Select(c => c.ExternalId), ReadTunersAsync, token).ConfigureAwait(false);
            var result = snapshot.ForVisibleChannels(channels.Select(c => (c.GetClientId(), c.ExternalId)));
            return MoonfinJson.Result(Request, ResultFactory, result);
        }

        private async Task ReadTunersAsync(ChannelGroupSnapshot result, CancellationToken token)
        {
            // Emby 4.9 exposes the registered instances on the manager implementation only.
            // Creating fresh tuner exports would skip the running tuners' caches and configuration.
            if (!(_manager.GetType().GetProperty("TunerHosts")?.GetValue(_manager) is IEnumerable<ITunerHost> tuners))
            {
                result.MissingTuner = true;
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
                        if (channels.Count == 0) result.MissingTuner = true;
                        foreach (var channel in channels)
                            result.Add(index, host.Id, channel.Id, channel.Tags, uncertain.Contains(channel.Id));
                    }
                    catch (Exception) when (!token.IsCancellationRequested)
                    {
                        result.MissingTuner = true;
                        _logger.Warn("Could not read channel groups from tuner {0}", index);
                    }
                    index++;
                }
            }
        }
    }
}
