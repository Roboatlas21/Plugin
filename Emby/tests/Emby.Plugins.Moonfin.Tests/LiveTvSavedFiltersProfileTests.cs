using Emby.Plugins.Moonfin.Models;
using Emby.Plugins.Moonfin.Services;
using Xunit;

namespace Emby.Plugins.Moonfin.Tests;

public sealed class LiveTvSavedFiltersProfileTests : IDisposable
{
    private readonly string _dataPath =
        Path.Combine(Path.GetTempPath(), "moonfin-emby-filter-tests-" + Guid.NewGuid().ToString("N"));
    private readonly MoonfinSettingsService _service;

    public LiveTvSavedFiltersProfileTests()
    {
        _service = new MoonfinSettingsService(AdminDefaultsMergeTests.NoOpLogger.Create(), _dataPath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dataPath)) Directory.Delete(_dataPath, recursive: true);
    }

    [Fact]
    public async Task AtomicEditsPreserveOtherPinsAndRejectStaleFullProfiles()
    {
        var userId = Guid.NewGuid();
        var initial = new List<string> { "group:US | SPORTS, Canada" };
        await _service.SaveProfileAsync(userId, "tv", new MoonfinSettingsProfile(),
            notifySettingsChanged: false,
            savedFilterChange: new LiveTvSavedFilterChange { Action = "initializeIfAbsent", Filters = initial });

        await _service.SaveProfileAsync(userId, "mobile",
            new MoonfinSettingsProfile { LiveTvSavedFilters = new List<string>() },
            notifySettingsChanged: false);
        var stored = (await _service.GetUserSettingsAsync(userId))!;
        Assert.Equal(initial, stored.Global!.LiveTvSavedFilters);
        Assert.Null(stored.Mobile!.LiveTvSavedFilters);

        await _service.SaveProfileAsync(userId, "tv", new MoonfinSettingsProfile(),
            notifySettingsChanged: false,
            savedFilterChange: new LiveTvSavedFilterChange { Action = "add", Filter = "group:News" });
        await _service.SaveProfileAsync(userId, "mobile", new MoonfinSettingsProfile(),
            notifySettingsChanged: false,
            savedFilterChange: new LiveTvSavedFilterChange { Action = "add", Filter = "group:Kids" });
        stored = (await _service.GetUserSettingsAsync(userId))!;
        Assert.Equal(new[] { "group:US | SPORTS, Canada", "group:News", "group:Kids" },
            _service.ResolveProfile(stored, "mobile").LiveTvSavedFilters);

        await _service.SaveProfileAsync(userId, "tv", new MoonfinSettingsProfile(),
            notifySettingsChanged: false,
            savedFilterChange: new LiveTvSavedFilterChange { Action = "remove", Filter = "group:News" });
        stored = (await _service.GetUserSettingsAsync(userId))!;
        Assert.Equal(new[] { "group:US | SPORTS, Canada", "group:Kids" }, stored.Global!.LiveTvSavedFilters);
    }

    [Fact]
    public void AdminPinsAreInheritedAndUsersCanClearEveryPin()
    {
        var stored = new MoonfinUserSettings();
        var defaults = new List<string> { "group:Sports", "group:News" };
        var config = new PluginConfiguration {
            DefaultUserSettings = new MoonfinSettingsProfile { LiveTvSavedFilters = defaults }
        };
        Assert.Equal(defaults, _service.ResolveProfile(stored, "tv", config).LiveTvSavedFilters);

        MoonfinSettingsService.ApplySavedFilterChange(stored,
            new LiveTvSavedFilterChange { Action = "remove", Filter = "group:Sports" }, defaults);
        Assert.Equal(new[] { "group:News" }, _service.ResolveProfile(stored, "tv", config).LiveTvSavedFilters);
        MoonfinSettingsService.ApplySavedFilterChange(stored,
            new LiveTvSavedFilterChange { Action = "remove", Filter = "group:News" }, defaults);
        Assert.Empty(_service.ResolveProfile(stored, "tv", config).LiveTvSavedFilters!);
        MoonfinSettingsService.ApplySavedFilterChange(stored,
            new LiveTvSavedFilterChange { Action = "add", Filter = "group:Kids" }, defaults);
        Assert.Equal(new[] { "group:Kids" }, _service.ResolveProfile(stored, "tv", config).LiveTvSavedFilters);
    }

    [Fact]
    public void ChangingAdminPinsUpdatesExistingUsersWithoutLosingPersonalChoices()
    {
        var stored = new MoonfinUserSettings();
        var config = new PluginConfiguration {
            DefaultUserSettings = new MoonfinSettingsProfile {
                LiveTvSavedFilters = new List<string> { "group:Sports", "group:News" }
            }
        };
        MoonfinSettingsService.ApplySavedFilterChange(stored,
            new LiveTvSavedFilterChange { Action = "add", Filter = "group:Formula 1" }, config.DefaultUserSettings.LiveTvSavedFilters);
        MoonfinSettingsService.ApplySavedFilterChange(stored,
            new LiveTvSavedFilterChange { Action = "remove", Filter = "group:Sports" }, config.DefaultUserSettings.LiveTvSavedFilters);

        config.DefaultUserSettings.LiveTvSavedFilters = new List<string> { "group:News", "group:Hockey" };
        Assert.Equal(new[] { "group:News", "group:Hockey", "group:Formula 1" },
            _service.ResolveProfile(stored, "tv", config).LiveTvSavedFilters);

        // A removed group stays hidden if the admin brings it back.
        config.DefaultUserSettings.LiveTvSavedFilters = new List<string> { "group:Sports", "group:Hockey" };
        Assert.Equal(new[] { "group:Hockey", "group:Formula 1" },
            _service.ResolveProfile(stored, "mobile", config).LiveTvSavedFilters);

        // Re-pinning a default is a personal choice, preserved after the admin removes it.
        MoonfinSettingsService.ApplySavedFilterChange(stored,
            new LiveTvSavedFilterChange { Action = "add", Filter = "group:Sports" }, config.DefaultUserSettings.LiveTvSavedFilters);
        config.DefaultUserSettings.LiveTvSavedFilters = new List<string> { "group:Hockey" };
        Assert.Equal(new[] { "group:Hockey", "group:Formula 1", "group:Sports" },
            _service.ResolveProfile(stored, "global", config).LiveTvSavedFilters);
    }

    [Fact]
    public void FirstLocalImportRemainsPersonalWhileNewDefaultsStillInherit()
    {
        var stored = new MoonfinUserSettings();
        var defaults = new List<string> { "group:Sports" };
        var config = new PluginConfiguration {
            DefaultUserSettings = new MoonfinSettingsProfile { LiveTvSavedFilters = defaults }
        };
        MoonfinSettingsService.ApplySavedFilterChange(stored,
            new LiveTvSavedFilterChange { Action = "initializeIfAbsent", Filters = new List<string> { "group:Local" } }, defaults);
        Assert.Equal(new[] { "group:Local" }, _service.ResolveProfile(stored, "tv", config).LiveTvSavedFilters);
        config.DefaultUserSettings.LiveTvSavedFilters = new List<string> { "group:Movies" };
        Assert.Equal(new[] { "group:Movies", "group:Local" },
            _service.ResolveProfile(stored, "tv", config).LiveTvSavedFilters);
    }

    [Fact]
    public async Task FirstImportIsConditionalAndExplicitEmptyIsNotReimported()
    {
        var userId = Guid.NewGuid();
        await _service.SaveProfileAsync(userId, "tv", new MoonfinSettingsProfile(),
            notifySettingsChanged: false,
            savedFilterChange: new LiveTvSavedFilterChange { Action = "initializeIfAbsent",
                Filters = new List<string> { "group:Sports" } });
        await _service.SaveProfileAsync(userId, "mobile", new MoonfinSettingsProfile(),
            notifySettingsChanged: false,
            savedFilterChange: new LiveTvSavedFilterChange { Action = "initializeIfAbsent",
                Filters = new List<string> { "group:News" } });
        await _service.SaveUserSettingsAsync(userId, new MoonfinUserSettings {
            Global = new MoonfinSettingsProfile { LiveTvSavedFilters = new List<string>() }
        }, mergeMode: "replace");
        await _service.SaveProfileAsync(userId, "global", new MoonfinSettingsProfile {
            LiveTvSavedFilters = new List<string>()
        }, replace: true, notifySettingsChanged: false);
        var stored = (await _service.GetUserSettingsAsync(userId))!;
        Assert.Equal(new[] { "group:Sports" }, stored.Global!.LiveTvSavedFilters);

        await _service.SaveProfileAsync(userId, "mobile", new MoonfinSettingsProfile(),
            notifySettingsChanged: false,
            savedFilterChange: new LiveTvSavedFilterChange { Action = "remove", Filter = "group:Sports" });
        await _service.SaveProfileAsync(userId, "tv", new MoonfinSettingsProfile(),
            notifySettingsChanged: false,
            savedFilterChange: new LiveTvSavedFilterChange { Action = "initializeIfAbsent",
                Filters = new List<string> { "group:News" } });
        stored = (await _service.GetUserSettingsAsync(userId))!;
        Assert.Empty(stored.Global!.LiveTvSavedFilters!);
        Assert.Empty(_service.ResolveProfile(stored, "tv").LiveTvSavedFilters!);
    }
}
