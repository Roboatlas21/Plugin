using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;
using Moonfin.LiveTv;

namespace Moonfin.Server.Services;

public sealed class LiveTvChannelGroupsService(
    ILiveTvManager liveTv, ITunerHostManager tuners, ILibraryManager library,
    IUserManager users, ITaskManager tasks, ILogger<LiveTvChannelGroupsService> logger)
{
    private readonly ChannelGroupCache _cache = new();

    internal string GetGuideRevision() => ChannelGroupResult.GetGuideRevision(tasks);

    internal async Task<ChannelGroupResult> GetAsync(Guid userId, CancellationToken token)
    {
        var revision = ChannelGroupResult.GetGuideRevision(tasks);
        var snapshot = await _cache.GetAsync(revision, ReadTunersAsync, token);
        var query = new LiveTvChannelQuery { UserId = userId, AddCurrentProgram = false };
        var options = new DtoOptions(false) { EnableImages = false, EnableUserData = false, AddCurrentProgram = false };
        var visible = liveTv.GetInternalChannels(query, options, token).Items;
        // Registered tuners feed Jellyfin's DefaultLiveTvService, whose service name is "Emby".
        var result = snapshot.ForVisibleChannels(visible.OfType<LiveTvChannel>()
            .Where(c => c.ServiceName == "Emby")
            .Select(c => (c.Id.ToString("N"), c.ExternalId)));
        if (result.Partial) _cache.Invalidate(snapshot);
        try
        {
            result.ReadProgrammeCategories(tasks, revision, () => ReadCategories(userId, visible.Select(c => c.Id).ToArray(), token));
        }
        catch (Exception ex) when (!token.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Could not read guide programme categories");
        }
        return result;
    }

    private string[] ReadCategories(Guid userId, Guid[] channelIds, CancellationToken token)
    {
        if (channelIds.Length == 0) return [];
        // Jellyfin moved the User type between 10.10 and 10.11. Bind the user-scoped
        // query constructor at runtime so both versions apply parental restrictions.
        var user = typeof(IUserManager).GetMethod(nameof(IUserManager.GetUserById), [typeof(Guid)])?.Invoke(users, [userId])
            ?? throw new InvalidOperationException("Guide user is unavailable");
        var query = (InternalItemsQuery)Activator.CreateInstance(typeof(InternalItemsQuery), user)!;
        query.IncludeItemTypes = [BaseItemKind.LiveTvProgram];
        query.ChannelIds = channelIds;
        query.Limit = 1;
        query.EnableTotalRecordCount = false;

        // The Jellyfin guide importer writes these tags on the programme itself.
        var found = new List<string>();
        foreach (var (key, tag) in new[] { ("movies", "Movie"), ("series", "Series"), ("sports", "Sports"),
                                           ("news", "News"), ("kids", "Kids"), ("premiere", "Premiere") })
        {
            token.ThrowIfCancellationRequested();
            query.Tags = [tag];
            if (library.GetItemsResult(query).Items.Count != 0) found.Add(key);
        }
        return found.ToArray();
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
