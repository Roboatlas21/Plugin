using System;
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
        public static async Task<List<ChannelInfo>> GetChannelsAsync(ITunerHost tuner, TunerHostInfo host, CancellationToken token)
        {
            var imported = await tuner.GetChannels(host, token).ConfigureAwait(false);
            if (imported.Count == 0 || tuner.Type != "m3u" || string.IsNullOrWhiteSpace(host.ProviderOptions))
                return imported;

            var options = JsonNode.Parse(host.ProviderOptions, new JsonNodeOptions { PropertyNameCaseInsensitive = true });
            var additional = options?["AdditionalTags"]?.Deserialize<string[]>();
            if (additional == null || additional.Length == 0) return imported;

            var playlistOptions = options!.AsObject();
            playlistOptions.Remove("AdditionalTags");
            var required = playlistOptions["RequiredTags"]?.Deserialize<string[]>();
            // Emby accepts a channel matching ANY required tag. A tuner-wide match admitted every entry.
            if (required != null && required.Intersect(additional, StringComparer.OrdinalIgnoreCase).Any())
                playlistOptions.Remove("RequiredTags");

            var playlistHost = JsonSerializer.Deserialize<TunerHostInfo>(JsonSerializer.Serialize(host))!;
            playlistHost.ProviderOptions = playlistOptions.ToJsonString();

            // GetChannels reads the merged tag cache. RefreshChannels would overwrite that cache.
            // Call the built-in tuner's read-only parser so Emby also handles channel IDs and playlist syntax.
            var readPlaylist = tuner.GetType().GetMethod("GetChannelsInternal", BindingFlags.Instance | BindingFlags.NonPublic,
                null, new[] { typeof(TunerHostInfo), typeof(CancellationToken) }, null)
                ?? throw new MissingMethodException(tuner.GetType().Name, "GetChannelsInternal");
            var read = (Func<TunerHostInfo, CancellationToken, Task<List<ChannelInfo>>>)readPlaylist.CreateDelegate(
                typeof(Func<TunerHostInfo, CancellationToken, Task<List<ChannelInfo>>>), tuner);
            var channels = await read(playlistHost, token).ConfigureAwait(false);
            var importedIds = new HashSet<string>(imported.Select(c => c.Id), StringComparer.Ordinal);
            return channels.Where(c => importedIds.Contains(c.Id)).ToList();
        }
    }
}
