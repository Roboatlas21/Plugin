using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;

namespace Moonfin.LiveTv
{
    internal sealed class ChannelGroupResult
    {
        [JsonPropertyName("version")] public int Version => 1;
        [JsonPropertyName("supported")] public bool Supported => Groups.Count != 0;
        [JsonPropertyName("partial")] public bool Partial { get; set; }
        [JsonPropertyName("groupsByChannelId")]
        public Dictionary<string, string[]> Groups { get; } = new Dictionary<string, string[]>(StringComparer.Ordinal);
        [JsonPropertyName("programmeCategories"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string[]? ProgrammeCategories { get; private set; }
        [JsonPropertyName("guideRevision")] public string GuideRevision { get; private set; } = "pending";

        public static string GetGuideRevision(ITaskManager tasks)
        {
            var refresh = tasks.ScheduledTasks.FirstOrDefault(t => t.ScheduledTask.Key == "RefreshGuide");
            var last = refresh?.LastExecutionResult;
            if (refresh == null || refresh.State != TaskState.Idle ||
                (last != null && last.Status != TaskCompletionStatus.Completed)) return "pending";
            return last == null ? "0" : last.EndTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture);
        }

        public void ReadProgrammeCategories(ITaskManager tasks, string revision, Func<string[]> read)
        {
            // The revision was sampled before reading the tuners and visible channels.
            if (revision == "pending" || GetGuideRevision(tasks) != revision)
            {
                Partial = true;
                return;
            }

            var categories = read();
            if (GetGuideRevision(tasks) != revision)
            {
                Partial = true;
                return;
            }
            ProgrammeCategories = categories;
            GuideRevision = revision;
        }
    }

    // Only tuner metadata is cached. Each response joins against a fresh, user-scoped lineup.
    internal sealed class ChannelGroupSnapshot
    {
        private const int MaxChannels = 100_000;
        private const int MaxGroupsPerChannel = 256;
        private readonly Dictionary<string, (int Adapter, string? HostId, string ExternalId, HashSet<string> Groups)> _channels =
            new Dictionary<string, (int, string?, string, HashSet<string>)>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _ambiguous = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public bool Partial { get; set; }

        public void Add(int adapter, string? hostId, string? externalId, IEnumerable<string>? labels)
        {
            if (string.IsNullOrEmpty(externalId) || _ambiguous.Contains(externalId)) return;
            if (!_channels.TryGetValue(externalId, out var source))
            {
                if (_channels.Count + _ambiguous.Count >= MaxChannels) { Partial = true; return; }
                source = (adapter, hostId, externalId, new HashSet<string>(StringComparer.Ordinal));
                _channels.Add(externalId, source);
            }
            else if (source.Adapter != adapter || source.HostId != hostId || source.ExternalId != externalId)
            {
                _channels.Remove(externalId);
                _ambiguous.Add(externalId);
                Partial = true;
                return;
            }

            if (labels == null) return;
            foreach (var label in labels)
            {
                if (string.IsNullOrWhiteSpace(label)) continue;
                var name = label.Trim();
                if (source.Groups.Count == MaxGroupsPerChannel && !source.Groups.Contains(name))
                {
                    Partial = true;
                    continue;
                }
                source.Groups.Add(name);
            }
        }

        public ChannelGroupResult ForVisibleChannels(IEnumerable<(string ClientId, string ExternalId)> channels, bool expectTunerChannel = false)
        {
            var result = new ChannelGroupResult { Partial = Partial };
            foreach (var channel in channels)
            {
                if (string.IsNullOrEmpty(channel.ExternalId) ||
                    !_channels.TryGetValue(channel.ExternalId, out var source) || source.ExternalId != channel.ExternalId)
                {
                    // Jellyfin can return an empty tuner list when the playlist is offline.
                    if (expectTunerChannel) result.Partial = true;
                    continue;
                }
                if (source.Groups.Count > 0)
                    result.Groups[channel.ClientId] = source.Groups.OrderBy(x => x, StringComparer.Ordinal).ToArray();
            }
            return result;
        }
    }

    internal sealed class ChannelGroupCache
    {
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1);
        private ChannelGroupSnapshot? _snapshot;
        private DateTime _expires;
        private string? _revision;

        public void Invalidate(ChannelGroupSnapshot snapshot) =>
            Interlocked.CompareExchange(ref _snapshot, null, snapshot);

        public async Task<ChannelGroupSnapshot> GetAsync(
            string revision, Func<CancellationToken, Task<ChannelGroupSnapshot>> read, CancellationToken token)
        {
            await _gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var cached = _snapshot;
                if (cached != null && revision == _revision && DateTime.UtcNow < _expires) return cached;
                var fresh = await read(token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                _revision = revision;
                _snapshot = revision == "pending" || fresh.Partial ? null : fresh;
                _expires = DateTime.UtcNow.AddMinutes(2);
                return fresh;
            }
            finally { _gate.Release(); }
        }
    }
}
