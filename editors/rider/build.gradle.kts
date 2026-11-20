import org.jetbrains.intellij.platform.gradle.TestFrameworkType
import org.jetbrains.intellij.platform.gradle.tasks.PrepareSandboxTask

plugins {
    id("org.jetbrains.kotlin.jvm") version "2.4.10"
    id("org.jetbrains.intellij.platform") version "2.20.1"
}

group = "dev.shaderlyn"
version = providers.gradleProperty("pluginVersion").get()

kotlin {
    jvmToolchain(21)
}

repositories {
    mavenCentral()
    intellijPlatform {
        defaultRepositories()
    }
}

dependencies {
    intellijPlatform {
        // 手元の Rider を使う (ダウンロードしない)。指定が無ければ riderVersion の Rider を取得する。
        val local = providers.gradleProperty("riderLocalPath")
        if (local.isPresent) {
            local(local.get())
        } else {
            rider(providers.gradleProperty("riderVersion").get()) { useInstaller = false }
        }

        bundledModule("intellij.platform.lsp")
        testFramework(TestFrameworkType.Platform)
    }

    testImplementation("junit:junit:4.13.2")
}

intellijPlatform {
    pluginConfiguration {
        ideaVersion {
            sinceBuild = providers.gradleProperty("sinceBuild")
        }
    }
    buildSearchableOptions = false
}

// 試すときに開くフォルダー (-PopenPath=<フォルダー>)。LSP のやり取りを idea.log に残す。
tasks.runIde {
    val open = providers.gradleProperty("openPath")
    if (open.isPresent) {
        args(open.get())
    }
    jvmArgs("-Didea.log.debug.categories=#com.intellij.platform.lsp")
}

// 言語サーバー (shaderlyn-lsp) をプラグインに同梱する。-PserverDirectory=<publish したフォルダー> を渡す。
// buildPlugin の prepareSandbox だけでなく、runIde・テストの prepareSandbox_* にも同梱する。
tasks.withType<PrepareSandboxTask>().configureEach {
    val server = providers.gradleProperty("serverDirectory")
    if (server.isPresent) {
        from(server.get()) {
            include("shaderlyn-lsp", "shaderlyn-lsp.exe")
            into("${rootProject.name}/server")
        }
    }
}
