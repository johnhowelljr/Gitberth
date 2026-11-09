using System.Text.Json.Nodes;
using Gitberth.Apps;
using Gitberth.Data;

namespace Gitberth.Images;

/// <summary>
/// Where an image comes from, by what Gitberth knows rather than by its RepoDigests: Docker's containerd image store gives
/// every image a RepoDigest, the ones built on this host included, so they can't tell a registry's image from a local
/// build. A followed tag is a registry's once Gitberth pulled it or a check found it there (both recorded in ImageChecks).
/// It's this host's own when a check found no registry has it, when Gitberth built it (a Git app's gitberth/&lt;slug&gt;,
/// gitberth:local, or anything carrying the label its builds put on images), or when the sample checker did
/// (gitberth-sample-*); no registry is ever asked about those. Anything else is local until a check says otherwise.
/// </summary>
public static class ImageOrigins
{
    /// <summary>A check's error when no registry has the reference (not found, or refused with no sign-in to try): it's only on this host.</summary>
    public const string OnlyHere = "Only on this host: no registry has it.";

    /// <summary>How long a reference no registry had is left alone, unless the owner checks it or signs in to its registry.</summary>
    public static readonly TimeSpan OnlyHereFor = TimeSpan.FromDays(7);

    /// <summary>The label Gitberth's builds put on their images (with gitberth.app): the commit they were built from.</summary>
    public const string BuildLabel = "gitberth.commit";

    /// <summary>Built by Gitberth or by the sample checker: no registry is asked about it, by the watcher or the owner.</summary>
    /// <param name="image">The image it names on the host, when known: its labels may say Gitberth built it.</param>
    public static bool NeverAsked(ImageReference reference, JsonObject? image = null) =>
        reference.IsDockerHub && (reference.Repository == "library/gitberth" || reference.Repository.StartsWith("gitberth/", StringComparison.Ordinal)
                                  || reference.Repository.StartsWith("library/gitberth-sample-", StringComparison.Ordinal))
        || image?["Config"]?["Labels"] is JsonObject labels && labels.ContainsKey(BuildLabel);

    /// <summary>A check found the reference in its registry, or Gitberth pulled it (which records the same).</summary>
    public static bool Confirmed(ImageCheck? check) => check?.RemoteDigest is { Length: > 0 };

    /// <summary>A check found that no registry has it.</summary>
    public static bool OnlyOnHost(ImageCheck? check) => check?.Error == OnlyHere;

    /// <summary>A followed tag's origin: Registry once a registry is known to have it, else Local.</summary>
    /// <param name="check">The last check of <paramref name="reference"/>'s canonical form, if any.</param>
    public static ImageOrigin Of(ImageReference reference, ImageCheck? check, JsonObject? image = null) =>
        !NeverAsked(reference, image) && Confirmed(check) ? ImageOrigin.Registry : ImageOrigin.Local;
}
