# Unreal Engine のエディタの中で動かし、シェーダーのデバッグ出力 (前処理済みの <シェーダー>.usf) を書かせる。
#
# 生成される取り込み (/Engine/Generated/...) はコンパイルの直前にメモリ上で作られ、ディスクには無い。
# デバッグ出力の前処理済みのソースには、その中身が #line の区切り付きで残る。
#
# 調べるためのスクリプトである (2026-10-15 に UE 5.5 で確かめた)。取り出した中身をそのまま解析に使うことはしない:
# 1 つの出力には、そのシェーダーが使う uniform buffer しか入らない。前処理の後なので、
# パーミュテーションごとに中身が違い、2 度取り込みを防ぐガード (#ifndef __UniformBuffer_View_Definition__) も消えている。
#
# 使い方 (プロジェクトで Python Editor Script Plugin を有効にしておく):
#
#   UnrealEditor.exe <プロジェクト>.uproject ^
#     -dpcvars="r.ShaderCompiler.DebugDumpDetailedShaderSource=1" ^
#     -ExecCmds="py <このファイル>" -unattended -nosplash
#
# -ExecutePythonScript はスクリプトを走らせた直後にエディタを閉じる (QUIT_EDITOR) ので使わない。
#
# r.ShaderCompiler.DebugDumpDetailedShaderSource は読み取り専用なので、起動時に -dpcvars で与える。
# r.DumpShaderDebugInfo はエンジンの ConsoleVariables.ini が起動後に 2 (エラーだけ) へ上書きするので、このスクリプトで 1 にする。
# 既存のマテリアルを再コンパイルしても、ジョブのキャッシュが当たってコンパイルされない (出力も出ない)。
# そこで、毎回違う色の定数を持つマテリアルを新しく作ってコンパイルさせ、出力を書かせたら消す (保存しない)。
#
# 出力先: <プロジェクト>/Saved/ShaderDebugInfo/<シェーダープラットフォーム>/

import os
import random
import time

import unreal

TEMP_FOLDER = "/Game/ShaderlynTemp"
MATERIAL_NAME = "M_ShaderlynDump"
TIMEOUT_SECONDS = 1800

started = time.time()
dump_root = os.path.join(unreal.Paths.project_saved_dir(), "ShaderDebugInfo")


def log(message):
    unreal.log("[shaderlyn] " + message)


def count_preprocessed():
    count = 0
    for root, _, files in os.walk(dump_root):
        # UE 5.5 は <シェーダー>.usf (前処理済み、#line 付き) と <シェーダー>_Stripped.usf を書く。
        count += sum(1 for name in files if name.endswith(".usf") and not name.endswith("_Stripped.usf"))
    return count


def create_material():
    tools = unreal.AssetToolsHelpers.get_asset_tools()
    material = tools.create_asset(MATERIAL_NAME, TEMP_FOLDER, unreal.Material, unreal.MaterialFactoryNew())

    # 定数のノード (Constant3Vector) の値はシェーダーのコードに入らず、実行時に渡される。
    # コードが既存のマテリアルと同じになり、ジョブのキャッシュが当たってコンパイルされない。
    # Custom ノードのコードに乱数を直接書き、毎回違うコードにする。
    custom = unreal.MaterialEditingLibrary.create_material_expression(material, unreal.MaterialExpressionCustom)
    custom.set_editor_property("output_type", unreal.CustomMaterialOutputType.CMOT_FLOAT3)
    custom.set_editor_property(
        "code",
        "return float3(%f, %f, %f); // shaderlyn %d" % (random.random(), random.random(), random.random(), random.randint(0, 1 << 30)))
    unreal.MaterialEditingLibrary.connect_material_property(custom, "", unreal.MaterialProperty.MP_BASE_COLOR)
    unreal.MaterialEditingLibrary.recompile_material(material)
    return material


def finish(handle):
    unreal.unregister_slate_post_tick_callback(handle)
    unreal.EditorAssetLibrary.delete_directory(TEMP_FOLDER)
    log("dumped shader sources: %d" % count_preprocessed())
    unreal.SystemLibrary.quit_editor()


unreal.SystemLibrary.execute_console_command(None, "r.DumpShaderDebugInfo 1")
before = count_preprocessed()
create_material()
log("material created; waiting for shader compilation (dumped shader sources before: %d)" % before)

state = {"last": -1, "stable_since": time.time()}


def on_tick(delta_seconds):
    current = count_preprocessed()
    now = time.time()

    if current != state["last"]:
        state["last"] = current
        state["stable_since"] = now
        return

    # 出力が増えなくなって 30 秒たったら、コンパイルが終わったとみなす。
    if (current > before and now - state["stable_since"] > 30) or now - started > TIMEOUT_SECONDS:
        finish(handle)


handle = unreal.register_slate_post_tick_callback(on_tick)
