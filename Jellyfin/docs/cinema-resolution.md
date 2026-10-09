# Typed Cinema Mode resolution

Jellyfin and Emby expose the same typed resolver contract:
`GET /Moonfin/Cinema/ResolveMedia?itemId=<uuid>&expectedMediaType=movie|tv`.

Moonfin first resolves unattached typed TMDB metadata locally. An unattached
video with an untyped TMDB ID also resolves locally as a movie if it is playing
before a movie. Neither fast path requires Moonbase. The server resolver validates
attached-trailer ownership and user access, and handles strict filename/display-name
fallback when identity is still unknown. The endpoint remains self-contained and can
also return direct typed metadata because attached trailers may need that metadata
validated against their owner, and callers may invoke the endpoint directly.

The endpoint requires the authenticated server user's access to the intro and any
trailer owner. The response contains `tmdbId` and `mediaType` (`movie` or `tv`).
An unresolved result has null identity fields. Lookups have an eight-second
budget and no completed-result cache.

TMDB IDs are not globally unique across movies and TV. An explicit
`ProviderIds.TmdbMediaType`, a Movie item, or an accessible Movie/Series trailer
owner establishes type. An untyped ID on a TV trailer requires either a matching ID from its
accessible Series owner or confirmation from a strict series lookup. Conflicting types and IDs
are rejected; no trailer-plugin-specific provider marker is required.

`expectedMediaType` restricts filename searches; it does not type an unverified
ID or override a trustworthy typed identity. Movie searches require exact normalized
title plus matching year. Series searches require an exact, unambiguous title
and ignore the trailer filename's release year, which need not be the show's
debut year. Season N/SNN suffixes are also ignored when matching series.
The Seerr request dialog handles season selection independently.

Filename-only setups must use movie trailers before movies and series trailers
before episodes. Mixed pools need explicit type metadata. Search never switches
categories after a miss. Readable display names can identify cached files with
hashed/unmatched names; ambiguous or contradictory matches return no identity.

Cinema Mode uses only `ResolveMedia` to resolve movies and series.
