package dev.shaderlyn.rider

import com.google.gson.JsonObject
import com.intellij.execution.configurations.GeneralCommandLine
import com.intellij.ide.plugins.PluginManagerCore
import com.intellij.openapi.extensions.PluginId
import com.intellij.openapi.project.Project
import com.intellij.openapi.util.SystemInfo
import com.intellij.openapi.vfs.VirtualFile
import com.intellij.platform.lsp.api.LspIntegrationProvider
import com.intellij.platform.lsp.api.ProjectWideLspClientDescriptor
import com.intellij.platform.lsp.api.customization.LspCompletionCustomizer
import com.intellij.platform.lsp.api.customization.LspCompletionDisabled
import com.intellij.platform.lsp.api.customization.LspCustomization
import com.intellij.platform.lsp.api.customization.LspFindReferencesCustomizer
import com.intellij.platform.lsp.api.customization.LspFindReferencesDisabled
import com.intellij.platform.lsp.api.customization.LspGoToDefinitionCustomizer
import com.intellij.platform.lsp.api.customization.LspGoToDefinitionDisabled
import com.intellij.platform.lsp.api.customization.LspHoverCustomizer
import com.intellij.platform.lsp.api.customization.LspHoverDisabled
import java.nio.file.Files

/**
 * Shaderlyn が解析するファイルを開いたら、言語サーバーを起こす。
 *
 * 拡張子は VS Code 拡張の documentSelector と揃える (サーバーの ShaderSourceKinds・各環境の Extensions)。
 */
internal class ShaderlynLspIntegrationProvider : LspIntegrationProvider {
    override fun fileOpened(project: Project, file: VirtualFile, clientStarter: LspIntegrationProvider.LspClientStarter) {
        if (isShaderFile(file)) {
            clientStarter.ensureClientStarted(ShaderlynLspClientDescriptor(project))
        }
    }
}

/** Shaderlyn が解析するファイルの拡張子。 */
private val shaderExtensions = setOf(
    "shader", "hlsl", "hlsli", "cginc", "hlslinc", "compute", "usf", "ush",
    "vert", "frag", "comp", "geom", "tesc", "tese", "task", "rgen", "rint", "rahit", "rchit", "rmiss", "rcall", "glsl",
    "gdshader", "gdshaderinc", "slang", "wgsl",
)

internal fun isShaderFile(file: VirtualFile): Boolean = file.extension?.lowercase() in shaderExtensions

/**
 * プロジェクトに 1 つの言語サーバー。
 *
 * Rider は Unity の ShaderLab と HLSL の補完・ホバー・移動を自前で持つので、重ねずに指摘とコードアクションだけを使う。
 * サーバーにも初期化の設定 `languageFeatures: false` で同じことを伝え、名乗る機能を絞らせる。
 */
private class ShaderlynLspClientDescriptor(project: Project) : ProjectWideLspClientDescriptor(project, "Shaderlyn") {
    override fun isSupportedFile(file: VirtualFile): Boolean = isShaderFile(file)

    override fun createCommandLine(): GeneralCommandLine = GeneralCommandLine(serverPath().toString())

    override fun createInitializationOptions(): Any = JsonObject().apply { addProperty("languageFeatures", false) }

    override val lspCustomization: LspCustomization = object : LspCustomization() {
        override val hoverCustomizer: LspHoverCustomizer = LspHoverDisabled
        override val completionCustomizer: LspCompletionCustomizer = LspCompletionDisabled
        override val goToDefinitionCustomizer: LspGoToDefinitionCustomizer = LspGoToDefinitionDisabled
        override val findReferencesCustomizer: LspFindReferencesCustomizer = LspFindReferencesDisabled
    }

    /**
     * 起動する言語サーバー。
     *
     * 環境変数 `SHADERLYN_SERVER_PATH` に実行ファイルを指定すると、同梱したものの代わりにそれを使う (開発中のサーバーを試すときなど)。
     * 設定の画面には置かない。設定は .shaderlyn.yaml だけにする方針で、実行ファイルの場所はリポジトリのファイルに書かせない。
     */
    private fun serverPath(): java.nio.file.Path {
        System.getenv(SERVER_PATH_VARIABLE)?.takeIf { it.isNotBlank() }?.let { specified ->
            val path = java.nio.file.Path.of(specified)
            check(Files.isRegularFile(path)) { "$SERVER_PATH_VARIABLE に指定された言語サーバーが見つかりません: $path" }
            return path
        }

        val plugin = PluginManagerCore.getPlugin(PluginId.getId(PLUGIN_ID))
            ?: error("プラグイン $PLUGIN_ID が見つかりません。")
        val executable = if (SystemInfo.isWindows) "shaderlyn-lsp.exe" else "shaderlyn-lsp"
        val path = plugin.pluginPath.resolve("server").resolve(executable)
        check(Files.isRegularFile(path)) { "言語サーバーが見つかりません: $path" }
        return path
    }
}

internal const val PLUGIN_ID = "dev.shaderlyn.rider"

/** 同梱したものの代わりに使う言語サーバーを指定する環境変数。 */
internal const val SERVER_PATH_VARIABLE = "SHADERLYN_SERVER_PATH"
