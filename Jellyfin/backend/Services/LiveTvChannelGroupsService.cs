using System.Text.Json;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Configuration;
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
    ILiveTvManager liveTv, ITunerHostManager tuners, IServerConfigurationManager config,
    ILibraryManager library, IUserManager users, ITaskManager tasks, ILogger<LiveTvChannelGroupsService> logger)
{
    private readonly ChannelGroupReader _reader = new();

    internal async Task<ChannelGroupResult> GetAsync(Guid userId, CancellationToken token)
    {
        var importTime = ChannelGroupResult.StableImportTime(tasks);
        var query = new LiveTvChannelQuery { UserId = userId, AddCurrentProgram = false };
        var options = new DtoOptions(false) { EnableImages = false, EnableUserData = false, AddCurrentProgram = false };
        var visible = liveTv.GetInternalChannels(query, options, token).Items;
        // Registered tuners feed Jellyfin's DefaultLiveTvService, whose service name is "Emby".
        var channels = visible.OfType<LiveTvChannel>()
            .Where(c => c.ServiceName == "Emby")
            .Select(c => (ClientId: c.Id.ToString("N"), c.ExternalId)).ToArray();
        var result = new ChannelGroupResult();
        if (channels.Length != 0)
        {
            var snapshot = await _reader.GetAsync(channels.Select(c => c.ExternalId), ReadTunersAsync, token);
            result = snapshot.ForVisibleChannels(channels);
        }
        try
        {
            result.ReadProgrammeCategories(tasks, importTime, () => ReadCategories(userId, visible.Select(c => c.Id).ToArray(), token));
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

    private async Task ReadTunersAsync(ChannelGroupSnapshot result, CancellationToken token)
    {
        var configured = ((LiveTvOptions)config.GetConfiguration("livetv")).TunerHosts;
        // Jellyfin writes one lineup per M3U host during import. Its tuner API can
        // fetch the playlist again after a restart, even when asked to use the cache.
        foreach (var host in configured.Where(host => IsM3u(host.Type)))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (string.IsNullOrEmpty(host.Id) || Path.GetFileName(host.Id) != host.Id)
                {
                    result.MissingTuner = true;
                    continue;
                }
                var path = Path.Combine(config.ApplicationPaths.CachePath, host.Id + "_channels");
                using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var channels = await JsonSerializer.DeserializeAsync<ImportedM3uChannel[]>(stream, cancellationToken: token)
                    ?? throw new JsonException("Expected an imported channel list");
                foreach (var channel in channels)
                    result.Add(0, host.Id, channel?.Id, channel?.ChannelGroup is { } group ? [group] : null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                result.MissingTuner = true;
                logger.LogDebug("Could not read the imported M3U lineup for tuner {HostId}", host.Id);
            }
        }

        var index = 0;
        foreach (var tuner in tuners.TunerHosts)
        {
            index++;
            token.ThrowIfCancellationRequested();
            try
            {
                var type = tuner.Type;
                if (IsM3u(type)) continue;
                var channels = await tuner.GetChannels(true, token);
                // Jellyfin can return a partial list when one configured host is offline.
                // An unused tuner type normally returns no channels at all.
                var responding = channels.Select(channel => channel.TunerHostId).ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (configured.Any(host => string.Equals(host.Type, type, StringComparison.OrdinalIgnoreCase)
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
        }
    }

    private static bool IsM3u(string type) => string.Equals(type, "m3u", StringComparison.OrdinalIgnoreCase);

    private sealed record ImportedM3uChannel(string? Id, string? ChannelGroup);
}
