using System.Reflection;
using System.Text.Json.Serialization;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Moonfin.Server.Services;

/// <summary>
/// Includes the owning item ID on Jellyfin's Intros DTOs. The normal DTO omits
/// OwnerId, so Moonfin would otherwise mistake an attached trailer for an
/// unattached one and trust its TMDB metadata without checking its owner.
/// </summary>
public sealed class CinemaIntroOwnershipFilter(ILibraryManager library, ILogger<CinemaIntroOwnershipFilter> logger)
    : IResultFilter
{
    private static readonly PropertyInfo[] DtoProperties = typeof(BaseItemDto)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(property => property.CanRead && property.CanWrite)
        .ToArray();

    public void OnResultExecuting(ResultExecutingContext context)
    {
        // The legacy and current Intros routes both end in /Intros.
        if (context.HttpContext.Request.Path.Value is not { } path ||
            !path.EndsWith("/Intros", StringComparison.OrdinalIgnoreCase) ||
            context.Result is not ObjectResult { Value: QueryResult<BaseItemDto> intros } response ||
            intros.Items.Count == 0)
        {
            return;
        }

        try
        {
            var ownerIds = intros.Items
                .Select(dto => library.GetItemById<Video>(dto.Id)?.OwnerId ?? Guid.Empty)
                .ToArray();
            if (!ownerIds.Any(ownerId => ownerId != Guid.Empty)) return;

            // Keep the normal Jellyfin DTO properties and response envelope intact.
            // A derived item type is necessary: System.Text.Json would otherwise
            // serialize the array as BaseItemDto and omit the additional field.
            var items = intros.Items.Select((dto, index) => CopyWithOwner(dto, ownerIds[index])).ToArray();
            response.Value = new QueryResult<OwnedIntroDto>(intros.StartIndex, intros.TotalRecordCount, items);
            response.DeclaredType = typeof(QueryResult<OwnedIntroDto>);
        }
        catch (Exception ex)
        {
            // A Cinema Mode hint must not prevent the server from returning intros.
            logger.LogDebug(ex, "Could not add trailer ownership to the Intros response");
        }
    }

    public void OnResultExecuted(ResultExecutedContext context)
    {
    }

    private static OwnedIntroDto CopyWithOwner(BaseItemDto dto, Guid ownerId)
    {
        var copy = new OwnedIntroDto { OwnerId = ownerId };
        foreach (var property in DtoProperties)
        {
            property.SetValue(copy, property.GetValue(dto));
        }

        return copy;
    }

    public sealed class OwnedIntroDto : BaseItemDto
    {
        [JsonPropertyName("OwnerId")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public Guid OwnerId { get; init; }
    }
}
