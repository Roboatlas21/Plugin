using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Moonfin.LiveTv;
using MediaBrowser.Model.Tasks;
using Xunit;

namespace Moonfin.Tests
{
    public class ChannelGroupTests
    {
        [Fact]
        public void OnlyAStableGuideSuppliesCategoriesAndARevision()
        {
            var task = TaskStub.Create<IScheduledTask>(new() { ["get_Key"] = "RefreshGuide" });
            var state = new Dictionary<string, object?> {
                ["get_ScheduledTask"] = task, ["get_State"] = TaskState.Idle, ["get_LastExecutionResult"] = null
            };
            var worker = TaskStub.Create<IScheduledTaskWorker>(state);
            var manager = TaskStub.Create<ITaskManager>(new() { ["get_ScheduledTasks"] = new[] { worker } });
            var empty = new ChannelGroupResult();
            empty.ReadProgrammeCategories(manager, ChannelGroupResult.GetGuideRevision(manager), () => Array.Empty<string>());
            Assert.Empty(empty.ProgrammeCategories!);
            Assert.Equal("0", empty.GuideRevision);

            state["get_State"] = TaskState.Running;
            var pending = new ChannelGroupResult();
            pending.ReadProgrammeCategories(manager, ChannelGroupResult.GetGuideRevision(manager), () => throw new Exception("queried an incomplete guide"));
            Assert.Null(pending.ProgrammeCategories);
            Assert.Equal("pending", pending.GuideRevision);

            state["get_State"] = TaskState.Idle;
            var completed = new TaskResult { EndTimeUtc = DateTime.UtcNow, Status = TaskCompletionStatus.Completed };
            state["get_LastExecutionResult"] = completed;
            var stable = new ChannelGroupResult();
            stable.ReadProgrammeCategories(manager, ChannelGroupResult.GetGuideRevision(manager), () => new[] { "movies" });
            Assert.Equal(new[] { "movies" }, stable.ProgrammeCategories);
            Assert.Equal(ChannelGroupResult.GetGuideRevision(manager), stable.GuideRevision);
            Assert.NotEqual(empty.GuideRevision, stable.GuideRevision);

            var overlapping = new ChannelGroupResult();
            overlapping.ReadProgrammeCategories(manager, ChannelGroupResult.GetGuideRevision(manager), () => {
                completed.EndTimeUtc = completed.EndTimeUtc.AddMinutes(1);
                return new[] { "sports" };
            });
            Assert.True(overlapping.Partial);
            Assert.Null(overlapping.ProgrammeCategories);
            Assert.Equal("pending", overlapping.GuideRevision);

            var oldTuners = new ChannelGroupResult();
            oldTuners.ReadProgrammeCategories(manager, stable.GuideRevision, () => throw new Exception("read after revision changed"));
            Assert.True(oldTuners.Partial);
            Assert.Equal("pending", oldTuners.GuideRevision);
        }

        public class TaskStub : DispatchProxy
        {
            private Dictionary<string, object?> _values = new();
            public static T Create<T>(Dictionary<string, object?> values) where T : class
            {
                var proxy = DispatchProxy.Create<T, TaskStub>();
                ((TaskStub)(object)proxy)._values = values;
                return proxy;
            }
            protected override object? Invoke(MethodInfo? method, object?[]? args) => _values[method!.Name];
        }

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
        public void MissingTunerChannelAndChannelWithNoGroupHaveDifferentResults()
        {
            var snapshot = new ChannelGroupSnapshot();
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
        public async Task NewRevisionAndIncompleteReadsBypassTheCachedSnapshot()
        {
            var cache = new ChannelGroupCache();
            var token = CancellationToken.None;
            Task<ChannelGroupSnapshot> Read(CancellationToken _) => Task.FromResult(new ChannelGroupSnapshot());
            var first = await cache.GetAsync("1", Read, token);
            var next = await cache.GetAsync("2", Read, token);
            Assert.NotSame(first, next);
            cache.Invalidate(first);
            Assert.Same(next, await cache.GetAsync("2", Read, token));
            cache.Invalidate(next);
            var partial = await cache.GetAsync("2", _ => Task.FromResult(new ChannelGroupSnapshot { Partial = true }), token);
            Assert.NotSame(partial, await cache.GetAsync("2", Read, token));
        }

        [Fact]
        public async Task CacheCoalescesReadersAndDoesNotKeepFailedReads()
        {
            var cache = new ChannelGroupCache();
            var callbacks = 0;
            var gate = new TaskCompletionSource<ChannelGroupSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<ChannelGroupSnapshot> Read(CancellationToken _) { callbacks++; return gate.Task; }
            await Assert.ThrowsAsync<InvalidOperationException>(() => cache.GetAsync("1",
                _ => throw new InvalidOperationException(), CancellationToken.None));
            var first = cache.GetAsync("1", Read, CancellationToken.None);
            var second = cache.GetAsync("1", Read, CancellationToken.None);
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.GetAsync("1", Read, cancelled.Token));
            gate.SetResult(new ChannelGroupSnapshot());
            Assert.Same(await first, await second);
            Assert.Equal(1, callbacks);
        }
    }
}
