using Gitberth.Detection;

namespace Gitberth.Tests;

public class JvmTests
{
    private static DetectionResult Analyze(TempRepo repo, string? hint = null, string? dir = null) =>
        new ProjectAnalyzer().Analyze(repo.Root, new AnalyzeOptions(hint, AppDir: dir));

    private static string Lf(string? text) => (text ?? "").ReplaceLineEndings("\n");

    private static string Pom(string inside, string parent = "") => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <project xmlns="http://maven.apache.org/POM/4.0.0">
          <modelVersion>4.0.0</modelVersion>
          {parent}
          <groupId>com.example</groupId>
          {inside}
        </project>
        """;

    private const string BootParent = "<parent><groupId>org.springframework.boot</groupId><artifactId>spring-boot-starter-parent</artifactId><version>4.1.2</version><relativePath/></parent>";
    private const string BootPlugin = "<build><plugins><plugin><groupId>org.springframework.boot</groupId><artifactId>spring-boot-maven-plugin</artifactId></plugin></plugins></build>";

    [Fact]
    public void Spring_boot_with_maven_wrapper_builds_with_mvnw_and_listens_on_its_configured_port()
    {
        using var repo = new TempRepo()
            .With("pom.xml", Pom($"<artifactId>shop</artifactId><properties><java.version>17</java.version></properties>{BootPlugin}", BootParent))
            .With("mvnw", "#!/bin/sh\n")
            .With(".mvn/wrapper/maven-wrapper.properties", "distributionUrl=https://repo.maven.apache.org/maven2/org/apache/maven/apache-maven/3.9.9/apache-maven-3.9.9-bin.zip\n")
            .With("src/main/resources/application.yml", "spring:\n  application:\n    name: shop\nserver:\n  port: ${PORT:9000} # the port\n");
        var r = Analyze(repo);

        Assert.Equal(ProjectKind.Java, r.Kind);
        Assert.Equal("Java (Spring Boot)", r.Title);
        Assert.Equal(9000, r.ContainerPort);
        Assert.Equal(
        [
            "Found pom.xml; builds with Maven (the repo's mvnw), skipping the tests",
            "Java 17 (<java.version> in pom.xml)",
            "Trusts the proxy's X-Forwarded-* headers (SERVER_FORWARD_HEADERS_STRATEGY=framework), so the app sees HTTPS and the visitor's address",
            "Listens on port 9000 (server.port in the app's config)",
            "Runs the Spring Boot jar with `java -jar app.jar`",
            "The heap is sized from the container's memory limit (-XX:MaxRAMPercentage=75 in JAVA_TOOL_OPTIONS)",
        ], r.Evidence);
        Assert.Empty(r.Warnings);
        Assert.Equal("""
            FROM eclipse-temurin:17-jdk AS build
            WORKDIR /app
            COPY . .
            # gitberth:build-args
            RUN --mount=type=cache,id=maven,target=/root/.m2 \
                sh mvnw -B -DskipTests package
            # The runnable jar is the biggest one the build made, rather than a -plain, -sources or original one.
            RUN cp "$(ls -S target/*.jar | head -n 1)" /app.jar

            FROM eclipse-temurin:17-jre
            WORKDIR /app
            COPY --from=build /app.jar app.jar
            # The heap can grow to 75% of the container's memory limit. Spring Boot trusts the X-Forwarded-* headers of the proxy in front (Cloudflare Tunnel).
            ENV PORT=9000 JAVA_TOOL_OPTIONS="-XX:MaxRAMPercentage=75" \
                SERVER_FORWARD_HEADERS_STRATEGY=framework
            # gitberth:volumes
            EXPOSE 9000
            CMD ["sh", "-c", "export SERVER_PORT=${PORT:-9000} && exec java -jar app.jar"]

