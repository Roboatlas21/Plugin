using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.LiveTv;
using Microsoft.Extensions.Logging;
using Moonfin.LiveTv;

namespace Moonfin.Server.Services;

public sealed class LiveTvChannelGroupsService(
    ILiveTvManager liveTv, ITunerHostManager tuners, ILogger<LiveTvChannelGroupsService> logger)
{
    private readonly ChannelGroupCache _cache = new();

    internal async Task<ChannelGroupResult> GetAsync(Guid userId, CancellationToken token)
    {
        var snapshot = await _cache.GetAsync(ReadTunersAsync, token);
        var query = new LiveTvChannelQuery { UserId = userId, AddCurrentProgram = false };
        var options = new DtoOptions(false) { EnableImages = false, EnableUserData = false, AddCurrentProgram = false };
        var visible = liveTv.GetInternalChannels(query, options, token).Items;
        // Registered tuners feed Jellyfin's DefaultLiveTvService, whose service name is "Emby".
        return snapshot.ForVisibleChannels(visible.OfType<LiveTvChannel>()
            .Where(c => c.ServiceName == "Emby")
            .Select(c => (c.Id.ToString("N"), c.ExternalId)));
    }

    private async Task<ChannelGroupSnapshot> ReadTunersAsync(CancellationToken token)
    {
        var result = new ChannelGroupSnapshot();
        var index = 0;
        foreach (var tuner in tuners.TunerHosts)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                foreach (var channel in await tuner.GetChannels(true, token))
                    result.Add(index, channel.TunerHostId, channel.Id, new[] { channel.ChannelGroup });
            }
            catch (Exception) when (!token.IsCancellationRequested)
            {
                result.Partial = true;
                logger.LogWarning("Could not read channel groups from tuner {Index}", index);
            }
            index++;
        }
        return result;
    }
}
