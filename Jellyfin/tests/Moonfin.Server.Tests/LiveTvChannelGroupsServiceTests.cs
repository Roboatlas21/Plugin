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
            File.WriteAllText(lineup, """[{"Id":"sports","ChannelGroup":"Sports"},{"Id":"hidden","ChannelGroup":"Hidden"}]""");
            if (corruptLineup)
                File.WriteAllText(Path.Combine(cache.FullName, "second_channels"), """[{"Id":"news","ChannelGroup":"Incomplete"},""");

            var paths = RecordingHostProxy.Create<IServerApplicationPaths>((_, _) => cache.FullName);
            var options = new LiveTvOptions { TunerHosts = [
                new() { Id = "first", Type = "m3u" }, new() { Id = "second", Type = "m3u" },
                new() { Id = "custom", Type = "plugin" }] };
            var config = RecordingHostProxy.Create<IServerConfigurationManager>((method, _) => method.Name switch
            {
                "get_ApplicationPaths" => paths,
                "GetConfiguration" => options,
                _ => throw new NotSupportedException(method.Name)
            });
            var playlistCalls = 0;
            var m3u = RecordingHostProxy.Create<ITunerHost>((method, _) =>
            {
                if (method.Name == "get_Type") return "m3u";
                playlistCalls++;
                throw new InvalidOperationException("Playlist is offline");
            });
            var plugin = RecordingHostProxy.Create<ITunerHost>((method, _) => method.Name switch
            {
                "get_Type" => "plugin",
                "GetChannels" => Task.FromResult(new List<ChannelInfo> { new() { TunerHostId = "custom", Id = "radio", ChannelGroup = "Radio" } }),
                _ => throw new NotSupportedException(method.Name)
            });
            var tuners = RecordingHostProxy.Create<ITunerHostManager>((_, _) => new[] { m3u, plugin });
            var visible = new[] { "sports", "news", "radio" }.Select(id =>
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

            File.WriteAllText(lineup, """[{"Id":"sports","ChannelGroup":"Hockey"}]""");
            var reload = await service.GetAsync(user.Id, CancellationToken.None);
            Assert.Equal("Hockey", Assert.Single(reload.Groups[visible[0].Id.ToString("N")]));
            Assert.Equal(0, playlistCalls);
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
            return new QueryResult<BaseItem> { Items = Assert.Single(query.Tags) == tagWithListings ? [new LiveTvProgram()] : [] };
        });
        var task = RecordingHostProxy.Create<IScheduledTask>((_, _) => "RefreshGuide");
        var state = TaskState.Idle;
        var worker = RecordingHostProxy.Create<IScheduledTaskWorker>((method, _) => method.Name switch
        {
            "get_ScheduledTask" => task,
            "get_State" => state,
            "get_LastExecutionResult" => null,
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
        state = TaskState.Running;
        Assert.Null((await service.GetAsync(user.Id, CancellationToken.None)).ProgrammeCategories);
        Assert.Equal(18, reads);
    }
}
