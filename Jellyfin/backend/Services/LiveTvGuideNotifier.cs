using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Session;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Moonfin.LiveTv;

namespace Moonfin.Server.Services;

public sealed class LiveTvGuideNotifier(
    ILibraryManager library, ITaskManager tasks, ISessionManager sessions,
    LiveTvChannelGroupsService groups, ILogger<LiveTvGuideNotifier> logger) : IHostedService
{
    private LiveTvGuideChanges? _changes;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _changes = new LiveTvGuideChanges(library, tasks, PublishAsync,
            ex => logger.LogWarning(ex, "Could not notify clients of a Live TV guide update"));
        return Task.CompletedTask;
    }

    internal void ChannelEdited(Guid id)
    {
        try
        {
            if (library.GetItemById(id) is LiveTvChannel) _changes?.ChannelChanged();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not check the edited channel for a guide update");
        }
    }

    private Task PublishAsync()
    {
        groups.Invalidate();
        // Jellyfin has a fixed websocket message enum. Extend its library change
        // payload for Moonfin devices without broadcasting user-specific channel IDs.
        var devices = sessions.Sessions.Where(s => s.UserId != Guid.Empty &&
            s.Client?.StartsWith("Moonfin", StringComparison.OrdinalIgnoreCase) == true)
            .Select(s => s.DeviceId).Distinct(StringComparer.Ordinal);
        return Task.WhenAll(devices.Select(device => sessions.SendMessageToUserDeviceSessions(
            device, SessionMessageType.LibraryChanged, new GuideUpdate(), CancellationToken.None)));
    }

    private sealed class GuideUpdate : LibraryUpdateInfo
    {
        public bool MoonfinLiveTvGuideChanged => true;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _changes?.Dispose();
        return Task.CompletedTask;
    }
}
