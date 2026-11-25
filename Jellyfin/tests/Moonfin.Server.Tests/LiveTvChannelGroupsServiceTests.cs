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
    [InlineData(true)]
    [InlineData(false)]
    public async Task MixedTunersUseOnlyCacheFilesUpdatedByTheNewImport(bool updated)
    {
        var cache = Directory.CreateTempSubdirectory();
        try {
            var path = Path.Combine(cache.FullName, "first_channels");
            void WriteM3u(string group) => File.WriteAllText(path,
                System.Text.Json.JsonSerializer.Serialize(new[] { new { Id = "m3u_news", ChannelGroup = group } }));
            WriteM3u("News");
            var start = DateTime.UtcNow.AddHours(-1);
            File.SetLastWriteTimeUtc(path, start.AddMinutes(1));
            var finished = new TaskResult { StartTimeUtc = start, EndTimeUtc = start.AddMinutes(5), Status = TaskCompletionStatus.Completed };
            var task = RecordingHostProxy.Create<IScheduledTask>((_, _) => "RefreshGuide");
            var worker = RecordingHostProxy.Create<IScheduledTaskWorker>((method, _) => method.Name switch {
                "get_ScheduledTask" => task, "get_State" => TaskState.Idle,
                "get_LastExecutionResult" => finished, _ => throw new NotSupportedException(method.Name)
            });
            var tasks = RecordingHostProxy.Create<ITaskManager>((_, _) => new[] { worker });
            var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<List<ChannelInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var tuner = RecordingHostProxy.Create<ITunerHost>((method, _) => {
                if (method.Name == "get_Type") return "xtream-library";
                waiting.TrySetResult();
                return release.Task;
            });
            var m3u = new LiveTvChannel { Id = Guid.NewGuid(), ExternalId = "m3u_news", ServiceName = "Emby" };
            var native = new LiveTvChannel { Id = Guid.NewGuid(), ExternalId = "xtream_42", ServiceName = "Emby" };
            var service = CreateGroupService(cache.FullName, [tuner], _ => [m3u, native], tasks);
            var viewer = Guid.NewGuid();
            var pending = service.GetAsync(viewer, CancellationToken.None);
            await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
            finished.StartTimeUtc = finished.EndTimeUtc;
            finished.EndTimeUtc = start.AddMinutes(10);
            if (updated) {
                WriteM3u("Sports");
                File.SetLastWriteTimeUtc(path, start.AddMinutes(6));
            }
            release.SetResult([new() { Id = "xtream_42", TunerHostId = "native", ChannelGroup = "Radio" }]);
            var first = await pending;
            var second = await service.GetAsync(viewer, CancellationToken.None);
            Assert.True(first.Partial);
            Assert.Empty(first.Groups);
            Assert.Equal(2, first.Channels.Length);
            Assert.Contains(m3u.Id.ToString("N"), first.UnresolvedChannelIds!);
            Assert.Contains(native.Id.ToString("N"), first.UnresolvedChannelIds!);
            Assert.Equal("Radio", Assert.Single(second.Groups[native.Id.ToString("N")]));
            Assert.Equal(!updated, second.Partial);
            if (!updated) {
                Assert.Equal(m3u.Id.ToString("N"), Assert.Single(second.UnresolvedChannelIds!));
                Assert.DoesNotContain(m3u.Id.ToString("N"), second.Groups.Keys);
                WriteM3u("Sports");
                second = await service.GetAsync(viewer, CancellationToken.None);
                Assert.False(second.Partial);
            }
            Assert.Equal("Sports", Assert.Single(second.Groups[m3u.Id.ToString("N")]));
        } finally { cache.Delete(true); }
    }

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
                    ? Task.FromResult(new List<ChannelInfo> { new() { Id = "xtream_42", TunerHostId = "native", ChannelGroup = "Radio" } })
                    : pending.Task;
            });
            var m3u = new LiveTvChannel { Id = Guid.NewGuid(), ExternalId = "m3u_news", ServiceName = "Emby" };
            var native = new LiveTvChannel { Id = Guid.NewGuid(), ExternalId = "xtream_42", ServiceName = "Emby" };
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
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OverlappingViewersShareGroupsOnlyWithinOneGuideImport(bool refresh)
    {
        var finished = new TaskResult { EndTimeUtc = DateTime.UtcNow };
        var task = RecordingHostProxy.Create<IScheduledTask>((_, _) => "RefreshGuide");
        var worker = RecordingHostProxy.Create<IScheduledTaskWorker>((method, _) => method.Name switch {
            "get_ScheduledTask" => task, "get_State" => TaskState.Idle,
            "get_LastExecutionResult" => finished, _ => throw new NotSupportedException(method.Name)
        });
        var tasks = RecordingHostProxy.Create<ITaskManager>((_, _) => new[] { worker });
        var pending = new TaskCompletionSource<List<ChannelInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        ChannelInfo Station(string label) => new() { Id = "xtream_42", TunerHostId = "native", ChannelGroup = label };
        var tuner = RecordingHostProxy.Create<ITunerHost>((method, _) => {
            if (method.Name == "get_Type") return "xtream-library";
            Assert.Equal("GetChannels", method.Name);
            return ++calls == 1 ? pending.Task : Task.FromResult(new List<ChannelInfo> { Station("New") });
        });
        var channel = new LiveTvChannel { Id = Guid.NewGuid(), ExternalId = "xtream_42", ServiceName = "Emby" };
        var service = CreateGroupService("", [tuner], _ => [channel], tasks);
        var first = service.GetAsync(Guid.NewGuid(), CancellationToken.None);
        Assert.Equal(1, calls);
        if (refresh) finished.EndTimeUtc = finished.EndTimeUtc.AddSeconds(1);
        var second = service.GetAsync(Guid.NewGuid(), CancellationToken.None);
        pending.SetResult([Station("Old")]);
        var before = await first;
        var after = await second;
        if (refresh) {
            Assert.Empty(before.Groups);
            Assert.Equal(channel.Id.ToString("N"), Assert.Single(before.UnresolvedChannelIds!));
        }
        Assert.Equal(refresh ? "New" : "Old", Assert.Single(after.Groups[channel.Id.ToString("N")]));
        Assert.Equal(refresh ? 2 : 1, calls);
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
