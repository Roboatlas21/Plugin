using Moonfin.LiveTv;
using Xunit;

namespace Moonfin.Tests
{
    public class ChannelGroupTests
    {
        [Fact]
        public void KnownConflictRequiresAnExactSourceMatchToClear()
        {
            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "same" };
            var channels = new[] { ("c1", "same"), ("c2", "missing") };
            ChannelGroupResult Read(string? externalId)
            {
                var snapshot = new ChannelGroupSnapshot(new[] { "same", "missing" });
                snapshot.Add(1, "B", externalId, new[] { "News" });
                snapshot.UpdateConflicts(known);
                var result = new ChannelGroupResult();
                snapshot.AddVisibleChannelsTo(result, channels);
                return result;
            }

            Assert.Equal(new[] { "c2" }, Read(null).UnresolvedChannelIds);
            Assert.Equal(new[] { "c2" }, Read("SAME").UnresolvedChannelIds);
            Assert.Equal("News", Assert.Single(Read("same").Groups["c1"]));
            Assert.Empty(known);
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
            var visible = new ChannelGroupResult();
            snapshot.AddVisibleChannelsTo(visible, new[] { ("c1", "same"), ("c2", "other"), ("c3", "untagged") });
            Assert.True(visible.Partial);
            Assert.Equal("c2", Assert.Single(visible.Groups).Key);
            Assert.Empty(visible.UnresolvedChannelIds!);

            var mixed = new ChannelGroupResult();
            snapshot.AddVisibleChannelsTo(mixed, new[] { ("c1", "same"), ("c4", "offline") });
            Assert.Equal(new[] { "c4" }, mixed.UnresolvedChannelIds);
            Assert.Empty(mixed.Groups);
        }
    }
}
