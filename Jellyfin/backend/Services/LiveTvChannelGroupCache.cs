using MediaBrowser.Controller.LiveTv;
using Microsoft.Extensions.Logging;

namespace Moonfin.Server.Services;

/// <summary>
/// Caches tuner-provided ChannelGroup metadata, keyed by service and external channel ID.
/// Access control is intentionally performed by the API *after* this server-wide cache.
/// </summary>
public sealed class LiveTvChannelGroupCache
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
    private readonly ILiveTvManager _liveTv;
    private readonly ILogger<LiveTvChannelGroupCache> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyDictionary<string, string> _cached =
        new Dictionary<string, string>(StringComparer.Ordinal);
    private DateTimeOffset _expires = DateTimeOffset.MinValue;

    public LiveTvChannelGroupCache(
        ILiveTvManager liveTv,
        ILogger<LiveTvChannelGroupCache> logger)
    {
        _liveTv = liveTv;
        _logger = logger;
    }

    public static string Key(string? service, string externalId) =>
        (service ?? string.Empty) + "\u001f" + externalId;

    public async Task<IReadOnlyDictionary<string, string>> GetAsync(CancellationToken ct)
    {
        if (DateTimeOffset.UtcNow < _expires) return _cached;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (DateTimeOffset.UtcNow < _expires) return _cached;

            var groups = new Dictionary<string, string>(StringComparer.Ordinal);
            var failed = false;
            foreach (var liveTvService in _liveTv.Services)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    foreach (var channel in await liveTvService.GetChannelsAsync(ct).ConfigureAwait(false))
                    {
                        if (string.IsNullOrWhiteSpace(channel.ChannelGroup)
                            || string.IsNullOrEmpty(channel.Id)) continue;
                        // Do not trim or split: "US| SPORTS" is one complete group.
                        groups[Key(liveTvService.Name, channel.Id)] = channel.ChannelGroup;
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failed = true;
                    _logger.LogWarning(ex, "Could not read channel groups from {Service}", liveTvService.Name);
                }
            }

            // Transient tuner failures should be retried soon, rather than freezing a
            // partial list for five minutes. Never retain stale groups indefinitely.
            _cached = groups;
            _expires = DateTimeOffset.UtcNow.Add(failed ? TimeSpan.FromSeconds(30) : Lifetime);
            return _cached;
        }
        finally
        {
            _gate.Release();
        }
    }
}
