using System;
using System.Collections.Generic;
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
        // Only returned for a partial lineup. Other visible IDs were resolved,
        // including channels that the tuner confirmed have no group.
        [JsonPropertyName("unresolvedChannelIds"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<string>? UnresolvedChannelIds { get; set; }

        [JsonPropertyName("programmeCategories"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string[]? ProgrammeCategories { get; private set; }

        // Sample only during this request. A guide import may replace both the channels and their listings.
        public static long? StableImportTime(ITaskManager tasks)
        {
            var import = tasks.ScheduledTasks.FirstOrDefault(t => t.ScheduledTask.Key == "RefreshGuide");
            var last = import?.LastExecutionResult;
            if (import != null && (import.State != TaskState.Idle || (last != null && last.Status != TaskCompletionStatus.Completed)))
                return null;
            return last?.EndTimeUtc.Ticks ?? 0;
        }

        public void ReadProgrammeCategories(ITaskManager tasks, long? importTime, Func<string[]> read)
        {
            if (!importTime.HasValue || StableImportTime(tasks) != importTime) return;
            var categories = read();
            if (StableImportTime(tasks) == importTime) ProgrammeCategories = categories;
        }
    }

    internal sealed class ChannelGroupSnapshot
    {
        private const int MaxGroupsPerChannel = 256;
        private readonly HashSet<string> _visibleIds;
        private readonly Dictionary<string, (int Adapter, string? HostId, string ExternalId, HashSet<string> Groups)> _channels =
            new Dictionary<string, (int, string?, string, HashSet<string>)>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _ambiguous = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _incompleteGroups = new HashSet<string>(StringComparer.Ordinal);
        public bool MissingTuner { get; set; }

        // Only an authoritative read can clear a previous conflict. A restricted
        // user can resolve only IDs present in their own lineup.
        public void UpdateConflicts(HashSet<string> known)
        {
            if (MissingTuner)
                _ambiguous.UnionWith(known.Where(_visibleIds.Contains));
            else
                known.ExceptWith(_visibleIds);
            known.UnionWith(_ambiguous);
        }

        public ChannelGroupSnapshot(IEnumerable<string> visibleIds) =>
            _visibleIds = new HashSet<string>(visibleIds, StringComparer.OrdinalIgnoreCase);

        public bool Covers(IEnumerable<string> visibleIds) => _visibleIds.IsSupersetOf(visibleIds);

        public bool CanShare => _visibleIds.Count <= 100_000;

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
                return;
            }

            if (incomplete) _incompleteGroups.Add(externalId);
            if (labels == null) return;
            foreach (var label in labels)
            {
                if (string.IsNullOrWhiteSpace(label)) continue;
                var name = label.Trim();
                if (source.Groups.Count == MaxGroupsPerChannel && !source.Groups.Contains(name))
                {
                    _incompleteGroups.Add(externalId);
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
                if (string.IsNullOrEmpty(channel.ExternalId) || _ambiguous.Contains(channel.ExternalId) ||
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

    // Each guide reload reads the current tuner metadata. Requests that overlap
    // can share a completed read when it covers their visible channels.
    internal sealed class ChannelGroupReader
    {
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1);
        private readonly HashSet<string> _conflicts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private ChannelGroupSnapshot? _lastRead;

        public async Task<ChannelGroupSnapshot> GetAsync(
            IEnumerable<string> visibleIds, Func<ChannelGroupSnapshot, CancellationToken, Task> read, CancellationToken token)
        {
            var previous = Volatile.Read(ref _lastRead);
            await _gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (_lastRead != null && _lastRead != previous && _lastRead.Covers(visibleIds))
                    return _lastRead;
                var current = new ChannelGroupSnapshot(visibleIds);
                await read(current, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                current.UpdateConflicts(_conflicts);
                Volatile.Write(ref _lastRead, current.CanShare ? current : null);
                return current;
            }
            finally { _gate.Release(); }
        }
    }
}
