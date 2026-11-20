using Moonfin.Server.Models;
using Moonfin.Server.Services;
using Xunit;

namespace Moonfin.Server.Tests;

public sealed class LiveTvSavedFiltersProfileTests : IDisposable
{
    private readonly string _dataPath =
        Path.Combine(Path.GetTempPath(), "moonfin-guide-filter-tests-" + Guid.NewGuid().ToString("N"));
    private readonly MoonfinSettingsService _service;

    public LiveTvSavedFiltersProfileTests()
    {
        _service = new MoonfinSettingsService(new NoOpLogger<MoonfinSettingsService>(), _dataPath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dataPath)) Directory.Delete(_dataPath, recursive: true);
    }

    [Fact]
    public async Task SavedFiltersAreSharedAcrossDevicesAndCanBeCleared()
    {
        var userId = Guid.NewGuid();
        var shortcuts = new List<string> { "group:US | SPORTS, Canada", "category:sports" };

        await _service.SaveProfileAsync(userId, "tv",
            new MoonfinSettingsProfile { LiveTvSavedFilters = shortcuts },
            notifySettingsChanged: false);

        var settings = (await _service.GetUserSettingsAsync(userId))!;
        Assert.Equal(shortcuts, settings.Global!.LiveTvSavedFilters);
        Assert.Null(settings.Tv!.LiveTvSavedFilters);
        Assert.Equal(shortcuts, _service.ResolveProfile(settings, "mobile").LiveTvSavedFilters);

        // An unrelated profile update must not erase shortcuts.
        await _service.SaveProfileAsync(userId, "desktop",
            new MoonfinSettingsProfile { CinemaModeEnabled = true },
            notifySettingsChanged: false);
        settings = (await _service.GetUserSettingsAsync(userId))!;
        Assert.Equal(shortcuts, _service.ResolveProfile(settings, "desktop").LiveTvSavedFilters);

        await _service.SaveProfileAsync(userId, "mobile",
            new MoonfinSettingsProfile { LiveTvSavedFilters = new List<string>() },
            notifySettingsChanged: false);
        settings = (await _service.GetUserSettingsAsync(userId))!;
        Assert.Empty(settings.Global!.LiveTvSavedFilters!);
        Assert.Null(settings.Mobile!.LiveTvSavedFilters);
        Assert.Empty(_service.ResolveProfile(settings, "tv").LiveTvSavedFilters!);
    }
}
