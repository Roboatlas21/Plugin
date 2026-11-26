using Jellyfin.Data.Entities;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Hosting;
using Moonfin.Server.Services;
using Xunit;
namespace Moonfin.Server.Tests;
public class LiveTvChannelGroupsServiceTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData(TaskCompletionStatus.Completed, null)]
    [InlineData(TaskCompletionStatus.Completed, true)]
    [InlineData(TaskCompletionStatus.Completed, false)]
    [InlineData(TaskCompletionStatus.Failed, true)]
    [InlineData(TaskCompletionStatus.Failed, false)]
    [InlineData(TaskCompletionStatus.Cancelled, true)]
    [InlineData(TaskCompletionStatus.Cancelled, false)]
    [InlineData(TaskCompletionStatus.Aborted, true)]
    [InlineData(TaskCompletionStatus.Aborted, false)]
    public async Task ConcurrentGuideReadsUseOnlyTheirOwnImport(TaskCompletionStatus? nextStatus, bool? cacheUpdated)
    {
        var cache = Directory.CreateTempSubdirectory();
        var gate = new TaskCompletionSource<List<ChannelInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        try {
            var start = DateTime.UtcNow.AddHours(-1);
            void WriteCache(string group, int minute) {
                var path = Path.Combine(cache.FullName, "first_channels");
                File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new[] { new { Id = "m3u_news", ChannelGroup = group } }));
                File.SetLastWriteTimeUtc(path, start.AddMinutes(minute));
            }
            WriteCache("News", 1);
            var import = new TaskResult { StartTimeUtc = start, EndTimeUtc = start.AddMinutes(5), Status = TaskCompletionStatus.Completed };
            var task = RecordingHostProxy.Create<IScheduledTask>((_, _) => "RefreshGuide");
            var worker = RecordingHostProxy.Create<IScheduledTaskWorker>((method, _) => method.Name switch {
                "get_ScheduledTask" => task, "get_State" => TaskState.Idle,
                "get_LastExecutionResult" => import, _ => throw new NotSupportedException(method.Name)
            });
            var calls = 0;
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<List<ChannelInfo>> ReadTuner() {
                var read = Interlocked.Increment(ref calls);
                started.TrySetResult();
                return read == 1 ? gate.Task : Task.FromResult(new List<ChannelInfo> { Station("New") });
            }
            var tuner = RecordingHostProxy.Create<ITunerHost>((method, _) => method.Name switch {
                "get_Type" => "xtream-library",
                "GetChannels" => ReadTuner(),
                _ => throw new NotSupportedException(method.Name)
            });
            var m3u = Channel("m3u_news");
            var native = Channel("xtream_42");
            var service = CreateGroupService(cacheUpdated.HasValue ? cache.FullName : "", [tuner],
                _ => cacheUpdated.HasValue ? [m3u, native] : [native],
                RecordingHostProxy.Create<ITaskManager>((_, _) => new[] { worker }));
            var viewer = Guid.NewGuid();
            var first = service.GetAsync(viewer, CancellationToken.None);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, calls);
            if (nextStatus.HasValue) {
                if (cacheUpdated.HasValue) import.StartTimeUtc = import.EndTimeUtc;
                import.EndTimeUtc = start.AddMinutes(10);
                import.Status = nextStatus.Value;
                if (cacheUpdated == true) WriteCache("Sports", 6);
            }
            var second = service.GetAsync(cacheUpdated.HasValue ? viewer : Guid.NewGuid(), CancellationToken.None);
            gate.SetResult([Station("Old")]);
            var before = await first;
            var after = await second;
            var m3uId = m3u.Id.ToString("N");
            var nativeId = native.Id.ToString("N");
            Assert.Equal(nextStatus.HasValue ? 2 : 1, calls);
            Assert.Equal(nextStatus.HasValue ? "New" : "Old", Assert.Single(after.Groups[nativeId]));
            if (nextStatus.HasValue) {
                Assert.True(before.Partial);
                Assert.Empty(before.Groups);
                var unresolved = cacheUpdated.HasValue ? new[] { m3uId, nativeId } : new[] { nativeId };
                Assert.Equal(unresolved.Length, before.Channels.Length);
                Assert.Equal(unresolved.Order(), before.UnresolvedChannelIds!.Order());
            }
            if (cacheUpdated is bool updated) {
                Assert.Equal(!updated, after.Partial);
                if (!updated) {
                    Assert.Equal(m3uId, Assert.Single(after.UnresolvedChannelIds!));
                    Assert.DoesNotContain(m3uId, after.Groups.Keys);
                    WriteCache("Sports", 7);
                    after = await service.GetAsync(viewer, CancellationToken.None);
                    Assert.False(after.Partial);
                }
                Assert.Equal("Sports", Assert.Single(after.Groups[m3uId]));
            }
        } finally {
            gate.TrySetResult([]);
            cache.Delete(true);
        }
    }

    private static LiveTvChannel Channel(string id) => new() { Id = Guid.NewGuid(), ExternalId = id, ServiceName = "Emby" };
    private static ChannelInfo Station(string group) => new() { Id = "xtream_42", TunerHostId = "native", ChannelGroup = group };

    [Fact]
    public async Task StalledXtreamLookupReturnsM3uGroupsAndDoesNotBlockOtherViewers()
    {
        var cache = Directory.CreateTempSubdirectory();
        var pending = new TaskCompletionSource<List<ChannelInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        try {
            File.WriteAllText(Path.Combine(cache.FullName, "first_channels"), """[{"Id":"m3u_news","ChannelGroup":"News"}]""");
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = 0;
            var recoveredTuner = false;
            var xtream = RecordingHostProxy.Create<ITunerHost>((method, _) => {
                if (method.Name == "get_Type") return "xtream-library";
                Assert.Equal("GetChannels", method.Name);
                Interlocked.Increment(ref calls);
                entered.TrySetResult();
                return recoveredTuner
                    ? Task.FromResult(new List<ChannelInfo> { Station("Radio") })
                    : pending.Task;
            });
            var m3u = Channel("m3u_news");
            var native = Channel("xtream_42");
            var mixedUser = Guid.NewGuid();
            var service = CreateGroupService(cache.FullName, [xtream], id => id == mixedUser ? [m3u, native] : [m3u]);
            var mixed = service.GetAsync(mixedUser, CancellationToken.None);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var other = service.GetAsync(mixedUser, CancellationToken.None);
            Assert.Equal("News", Assert.Single((await service.GetAsync(
                Guid.NewGuid(), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10))).Groups[m3u.Id.ToString("N")]));
            foreach (var partial in await Task.WhenAll(mixed, other).WaitAsync(TimeSpan.FromSeconds(10))) {
                Assert.True(partial.Partial);
                Assert.Equal("News", Assert.Single(partial.Groups[m3u.Id.ToString("N")]));
                Assert.Equal(native.Id.ToString("N"), Assert.Single(partial.UnresolvedChannelIds!));
            }
            var callsBeforeRecovery = calls;
            Assert.InRange(callsBeforeRecovery, 1, 2);
            // The first tuner request still has not completed, yet a fresh one must run.
            recoveredTuner = true;
            var recovered = await service.GetAsync(mixedUser, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(calls > callsBeforeRecovery);
            Assert.False(recovered.Partial);
            Assert.Equal("Radio", Assert.Single(recovered.Groups[native.Id.ToString("N")]));
        }
        finally { pending.TrySetResult([]); cache.Delete(true); }
    }
    private static LiveTvChannelGroupsService CreateGroupService(
        string cache, ITunerHost[] tuners, Func<Guid, BaseItem[]> channels, ITaskManager? tasks = null) => new(
            RecordingHostProxy.Create<ILiveTvManager>((_, args) => new QueryResult<BaseItem> {
                Items = channels(((LiveTvChannelQuery)args![0]!).UserId) }),
            RecordingHostProxy.Create<ITunerHostManager>((_, _) => tuners),
            RecordingHostProxy.Create<IServerConfigurationManager>((method, _) => method.Name switch {
                "get_ApplicationPaths" => RecordingHostProxy.Create<IServerApplicationPaths>((_, _) => cache),
                "GetConfiguration" => new LiveTvOptions { TunerHosts = [
                    new() { Id = "first", Type = "m3u" }, new() { Id = "native", Type = "xtream-library" }] },
                _ => throw new NotSupportedException(method.Name)
            }),
            RecordingHostProxy.Create<ILibraryManager>((_, _) => new QueryResult<BaseItem>()),
            RecordingHostProxy.Create<IUserManager>((_, _) => new User("viewer", "auth", "reset")),
            tasks ?? RecordingHostProxy.Create<ITaskManager>((_, _) => Array.Empty<IScheduledTaskWorker>()),
            new NoOpLogger<LiveTvChannelGroupsService>(),
            RecordingHostProxy.Create<IHostApplicationLifetime>((_, _) => CancellationToken.None),
            RecordingHostProxy.Create<IDtoService>((_, args) =>
                ((IReadOnlyList<BaseItem>)args![0]!).Select(item => new BaseItemDto {
                    Id = item.Id, Name = item.ExternalId }).ToArray()));
}
