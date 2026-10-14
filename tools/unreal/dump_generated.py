# Unreal Engine のエディタの中で動かし、シェーダーのデバッグ出力 (Preprocessed_*.usf) を書かせる。
#
# 生成される取り込み (/Engine/Generated/...) はコンパイルの直前にメモリ上で作られ、ディスクには無い。
# デバッグ出力の前処理済みのソースには、その中身が #line の区切り付きで残る。
# shaderlyn unreal extract-generated で、そこから取り出す。
#
# 使い方 (プロジェクトで Python Editor Script Plugin を有効にしておく):
#
#   UnrealEditor.exe <プロジェクト>.uproject ^
#     -dpcvars="r.ShaderCompiler.DebugDumpDetailedShaderSource=1" ^
#     -ExecutePythonScript="<このファイル>" -unattended -nosplash
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
        count += sum(1 for name in files if name.startswith("Preprocessed_"))
    return count


def create_material():
    tools = unreal.AssetToolsHelpers.get_asset_tools()
    material = tools.create_asset(MATERIAL_NAME, TEMP_FOLDER, unreal.Material, unreal.MaterialFactoryNew())
    color = unreal.MaterialEditingLibrary.create_material_expression(material, unreal.MaterialExpressionConstant3Vector)
    color.set_editor_property("constant", unreal.LinearColor(random.random(), random.random(), random.random(), 1.0))
    unreal.MaterialEditingLibrary.connect_material_property(color, "", unreal.MaterialProperty.MP_BASE_COLOR)
    unreal.MaterialEditingLibrary.recompile_material(material)
    return material


def finish(handle):
    unreal.unregister_slate_post_tick_callback(handle)
    unreal.EditorAssetLibrary.delete_directory(TEMP_FOLDER)
    log("Preprocessed files: %d" % count_preprocessed())
    unreal.SystemLibrary.quit_editor()


unreal.SystemLibrary.execute_console_command(None, "r.DumpShaderDebugInfo 1")
before = count_preprocessed()
create_material()
log("material created; waiting for shader compilation (Preprocessed files before: %d)" % before)

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
