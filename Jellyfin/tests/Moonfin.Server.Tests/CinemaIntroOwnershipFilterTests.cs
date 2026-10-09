using System.Text.Json;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Moonfin.Server.Services;
using Xunit;

namespace Moonfin.Server.Tests;

public sealed class CinemaIntroOwnershipFilterTests
{
    private static ResultExecutingContext Context(string path, QueryResult<BaseItemDto> intros)
    {
        var http = new DefaultHttpContext();
        http.Request.Path = path;
        return new ResultExecutingContext(
            new ActionContext(http, new RouteData(), new ActionDescriptor()),
            new List<IFilterMetadata>(),
            new ObjectResult(intros) { DeclaredType = typeof(QueryResult<BaseItemDto>) },
            new object());
    }

    private static CinemaIntroOwnershipFilter Filter(FakeLibraryManager library) =>
        new(library, NullLogger<CinemaIntroOwnershipFilter>.Instance);

    [Theory]
    [InlineData("/Users/123/Items/456/Intros")]
    [InlineData("/Items/456/Intros")]
    public void AttachedTrailerAdvertisesOwnerWithoutChangingTheNormalIntroFields(string path)
    {
        var ownerId = Guid.NewGuid();
        var attachedId = Guid.NewGuid();
        var unownedId = Guid.NewGuid();
        var library = new FakeLibraryManager
        {
            ItemHandler = id => id == attachedId
                ? new Video { Id = attachedId, OwnerId = ownerId }
                : id == unownedId ? new Video { Id = unownedId } : null,
        };
        var intro = new BaseItemDto
        {
            Id = attachedId,
            Name = "Attached Trailer",
            RunTimeTicks = 10000000,
            ProviderIds = new() { ["Tmdb"] = "42" },
        };
        var standalone = new BaseItemDto { Id = unownedId, Name = "Standalone Trailer" };
        var original = new QueryResult<BaseItemDto>(3, 11, new[] { intro, standalone });
        var context = Context(path, original);

        Filter(library).OnResultExecuting(context);

        var response = Assert.IsType<ObjectResult>(context.Result);
        var transformed = Assert.IsType<QueryResult<CinemaIntroOwnershipFilter.OwnedIntroDto>>(response.Value);
        Assert.Equal(typeof(QueryResult<CinemaIntroOwnershipFilter.OwnedIntroDto>), response.DeclaredType);
        Assert.Equal(3, transformed.StartIndex);
        Assert.Equal(11, transformed.TotalRecordCount);
        Assert.Equal(ownerId, transformed.Items[0].OwnerId);
        Assert.Equal(Guid.Empty, transformed.Items[1].OwnerId);
        Assert.Equal(intro.Id, transformed.Items[0].Id);
        Assert.Equal(intro.Name, transformed.Items[0].Name);
        Assert.Equal(intro.ProviderIds, transformed.Items[0].ProviderIds);
        Assert.Equal(intro.RunTimeTicks, transformed.Items[0].RunTimeTicks);
        Assert.Equal("Standalone Trailer", transformed.Items[1].Name);
        Assert.IsType<BaseItemDto>(original.Items[0]);

        var json = JsonSerializer.Serialize(transformed);
        Assert.Contains("\"OwnerId\":\"" + ownerId.ToString("D") + "\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, json.Split("\"OwnerId\"").Length - 1);
    }

    [Fact]
    public void StandaloneIntrosStayUnchanged()
    {
        var id = Guid.NewGuid();
        var library = new FakeLibraryManager { ItemHandler = _ => new Video { Id = id } };
        var result = new QueryResult<BaseItemDto>(new[] { new BaseItemDto { Id = id, Name = "Standalone" } });
        var context = Context("/Users/123/Items/456/Intros", result);
        var original = context.Result;

        Filter(library).OnResultExecuting(context);

        Assert.Same(original, context.Result);
        Assert.Same(result, Assert.IsType<ObjectResult>(context.Result).Value);
    }

    [Fact]
    public void OtherEndpointsCannotChangeOrReadIntroData()
    {
        var library = new FakeLibraryManager();
        var result = new QueryResult<BaseItemDto>(new[] { new BaseItemDto { Id = Guid.NewGuid() } });
        var context = Context("/Items/123/LocalTrailers", result);

        Filter(library).OnResultExecuting(context);

        Assert.Same(result, Assert.IsType<ObjectResult>(context.Result).Value);
    }

    [Fact]
    public void OwnershipLookupFailureDoesNotBreakIntroPlayback()
    {
        var library = new FakeLibraryManager(); // Unconfigured lookup throws.
        var result = new QueryResult<BaseItemDto>(new[] { new BaseItemDto { Id = Guid.NewGuid() } });
        var context = Context("/Users/123/Items/456/Intros", result);

        Filter(library).OnResultExecuting(context);

        Assert.Same(result, Assert.IsType<ObjectResult>(context.Result).Value);
    }
}
