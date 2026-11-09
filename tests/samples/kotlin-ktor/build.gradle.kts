plugins {
    kotlin("jvm") version "2.4.21"
    application
}

repositories {
    mavenCentral()
}

dependencies {
    implementation("io.ktor:ktor-server-netty:3.6.1")
}

kotlin {
    jvmToolchain(21)
}

application {
    mainClass = "ApplicationKt"
}
