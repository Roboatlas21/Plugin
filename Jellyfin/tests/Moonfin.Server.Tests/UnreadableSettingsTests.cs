using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moonfin.Server.Api;
using Moonfin.Server.Models;
using Moonfin.Server.Services;
using Xunit;

namespace Moonfin.Server.Tests;

public sealed class UnreadableSettingsTests
{
    [Theory]
    [InlineData("save settings")]
    [InlineData("save profile")]
    [InlineData("delete profile")]
    public async Task UnreadableCopiesReturn503AndRemainIntact(string operation)
    {
        var directory = Directory.CreateTempSubdirectory("moonfin-settings-");
        try
        {
            var service = new MoonfinSettingsService(new NoOpLogger<MoonfinSettingsService>(), directory.FullName);
            var id = Guid.NewGuid();
            var primary = Path.Combine(directory.FullName, id + ".json");
            var backup = AtomicFile.BackupPath(primary);
            await File.WriteAllTextAsync(primary, "{bad primary");
            await File.WriteAllTextAsync(backup, "{bad backup");

            var error = await Assert.ThrowsAsync<UnreadableSettingsException>(() => operation switch
            {
                "save settings" => service.SaveUserSettingsAsync(id, new MoonfinUserSettings()),
                "save profile" => service.SaveProfileAsync(id, "tv", new MoonfinSettingsProfile()),
                "delete profile" => service.DeleteProfileAsync(id, "tv"),
                _ => throw new ArgumentOutOfRangeException(nameof(operation))
            });
            var context = ApplySettingsFilter(error);
            var result = Assert.IsType<ObjectResult>(context.Result);
            Assert.True(context.ExceptionHandled);
            Assert.Equal(StatusCodes.Status503ServiceUnavailable, result.StatusCode);
            Assert.Equal(error.Message, JsonSerializer.SerializeToElement(result.Value).GetProperty("Error").GetString());
            Assert.DoesNotContain(primary, error.Message);
            Assert.Equal("{bad primary", await File.ReadAllTextAsync(primary));
            Assert.Equal("{bad backup", await File.ReadAllTextAsync(backup));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void UnrelatedIoErrorsKeepTheirExistingHandling()
    {
        var context = ApplySettingsFilter(new IOException("Other I/O failure"));
        Assert.False(context.ExceptionHandled);
        Assert.Null(context.Result);
    }

    private static ExceptionContext ApplySettingsFilter(Exception error)
    {
        var action = new ActionContext(new DefaultHttpContext(), new RouteData(), new ActionDescriptor());
        var context = new ExceptionContext(action, []) { Exception = error };
        var services = new ServiceCollection();
        new PluginServiceRegistrator().RegisterServices(services, null!);
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<MvcOptions>>().Value;
        var filter = Assert.Single(options.Filters.OfType<UnreadableSettingsFilter>());
        filter.OnException(context);
        return context;
    }
}
