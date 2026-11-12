using Jellyfin.Data.Entities;
using Jellyfin.Data.Enums;
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
