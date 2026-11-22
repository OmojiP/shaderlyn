package dev.shaderlyn.rider

import com.intellij.openapi.components.BaseState
import com.intellij.openapi.components.Service
import com.intellij.openapi.components.SimplePersistentStateComponent
import com.intellij.openapi.components.State
import com.intellij.openapi.components.Storage
import com.intellij.openapi.components.service
import com.intellij.openapi.fileChooser.FileChooserDescriptorFactory
import com.intellij.openapi.options.BoundConfigurable
import com.intellij.openapi.project.ProjectManager
import com.intellij.platform.lsp.api.LspClientManager
import com.intellij.ui.dsl.builder.AlignX
import com.intellij.ui.dsl.builder.bindText
import com.intellij.ui.dsl.builder.panel

/**
 * Shaderlyn の設定 (Rider 全体で共通。リポジトリのファイルには書かない)。
 */
@Service(Service.Level.APP)
@State(name = "ShaderlynSettings", storages = [Storage("shaderlyn.xml")])
internal class ShaderlynSettings : SimplePersistentStateComponent<ShaderlynSettings.Values>(Values()) {
    internal class Values : BaseState() {
        /** 起動する言語サーバーの実行ファイル。空の場合は、プラグインに同梱したものを使う。 */
        var serverPath by string("")
    }

    companion object {
        fun getInstance(): ShaderlynSettings = service()
    }
}

/**
 * 設定の画面 (Settings > Tools > Shaderlyn)。
 *
 * 実行ファイルを変更すると、開いているプロジェクトの言語サーバーを起動し直す。
 */
internal class ShaderlynConfigurable : BoundConfigurable("Shaderlyn") {
    private var serverPath: String = ShaderlynSettings.getInstance().state.serverPath.orEmpty()

    override fun createPanel() = panel {
        group("言語サーバー") {
            row("実行ファイル:") {
                textFieldWithBrowseButton(FileChooserDescriptorFactory.singleFile().withTitle("言語サーバーの実行ファイル"))
                    .bindText(::serverPath)
                    .align(AlignX.FILL)
                    .comment("空の場合は、プラグインに同梱したものを使います。変更すると言語サーバーを起動し直します。")
            }
        }
    }

    override fun reset() {
        serverPath = ShaderlynSettings.getInstance().state.serverPath.orEmpty()
        super.reset()
    }

    override fun apply() {
        super.apply()
        val settings = ShaderlynSettings.getInstance().state
        val changed = settings.serverPath.orEmpty() != serverPath.trim()
        settings.serverPath = serverPath.trim()

        if (changed) {
            for (project in ProjectManager.getInstance().openProjects) {
                LspClientManager.getInstance(project).stopAndRestartClientsIfNeeded(ShaderlynLspIntegrationProvider::class.java)
            }
        }
    }
}
