using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationModels;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;

namespace Moonfin.Server.Services;

// Jellyfin deliberately skips ILibraryManager.ItemUpdated for Live TV. Its metadata
// editor can still save a channel name or number independently of a guide import.
internal sealed class LiveTvChannelEditFilter : ActionFilterAttribute, IActionModelConvention
{
    public void Apply(ActionModel action)
    {
        if (action.Controller.ControllerName == "ItemUpdate" && action.ActionName == "UpdateItem")
            action.Filters.Add(this);
    }

    public override async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var result = await next();
        if (result.Exception == null && result.Result is NoContentResult &&
            context.ActionArguments.TryGetValue("itemId", out var itemId) && itemId is Guid id)
            context.HttpContext.RequestServices.GetRequiredService<LiveTvGuideNotifier>().ChannelEdited(id);
    }
}
