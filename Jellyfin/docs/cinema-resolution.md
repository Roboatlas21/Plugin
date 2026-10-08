# Typed Cinema Mode resolution

Jellyfin and Emby expose the same typed resolver contract:
`GET /Moonfin/Cinema/ResolveMedia?itemId=<uuid>&expectedMediaType=movie|tv`.

Moonfin normally resolves trustworthy, unattached typed TMDB metadata locally and
does not call this endpoint for that fast path. The server resolver is used when
server-only information is needed—most importantly to validate an attached
trailer's owner and the current user's access—or when identity still requires the
strict filename/display-name fallback. The endpoint remains self-contained and can
also return direct typed metadata because attached trailers may need that metadata
validated against their owner, and callers may invoke the endpoint directly.

The endpoint requires the authenticated server user's access to the intro and any
trailer owner. The response contains `tmdbId`, `mediaType` (`movie` or `tv`),
and `source`. An unresolved result has null identity fields. Lookups have an eight-second budget and no completed-result cache.

TMDB IDs are not globally unique across movies and TV. An explicit
`ProviderIds.TmdbMediaType`, a Movie item, or an accessible Movie/Series trailer
owner establishes type.
The legacy `trailers4jellyfin.trailer` marker denotes the enhanced plugin's
movie-only downloads unless an explicit media type is supplied. Conflicting
identity data and untyped generic-video TMDB IDs are rejected.

`expectedMediaType` restricts filename searches; it never types a bare ID or
overrides a trustworthy typed identity. Movie searches require exact normalized
title plus matching year. Series searches require an exact, unambiguous title;
year is optional. A trailer filename's Season N/SNN suffix is ignored when
finding the series; its accompanying year is not treated as the show's debut year.
The Seerr request dialog handles season selection independently.

Filename-only setups must use movie trailers before movies and series trailers
before episodes. Mixed pools need explicit type metadata. Search never switches
categories after a miss. Readable display names can identify cached files with
hashed/unmatched names; ambiguous or contradictory matches return no identity.

The legacy `ResolveMovie` endpoint remains movie-only for older clients and
will never expose a series ID as a movie. Existing clients need the updated
Moonfin typed resolver to request series.
