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
            var snapshot = new ChannelGroupSnapshot(new[] { "external", "private" });
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
            var snapshot = new ChannelGroupSnapshot(new[] { "same", "other", "untagged" });
            snapshot.Add(1, "A", "same", new[] { "Sport" });
            snapshot.Add(1, "B", "same", new[] { "News" });
            snapshot.Add(1, "A", "same", new[] { "Sport" });
            snapshot.Add(2, "B", "other", new[] { "Kids" });
            snapshot.Add(2, "B", "untagged", null);
            var visible = snapshot.ForVisibleChannels(new[] { ("c1", "same"), ("c2", "other"), ("c3", "untagged") });
            Assert.True(visible.Partial);
            Assert.Equal("c2", Assert.Single(visible.Groups).Key);
            Assert.Empty(visible.UnresolvedChannelIds!);

            var mixed = snapshot.ForVisibleChannels(new[] { ("c1", "same"), ("c4", "offline") });
            Assert.Equal(new[] { "c4" }, mixed.UnresolvedChannelIds);
            Assert.Empty(mixed.Groups);
        }

        [Fact]
        public void MissingTunerChannelAndChannelWithNoGroupHaveDifferentResults()
        {
            var snapshot = new ChannelGroupSnapshot(new[] { "ungrouped" });
            snapshot.Add(0, "tuner", "ungrouped", null);
            var known = snapshot.ForVisibleChannels(new[] { ("id", "ungrouped") });
            Assert.False(known.Partial);
            Assert.Empty(known.Groups);
            var mixed = snapshot.ForVisibleChannels(new[] { ("healthy", "ungrouped"), ("lost", "offline") });
            Assert.True(mixed.Partial);
            Assert.Equal(new[] { "lost" }, mixed.UnresolvedChannelIds);
            Assert.Empty(mixed.Groups);
        }

        [Fact]
        public void OnlyUnresolvedVisibleChannelsMakeTheResponsePartial()
        {
            var snapshot = new ChannelGroupSnapshot(new[] { "healthy", "offline" }) { MissingTuner = true };
            snapshot.Add(1, "tuner", "healthy", new[] { "Kids" });
            snapshot.Add(2, "other", "offline", new[] { "Sports" }, incomplete: true);
            Assert.False(snapshot.ForVisibleChannels(new[] { ("1", "healthy") }).Partial);
            var mixed = snapshot.ForVisibleChannels(new[] { ("1", "healthy"), ("2", "offline") });
            Assert.Equal(new[] { "2" }, mixed.UnresolvedChannelIds);
            Assert.Equal(new[] { "Sports" }, mixed.Groups["2"]);
        }

        [Fact]
        public async Task ARestrictedUserCanSeeTheLastChannelOfALargeTuner()
        {
            var reader = new ChannelGroupReader();
            int reads = 0;
            Task Read(ChannelGroupSnapshot snapshot, CancellationToken token)
            {
                reads++;
                for (var i = 0; i <= 100_000; i++)
                    snapshot.Add(0, "tuner", $"c{i}", new[] { "Sports" });
                return Task.CompletedTask;
            }
            var token = CancellationToken.None;
            await reader.GetAsync(new[] { "c0" }, Read, token);
            var last = await reader.GetAsync(new[] { "c100000" }, Read, token);
            var result = last.ForVisibleChannels(new[] { ("visible", "c100000") });
            Assert.False(result.Partial);
            Assert.Equal(new[] { "Sports" }, result.Groups["visible"]);
            Assert.Equal(2, reads);
        }

        [Fact]
        public async Task ReloadReadsGroupEditsAndKeepsKnownConflictsDuringATunerOutage()
        {
            var reader = new ChannelGroupReader();
            var ids = new[] { "shared", "healthy" };
            var visible = new[] { ("1", "shared"), ("2", "healthy") };
            var otherOnline = true;
            var otherRemoved = false;
            var label = "Kids";
            Task Read(ChannelGroupSnapshot snapshot, CancellationToken token)
            {
                snapshot.Add(0, "A", "shared", new[] { "Sports" });
                snapshot.Add(0, "A", "healthy", new[] { label });
                if (otherOnline) snapshot.Add(1, "B", "shared", new[] { "News" });
                else if (!otherRemoved) snapshot.MissingTuner = true;
                return Task.CompletedTask;
            }
            async Task<ChannelGroupResult> Open() =>
                (await reader.GetAsync(ids, Read, CancellationToken.None)).ForVisibleChannels(visible);

            var first = await Open();
            Assert.False(first.Groups.ContainsKey("1"));
            Assert.Equal("Kids", first.Groups["2"].Single());
            otherOnline = false;
            label = "Movies";
            await reader.GetAsync(new[] { "healthy" }, Read, CancellationToken.None);
            var duringOutage = await Open();
            Assert.Empty(duringOutage.UnresolvedChannelIds!);
            Assert.False(duringOutage.Groups.ContainsKey("1"));
            Assert.Equal("Movies", duringOutage.Groups["2"].Single());
            otherRemoved = true;
            Assert.Equal("Sports", (await Open()).Groups["1"].Single());
        }

        [Fact]
        public async Task ConcurrentReadsShareWorkAndFailuresDoNotPersist()
        {
            var reader = new ChannelGroupReader();
            var callbacks = 0;
            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Task Read(ChannelGroupSnapshot snapshot, CancellationToken token) { callbacks++; return gate.Task; }
            await Assert.ThrowsAsync<InvalidOperationException>(() => reader.GetAsync(Array.Empty<string>(),
                (_, _) => throw new InvalidOperationException(), CancellationToken.None));
            var first = reader.GetAsync(Array.Empty<string>(), Read, CancellationToken.None);
            var second = reader.GetAsync(Array.Empty<string>(), Read, CancellationToken.None);
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.GetAsync(Array.Empty<string>(), Read, cancelled.Token));
            gate.SetResult(true);
            Assert.Same(await first, await second);
            Assert.Equal(1, callbacks);
        }
    }
}
