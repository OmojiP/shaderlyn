package dev.shaderlyn.rider

import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * 言語サーバーを起こすファイルの判定の試験。
 *
 * 拡張子の一覧が VS Code・Visual Studio・サーバーと揃っていることは、リポジトリの EditorExtensionListTests が確かめる。
 */
class ShaderFileNameTest {
    @Test
    fun shaderの拡張子を対象にする() {
        for (name in listOf("A.shader", "a.hlsl", "Common.cginc", "x.compute", "y.usf", "z.ush", "m.slang", "w.wgsl", "g.gdshader", "v.vert")) {
            assertTrue(name, isShaderFileName(name))
        }
    }

    @Test
    fun 大文字の拡張子も対象にする() {
        assertTrue(isShaderFileName("Lit.SHADER"))
        assertTrue(isShaderFileName("Common.HLSL"))
    }

    @Test
    fun ほかの拡張子と拡張子の無いファイルは対象にしない() {
        for (name in listOf("A.cs", "mesh.mesh", "shader", "README.md", "a.shader.meta")) {
            assertFalse(name, isShaderFileName(name))
        }
    }
}
