using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Emby.Plugins.Moonfin.Api;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.LiveTv;
using Moonfin.LiveTv;
using Xunit;

namespace Emby.Plugins.Moonfin.Tests;

public class EmbyTunerGroupReaderTests
{
    [Fact]
    public void CombinedLineupUsesEmbyClientIdsAndIncludesUngroupedChannels()
    {
        var result = new ChannelGroupResult();
        result.Groups["17"] = new[] { "CA | Sports" };
        result.IncludeChannels(new[]
        {
            new BaseItemDto { Id = "17", Name = "Sports", ChannelNumber = "7", UserData = new() { IsFavorite = true } },
            new BaseItemDto { Id = "24", Name = "Radio" }
        }, item => item.Id);
        var wire = JsonSerializer.SerializeToElement(result, MoonfinJson.Options);
        var channels = wire.GetProperty("channels");
        Assert.Equal("17", channels[0].GetProperty("Id").GetString());
        Assert.True(channels[0].GetProperty("UserData").GetProperty("IsFavorite").GetBoolean());
        Assert.Equal("CA | Sports", channels[0].GetProperty("groups")[0].GetString());
        Assert.Empty(channels[1].GetProperty("groups").EnumerateArray());
    }

    [Theory]
    [InlineData("HOUSEHOLD", false)]
    [InlineData("Sports", true)]
    public async Task RereadsPlaylistGroupsWithoutChangingTheImportedLineup(string required, bool keepRequired)
    {
        var tuner = DispatchProxy.Create<ITunerHost, Tuner>();
        var probe = (Tuner)tuner;
        var host = new TunerHostInfo
        {
            Id = "test", DataVersion = 2, Url = "fixture.m3u", UserAgent = "fixture",
            ProviderOptions = $$"""{"additionalTags":["Household"],"RequiredTags":["{{required}}"],"ImportGuideData":true}"""
        };
        var original = host.ProviderOptions;
        probe.Imported.AddRange(new[] { Channel("1", "Sports", "Household"), Channel("2", "Household"), Channel("3", "Household") });
        probe.Playlist.AddRange(new[] { Channel("1", "Sports"), Channel("2", "Household"), Channel("3"), Channel("new", "News") });
        var channels = await EmbyTunerGroupReader.GetChannelsAsync(
            tuner, host, CancellationToken.None, CancellationToken.None, _ => true, _ => { });

        Assert.Equal(new[] { "1", "2", "3" }, channels.Select(c => c.Id));
        Assert.Equal(new[] { "Sports" }, channels[0].Tags);
        Assert.Equal(new[] { "Household" }, channels[1].Tags);
        Assert.Empty(channels[2].Tags);
        Assert.NotSame(host, probe.ReadHost);
        Assert.Equal((host.Id, host.Url, host.UserAgent, host.DataVersion),
            (probe.ReadHost!.Id, probe.ReadHost.Url, probe.ReadHost.UserAgent, probe.ReadHost.DataVersion));
        var options = JsonNode.Parse(probe.ReadHost.ProviderOptions)!;
        Assert.Null(options["additionalTags"]);
        Assert.Equal(keepRequired ? required : null, options["RequiredTags"]?[0]?.GetValue<string>());
        Assert.True(options["ImportGuideData"]!.GetValue<bool>());
        Assert.Equal(original, host.ProviderOptions);
        Assert.Equal(new[] { "Sports", "Household" }, probe.Imported[0].Tags);
    }

    [Theory]
    [InlineData("m3u", "{}")]
    [InlineData("m3u", """{"AdditionalTags":[]}""")]
    [InlineData("other", """{"AdditionalTags":["Household"]}""")]
    public async Task UsesImportedChannelsWithoutOpeningThePlaylist(string type, string options)
    {
        var tuner = DispatchProxy.Create<ITunerHost, Tuner>();
        var probe = (Tuner)tuner;
        probe.Type = type;
        probe.Imported.Add(Channel("1", "Sports"));
        var host = new TunerHostInfo { ProviderOptions = options };

        Assert.Same(probe.Imported, await EmbyTunerGroupReader.GetChannelsAsync(
            tuner, host, CancellationToken.None, CancellationToken.None, _ => true, _ => { }));
        Assert.Null(probe.ReadHost);
    }

    [Fact]
    public async Task OfflinePlaylistKeepsKnownGroupsWithoutPublishingTunerTags()
    {
        var tuner = DispatchProxy.Create<ITunerHost, Tuner>();
        var probe = (Tuner)tuner;
        probe.Offline = true;
        probe.Imported.Add(Channel("1", "Sports", "Household"));
        var unresolved = new List<string>();
        var host = new TunerHostInfo { ProviderOptions = """{"AdditionalTags":["Household"]}""" };
        var result = await EmbyTunerGroupReader.GetChannelsAsync(
            tuner, host, CancellationToken.None, CancellationToken.None, _ => true, unresolved.Add);
        Assert.Equal(new[] { "Sports" }, result.Single().Tags);
        Assert.Equal(new[] { "1" }, unresolved);
        Assert.Equal(new[] { "Sports", "Household" }, probe.Imported.Single().Tags);
    }

