using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moonfin.Server.Api;
using Moonfin.Server.Services;
using Xunit;

namespace Moonfin.Server.Tests;

public sealed class UnreadableSettingsTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OnlyUnreadableSettingsUseTheRegistered503Response(bool unreadable)
    {
        var cause = new IOException("Other I/O failure");
        Exception error = unreadable ? new UnreadableSettingsException(cause) : cause;
        var context = ApplySettingsFilter(error);
        Assert.Equal(unreadable, context.ExceptionHandled);
        if (unreadable) {
            var result = Assert.IsType<ObjectResult>(context.Result);
            Assert.Equal(StatusCodes.Status503ServiceUnavailable, result.StatusCode);
            Assert.Equal(error.Message, JsonSerializer.SerializeToElement(result.Value).GetProperty("Error").GetString());
        } else {
            Assert.Null(context.Result);
        }
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
