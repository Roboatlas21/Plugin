using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.LiveTv;

namespace Emby.Plugins.Moonfin.Api
{
    internal static class EmbyTunerGroupReader
    {
        // A parser that ignores cancellation must finish before this source can be opened again.
        private static readonly ConcurrentDictionary<(ITunerHost, string), byte> Reading = new();

        public static async Task<List<ChannelInfo>> GetChannelsAsync(ITunerHost tuner, TunerHostInfo host,
            CancellationToken token, CancellationToken playlistDeadline, Func<string, bool> isVisible, Action<string> unresolved)
        {
            var imported = await tuner.GetChannels(host, token).ConfigureAwait(false);
            if (imported.Count == 0 || tuner.Type != "m3u" || string.IsNullOrWhiteSpace(host.ProviderOptions))
                return imported;

            var options = JsonNode.Parse(host.ProviderOptions, new JsonNodeOptions { PropertyNameCaseInsensitive = true });
            var additional = options?["AdditionalTags"]?.Deserialize<string[]>();
            if (additional == null || additional.Length == 0) return imported;

            var comparer = StringComparer.OrdinalIgnoreCase;
            var tunerTags = new HashSet<string>(additional, comparer);
            var playlist = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            if (!playlistDeadline.IsCancellationRequested && imported.Any(c =>
                isVisible(c.Id) && (c.Tags?.Any(tunerTags.Contains) ?? false)))
            {
                var key = (tuner, host.Id ?? host.Url ?? string.Empty);
                if (Reading.TryAdd(key, 0))
                {
                    var pending = Task.Run(() => ReadPlaylistAsync(key, host, options!.AsObject(), tunerTags, playlistDeadline));
                    var expired = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    using (playlistDeadline.Register(() => expired.TrySetResult(true)))
                    {
                        if (await Task.WhenAny(pending, expired.Task).ConfigureAwait(false) == pending)
                        {
                            foreach (var entry in await pending.ConfigureAwait(false))
                            {
                                if (!playlist.TryGetValue(entry.Id, out var labels)) playlist[entry.Id] = labels = new List<string>();
                                labels.AddRange(entry.Tags ?? Array.Empty<string>());
                            }
                        }
                    }
                }
            }
            token.ThrowIfCancellationRequested();

            var result = new List<ChannelInfo>(imported.Count);
            foreach (var channel in imported)
            {
                var tags = channel.Tags ?? Array.Empty<string>();
                var groups = tags.Where(tag => !tunerTags.Contains(tag)).ToList();
                if (playlist.TryGetValue(channel.Id, out var labels))
                    groups.AddRange(labels.Where(label => tunerTags.Contains(label) && tags.Contains(label, comparer)));
                else if (groups.Count != tags.Length)
                    unresolved(channel.Id);
                result.Add(new ChannelInfo { Id = channel.Id, Tags = groups.ToArray() });
            }
            return result;
        }

        private static async Task<List<ChannelInfo>> ReadPlaylistAsync((ITunerHost Tuner, string Id) key,
            TunerHostInfo host, JsonObject options, HashSet<string> tunerTags, CancellationToken token)
        {
            try
            {
                token.ThrowIfCancellationRequested();
                options.Remove("AdditionalTags");
                // A tuner-wide match admitted every entry even if other tags were required.
                var required = options["RequiredTags"]?.Deserialize<string[]>();
                if (required?.Any(tunerTags.Contains) == true) options.Remove("RequiredTags");
                var playlistHost = JsonSerializer.Deserialize<TunerHostInfo>(JsonSerializer.Serialize(host))!;
                playlistHost.ProviderOptions = options.ToJsonString();

                // Emby's read-only parser preserves native channel IDs without replacing the imported cache.
                var tuner = key.Tuner;
                var parser = tuner.GetType().GetMethod("GetChannelsInternal", BindingFlags.Instance | BindingFlags.NonPublic,
                    null, new[] { typeof(TunerHostInfo), typeof(CancellationToken) }, null)
                    ?? throw new MissingMethodException(tuner.GetType().Name, "GetChannelsInternal");
                var read = (Func<TunerHostInfo, CancellationToken, Task<List<ChannelInfo>>>)parser.CreateDelegate(
                    typeof(Func<TunerHostInfo, CancellationToken, Task<List<ChannelInfo>>>), tuner);
                return await read(playlistHost, token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Even after timeout, observe parser failures and retain unambiguous imported groups.
                return new List<ChannelInfo>();
            }
            finally { Reading.TryRemove(key, out _); }
        }
    }
}
