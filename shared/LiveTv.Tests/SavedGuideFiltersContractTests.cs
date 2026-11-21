using System.Text.Json;
using System.Xml.Serialization;
using Xunit;
#if MOONFIN_EMBY_TESTS
using Emby.Plugins.Moonfin.Models;
using Emby.Plugins.Moonfin.Services;
namespace Emby.Plugins.Moonfin.Tests;
#else
using Moonfin.Server.Models;
using Moonfin.Server.Services;
namespace Moonfin.Server.Tests;
#endif

// Shared contract tests run independently against both Jellyfin and Emby.
public sealed class SavedGuideFiltersContractTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "guide-pins-" + Guid.NewGuid().ToString("N"));
    private readonly MoonfinSettingsService _service;

    public SavedGuideFiltersContractTests()
    {
#if MOONFIN_EMBY_TESTS
        _service = new MoonfinSettingsService(AdminDefaultsMergeTests.NoOpLogger.Create(), _path);
#else
        _service = new MoonfinSettingsService(new NoOpLogger<MoonfinSettingsService>(), _path);
#endif
    }

    public void Dispose() { if (Directory.Exists(_path)) Directory.Delete(_path, true); }
    private Task<MoonfinUserSettings?> Read(Guid id) => _service.GetUserSettingsAsync(id);
    private Task Edit(Guid id, string profile, string action, string? key = null, params string[] filters) =>
        _service.SaveProfileAsync(id, profile, new MoonfinSettingsProfile(), notifySettingsChanged: false,
            savedFilterChange: new LiveTvSavedFilterChange { Action = action, Filter = key,
                Filters = action == "initializeIfAbsent" ? new List<string>(filters) : null });
    private static PluginConfiguration Defaults(params string[] pins) => new()
    { DefaultUserSettings = new MoonfinSettingsProfile { LiveTvSavedFilters = new List<string>(pins) } };
    private static void Change(MoonfinUserSettings user, string action, string pin, List<string> defaults) =>
        MoonfinSettingsService.ApplySavedFilterChange(user,
            new LiveTvSavedFilterChange { Action = action, Filter = pin }, defaults);

    [Fact]
    public async Task AtomicEditsFromDifferentDevicesSurviveStaleProfileSaves()
    {
        var id = Guid.NewGuid();
        await Edit(id, "tv", "initializeIfAbsent", null, "group:US | SPORTS, Canada");
        await _service.SaveProfileAsync(id, "mobile", new MoonfinSettingsProfile { LiveTvSavedFilters = [] },
            notifySettingsChanged: false);
        Assert.Null((await Read(id))!.Mobile!.LiveTvSavedFilters);
        await Edit(id, "tv", "add", "group:News");
        await Edit(id, "mobile", "add", "group:Kids");
        Assert.Equal(["group:US | SPORTS, Canada", "group:News", "group:Kids"],
            _service.ResolveProfile((await Read(id))!, "mobile").LiveTvSavedFilters);
        await Edit(id, "tv", "remove", "group:News");
        Assert.Equal(["group:US | SPORTS, Canada", "group:Kids"], (await Read(id))!.Global!.LiveTvSavedFilters);
    }

    [Theory]
    [InlineData("merge")]
    [InlineData("replace")]
    public async Task WholeSettingsWritesDoNotReplaceManagedPinExclusions(string mode)
    {
        var id = Guid.NewGuid();
        await Edit(id, "tv", "remove", "group:Hockey");
        await _service.SaveUserSettingsAsync(id, new MoonfinUserSettings {
            Global = new MoonfinSettingsProfile { CinemaModeEnabled = true },
            LiveTvSavedFilterExclusions = ["group:News"] }, mergeMode: mode);
        var saved = (await Read(id))!;
        Assert.Equal(["group:Hockey"], saved.LiveTvSavedFilterExclusions);
        Assert.True(saved.Global!.CinemaModeEnabled);
        Assert.Equal(["group:News"], _service.ResolveProfile(saved, "tv", Defaults("group:Hockey", "group:News")).LiveTvSavedFilters);
    }

    [Fact]
    public void UsersCanRemoveEveryInheritedPinAndAddPersonalPins()
    {
        var user = new MoonfinUserSettings();
        var config = Defaults("group:Sports", "group:News");
        var defaults = config.DefaultUserSettings!.LiveTvSavedFilters!;
        Assert.Equal(defaults, _service.ResolveProfile(user, "tv", config).LiveTvSavedFilters);
        Change(user, "remove", "group:Sports", defaults);
        Assert.Equal(["group:News"], _service.ResolveProfile(user, "tv", config).LiveTvSavedFilters);
        Change(user, "remove", "group:News", defaults);
        Assert.Empty(_service.ResolveProfile(user, "tv", config).LiveTvSavedFilters!);
        Change(user, "add", "group:Kids", defaults);
        Assert.Equal(["group:Kids"], _service.ResolveProfile(user, "tv", config).LiveTvSavedFilters);
    }

    [Fact]
    public void UpdatedAdminDefaultsRespectExclusionsAndPersonalRePins()
    {
        var user = new MoonfinUserSettings();
        var config = Defaults("group:Sports", "group:News");
        Change(user, "add", "group:Formula 1", config.DefaultUserSettings!.LiveTvSavedFilters!);
        Change(user, "remove", "group:Sports", config.DefaultUserSettings.LiveTvSavedFilters!);
        config.DefaultUserSettings.LiveTvSavedFilters = ["group:News", "group:Hockey"];
        Assert.Equal(["group:News", "group:Hockey", "group:Formula 1"],
            _service.ResolveProfile(user, "tv", config).LiveTvSavedFilters);
        config.DefaultUserSettings.LiveTvSavedFilters = ["group:Sports", "group:Hockey"];
        Assert.Equal(["group:Hockey", "group:Formula 1"],
            _service.ResolveProfile(user, "mobile", config).LiveTvSavedFilters);
        Change(user, "add", "group:Sports", config.DefaultUserSettings.LiveTvSavedFilters!);
        config.DefaultUserSettings.LiveTvSavedFilters = ["group:Hockey"];
        Assert.Equal(["group:Hockey", "group:Formula 1", "group:Sports"],
            _service.ResolveProfile(user, "global", config).LiveTvSavedFilters);
    }

    [Fact]
    public void FirstLocalImportWinsOnlyAgainstExistingAdminDefaults()
    {
        var user = new MoonfinUserSettings();
        var config = Defaults("group:Sports");
        MoonfinSettingsService.ApplySavedFilterChange(user, new LiveTvSavedFilterChange {
            Action = "initializeIfAbsent", Filters = ["group:Local"] }, config.DefaultUserSettings!.LiveTvSavedFilters);
        Assert.Equal(["group:Local"], _service.ResolveProfile(user, "tv", config).LiveTvSavedFilters);
        config.DefaultUserSettings.LiveTvSavedFilters = ["group:Movies"];
        Assert.Equal(["group:Movies", "group:Local"], _service.ResolveProfile(user, "tv", config).LiveTvSavedFilters);
    }

    [Fact]
    public async Task FirstImportIsConditionalEvenAfterAnExplicitClear()
    {
        var id = Guid.NewGuid();
        await Edit(id, "tv", "initializeIfAbsent", null, "group:Sports");
        await Edit(id, "mobile", "initializeIfAbsent", null, "group:News");
        await _service.SaveUserSettingsAsync(id, new MoonfinUserSettings {
            Global = new MoonfinSettingsProfile { LiveTvSavedFilters = [] } }, mergeMode: "replace");
        await _service.SaveProfileAsync(id, "global", new MoonfinSettingsProfile { LiveTvSavedFilters = [] },
            replace: true, notifySettingsChanged: false);
        Assert.Equal(["group:Sports"], (await Read(id))!.Global!.LiveTvSavedFilters);
        await Edit(id, "mobile", "remove", "group:Sports");
        await Edit(id, "tv", "initializeIfAbsent", null, "group:News");
        Assert.Empty((await Read(id))!.Global!.LiveTvSavedFilters!);
        Assert.Empty(_service.ResolveProfile((await Read(id))!, "tv").LiveTvSavedFilters!);
    }

    [Fact]
    public void DeviceOnlyAdminPinsAreSharedAcrossAllProfiles()
    {
        var config = new PluginConfiguration { DefaultDeviceSettings = new MoonfinDeviceDefaults {
            Tv = new MoonfinSettingsProfile { LiveTvSavedFilters = ["group:Sports", "group:News"] } } };
        foreach (var profile in new string?[] { "global", "desktop", "mobile", "tv", null })
            Assert.Equal(["group:Sports", "group:News"],
                MoonfinSettingsService.AdminDefaultsFor(profile, config)!.LiveTvSavedFilters);
        config.DefaultDeviceSettings.Tv!.LiveTvSavedFilters = [];
        Assert.Null(MoonfinSettingsService.AdminDefaultsFor("mobile", config));
    }

    [Fact]
    public async Task CategoryDisplaySurvivesSerializationAndDeviceOverrides()
    {
        var profile = new MoonfinSettingsProfile { LiveTvCategoryDisplay = "both" };
        Assert.Equal("both", JsonSerializer.Deserialize<MoonfinSettingsProfile>(JsonSerializer.Serialize(profile))!.LiveTvCategoryDisplay);
        var config = Defaults();
        config.DefaultUserSettings!.LiveTvCategoryDisplay = "groups";
        config.DefaultDeviceSettings = new MoonfinDeviceDefaults {
            Tv = new MoonfinSettingsProfile { LiveTvCategoryDisplay = "programmes" } };
        var xml = new XmlSerializer(typeof(PluginConfiguration));
        using var stream = new MemoryStream();
        xml.Serialize(stream, config);
        stream.Position = 0;
        config = (PluginConfiguration)xml.Deserialize(stream)!;
        var id = Guid.NewGuid();
        Assert.Equal("groups", _service.ResolveProfile(new MoonfinUserSettings(), "desktop", config).LiveTvCategoryDisplay);
        Assert.Equal("programmes", _service.ResolveProfile(new MoonfinUserSettings(), "tv", config).LiveTvCategoryDisplay);
        Assert.Equal("groups", MoonfinSettingsService.AdminDefaultsFor("mobile", config)!.LiveTvCategoryDisplay);
        Assert.Equal("programmes", MoonfinSettingsService.AdminDefaultsFor("tv", config)!.LiveTvCategoryDisplay);
        await _service.SaveProfileAsync(id, "global", new MoonfinSettingsProfile { LiveTvCategoryDisplay = "groups" }, notifySettingsChanged: false);
        await _service.SaveProfileAsync(id, "tv", new MoonfinSettingsProfile { LiveTvCategoryDisplay = "both" }, notifySettingsChanged: false);
        Assert.Equal("groups", _service.ResolveProfile((await Read(id))!, "mobile", config).LiveTvCategoryDisplay);
        Assert.Equal("both", _service.ResolveProfile((await Read(id))!, "tv", config).LiveTvCategoryDisplay);
    }

    [Fact]
    public async Task MergeAndResetRetainPersonalPinsOnlyUntilFullReset()
    {
        var config = Defaults("group:Sports", "group:News");
        config.DefaultDeviceSettings = new MoonfinDeviceDefaults {
            Tv = new MoonfinSettingsProfile { LiveTvSavedFilters = ["group:Kids"] } };
        var id = Guid.NewGuid();
        await Edit(id, "global", "initializeIfAbsent", null, "group:Sports", "group:Movies");
        var inherited = Guid.NewGuid();
        await _service.SaveProfileAsync(inherited, "global", new MoonfinSettingsProfile(), notifySettingsChanged: false);
        await _service.MergeDefaultsAsync(config, ["global"], ["use24HourClock"], [id]);
        Assert.Equal(["group:Sports", "group:Movies"], (await Read(id))!.Global!.LiveTvSavedFilters);
        await _service.MergeDefaultsAsync(config, ["global"], ["liveTvSavedFilters"], [id, inherited]);
        await _service.MergeDefaultsAsync(config, ["tv"], ["liveTvSavedFilters"], [id]);
        Assert.Null((await Read(inherited))!.Global!.LiveTvSavedFilters);
        Assert.Equal(["group:Sports", "group:News", "group:Kids", "group:Movies"],
            _service.ResolveProfile((await Read(id))!, "tv", config).LiveTvSavedFilters);
        await _service.ResetUsersToDefaultsAsync(config, ["global"], [id], deleteOrphans: false);
        Assert.Empty((await Read(id))!.Global!.LiveTvSavedFilters!);
        Assert.Equal(["group:Sports", "group:News", "group:Kids"],
            _service.ResolveProfile((await Read(id))!, "tv", config).LiveTvSavedFilters);
        config.DefaultUserSettings!.LiveTvSavedFilters = ["group:Sports", "group:Tennis"];
        Assert.Equal(["group:Sports", "group:Tennis", "group:Kids"],
            _service.ResolveProfile((await Read(id))!, "tv", config).LiveTvSavedFilters);
    }
}
