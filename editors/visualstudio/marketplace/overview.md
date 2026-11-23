# Shaderlyn for Visual Studio

Shaderlyn finds errors in shaders as you edit: Unity ShaderLab and HLSL, Unreal Engine, GLSL, Godot, Slang and WGSL.
It uses the same language server as the [VS Code extension](https://marketplace.visualstudio.com/items?itemName=OmojiP.shaderlyn) and reports the same diagnostics.

Supported versions: Visual Studio 2022 (17.14 or later) and Visual Studio 2026.

## Features

| Feature | Description |
| --- | --- |
| Diagnostics | Squiggles and the Error List. Syntax and ShaderLab values are checked as you type. Declarations, types and `#include` resolution are checked when a file is opened or saved |
| Quick fixes | Fixes for diagnostics with a single correct fix (typos, for example), and insertion of a suppression comment (`// shaderlyn-disable-next-line <ID>`) |
| Hover | Types, macro expansions, the mapping between ShaderLab properties and HLSL, semantics and `#pragma` |
| Completion and signature help | Candidates that take conditional compilation into account, and function parameters |
| Go to definition, references, rename | Variables, functions, structs, macros, and ShaderLab properties mapped to HLSL uniforms |

| Language | Extensions |
| --- | --- |
| Unity ShaderLab | `.shader` |
| HLSL | `.hlsl` `.hlsli` `.cginc` `.hlslinc` `.compute` `.usf` `.ush` |
| GLSL | `.vert` `.frag` `.comp` `.geom` `.tesc` `.tese` `.task` `.rgen` `.rint` `.rahit` `.rchit` `.rmiss` `.rcall` `.glsl` |
| Godot | `.gdshader` `.gdshaderinc` |
| Slang | `.slang` |
| WGSL | `.wgsl` |

When HLSL Tools (part of the "Game development with C++" workload) is installed, it handles `.hlsl`, `.hlsli`, `.cginc`, `.compute`, `.usf` and `.ush`.
For those files Shaderlyn provides diagnostics and quick fixes only, and the hover, completion and navigation of HLSL Tools stay as they are.

## Getting started

- For a Unity project, open the project root (the folder that contains `Assets` and `ProjectSettings`) with **File > Open > Folder**. Package headers are resolved automatically.
- Unreal Engine `.usf` and `.ush` files are analyzed as Unreal Engine without any configuration. The engine and project locations are found from the opened file (a file inside the engine, or a `.uproject` in the workspace). If they are not found, set `unreal: engine` / `unreal: project` in `.shaderlyn.yaml`.
- For other environments (plain HLSL and so on), set `environment` in `.shaderlyn.yaml`
  ([environments](https://github.com/OmojiP/shaderlyn/blob/main/docs/guide/environments.md)).
- Rule severities are configured in `.shaderlyn.yaml` ([configuration](https://github.com/OmojiP/shaderlyn/blob/main/docs/guide/configuration.md)).
- Extension settings are under **Tools > Options > Shaderlyn > General**.

The documentation is written in Japanese. See the [repository](https://github.com/OmojiP/shaderlyn) and the [extension README](https://github.com/OmojiP/shaderlyn/blob/main/editors/visualstudio/README.md).

## Privacy

The extension does not collect telemetry and does not connect to the network. Analysis runs in a local process (`shaderlyn-lsp.exe`) bundled with the extension.

## License

MIT License. Third-party notices (the intrinsic function tables are generated from DirectXShaderCompiler data) are in THIRD-PARTY-NOTICES.txt in the extension.

---

# Shaderlyn for Visual Studio (日本語)

Unity の ShaderLab と HLSL、Unreal Engine、GLSL、Godot、Slang、WGSL のシェーダーの誤りを、Visual Studio で指摘する拡張機能です。
解析は VS Code 拡張と同じ言語サーバーで行い、同じ指摘を出します。対象のバージョンは Visual Studio 2022 (17.14 以降) と Visual Studio 2026 です。

主な機能は、指摘 (波線とエラー一覧)、クイックフィックス、ホバー、補完・引数の案内、定義へ移動・参照・名前の変更です。
HLSL Tools が入っている場合は、HLSL Tools が受け持つファイルでは指摘とクイックフィックスだけを出します。

使用方法と設定は [拡張機能の README](https://github.com/OmojiP/shaderlyn/blob/main/editors/visualstudio/README.md) を参照してください。
