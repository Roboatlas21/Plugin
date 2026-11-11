using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Moonfin.LiveTv;
using Xunit;

namespace Moonfin.Tests
{
    public class ChannelGroupTests
    {
        [Fact]
        public void VisibleIdsKeepOriginalNamesAndNeverIncludeOtherUsersChannels()
        {
            var snapshot = new ChannelGroupSnapshot();
            snapshot.Add(1, "tuner", "external", new[] { "US| SPORTS", "US|SPORTS", "FR | Kids", "音楽", "Kids", "Kids", " " });
            snapshot.Add(1, "tuner", "private", new[] { "Hidden" });
            var visible = snapshot.ForVisibleChannels(new[] { ("guide-id", "external") });
            Assert.True(visible.Supported);
            Assert.False(visible.Partial);
            Assert.Equal(new[] { "FR | Kids", "Kids", "US| SPORTS", "US|SPORTS", "音楽" }, Assert.Single(visible.Groups).Value);
            Assert.Equal("guide-id", visible.Groups.Single().Key);
            Assert.False(snapshot.ForVisibleChannels(Array.Empty<(string, string)>()).Supported);
        }

        [Fact]
        public void AmbiguousExternalIdRemainsUngroupedEvenWhenSeenAgain()
        {
            var snapshot = new ChannelGroupSnapshot();
            snapshot.Add(1, "A", "same", new[] { "Sport" });
            snapshot.Add(1, "B", "same", new[] { "News" });
            snapshot.Add(1, "A", "same", new[] { "Sport" });
            snapshot.Add(2, "B", "other", new[] { "Kids" });
            snapshot.Add(2, "B", "untagged", null);
            var visible = snapshot.ForVisibleChannels(new[] { ("c1", "same"), ("c2", "other"), ("c3", "untagged") });
            Assert.True(visible.Partial);
            Assert.Equal("c2", Assert.Single(visible.Groups).Key);
        }

        [Fact]
        public async Task CacheCoalescesReadersAndDoesNotKeepFailedReads()
        {
            var cache = new ChannelGroupCache();
            var callbacks = 0;
            var gate = new TaskCompletionSource<ChannelGroupSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<ChannelGroupSnapshot> Read(CancellationToken _) { callbacks++; return gate.Task; }
            await Assert.ThrowsAsync<InvalidOperationException>(() => cache.GetAsync(
                _ => throw new InvalidOperationException(), CancellationToken.None));
            var first = cache.GetAsync(Read, CancellationToken.None);
            var second = cache.GetAsync(Read, CancellationToken.None);
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.GetAsync(Read, cancelled.Token));
            gate.SetResult(new ChannelGroupSnapshot());
            Assert.Same(await first, await second);
            Assert.Equal(1, callbacks);
        }
    }
}
