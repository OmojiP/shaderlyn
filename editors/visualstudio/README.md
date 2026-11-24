# Shaderlyn for Visual Studio

English | [日本語](https://github.com/OmojiP/shaderlyn/blob/main/editors/visualstudio/README.ja.md)

Shaderlyn finds errors in shaders as you edit.
It analyzes Unity ShaderLab and HLSL, Unreal Engine, plain HLSL, GLSL, Godot, Slang and WGSL, and also provides hover, completion and navigation.
For GLSL, Godot, Slang and WGSL, it reports the same errors as each language's compiler (glslang, Godot 4, slangc and naga).

| Language | Extensions | Configuration |
| --- | --- | --- |
| Unity ShaderLab and HLSL | `.shader` `.hlsl` `.cginc` `.hlslinc` `.compute` | Not needed (open the Unity project root) |
| Unreal Engine | `.usf` `.ush` | Not needed (the engine and project are found from the opened file) |
| HLSL compiled directly with DXC | `.hlsl` `.hlsli` | Select the environment in `.shaderlyn.yaml` ([environments](https://github.com/OmojiP/shaderlyn/blob/main/docs/guide/environments.md)) |
| GLSL | `.vert` `.frag` `.comp` `.geom` `.tesc` `.tese` `.task` `.rgen` `.rint` `.rahit` `.rchit` `.rmiss` `.rcall` `.glsl` | Not needed |
| Godot | `.gdshader` `.gdshaderinc` | Not needed (the renderer is read from `project.godot`) |
| Slang | `.slang` | Not needed (`import`ed modules and the standard library are used) |
| WGSL | `.wgsl` | Not needed |

The same analysis is available for [VS Code](https://marketplace.visualstudio.com/items?itemName=OmojiP.shaderlyn) and [Rider](https://plugins.jetbrains.com/plugin/36384-shaderlyn).
Supported versions: Visual Studio 2022 (17.14 or later) and Visual Studio 2026.

## Features

| Feature | Description |
| --- | --- |
| Diagnostics | Squiggles and the Error List. Syntax and ShaderLab values are checked as you type. Declarations, types and `#include` resolution are checked when a file is opened or saved |
| Quick fixes | Fixes for diagnostics with a single correct fix (typos, for example), and insertion of a suppression comment (`// shaderlyn-disable-next-line <ID>`) |
| Hover | Types, macro expansions, the mapping between ShaderLab properties and HLSL, semantics and `#pragma` |
| Completion and signature help | Candidates that take conditional compilation into account, and function parameters |
| Go to definition, references, rename | Variables, functions, structs, macros, and ShaderLab properties mapped to HLSL uniforms |

The following features are available only in the [VS Code extension](https://marketplace.visualstudio.com/items?itemName=OmojiP.shaderlyn):

- Dimming `#if` / `#ifdef` blocks whose condition is false
- Selecting symbols and viewing that configuration
- Inspecting the analysis (syntax tree, tokens, types and macros)

## Using with HLSL Tools

When HLSL Tools (part of the "Game development with C++" workload) is installed, it handles `.hlsl`, `.hlsli`, `.cginc`, `.compute`, `.usf` and `.ush`.
For those files Shaderlyn provides diagnostics and quick fixes only, and the hover, completion and go to definition of HLSL Tools stay as they are.
The Error List shows the diagnostics of HLSL Tools (`HLSL0033` and so on) and those of Shaderlyn (`CF0310` and so on) together.

When HLSL Tools is not installed, Shaderlyn provides all features for these files too.

## Installation

Search for **Shaderlyn** in **Extensions > Manage Extensions** and install it ([Visual Studio Marketplace](https://marketplace.visualstudio.com/items?itemName=OmojiP.shaderlyn-visualstudio)).
Restart Visual Studio after installing.

Where the Marketplace is not available, download `shaderlyn-visualstudio.vsix` from the [releases page](https://github.com/OmojiP/shaderlyn/releases) and double-click it to install.

## Getting started

- For a Unity project, open the project root (the folder that contains `Assets` and `ProjectSettings`) with **File > Open > Folder**. Package headers are resolved automatically
- Unreal Engine `.usf` and `.ush` files are analyzed as Unreal Engine without any configuration. The engine and project locations are found from the opened file (a file inside the engine, or a `.uproject` in the workspace). If they are not found, set `unreal: engine` / `unreal: project` in `.shaderlyn.yaml`
- For other environments (plain HLSL and so on), set `environment` in `.shaderlyn.yaml` ([environments](https://github.com/OmojiP/shaderlyn/blob/main/docs/guide/environments.md))

## Settings

Rule severities and disabling rules are configured in `.shaderlyn.yaml` ([configuration](https://github.com/OmojiP/shaderlyn/blob/main/docs/guide/configuration.md)). The nearest `.shaderlyn.yaml` above the opened file is used.

Extension settings are under **Tools > Options > Shaderlyn > 全般** (General). The settings page is in Japanese.
In Visual Studio 2026, selecting **Shaderlyn** in the settings shows that the settings have not been migrated yet. Open the classic Options dialog from the **全般** link below it.

| Setting | Description | Default |
| --- | --- | --- |
| 言語サーバー > 実行ファイル (Language server > Executable) | The language server (`shaderlyn-lsp.exe`) to start. When empty, the bundled one is used. Changing it restarts the language server | Empty |

If the specified executable does not exist, a message says so and the bundled one is used.

## Using custom rules

The bundled language server runs only the built-in rules.
To see diagnostics from your own rules in the editor, build a language server that includes them ([writing custom rules, 5-1](https://github.com/OmojiP/shaderlyn/blob/main/docs/custom-rules/tutorial.md#5-1-エディタでも働かせる))
and set its path in the executable setting.

## Documentation

The documentation is written in Japanese. See the [repository](https://github.com/OmojiP/shaderlyn).

## Privacy

The extension does not collect telemetry and does not connect to the network. Analysis runs in a local process (`shaderlyn-lsp.exe`) bundled with the extension.

## License

MIT License

The intrinsic function tables are generated from DirectXShaderCompiler data. Its copyright notice and license are in THIRD-PARTY-NOTICES.txt bundled with the extension.
