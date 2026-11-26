using System.Linq;
using Moonfin.LiveTv;
using Xunit;
#if MOONFIN_EMBY_TESTS
using SettingsReadError = MediaBrowser.Common.Extensions.ServiceUnavailableException;
using Emby.Plugins.Moonfin.Models;
using Emby.Plugins.Moonfin.Services;
namespace Emby.Plugins.Moonfin.Tests;
#else
using SettingsReadError = Moonfin.Server.Services.UnreadableSettingsException;
using Moonfin.Server.Models;
using Moonfin.Server.Services;
namespace Moonfin.Server.Tests;
#endif

// One contract, compiled against Jellyfin and Emby's independent settings services.
public sealed class SavedGuideFiltersContractTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "guide-pins-" + Guid.NewGuid().ToString("N"));
    private readonly MoonfinSettingsService _service;
    private readonly Guid _user = Guid.NewGuid();
    public SavedGuideFiltersContractTests()
    {
#if MOONFIN_EMBY_TESTS
        _service = new MoonfinSettingsService(AdminDefaultsMergeTests.NoOpLogger.Create(), _path);
#else
        _service = new MoonfinSettingsService(new NoOpLogger<MoonfinSettingsService>(), _path);
#endif
    }
    public void Dispose() { if (Directory.Exists(_path)) Directory.Delete(_path, true); }
    private Task<MoonfinUserSettings?> Read() => _service.GetUserSettingsAsync(_user);
    private Task Edit(string profile, string action, string? pin = null, params string[] filters) =>
        _service.SaveProfileAsync(_user, profile, new MoonfinSettingsProfile(), notifySettingsChanged: false,
            savedFilterChange: new LiveTvSavedFilterChange { Action = action, Filter = pin,
                Filters = action == "initializeIfAbsent" ? new List<string>(filters) : null });
    private static PluginConfiguration Defaults(params string[] pins) => new() {
        DefaultUserSettings = new MoonfinSettingsProfile { LiveTvSavedFilters = new List<string>(pins) } };

    private static LiveTvSavedFilterChange Change(string action, string pin) => new() { Action = action, Filter = pin };
    private List<string>? Pins(MoonfinUserSettings user, PluginConfiguration config) =>
        _service.ResolveProfile(user, "tv", config).LiveTvSavedFilters;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnUnreadableProfileKeepsItsPinsUntilTheEditCanBeRetried(bool backupOnly)
    {
        Assert.Null(await _service.GetResolvedProfileAsync(_user, "tv"));
        await Edit("tv", "add", "group:Sports");
        var primary = Path.Combine(_path, _user + ".json");
        var file = backupOnly ? AtomicFile.BackupPath(primary) : primary;
        if (backupOnly) File.Move(primary, file);
        using (File.Open(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await Assert.ThrowsAsync<SettingsReadError>(() => _service.GetResolvedProfileAsync(_user, "tv"));
            await Assert.ThrowsAsync<SettingsReadError>(() => Edit("tv", "add", "group:News"));
            await Assert.ThrowsAsync<SettingsReadError>(() => _service.SaveUserSettingsAsync(_user, new MoonfinUserSettings()));
            await Assert.ThrowsAsync<SettingsReadError>(() => _service.DeleteProfileAsync(_user, "tv"));
        }
        await Edit("tv", "add", "group:News");
        Assert.Equal(["group:Sports", "group:News"], (await Read())!.Global!.LiveTvSavedFilters);
    }

    [Fact]
    public async Task CorruptCopiesSurviveRejectedWrites()
    {
        Directory.CreateDirectory(_path);
        var primary = Path.Combine(_path, _user + ".json");
        var backup = AtomicFile.BackupPath(primary);
        File.WriteAllText(primary, "{bad primary");
        File.WriteAllText(backup, "{bad backup");
        var error = await Assert.ThrowsAsync<SettingsReadError>(() => _service.SaveUserSettingsAsync(_user, new()));
        await Assert.ThrowsAsync<SettingsReadError>(() => Edit("tv", "add", "group:News"));
        await Assert.ThrowsAsync<SettingsReadError>(() => _service.DeleteProfileAsync(_user, "tv"));
        Assert.DoesNotContain(primary, error.Message);
        Assert.Equal("{bad primary", File.ReadAllText(primary));
        Assert.Equal("{bad backup", File.ReadAllText(backup));
    }

    [Fact]
    public void ValidationAndLocalImportRespectCurrentButNotFutureDefaults()
    {
        Assert.True(Change("add", "group:News").IsValid());
        Assert.False(Change("remove", "group:").IsValid());
        Assert.False(new LiveTvSavedFilterChange { Action = "initializeIfAbsent",
            Filters = Enumerable.Repeat("group:News", 1001).ToList() }.IsValid());
        var atLimit = "group:" + new string('x', 1018);
        var tooLong = atLimit + "x";
        Assert.False(Change("add", tooLong).IsValid());
        Assert.False(Change("remove", tooLong).IsValid());
        Assert.False(new LiveTvSavedFilterChange { Action = "initializeIfAbsent", Filters = [tooLong] }.IsValid());
        var saved = new MoonfinUserSettings { Global = new MoonfinSettingsProfile { LiveTvSavedFilters = [tooLong] } };
        Assert.Equal([atLimit], Pins(saved, Defaults(atLimit, tooLong)));
        var config = Defaults("group:Sports");
        var user = new MoonfinUserSettings();
        MoonfinSettingsService.ApplySavedFilterChange(user, new() {
            Action = "initializeIfAbsent", Filters = ["group:Local"] }, config.DefaultUserSettings!.LiveTvSavedFilters);
        Assert.Equal(["group:Local"], Pins(user, config));
        config.DefaultUserSettings.LiveTvSavedFilters = ["group:News"];
        Assert.Equal(["group:News", "group:Local"], Pins(user, config));
        var remove = Change("remove", "group:Local");
        MoonfinSettingsService.ApplySavedFilterChange(user, remove, []);
        Assert.DoesNotContain("group:Local", user.LiveTvSavedFilterExclusions!);
        var managed = Defaults("group:Local");
        Assert.Equal(["group:Local"], Pins(user, managed));
        MoonfinSettingsService.ApplySavedFilterChange(user, remove, ["group:Local"]);
        Assert.Contains("group:Local", user.LiveTvSavedFilterExclusions!);
        Assert.Empty(Pins(user, managed)!);
    }

    [Fact]
    public async Task PinLimitCountsPersonalPinsAndExclusionsAndRejectsTheWholeSave()
    {
        var pins = Enumerable.Range(0, 1000).Select(i => $"group:{i}").ToArray();
        await Edit("tv", "initializeIfAbsent", null, pins);
        await Assert.ThrowsAsync<GuidePinLimitException>(() => _service.SaveProfileAsync(_user, "mobile",
            new MoonfinSettingsProfile { LiveTvCategoryDisplay = "groups" }, savedFilterChange:
            Change("add", "group:extra")));
        var stored = (await Read())!;
        Assert.Null(stored.Mobile?.LiveTvCategoryDisplay);
        Assert.Equal(pins, stored.Global!.LiveTvSavedFilters);
        var exclude = Change("remove", "group:managed");
        Assert.Throws<GuidePinLimitException>(() => MoonfinSettingsService.ApplySavedFilterChange(stored, exclude, ["group:managed"]));
        Assert.Empty(stored.LiveTvSavedFilterExclusions!);
        await Edit("tv", "remove", pins[0]);
        await Edit("tv", "add", "group:extra");
        Assert.Equal(1000, (await Read())!.Global!.LiveTvSavedFilters!.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnsetAndEmptyPinsAreDistinctInAdminAndUserProfiles(bool configured)
    {
        var config = configured ? Defaults() : new PluginConfiguration();
        var pins = configured ? new List<string>() : null;
        Assert.Equal(pins, MoonfinSettingsService.AdminDefaultsFor("tv", config)?.LiveTvSavedFilters);
        var user = new MoonfinUserSettings();
        Assert.Equal(pins, Pins(user, config));
        user.Global = new() { LiveTvSavedFilters = [] };
        Assert.Empty(Pins(user, config)!);
    }

    [Fact]
    public async Task PinAwarePullClearsStalePinsAfterAResetAndUnrelatedSave()
    {
        await Edit("tv", "add", "group:Sports");
        Assert.Equal(["group:Sports"], (await _service.GetProfileForPullAsync(_user, "tv", true))!.LiveTvSavedFilters);

        await _service.DeleteUserSettingsAsync(_user);
        Assert.Null(await _service.GetProfileForPullAsync(_user, "tv", true));
        await _service.SaveProfileAsync(_user, "global", new MoonfinSettingsProfile { LiveTvCategoryDisplay = "groups" });

        var pulled = await _service.GetProfileForPullAsync(_user, "tv", true);
        Assert.Equal("groups", pulled!.LiveTvCategoryDisplay);
        Assert.Empty(pulled.LiveTvSavedFilters!);
        Assert.Null((await _service.GetProfileForPullAsync(_user, "tv", false))!.LiveTvSavedFilters);
        Assert.Null((await Read())!.Global!.LiveTvSavedFilters);
    }

    [Fact]
    public async Task AtomicEditsOutliveStaleFullSettingsAndImportWrites()
    {
        await Edit("tv", "initializeIfAbsent", null, "group:US | SPORTS, Canada");
        await Edit("mobile", "add", "group:News");
        await Edit("tv", "remove", "group:Sports");
        await _service.SaveProfileAsync(_user, "mobile", new MoonfinSettingsProfile { LiveTvSavedFilters = [] }, notifySettingsChanged: false);
        await _service.SaveUserSettingsAsync(_user, new MoonfinUserSettings {
            Global = new MoonfinSettingsProfile { LiveTvSavedFilters = [] },
            LiveTvSavedFilterExclusions = ["group:News"] }, mergeMode: "replace");
        Assert.Equal(["group:US | SPORTS, Canada", "group:News"], (await Read())!.Global!.LiveTvSavedFilters);
        Assert.Empty((await Read())!.LiveTvSavedFilterExclusions!);
        await Edit("mobile", "remove", "group:News");
        await Edit("tv", "initializeIfAbsent", null, "group:Ignored");
        Assert.Equal(["group:US | SPORTS, Canada"], (await Read())!.Global!.LiveTvSavedFilters);
    }

    [Fact]
    public async Task GlobalAdminPinsMergeAndResetWithoutDeviceOnlyPins()
    {
        var config = Defaults("group:Sports", "group:News");
        config.DefaultDeviceSettings = new MoonfinDeviceDefaults {
            Tv = new MoonfinSettingsProfile { LiveTvSavedFilters = ["group:TV"], LiveTvCategoryDisplay = "programmes" } };
        config.DefaultUserSettings!.LiveTvCategoryDisplay = "groups";
        var viewer = new MoonfinUserSettings();
        Assert.Equal("programmes", _service.ResolveProfile(viewer, "tv", config).LiveTvCategoryDisplay);
        Assert.Equal("groups", _service.ResolveProfile(viewer, "mobile", config).LiveTvCategoryDisplay);
        var user = new MoonfinUserSettings {
            Global = new MoonfinSettingsProfile { LiveTvSavedFilters = ["group:Local"] },
            LiveTvSavedFilterExclusions = ["group:Sports"] };
        Directory.CreateDirectory(_path);
        File.WriteAllText(Path.Combine(_path, _user + ".json"), System.Text.Json.JsonSerializer.Serialize(user));
        Assert.Equal(["group:News", "group:Local"], Pins((await Read())!, config));
        await _service.MergeDefaultsAsync(config, ["tv"], ["liveTvSavedFilters"], [_user]);
        Assert.Equal(["group:News", "group:Local"], Pins((await Read())!, config));
        await _service.MergeDefaultsAsync(config, ["global"], ["liveTvSavedFilters"], [_user]);
        Assert.Equal(["group:Sports", "group:News", "group:Local"], Pins((await Read())!, config));
        await _service.ResetUsersToDefaultsAsync(config, ["global"], [_user], deleteOrphans: false);
        Assert.Empty((await Read())!.Global!.LiveTvSavedFilters!);
        Assert.Equal(["group:Sports", "group:News"], Pins((await Read())!, config));
    }

    [Fact]
    public async Task AdminCopyAndProfileDeletionPreserveSavedPins()
    {
        var to = Guid.NewGuid();
        await Edit("tv", "add", "group:News");
        await _service.SaveProfileAsync(to, "tv", new(), savedFilterChange: Change("add", "group:Old"));
        await _service.SaveProfileAsync(to, "tv", new MoonfinSettingsProfile { ClockBehavior = "never" }, notifySettingsChanged: false);

        Assert.Equal(1, await _service.CopySettingsAsync(_user, [to], ["global"], ["liveTvSavedFilters"], replace: false));
        Assert.Equal(["group:News"], (await _service.GetUserSettingsAsync(to))!.Global!.LiveTvSavedFilters);
        Assert.Equal("never", (await _service.GetUserSettingsAsync(to))!.Tv!.ClockBehavior);

        await _service.DeleteProfileAsync(to, "tv");
        Assert.Equal(["group:News"], (await _service.GetUserSettingsAsync(to))!.Global!.LiveTvSavedFilters);
        await _service.SaveProfileAsync(to, "mobile", new MoonfinSettingsProfile { LiveTvSavedFilters = ["group:Stale"] }, notifySettingsChanged: false);
        Assert.Equal(["group:News"], (await _service.GetUserSettingsAsync(to))!.Global!.LiveTvSavedFilters);
    }

}
