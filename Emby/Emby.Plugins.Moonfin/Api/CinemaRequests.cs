using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace Emby.Plugins.Moonfin.Api
{
    [Route("/Moonfin/Cinema/ResolveMedia", "GET")]
    [Authenticated]
    public class ResolveCinemaMediaRequest : IReturn<object>
    {
        public Guid ItemId { get; set; }
        public string? ExpectedMediaType { get; set; }
    }
}
