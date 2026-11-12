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
        // Only returned for a partial, stable lineup. Other visible IDs were resolved,
        // including channels that the tuner confirmed have no group.
        [JsonPropertyName("unresolvedChannelIds"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<string>? UnresolvedChannelIds { get; set; }
        [JsonPropertyName("programmeCategories"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string[]? ProgrammeCategories { get; private set; }
        [JsonPropertyName("guideRevision")] public string GuideRevision { get; private set; } = "pending";

        public static string GetGuideRevision(ITaskManager tasks)
        {
            var refresh = tasks.ScheduledTasks.FirstOrDefault(t => t.ScheduledTask.Key == "RefreshGuide");
            var last = refresh?.LastExecutionResult;
            if (refresh == null || refresh.State != TaskState.Idle) return "pending";
            return last == null ? "0" : last.EndTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture);
        }

        public void ReadProgrammeCategories(ITaskManager tasks, string revision, Func<string[]> read)
        {
            // The revision was sampled before reading the tuners and visible channels.
            if (revision == "pending" || GetGuideRevision(tasks) != revision)
            {
                Partial = true;
                UnresolvedChannelIds = null;
                return;
            }

            string[]? categories = null;
            try { categories = read(); }
            finally
            {
                if (GetGuideRevision(tasks) == revision)
                {
                    // A category query failure leaves this field unknown. The tuner data is still valid.
                    ProgrammeCategories = categories;
                    GuideRevision = revision;
                }
                else
                {
                    Partial = true;
                    UnresolvedChannelIds = null;
                }
            }
        }
    }

    // Only tuner metadata is cached. Each response joins against a fresh, user-scoped lineup.
    internal sealed class ChannelGroupSnapshot
    {
        private const int MaxGroupsPerChannel = 256;
        private readonly HashSet<string> _visibleIds;
        private readonly Dictionary<string, (int Adapter, string? HostId, string ExternalId, HashSet<string> Groups)> _channels =
            new Dictionary<string, (int, string?, string, HashSet<string>)>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _ambiguous = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _incompleteGroups = new HashSet<string>(StringComparer.Ordinal);
        public bool Partial { get; set; }

        public ChannelGroupSnapshot(IEnumerable<string> visibleIds) =>
            _visibleIds = new HashSet<string>(visibleIds, StringComparer.OrdinalIgnoreCase);

        public bool Covers(IEnumerable<string> visibleIds) => _visibleIds.IsSupersetOf(visibleIds);

        // Large authorized lineups still get a complete response without retaining them between requests.
        public bool Cacheable => !Partial && _visibleIds.Count <= 100_000;

        public void Add(int adapter, string? hostId, string? externalId, IEnumerable<string>? labels, bool incomplete = false)
        {
            if (string.IsNullOrEmpty(externalId) || !_visibleIds.Contains(externalId) || _ambiguous.Contains(externalId)) return;
            if (!_channels.TryGetValue(externalId, out var source))
            {
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

            if (incomplete) { _incompleteGroups.Add(externalId); Partial = true; }
            if (labels == null) return;
            foreach (var label in labels)
            {
                if (string.IsNullOrWhiteSpace(label)) continue;
                var name = label.Trim();
                if (source.Groups.Count == MaxGroupsPerChannel && !source.Groups.Contains(name))
                {
                    _incompleteGroups.Add(externalId);
                    Partial = true;
                    continue;
                }
                source.Groups.Add(name);
            }
        }

        public ChannelGroupResult ForVisibleChannels(IEnumerable<(string ClientId, string ExternalId)> channels)
        {
            var result = new ChannelGroupResult();
            foreach (var channel in channels)
            {
                if (string.IsNullOrEmpty(channel.ExternalId) ||
                    !_channels.TryGetValue(channel.ExternalId, out var source) || source.ExternalId != channel.ExternalId)
                {
                    // Either server can return an empty tuner list after losing its cache.
                    result.Partial = true;
                    result.UnresolvedChannelIds ??= new List<string>();
                    // A known collision has no safe group. Only a missing tuner
                    // answer may reuse this client's last known membership.
                    if (!_ambiguous.Contains(channel.ExternalId))
                        result.UnresolvedChannelIds.Add(channel.ClientId);
                    continue;
                }
                if (_incompleteGroups.Contains(channel.ExternalId))
                {
                    result.Partial = true;
                    (result.UnresolvedChannelIds ??= new List<string>()).Add(channel.ClientId);
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
        private int _epoch;
        private int _snapshotEpoch;

        public void Clear() => Interlocked.Increment(ref _epoch);

        public void Invalidate(ChannelGroupSnapshot snapshot) =>
            Interlocked.CompareExchange(ref _snapshot, null, snapshot);

        public async Task<ChannelGroupSnapshot> GetAsync(
            string revision, IEnumerable<string> visibleIds,
            Func<ChannelGroupSnapshot, CancellationToken, Task> read, CancellationToken token)
        {
            await _gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var epoch = Volatile.Read(ref _epoch);
                var cached = _snapshot;
                if (cached != null && epoch == _snapshotEpoch && revision == _revision &&
                    DateTime.UtcNow < _expires && cached.Covers(visibleIds)) return cached;
                var fresh = new ChannelGroupSnapshot(visibleIds);
                await read(fresh, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                _snapshotEpoch = epoch;
                _revision = revision;
                _snapshot = revision != "pending" && fresh.Cacheable ? fresh : null;
                _expires = DateTime.UtcNow.AddMinutes(2);
                return fresh;
            }
            finally { _gate.Release(); }
        }
    }
}
