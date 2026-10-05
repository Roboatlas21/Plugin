using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moonfin.Server.Services;

namespace Moonfin.Server.Api;

[ApiController]
[Authorize]
[Route("Moonfin/Cinema")]
public sealed class CinemaController(CinemaMovieResolver resolver, ILogger<CinemaController> logger) : ControllerBase
{
    [HttpGet("ResolveMovie")]
    public async Task<ActionResult<CinemaMovieResolver.Resolution>> ResolveMovie(
        [FromQuery] Guid itemId, CancellationToken cancellationToken)
    {
        var userId = this.GetUserIdFromClaims();
        if (!userId.HasValue || userId == Guid.Empty) return Unauthorized();
        if (itemId == Guid.Empty) return BadRequest();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            return Ok(await resolver.ResolveAsync(itemId, userId.Value, timeout.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Ok(new CinemaMovieResolver.Resolution(null, null));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Cinema movie resolution failed for {ItemId}", itemId);
            return Ok(new CinemaMovieResolver.Resolution(null, null));
        }
    }
}