            """.ReplaceLineEndings("\n"), Lf(r.Dockerfile));
    }

    [Fact]
    public void Spring_boot_without_its_maven_plugin_is_repackaged_through_the_starter_parent()
    {
        using var repo = new TempRepo().With("pom.xml", Pom("<artifactId>api</artifactId>", BootParent));
        var r = Analyze(repo);
        Assert.Contains("mvn -B -DskipTests package spring-boot:repackage", r.Dockerfile);
        Assert.Contains("FROM maven:3-eclipse-temurin-21 AS build", r.Dockerfile);
        Assert.Contains("Java 21 (Gitberth's default)", r.Evidence);
    }

    [Fact]
    public void Spring_boot_war_runs_as_an_executable_war()
    {
        using var maven = new TempRepo().With("pom.xml", Pom($"<artifactId>w</artifactId><packaging>war</packaging>{BootPlugin}", BootParent));
        var r = Analyze(maven);
        Assert.Contains("RUN cp \"$(ls -S target/*.war | head -n 1)\" /app.war", r.Dockerfile);
        Assert.Contains("COPY --from=build /app.war app.war", r.Dockerfile);
        Assert.Contains("exec java -jar app.war", r.Dockerfile);

        using var gradle = new TempRepo().With("build.gradle", "plugins {\n  id 'war'\n  id 'org.springframework.boot' version '4.1.2'\n}\n");
        Assert.Contains("gradle --no-daemon bootWar", Analyze(gradle).Dockerfile);
    }

    [Fact]
    public void Spring_boot_with_gradle_groovy_and_its_wrapper_runs_boot_jar()
    {
        using var repo = new TempRepo()
            .With("build.gradle", "plugins {\n  id 'java'\n  id 'org.springframework.boot' version '3.5.6'\n  id 'io.spring.dependency-management' version '1.1.7'\n}\nsourceCompatibility = '17'\n")
            .With("gradlew", "#!/bin/sh\n")
            .With("gradle/wrapper/gradle-wrapper.jar", "jar")
            .With("src/main/resources/application.properties", "server.forward-headers-strategy=native\nserver.port=8085\n");
        var r = Analyze(repo);

        Assert.Equal(8085, r.ContainerPort);
        var dockerfile = Lf(r.Dockerfile);
        Assert.Contains("FROM eclipse-temurin:17-jdk AS build\nWORKDIR /app\nENV GRADLE_USER_HOME=/cache/gradle\n", dockerfile);
        Assert.Contains("RUN --mount=type=cache,id=gradle,target=/cache/gradle \\\n    sh gradlew --no-daemon bootJar\n", dockerfile);
        Assert.Contains("RUN cp \"$(ls -S build/libs/*.jar | head -n 1)\" /app.jar\n", dockerfile);
        // The app chose its own forwarded-headers strategy, so Gitberth doesn't override it.
        Assert.DoesNotContain("SERVER_FORWARD_HEADERS_STRATEGY", dockerfile);
        Assert.Contains("Java 17 (Gradle's Java compatibility setting)", r.Evidence);
    }

    [Fact]
    public void Gradle_wrapper_without_its_jar_falls_back_to_the_gradle_image()
    {
        using var repo = new TempRepo()
            .With("build.gradle.kts", "plugins {\n    id(\"org.springframework.boot\") version \"4.1.2\"\n    java\n}\njava { toolchain { languageVersion = JavaLanguageVersion.of(25) } }\n")
            .With("gradlew", "#!/bin/sh\n");
        var r = Analyze(repo);
        Assert.Contains("FROM gradle:9-jdk25 AS build", r.Dockerfile);
        Assert.Contains("    gradle --no-daemon bootJar\n", Lf(r.Dockerfile));
        Assert.Contains(r.Warnings, w => w.Contains("gradle/wrapper/gradle-wrapper.jar"));
    }

    [Fact]
    public void Maven_multi_module_build_runs_its_application_module()
    {
        using var repo = new TempRepo()
            .With("pom.xml", Pom("<artifactId>parent</artifactId><version>1.0</version><packaging>pom</packaging><modules><module>core</module><module>web</module></modules><properties><maven.compiler.release>21</maven.compiler.release></properties>"))
            .With("core/pom.xml", Pom("<artifactId>core</artifactId>", "<parent><groupId>com.example</groupId><artifactId>parent</artifactId><version>1.0</version></parent>"))
            .With("web/pom.xml", Pom($"<artifactId>web</artifactId>{BootPlugin}", "<parent><groupId>com.example</groupId><artifactId>parent</artifactId><version>1.0</version></parent>"));
        var r = Analyze(repo);

        Assert.Equal("Java (Spring Boot)", r.Title);
        Assert.Contains("Multi-module build: runs the web module", r.Evidence);
        Assert.Contains("    mvn -B -DskipTests -pl web -am package\n", Lf(r.Dockerfile));
        Assert.Contains("ls -S web/target/*.jar", r.Dockerfile);
        Assert.Contains("Java 21 (<maven.compiler.release> in pom.xml)", r.Evidence);

        // The module can't build on its own, so it isn't offered, and analyzing it says where the build is.
        var module = Analyze(repo, dir: "web");
        Assert.Equal(ProjectKind.Unknown, module.Kind);
        Assert.Contains(module.Warnings, w => w.Contains("module of the Maven build in the repo root"));
        var apps = new ProjectAnalyzer().FindApps(repo.Root);
        Assert.Equal([""], apps.Select(a => a.Dir));
    }

    [Fact]
    public void Gradle_multi_project_build_with_a_version_catalog_runs_the_app_project()
    {
        using var repo = new TempRepo()
            .With("settings.gradle.kts", "rootProject.name = \"store\"\ninclude(\n    \":app\",\n    \":lib\",\n)\n")
            .With("gradle/libs.versions.toml", "[versions]\nboot = \"4.1.2\"\n\n[plugins]\nspring-boot = { id = \"org.springframework.boot\", version.ref = \"boot\" }\nkotlin-jvm = \"org.jetbrains.kotlin.jvm:2.4.21\"\n")
            .With("build.gradle.kts", "plugins {\n    alias(libs.plugins.spring.boot) apply false\n    alias(libs.plugins.kotlin.jvm) apply false\n}\n")
            .With("app/build.gradle.kts", "plugins {\n    alias(libs.plugins.kotlin.jvm)\n    alias(libs.plugins.spring.boot)\n}\nkotlin { jvmToolchain(21) }\n")
            .With("lib/build.gradle.kts", "plugins {\n    `java-library`\n}\n")
            .With("gradlew", "#!/bin/sh\n")
            .With("gradle/wrapper/gradle-wrapper.jar", "jar");
        var r = Analyze(repo);

        Assert.Equal("Kotlin (Spring Boot)", r.Title);
        Assert.Contains("    sh gradlew --no-daemon :app:bootJar\n", Lf(r.Dockerfile));
        Assert.Contains("ls -S app/build/libs/*.jar", r.Dockerfile);
        Assert.Contains("Java 21 (Gradle toolchain in the build file)", r.Evidence);
        // The module applies its plugins through the root's version catalog, and still gets told where its build is.
        var module = Analyze(repo, dir: "app");
        Assert.Equal(ProjectKind.Unknown, module.Kind);
        Assert.Contains(module.Warnings, w => w.Contains("module of the Gradle build in the repo root"));
    }

    [Fact]
    public void Two_applications_in_one_build_say_which_one_runs()
    {
        using var repo = new TempRepo()
            .With("settings.gradle", "include 'admin-api', 'api'\n")
            .With("admin-api/build.gradle", "plugins { id 'org.springframework.boot' version '4.1.2' }\n")
            .With("api/build.gradle", "plugins { id 'org.springframework.boot' version '4.1.2' }\n");
        var r = Analyze(repo);
        Assert.Contains("gradle --no-daemon :api:bootJar", r.Dockerfile);
        Assert.Contains(r.Warnings, w => w.Contains("2 applications (api, admin-api)"));
    }

    [Fact]
    public void Quarkus_runs_its_fast_jar_and_trusts_the_proxy()
    {
        using var repo = new TempRepo()
            .With("build.gradle.kts", "plugins {\n    java\n    id(\"io.quarkus\")\n}\n")
            .With("settings.gradle.kts", "pluginManagement { plugins { id(\"io.quarkus\") version \"3.40.1\" } }\nrootProject.name = \"greeter\"\n")
            .With("src/main/resources/application.properties", "quarkus.http.port=8090\n");
        var r = Analyze(repo);

        Assert.Equal("Java (Quarkus)", r.Title);
        Assert.Equal(8090, r.ContainerPort);
        var dockerfile = Lf(r.Dockerfile);
        Assert.Contains("    gradle --no-daemon quarkusBuild\n", dockerfile);
        Assert.Contains("COPY --from=build /app/build/quarkus-app/ ./\n", dockerfile);
        Assert.Contains("QUARKUS_HTTP_HOST=0.0.0.0 QUARKUS_HTTP_PROXY_PROXY_ADDRESS_FORWARDING=true", dockerfile);
        Assert.Contains("CMD [\"sh\", \"-c\", \"export QUARKUS_HTTP_PORT=${PORT:-8090} && exec java -jar quarkus-run.jar\"]", dockerfile);
    }

    [Fact]
    public void Quarkus_uber_jar_runs_as_one_jar()
    {
        using var repo = new TempRepo()
            .With("pom.xml", Pom("<artifactId>q</artifactId><build><plugins><plugin><groupId>io.quarkus.platform</groupId><artifactId>quarkus-maven-plugin</artifactId></plugin></plugins></build>"))
            .With("src/main/resources/application.properties", "quarkus.package.jar.type=uber-jar\nquarkus.http.proxy.proxy-address-forwarding=true\n");
        var r = Analyze(repo);
        Assert.Contains("RUN cp \"$(ls -S target/*.jar | head -n 1)\" /app.jar", r.Dockerfile);
        Assert.Contains("exec java -jar app.jar", r.Dockerfile);
        Assert.DoesNotContain("QUARKUS_HTTP_PROXY", r.Dockerfile);
    }

    [Fact]
    public void Micronaut_listens_on_its_own_port_variable()
    {
        using var maven = new TempRepo().With("pom.xml", Pom("<artifactId>m</artifactId>", "<parent><groupId>io.micronaut.platform</groupId><artifactId>micronaut-parent</artifactId><version>4.9.0</version></parent>"));
        var r = Analyze(maven);
        Assert.Equal("Java (Micronaut)", r.Title);
        Assert.Contains("export MICRONAUT_SERVER_PORT=${PORT:-8080} && exec java -jar app.jar", r.Dockerfile);

        using var gradle = new TempRepo()
            .With("build.gradle.kts", "plugins {\n    id(\"io.micronaut.application\") version \"4.5.4\"\n}\napplication { mainClass = \"example.Application\" }\n")
            .With("settings.gradle.kts", "rootProject.name = \"hello\"\n");
        var g = Analyze(gradle);
        Assert.Contains("gradle --no-daemon installDist", g.Dockerfile);
        Assert.Contains("COPY --from=build /app/build/install/hello/ ./", g.Dockerfile);
        Assert.Contains("export MICRONAUT_SERVER_PORT=${PORT:-8080} && exec bin/hello", g.Dockerfile);
    }

    [Fact]
    public void Ktor_reads_its_port_from_application_conf_and_warns_about_forwarded_headers()
    {
        using var repo = new TempRepo()
            .With("build.gradle.kts", "plugins {\n    kotlin(\"jvm\") version \"2.4.21\"\n    id(\"io.ktor.plugin\") version \"3.6.1\"\n}\ndependencies { implementation(\"io.ktor:ktor-server-netty-jvm\") }\napplication { mainClass.set(\"io.ktor.server.netty.EngineMain\")\n    applicationName = \"shop-api\" }\n")
            .With("settings.gradle.kts", "rootProject.name = \"shop\"\n")
            .With("src/main/resources/application.conf", "ktor {\n    deployment {\n        port = 9090\n        port = ${?PORT}\n    }\n}\n")
            .With("src/main/kotlin/Application.kt", "fun Application.module() { }\n");
        var r = Analyze(repo);

        Assert.Equal("Kotlin (Ktor)", r.Title);
        Assert.Equal(9090, r.ContainerPort);
        Assert.Contains("COPY --from=build /app/build/install/shop-api/ ./", r.Dockerfile);
        Assert.Contains("CMD [\"sh\", \"-c\", \"exec bin/shop-api\"]", r.Dockerfile);
        Assert.Contains(r.Warnings, w => w.Contains("XForwardedHeaders"));

        repo.With("src/main/kotlin/Plugins.kt", "fun Application.proxy() { install(XForwardedHeaders) }\n");
        Assert.DoesNotContain(Analyze(repo).Warnings, w => w.Contains("XForwardedHeaders"));
    }

    [Fact]
    public void Javalin_in_a_shaded_jar_listens_where_its_code_says()
    {
        using var repo = new TempRepo()
            .With("pom.xml", Pom("<artifactId>j</artifactId><dependencies><dependency><groupId>io.javalin</groupId><artifactId>javalin</artifactId></dependency></dependencies><build><plugins><plugin><artifactId>maven-shade-plugin</artifactId></plugin></plugins></build>"))
            .With("src/main/java/app/Main.java", "class Main { public static void main(String[] a) { Javalin.create().start(7070); } }\n");
        var r = Analyze(repo);
        Assert.Equal("Java (Javalin)", r.Title);
        Assert.Equal(7070, r.ContainerPort);
        Assert.Contains("Listens on port 7070 (set in Main.java)", r.Evidence);
    }

    [Fact]
    public void A_library_is_not_offered()
    {
        using var repo = new TempRepo().With("pom.xml", Pom("<artifactId>util</artifactId><packaging>jar</packaging>")).With("src/main/java/Util.java", "class Util {}\n");
        Assert.Equal(ProjectKind.Unknown, Analyze(repo).Kind);
        Assert.Empty(new ProjectAnalyzer().FindApps(repo.Root));
    }

    [Fact]
    public void Short_term_java_releases_run_on_the_next_lts_but_a_toolchain_builds_with_its_own_jdk()
    {
        using var repo = new TempRepo()
            .With("build.gradle", "plugins { id 'application' }\njava { toolchain { languageVersion = JavaLanguageVersion.of(23) } }\n")
            .With("gradlew", "#!/bin/sh\n")
            .With("gradle/wrapper/gradle-wrapper.jar", "jar");
        var r = Analyze(repo);
        Assert.Contains("FROM eclipse-temurin:23-jdk AS build", r.Dockerfile);
        Assert.Contains("FROM eclipse-temurin:25-jre", r.Dockerfile);
        Assert.Contains(r.Warnings, w => w.Contains("Java 23 is a short-term release"));
        // Without a project name in settings.gradle, Gradle names the project after its folder: /app.
        Assert.Contains("exec bin/app", r.Dockerfile);
    }

    [Theory]
    [InlineData(".java-version", "17.0.2\n", 17, ".java-version: 17.0.2")]
    [InlineData(".sdkmanrc", "java=21.0.5-tem\n", 21, ".sdkmanrc: java=21.0.5-tem")]
    [InlineData(".tool-versions", "java temurin-25.0.1+8.0.LTS\n", 25, ".tool-versions: java temurin-25.0.1+8.0.LTS")]
    [InlineData("system.properties", "java.runtime.version=11\n", 11, "system.properties: java.runtime.version=11")]
    public void Java_version_files_are_read(string file, string content, int version, string source)
    {
        using var repo = new TempRepo().With(file, content).With("pom.xml", Pom($"<artifactId>a</artifactId>{BootPlugin}"));
        var r = Analyze(repo);
        Assert.Contains($"Java {version} ({source})", r.Evidence);
        Assert.Contains($"FROM eclipse-temurin:{version}-jre", r.Dockerfile);
    }

    [Fact]
    public void Maven_compiler_settings_resolve_property_references()
    {
        using var repo = new TempRepo().With("pom.xml", Pom($"<artifactId>a</artifactId><properties><jdk>1.8</jdk></properties><build><plugins><plugin><artifactId>spring-boot-maven-plugin</artifactId></plugin><plugin><artifactId>maven-compiler-plugin</artifactId><configuration><source>${{jdk}}</source><target>${{jdk}}</target></configuration></plugin></plugins></build>"));
        var r = Analyze(repo);
        Assert.Contains("Java 8 (<target> of maven-compiler-plugin)", r.Evidence);
        Assert.Contains("FROM maven:3-eclipse-temurin-8 AS build", r.Dockerfile);
    }

    [Fact]
    public void A_front_end_builds_before_the_jvm_build_unless_maven_builds_it()
    {
        using var repo = new TempRepo()
            .With("pom.xml", Pom($"<artifactId>a</artifactId>{BootPlugin}", BootParent))
            .With("package.json", """{ "scripts": { "build": "vite build" }, "devDependencies": { "vite": "^7.0.0" } }""")
            .With("package-lock.json", "{}");
        var r = Analyze(repo);
        Assert.Equal(ProjectKind.Java, r.Kind);
        Assert.Contains("COPY . .\n# gitberth:build-args\nRUN --mount=type=cache,id=npm,target=/root/.npm npm ci\nRUN npm run build\nRUN --mount=type=cache,id=maven", Lf(r.Dockerfile));

        repo.With("pom.xml", Pom($"<artifactId>a</artifactId><build><plugins><plugin><artifactId>spring-boot-maven-plugin</artifactId></plugin><plugin><groupId>com.github.eirslett</groupId><artifactId>frontend-maven-plugin</artifactId></plugin></plugins></build>", BootParent));
        Assert.DoesNotContain("npm", Analyze(repo).Dockerfile);
    }

    [Fact]
    public void The_java_hint_picks_the_app_next_to_a_dockerfile()
    {
        using var repo = new TempRepo().With("pom.xml", Pom($"<artifactId>a</artifactId>{BootPlugin}")).With("Dockerfile", "FROM eclipse-temurin:21\nEXPOSE 8080\n");
        Assert.Equal(ProjectKind.Dockerfile, Analyze(repo).Kind);
        Assert.Equal(ProjectKind.Java, Analyze(repo, "java").Kind);
    }

    [Fact]
    public void Env_scan_finds_getenv_and_placeholders()
    {
        using var repo = new TempRepo()
            .With("pom.xml", Pom($"<artifactId>a</artifactId>{BootPlugin}"))
            .With("src/main/java/app/Keys.java", "class Keys { String k = System.getenv(\"PAYMENT_API_KEY\"); @Value(\"${FEATURE_FLAG:off}\") String f; }\n")
            .With("src/main/resources/application.yml", "spring:\n  datasource:\n    url: ${DATABASE_URL:jdbc:postgresql://localhost/app}\n    password: ${DB_PASSWORD}\n  app:\n    name: ${spring.application.name}\nserver:\n  port: ${PORT:8080}\n")
            .With("src/main/resources/application.conf", "auth { secret = ${?JWT_SECRET} }\n")
            .With("src/test/java/app/KeysTest.java", "class KeysTest { String t = System.getenv(\"TEST_ONLY\"); }\n");
        var env = Analyze(repo).EnvSuggestions.ToDictionary(s => s.Key);

        Assert.True(env["PAYMENT_API_KEY"].IsSecret);
        Assert.Equal("used in src/main/java/app/Keys.java (default off)", env["FEATURE_FLAG"].Source);
        Assert.Equal("used in src/main/resources/application.yml (default jdbc:postgresql://localhost/app)", env["DATABASE_URL"].Source);
        Assert.True(env["DB_PASSWORD"].IsSecret);
        Assert.Contains("JWT_SECRET", env.Keys);
        Assert.DoesNotContain("PORT", env.Keys);
        Assert.DoesNotContain("TEST_ONLY", env.Keys);
        Assert.DoesNotContain(env.Keys, k => k.Contains('.'));
    }

    [Fact]
    public void Yaml_is_flattened_to_dotted_keys()
    {
        var yaml = JavaLanguage.FlattenYaml("# settings\nserver:\n  port: 8081\n  servlet:\n    context-path: /api\nspring.application.name: 'shop'\nlist:\n  - a\n---\nserver:\n  port: 9999\n");
        Assert.Equal("8081", yaml["server.port"]);
        Assert.Equal("/api", yaml["server.servlet.context-path"]);
        Assert.Equal("shop", yaml["spring.application.name"]);
        Assert.False(yaml.ContainsKey("list"));
    }

    [Theory]
    [InlineData("8081", 8081)]
    [InlineData("${PORT:8082}", 8082)]
    [InlineData("${SERVER_PORT:8083}", 8083)]
    [InlineData("$PORT:8084", 8084)]
    [InlineData("${PORT}", null)]
    public void Port_settings_give_their_port(string value, int? port) =>
        Assert.Equal(port, JavaLanguage.ConfigPort(new Dictionary<string, string> { ["server.port"] = value }, "server.port")?.Port);

    [Fact]
    public void Gradle_plugins_includes_and_catalogs_are_read()
    {
        var catalog = JvmProject.CatalogPlugins("[plugins]\nspring-boot = { id = \"org.springframework.boot\", version = \"4.1.2\" }\nktor = \"io.ktor.plugin:3.6.1\"\n");
        Assert.Equal("org.springframework.boot", catalog["spring.boot"]);
        Assert.Equal("io.ktor.plugin", catalog["ktor"]);
        var ids = JvmProject.PluginIds("plugins {\n    alias(libs.plugins.ktor)\n    kotlin(\"plugin.serialization\")\n    application\n    id(\"com.gradleup.shadow\") version \"9.0.0\" apply false\n}\napply plugin: 'war'\n", catalog);
        Assert.Equal(["application", "io.ktor.plugin", "org.jetbrains.kotlin.plugin.serialization", "war"], ids.Order());
        Assert.Equal(["app", "services:billing", "web"], JvmProject.Includes("include(\":app\", \":services:billing\")\ninclude 'web'\nincludeBuild(\"build-logic\")\n").Order());
    }

    [Theory]
    [InlineData("1.8", 8)]
    [InlineData("17", 17)]
    [InlineData("21.0.2", 21)]
    [InlineData("temurin-21.0.2+13.0.LTS", 21)]
    [InlineData("1_8", 8)]
    [InlineData("latest", null)]
    public void Java_versions_are_parsed(string text, int? version) => Assert.Equal(version, JavaVersions.Parse(text));
}
