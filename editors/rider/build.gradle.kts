import org.jetbrains.intellij.platform.gradle.IntelliJPlatformType
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
        pluginVerifier()
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

    // JetBrains Marketplace の審査の条件 (Plugin Verifier で互換性を確認する)。gradle verifyPlugin で実行する。
    // 手元の Rider (-PriderLocalPath) があればそれで、無ければ riderVersion の Rider で確かめる。
    pluginVerification {
        ides {
            val local = providers.gradleProperty("riderLocalPath")
            if (local.isPresent) {
                local(file(local.get()))
            } else {
                create(IntelliJPlatformType.Rider, providers.gradleProperty("riderVersion").get()) { useInstaller = false }
            }
        }
    }

    // OS と CPU の組ごとの zip を作る (JetBrains Marketplace が利用者の環境に合うものを配る)。
    // 言語サーバーは Native AOT なので組ごとに違う。-PnativeServerRoot=<フォルダー> に、
    // <runtime>/server/shaderlyn-lsp(.exe) の形で publish した言語サーバーを置いて渡す (runtime は linux-x64 などの .NET の名前)。
    // Windows の arm64 は x64 の言語サーバーを同梱する (Windows 11 の arm64 は x64 を実行できる。リリースで arm64 を作っていないため)。
    val nativeServerRoot = providers.gradleProperty("nativeServerRoot")
    if (nativeServerRoot.isPresent) {
        val root = file(nativeServerRoot.get())
        nativeVariants {
            enabled = true
            linux {
                x86_64.from(root.resolve("linux-x64"))
                arm64.from(root.resolve("linux-arm64"))
            }
            mac {
                x86_64.from(root.resolve("osx-x64"))
                arm64.from(root.resolve("osx-arm64"))
            }
            windows {
                x86_64.from(root.resolve("win-x64"))
                arm64.from(root.resolve("win-x64"))
            }
        }
    }
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
    // ライセンスと、同梱した言語サーバーに入っている第三者のデータ (DXC の組み込み関数の表など) の著作権表示。
    from(rootProject.layout.projectDirectory.dir("../..")) {
        include("LICENSE", "THIRD-PARTY-NOTICES.txt")
        into(rootProject.name)
    }

    val server = providers.gradleProperty("serverDirectory")
    if (server.isPresent) {
        from(server.get()) {
            include("shaderlyn-lsp", "shaderlyn-lsp.exe")
            into("${rootProject.name}/server")
        }
    }
}
