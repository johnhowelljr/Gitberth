namespace Gitberth.Detection;

/// <summary>How a JVM app is built and started.</summary>
/// <param name="BuildImage">The build stage's image: maven, gradle, or eclipse-temurin's JDK for a wrapper.</param>
/// <param name="Gradle">Gradle builds it (its cache is GRADLE_USER_HOME), else Maven (~/.m2).</param>
/// <param name="BuildCommand">The build tool's command, like `mvn -B -DskipTests package`.</param>
/// <param name="Node">A package.json front end that builds first, so the jar includes it.</param>
/// <param name="ArtifactPath">Where the build puts it, relative to the build's root: target, build/libs, build/install/app.</param>
/// <param name="Archive">jar, or war for an executable war.</param>
/// <param name="Java">The eclipse-temurin JRE that runs it.</param>
/// <param name="Env">The framework's settings, like SERVER_FORWARD_HEADERS_STRATEGY=framework.</param>
/// <param name="EnvComment">Why those settings are there.</param>
/// <param name="PortVariable">The variable the framework listens on, set from PORT at start (SERVER_PORT for Spring Boot).</param>
/// <param name="Run">The command that starts it, from /app.</param>
internal sealed record JvmPlan(
    string BuildImage, bool Gradle, string BuildCommand, NodeAssets? Node, JvmArtifact Artifact, string ArtifactPath, string Archive, int Java,
    IReadOnlyList<string> Env, string? EnvComment, string? PortVariable, int Port, string Run);

internal static class JvmDockerfiles
{
    // Build output of the root project and of modules one level down; deeper, a folder named build can be a Java package.
    public const string Ignore = ".git\ntarget\nbuild\n.gradle\n*/target\n*/build\n*/.gradle\n**/node_modules\n.idea\n";

    /// <summary>
    /// The JDK builds it with the build tool's caches mounted, skipping tests, and a JRE runs what it made. The heap is
    /// sized from the container's memory limit, and the framework listens on PORT through its own variable.
    /// </summary>
    public static string Render(JvmPlan p) => RubyDockerfiles.Lines(
    [
        $"FROM {p.BuildImage} AS build",
        "WORKDIR /app",
        p.Gradle ? "ENV GRADLE_USER_HOME=/cache/gradle" : null,
        .. p.Node?.Setup() ?? [],
        "COPY . .",
        DockerfileMarkers.BuildArgs,
        p.Node?.Install,
        p.Node?.Build,
        p.Gradle ? "RUN --mount=type=cache,id=gradle,target=/cache/gradle \\" : "RUN --mount=type=cache,id=maven,target=/root/.m2 \\",
        $"    {p.BuildCommand}",
        p.Artifact == JvmArtifact.Jar ? $"# The runnable {p.Archive} is the biggest one the build made, rather than a -plain, -sources or original one." : null,
        p.Artifact == JvmArtifact.Jar ? $"RUN cp \"$(ls -S {p.ArtifactPath}/*.{p.Archive} | head -n 1)\" /app.{p.Archive}" : null,
        "",
        $"FROM eclipse-temurin:{p.Java}-jre",
        "WORKDIR /app",
        .. Copy(p),
        "# The heap can grow to 75% of the container's memory limit." + (p.EnvComment is null ? "" : " " + p.EnvComment),
        "ENV " + string.Join(" \\\n    ", new[] { $"PORT={p.Port}", "JAVA_TOOL_OPTIONS=\"-XX:MaxRAMPercentage=75\"" }.Concat(p.Env).Chunk(2).Select(c => string.Join(' ', c))),
        DockerfileMarkers.Volumes,
        $"EXPOSE {p.Port}",
        $"CMD {RubyDockerfiles.Json("sh", "-c", (p.PortVariable is null ? "" : $"export {p.PortVariable}=${{PORT:-{p.Port}}} && ") + "exec " + p.Run)}",
    ]);

    private static IEnumerable<string> Copy(JvmPlan p) => p.Artifact switch
    {
        JvmArtifact.Jar => [$"COPY --from=build /app.{p.Archive} app.{p.Archive}"],
        JvmArtifact.QuarkusApp => ["# Quarkus' fast-jar layout: quarkus-run.jar, with lib/, app/ and quarkus/ next to it.", $"COPY --from=build /app/{p.ArtifactPath}/ ./"],
        _ => ["# The application plugin's layout: the start script in bin/, the jars in lib/.", $"COPY --from=build /app/{p.ArtifactPath}/ ./"],
    };
}
