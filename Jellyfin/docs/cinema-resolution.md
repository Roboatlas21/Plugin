# Typed Cinema Mode resolution

Jellyfin and Emby expose the same typed endpoint:
`GET /Moonfin/Cinema/ResolveMedia?itemId=<uuid>&expectedMediaType=movie|tv`.

Moonfin uses a positive TMDB ID when the trailer's media type is known from
`TmdbMediaType`, its Movie/Series item classification, or the playing feature.
The last option assumes that untyped movie and TV trailers are kept in separate
intro pools. No changes to Jellyfin's Intros response are needed.

When Moonfin cannot establish an identity, the authenticated resolver validates
access to the video and its owner. Movie and Series owners supply the
authoritative identity. If a Season or Episode owns a trailer, Moonbase instead
uses the accessible parent Series and its metadata; missing or inaccessible
series remain unresolved. Movie title searches use the release year when
available; yearless searches require one distinct exact TMDB match.
TV title searches do not require a year.

The resolver also recognizes provider IDs embedded in standalone trailer names:
`Title_tmdb123_trailer`, `Title_123_trailer`, and `Title_tvdb456_trailer`.
Explicit prefixes identify TMDB versus TVDB; an unprefixed numeric suffix is
treated as a movie TMDB ID unless it resembles a release year. Contradicting
item, owner, path, or display-name metadata is rejected.

A TVDB ID from either the filename or `ProviderIds.Tvdb` is resolved using
the host's `TheMovieDb` remote series provider. This is an ID-only lookup,
not a fuzzy title search. A unique positive TMDB mapping is accepted; conflicting
or invalid nonempty results are rejected. A result's TVDB ID must match if the
provider includes one (Jellyfin does; some Emby versions do not). If no mapping
is returned, the resolver falls back to strict, unambiguous series-title matching.

Other filenames use the host's native parser with a small trailer-label cleanup.
Movie name searches require an exact normalized title and, when available, a
matching year. Without a year, the exact match must identify one distinct TMDB
movie among the provider results. TV searches require an exact series title
and ignore season numbers or trailer-release years.
Readable display names can recover hashed file paths. Display names are treated
as titles, not filesystem paths, so a slash in `Ranma1/2` is preserved.
Only after a full-title miss are delimiter-separated or generated-ID-suffix
candidates tried. Ambiguous matches remain unresolved.

`expectedMediaType` only selects a name-search category when authoritative
item/owner and embedded-ID metadata cannot establish it. Searches do not switch
from movie to TV or vice versa after a miss. Unresolvable media simply hides
Request; Cinema Mode playback and Skip remain usable.

The endpoint returns `tmdbId` and `mediaType` (`movie` or `tv`).
It uses an eight-second server-side timeout, has no completed-result cache,
and requires access to the intro and any registered owner.
