namespace Gitberth.Images;

// What New app's image search and the tag picker show: Docker Hub's search, and an image's tags ranked newest stable
// first. ImageService answers both (SearchAsync, TagsAsync); TagViews turns a TagList into the rows under one chip.

/// <summary>Docker Hub's mark on a repository. SponsoredOss is its "Sponsored OSS" (open_source).</summary>
public enum HubBadge { None, Official, VerifiedPublisher, SponsoredOss }

/// <summary>Why a search or a tag list has nothing: each comes back with a one-sentence message, never thrown.</summary>
public enum LookupProblem { None, NotFound, NeedsSignIn, Unreachable, RateLimited, Failed }

/// <summary>One Docker Hub search result.</summary>
/// <param name="Name">As pulled: nginx, bitnami/nginx.</param>
/// <param name="Description">Plain text, at most 300 characters, control and bidi characters taken out.</param>
/// <param name="Pulls">The exact count; null when Docker Hub didn't give one.</param>
/// <param name="PullsText">Docker Hub's own words for it ("1B+"); null from the basic search.</param>
/// <param name="Updated">UTC; null from the basic search.</param>
/// <param name="Architectures">amd64, arm64…; empty when Docker Hub didn't say.</param>
/// <param name="FitsHost">False only when the architectures are known and lack this host's.</param>
public sealed record HubImage(string Name, string Description, HubBadge Badge, int Stars, long? Pulls, string? PullsText,
    DateTime? Updated, IReadOnlyList<string> Architectures, bool FitsHost);

/// <summary>A Docker Hub search's answer.</summary>
/// <param name="Results">In Docker Hub's order, at most 25.</param>
/// <param name="Basic">From the basic (v1) search: official images marked only, no dates or architectures.</param>
/// <param name="Message">One sentence for the owner when there's a problem; never a time (the UI counts down to RetryAt).</param>
/// <param name="RetryAt">UTC: when a rate-limited Docker Hub may be asked again.</param>
public sealed record HubSearch(string Query, IReadOnlyList<HubImage> Results, int Total, bool Basic,
    LookupProblem Problem = LookupProblem.None, string? Message = null, DateTime? RetryAt = null);

/// <summary>A tag as the registry lists it, before ranking.</summary>
/// <param name="Platforms">linux/amd64, linux/arm64/v8… (attestations left out); null when the registry didn't say.</param>
public sealed record TagFacts(string Name, string? Digest, DateTime? Pushed, long? Size, IReadOnlyList<string>? Platforms);

/// <summary>The tags that name one image (one digest), as one row.</summary>
/// <param name="Tag">The canonical member: 1.31.2.</param>
/// <param name="Version">Its version; null for a channel (latest, bookworm…) only.</param>
/// <param name="Also">The other members: versions by parts, longest first, then channels.</param>
/// <param name="Inferred">The registry gave no digests: Also was worked out from the names (1.27 → the highest 1.27.x).</param>
/// <param name="Families">The members' families; "" is the plain one (1.31.2), alpine for 1.31.2-alpine.</param>
/// <param name="Size">Bytes, for this host's platform, when the registry said.</param>
/// <param name="Platforms">Empty when unknown.</param>
/// <param name="PreRelease">No member is a stable version.</param>
/// <param name="FitsHost">False only when the platforms are known and none is this host's.</param>
public sealed record TagRow(string Tag, string? Version, IReadOnlyList<string> Also, bool Inferred,
    IReadOnlyList<string> Families, string? Digest, DateTime? Pushed, long? Size, IReadOnlyList<string> Platforms,
    bool PreRelease, bool FitsHost);

/// <summary>A variant chip: the family's name ("" = plain), its label ("Standard" for ""), its rows, and whether it's the default.</summary>
public sealed record TagFamily(string Name, string Label, int Rows, bool Default);

/// <summary>An image's tags, ranked.</summary>
/// <param name="Image">As people write it: nginx, ghcr.io/o/r.</param>
/// <param name="Registry">"Docker Hub", or the registry's host.</param>
/// <param name="Dates">The registry gave push dates (Docker Hub does, tags/list doesn't).</param>
/// <param name="Families">The chips, default first, at most 12.</param>
/// <param name="Versions">Rows whose tag is a version, newest first, pre-releases among them (flagged).</param>
/// <param name="Other">Channels that name no version, other schemes and unparsed names, newest push first.</param>
/// <param name="Recommended">latest's row when it's stable and fits the host, else the default family's newest stable one.</param>
/// <param name="Default">The tag to preselect: Recommended's version one part shorter (1.31 for 1.31.2), else its tag; else latest.</param>
/// <param name="Read">Tags read, after hiding signatures, CI tags and the like.</param>
/// <param name="Total">The registry's count of tags.</param>
/// <param name="More">Older pages exist (Docker Hub): Load older tags.</param>
public sealed record TagList(string Image, string Registry, bool Dates, IReadOnlyList<TagFamily> Families,
    IReadOnlyList<TagRow> Versions, IReadOnlyList<TagRow> Other, TagRow? Recommended, string? Default,
    int Read, int Total, bool More, LookupProblem Problem = LookupProblem.None, string? Message = null, DateTime? RetryAt = null);

/// <summary>A row as one chip shows it.</summary>
/// <param name="Tag">The row's member in that family with the most parts: 1.31.2-alpine under alpine.</param>
/// <param name="Follows">That family's other members naming it now (1.31, 1, latest): the tags that follow it.</param>
public sealed record TagChoice(TagRow Row, string Tag, IReadOnlyList<string> Follows, bool Recommended);
