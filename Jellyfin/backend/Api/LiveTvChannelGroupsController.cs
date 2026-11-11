using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.LiveTv;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moonfin.Server.Services;

namespace Moonfin.Server.Api;

/// <summary>
/// Exposes the original group names supplied by Jellyfin's live TV services. A tuner
/// can set ChannelInfo.ChannelGroup without storing anything in the channel's Tags.
/// </summary>
[ApiController]
[Route("Moonfin/LiveTv/ChannelGroups")]
[Produces("application/json")]
public sealed class LiveTvChannelGroupsController : ControllerBase
{
    private readonly ILiveTvManager _liveTv;
    private readonly IUserManager _users;
    private readonly LiveTvChannelGroupCache _groups;

    public LiveTvChannelGroupsController(
        ILiveTvManager liveTv,
        IUserManager users,
        LiveTvChannelGroupCache groups)
    {
        _liveTv = liveTv;
        _users = users;
        _groups = groups;
    }

    [HttpGet]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        var userId = this.GetUserIdFromClaims();
        if (userId is null)
        {
            return Unauthorized();
        }

        var user = _users.GetUserById(userId.Value);
        if (user is null || !_liveTv.GetEnabledUsers().Any(enabled => enabled.Id == userId.Value))
        {
            return Ok(new { Channels = Array.Empty<object>() });
        }

        // The public LiveTv/Channels endpoint uses the same user-scoped query.
        // Only publish groups for channels this user can actually see.
        var visible = _liveTv.GetInternalChannels(
            new LiveTvChannelQuery { UserId = userId.Value, AddCurrentProgram = false },
            new DtoOptions { AddCurrentProgram = false },
            cancellationToken).Items.OfType<LiveTvChannel>();

        var sourceGroups = await _groups.GetAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<object>();
        foreach (var channel in visible)
        {
            if (string.IsNullOrEmpty(channel.ExternalId)) continue;
            if (!sourceGroups.TryGetValue(
                LiveTvChannelGroupCache.Key(channel.ServiceName, channel.ExternalId),
                out var group)) continue;
            result.Add(new { ChannelId = channel.Id.ToString("N"), Group = group });
        }

        return Ok(new { Channels = result });
    }
}