    [Fact]
    public async Task UnrelatedViewerSkipsPlaylistAndExpiredReadRetainsKnownGroups()
    {
        var tuner = DispatchProxy.Create<ITunerHost, Tuner>();
        var probe = (Tuner)tuner;
        probe.Imported.Add(Channel("slow", "Sports", "Household"));
        probe.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = new TunerHostInfo { Id = "slow-host", ProviderOptions = """{"AdditionalTags":["Household"]}""" };
        using var deadline = new CancellationTokenSource();
        var missing = new List<string>();
        var mixed = EmbyTunerGroupReader.GetChannelsAsync(tuner, host, CancellationToken.None,
            deadline.Token, _ => true, missing.Add);
        await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        try
        {
            var restricted = await EmbyTunerGroupReader.GetChannelsAsync(tuner, host, CancellationToken.None,
                CancellationToken.None, id => id == "healthy", _ => { });
            Assert.DoesNotContain("Household", restricted.Single().Tags);
            var other = EmbyTunerGroupReader.GetChannelsAsync(tuner, host, CancellationToken.None,
                CancellationToken.None, _ => true, _ => { });
            deadline.Cancel();
            var fallback = await mixed.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(new[] { "Sports" }, fallback.Single().Tags);
            Assert.Equal(new[] { "slow" }, missing);
            probe.Pending.SetResult(new() { Channel("slow", "Household") });
            Assert.Equal(new[] { "Sports", "Household" }, (await other).Single().Tags);
            Assert.Equal(1, probe.ReadCount);
        }
        finally { probe.Pending.TrySetResult(probe.Playlist); }
    }

    [Fact]
    public async Task StalledImportDoesNotBlockAnotherHostOrStartDuplicateReads()
    {
        var tuner = DispatchProxy.Create<ITunerHost, Tuner>();
        var probe = (Tuner)tuner;
        var slow = new TunerHostInfo { Id = "slow" };
        var healthy = new TunerHostInfo { Id = "healthy" };
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stalled = new TaskCompletionSource<List<ChannelInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var slowReads = 0;
        probe.ReadImported = host =>
        {
            if (host.Id == healthy.Id) return Task.FromResult(new List<ChannelInfo> { Channel("2", "News") });
            Interlocked.Increment(ref slowReads);
            started.TrySetResult(true);
            return stalled.Task;
        };
        Task<List<ChannelInfo>> Read(TunerHostInfo host, CancellationToken deadline) =>
            EmbyTunerGroupReader.GetChannelsAsync(tuner, host, CancellationToken.None, deadline, _ => true, _ => { });

        using var deadline = new CancellationTokenSource();
        try
        {
            var first = Read(slow, deadline.Token);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var otherViewer = Read(slow, CancellationToken.None);
            Assert.Equal("News", (await Read(healthy, deadline.Token)).Single().Tags.Single());
            deadline.Cancel();
            Assert.Empty(await first.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.False(otherViewer.IsCompleted);
            stalled.SetResult(new() { Channel("1", "Sports") });
            Assert.Equal("Sports", (await otherViewer.WaitAsync(TimeSpan.FromSeconds(2))).Single().Tags.Single());
            Assert.Equal(1, slowReads);
        }
        finally { stalled.TrySetResult(new()); }
    }

    [Fact]
    public async Task SkipsParserWhenOnlyOtherViewersHaveAmbiguousTags()
    {
        var tuner = DispatchProxy.Create<ITunerHost, Tuner>();
        var probe = (Tuner)tuner;
        probe.Imported.AddRange(new[] { Channel("healthy", "Sports"), Channel("slow", "Household") });
        var host = new TunerHostInfo { ProviderOptions = """{"AdditionalTags":["Household"]}""" };
        await EmbyTunerGroupReader.GetChannelsAsync(tuner, host, CancellationToken.None,
            CancellationToken.None, id => id == "healthy", _ => { });
        Assert.Equal(0, probe.ReadCount);
    }

    private static ChannelInfo Channel(string id, params string[] tags) => new() { Id = id, Tags = tags };

    public class Tuner : DispatchProxy
    {
        public TaskCompletionSource<List<ChannelInfo>>? Pending { get; set; }
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ReadCount { get; private set; }
        public bool Offline { get; set; }
        public string Type { get; set; } = "m3u";
        public List<ChannelInfo> Imported { get; } = new();
        public Func<TunerHostInfo, Task<List<ChannelInfo>>>? ReadImported { get; set; }
        public List<ChannelInfo> Playlist { get; } = new();
        public TunerHostInfo? ReadHost { get; private set; }

        protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch
        {
            "get_Type" => Type,
            nameof(ITunerHost.GetChannels) => ReadImported?.Invoke((TunerHostInfo)args![0]!) ?? Task.FromResult(Imported),
            _ => throw new NotSupportedException(method?.Name)
        };

        protected Task<List<ChannelInfo>> GetChannelsInternal(TunerHostInfo host, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (Offline) throw new FileNotFoundException();
            ReadHost = host;
            ReadCount++;
            Started.TrySetResult(true);
            return Pending?.Task ?? Task.FromResult(Playlist);
        }
    }
}
