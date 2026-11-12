using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.LiveTv;
using Microsoft.Extensions.Logging;
using Moonfin.LiveTv;

namespace Moonfin.Server.Services;

public sealed class LiveTvChannelGroupsService(
    ILiveTvManager liveTv, ITunerHostManager tuners, IServerConfigurationManager config, ILogger<LiveTvChannelGroupsService> logger)
{
    private readonly ChannelGroupReader _reader = new();

    internal async Task<ChannelGroupResult> GetAsync(Guid userId, CancellationToken token)
    {
        var query = new LiveTvChannelQuery { UserId = userId, AddCurrentProgram = false };
        var options = new DtoOptions(false) { EnableImages = false, EnableUserData = false, AddCurrentProgram = false };
        var visible = liveTv.GetInternalChannels(query, options, token).Items;
        // Registered tuners feed Jellyfin's DefaultLiveTvService, whose service name is "Emby".
        var channels = visible.OfType<LiveTvChannel>()
            .Where(c => c.ServiceName == "Emby")
            .Select(c => (ClientId: c.Id.ToString("N"), c.ExternalId)).ToArray();
        if (channels.Length == 0) return new ChannelGroupResult();
        var snapshot = await _reader.GetAsync(channels.Select(c => c.ExternalId), ReadTunersAsync, token);
        return snapshot.ForVisibleChannels(channels);
    }

    private async Task ReadTunersAsync(ChannelGroupSnapshot result, CancellationToken token)
    {
        var configured = ((LiveTvOptions)config.GetConfiguration("livetv")).TunerHosts;
        var index = 0;
        foreach (var tuner in tuners.TunerHosts)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var channels = await tuner.GetChannels(true, token);
                // Jellyfin can return a partial list when one configured host is offline.
                // An unused tuner type normally returns no channels at all.
                var responding = channels.Select(channel => channel.TunerHostId).ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (configured.Any(host => string.Equals(host.Type, tuner.Type, StringComparison.OrdinalIgnoreCase)
                    && !responding.Contains(host.Id)))
                    result.MissingTuner = true;
                foreach (var channel in channels)
                    result.Add(index, channel.TunerHostId, channel.Id, new[] { channel.ChannelGroup });
            }
            catch (Exception) when (!token.IsCancellationRequested)
            {
                result.MissingTuner = true;
                logger.LogWarning("Could not read channel groups from tuner {Index}", index);
            }
            index++;
        }
    }
}
