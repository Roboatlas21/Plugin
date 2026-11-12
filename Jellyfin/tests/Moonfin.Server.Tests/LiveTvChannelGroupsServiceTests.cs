using Jellyfin.Data.Entities;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Tasks;
using Moonfin.Server.Services;
using Xunit;

namespace Moonfin.Server.Tests;

public class LiveTvChannelGroupsServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GuideReadsImportedM3uGroupsEvenWhenAnotherLineupIsUnavailable(bool corruptLineup)
    {
        var cache = Directory.CreateTempSubdirectory();
        try
        {
            var lineup = Path.Combine(cache.FullName, "first_channels");
            File.WriteAllText(lineup, """[{"Id":"m3u_sports","ChannelGroup":"Sports"},{"Id":"m3u_hidden","ChannelGroup":"Hidden"}]""");
            if (corruptLineup)
                File.WriteAllText(Path.Combine(cache.FullName, "second_channels"), """[{"Id":"m3u_news","ChannelGroup":"Incomplete"},""");

            var paths = RecordingHostProxy.Create<IServerApplicationPaths>((_, _) => cache.FullName);
            var options = new LiveTvOptions { TunerHosts = [
                new() { Id = "first", Type = "m3u" }, new() { Id = "second", Type = "m3u" },
                new() { Id = "native", Type = "xtream-library" }] };
            var config = RecordingHostProxy.Create<IServerConfigurationManager>((method, _) => method.Name switch
            {
                "get_ApplicationPaths" => paths,
                "GetConfiguration" => options,
                _ => throw new NotSupportedException(method.Name)
            });
            var unrelatedCalls = 0;
            ITunerHost UnrelatedTuner(string type) => RecordingHostProxy.Create<ITunerHost>((method, _) =>
            {
                if (method.Name == "get_Type") return type;
                unrelatedCalls++;
                throw new InvalidOperationException("Tuner is offline");
            });
            var xtreamCalls = 0;
            var xtream = RecordingHostProxy.Create<ITunerHost>((method, args) =>
            {
                if (method.Name == "get_Type") return "xtream-library";
                Assert.Equal("GetChannels", method.Name);
                Assert.Equal(true, args![0]);
                xtreamCalls++;
                return Task.FromResult(new List<ChannelInfo> { new() { TunerHostId = "xtream-library", Id = "xtream_42", ChannelGroup = "Radio" } });
            });
            var tuners = RecordingHostProxy.Create<ITunerHostManager>((_, _) => new[] { UnrelatedTuner("m3u"), UnrelatedTuner("hdhomerun"), xtream });
            var visible = new[] { "m3u_sports", "m3u_news", "xtream_42", "hdhr_8" }.Select(id =>
                new LiveTvChannel { Id = Guid.NewGuid(), ExternalId = id, ServiceName = "Emby" }).ToArray();
            var liveTv = RecordingHostProxy.Create<ILiveTvManager>((_, _) => new QueryResult<BaseItem> { Items = visible });
            var user = new User("viewer", "auth", "reset");
            var users = RecordingHostProxy.Create<IUserManager>((_, _) => user);
            var library = RecordingHostProxy.Create<ILibraryManager>((_, _) => new QueryResult<BaseItem>());
            var tasks = RecordingHostProxy.Create<ITaskManager>((_, _) => Array.Empty<IScheduledTaskWorker>());
            var service = new LiveTvChannelGroupsService(liveTv, tuners, config, library, users, tasks,
                new NoOpLogger<LiveTvChannelGroupsService>());

            var result = await service.GetAsync(user.Id, CancellationToken.None);
            Assert.Equal(2, result.Groups.Count);
            Assert.Equal("Sports", Assert.Single(result.Groups[visible[0].Id.ToString("N")]));
            Assert.Equal("Radio", Assert.Single(result.Groups[visible[2].Id.ToString("N")]));
            Assert.Equal(visible[1].Id.ToString("N"), Assert.Single(result.UnresolvedChannelIds!));

            visible = visible.Where(c => c.ExternalId != "xtream_42").ToArray();
            File.WriteAllText(lineup, """[{"Id":"m3u_sports","ChannelGroup":"Hockey"}]""");
            var reload = await service.GetAsync(user.Id, CancellationToken.None);
            Assert.Equal("Hockey", Assert.Single(reload.Groups[visible[0].Id.ToString("N")]));
            Assert.Equal(1, xtreamCalls);
            Assert.Equal(0, unrelatedCalls);
        }
        finally
        {
            cache.Delete(true);
        }
    }

    [Fact]
    public async Task CategoriesUseTheViewersFullScheduleEvenWithoutTunerGroups()
    {
        var user = new User("viewer", "auth", "reset");
        var channel = new LiveTvChannel { Id = Guid.NewGuid(), ServiceName = "other" };
        var liveTv = RecordingHostProxy.Create<ILiveTvManager>((_, args) =>
        {
            Assert.Equal(user.Id, ((LiveTvChannelQuery)args![0]!).UserId);
            return new QueryResult<BaseItem> { Items = [channel] };
        });
        var users = RecordingHostProxy.Create<IUserManager>((_, args) =>
        {
            Assert.Equal(user.Id, args![0]);
            return user;
        });
        var reads = 0;
        var tagWithListings = "Sports";
        var lastImport = new TaskResult { Status = TaskCompletionStatus.Cancelled, EndTimeUtc = DateTime.UtcNow };
        var importFinishesDuringRead = false;
        var library = RecordingHostProxy.Create<ILibraryManager>((_, args) =>
        {
            var query = (InternalItemsQuery)args![0]!;
            Assert.Same(user, query.User);
            Assert.Equal(channel.Id, Assert.Single(query.ChannelIds));
            Assert.Equal(BaseItemKind.LiveTvProgram, Assert.Single(query.IncludeItemTypes));
            Assert.Null(query.MinStartDate);
            Assert.Null(query.MaxEndDate);
            Assert.Equal(1, query.Limit);
            reads++;
            if (importFinishesDuringRead && Assert.Single(query.Tags) == "Movie")
                lastImport.EndTimeUtc = lastImport.EndTimeUtc.AddSeconds(1);
            return new QueryResult<BaseItem> { Items = Assert.Single(query.Tags) == tagWithListings ? [new LiveTvProgram()] : [] };
        });
        var task = RecordingHostProxy.Create<IScheduledTask>((_, _) => "RefreshGuide");
        var state = TaskState.Idle;
        var worker = RecordingHostProxy.Create<IScheduledTaskWorker>((method, _) => method.Name switch
        {
            "get_ScheduledTask" => task,
            "get_State" => state,
            "get_LastExecutionResult" => lastImport,
            _ => throw new NotSupportedException(method.Name)
        });
        var tasks = RecordingHostProxy.Create<ITaskManager>((_, _) => new[] { worker });
        var service = new LiveTvChannelGroupsService(liveTv, null!, null!, library, users, tasks,
            new NoOpLogger<LiveTvChannelGroupsService>());

        var result = await service.GetAsync(user.Id, CancellationToken.None);
        Assert.Empty(result.Groups);
        Assert.Equal(new[] { "sports" }, result.ProgrammeCategories);
        tagWithListings = "News";
        Assert.Equal(new[] { "news" }, (await service.GetAsync(user.Id, CancellationToken.None)).ProgrammeCategories);
        tagWithListings = "";
        Assert.Empty((await service.GetAsync(user.Id, CancellationToken.None)).ProgrammeCategories!);
        importFinishesDuringRead = true;
        Assert.Null((await service.GetAsync(user.Id, CancellationToken.None)).ProgrammeCategories);
        state = TaskState.Running;
        Assert.Null((await service.GetAsync(user.Id, CancellationToken.None)).ProgrammeCategories);
        Assert.Equal(24, reads);
    }
}
