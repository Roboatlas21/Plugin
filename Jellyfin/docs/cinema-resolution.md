# Typed Cinema Mode resolution

Jellyfin and Emby expose the same typed resolver contract:
`GET /Moonfin/Cinema/ResolveMedia?itemId=<uuid>&expectedMediaType=movie|tv`.

Moonfin uses a positive TMDB ID locally, determining its media type from the
explicit `TmdbMediaType` provider value, the item's Movie/Series classification,
or the playback context (movie before a movie, series before an episode).
Playback context assumes that the intro provider keeps movie and series trailer
pools separate. Moonfin does not read `OwnerId` or require changes to Jellyfin's
Intros response.

When there is no usable TMDB identity, Moonfin asks Moonbase to resolve it.
The resolver checks ownership and user access internally. If an attached
movie or series has no TMDB ID, it searches using the owner's title (and the
owner's production year for movies), never the trailer filename. Standalone
trailers still use strict filename/display-name matching. Clients may also
call this endpoint directly with typed metadata.

The endpoint requires the authenticated server user's access to the intro and any
trailer owner. The response contains `tmdbId` and `mediaType` (`movie` or `tv`).
An unresolved result has no usable identity: its fields may be null or omitted
(Emby omits null-valued JSON properties). Moonfin handles both forms. Lookups
have an eight-second budget and no completed-result cache.

TMDB IDs are not globally unique across movies and TV. For server-side
resolution, an explicit `ProviderIds.TmdbMediaType`, a Movie item, or an
accessible Movie/Series trailer owner establishes type. Once the type is known,
a positive trailer TMDB ID is trusted for movies and series alike; if the
owner also has an ID, they must agree. Untyped standalone videos still need
strict filename matching. Conflicting types and IDs are rejected. The owner
relationship is only examined on the server; no trailer-plugin-specific
marker is required.

`expectedMediaType` selects the filename-search category only when the intro's
own type cannot be established. It never types a bare TMDB ID or overrides
explicit, item, or owner type. Movie searches still require exact normalized
title plus matching year. Filename parsing delegates standard name/year and
technical-label handling to the host's built-in library parser, then removes
text from the last Trailer/Teaser label onward and trims TV season hints.
It avoids guessing whether ordinary title words are video IDs, but still supports
compact years such as Dune2021. Remote
matches still require exact title and year. Series
searches require an exact, unambiguous title
and ignore the trailer filename's release year, which need not be the show's
debut year. Season N/SNN suffixes are also ignored when matching series.
The Seerr request dialog handles season selection independently.

Untyped filename-only setups must use movie trailers before movies and series
trailers before episodes. Mixed pools need explicit type metadata. Search never switches
categories after a miss. Readable display names can identify cached files with
hashed/unmatched names; ambiguous or contradictory matches return no identity.

Cinema Mode uses only `ResolveMedia` to resolve movies and series.
