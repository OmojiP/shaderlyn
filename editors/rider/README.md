# Shaderlyn for Rider

English | [日本語](README.ja.md)

Shaderlyn finds errors in shaders as you edit.
It analyzes Unity ShaderLab and HLSL, Unreal Engine, plain HLSL, GLSL, Godot, Slang and WGSL.
For GLSL, Godot, Slang and WGSL, it reports the same errors as each language's compiler (glslang, Godot 4, slangc and naga).

The file extensions and the configuration needed for each language are the same as for the [Visual Studio extension](../visualstudio/README.md).
The same analysis is available for [VS Code](https://marketplace.visualstudio.com/items?itemName=OmojiP.shaderlyn) and [Visual Studio](https://marketplace.visualstudio.com/items?itemName=OmojiP.shaderlyn-visualstudio).
Supported versions: Rider 2026.3 or later.

## Features

| Feature | Description |
| --- | --- |
| Diagnostics | Squiggles in the editor and the Problems tool window. Syntax and ShaderLab values are checked as you type. Declarations, types and `#include` resolution are checked when a file is opened or saved |
| Quick fixes | Fixes for diagnostics with a single correct fix (typos, for example), and insertion of a suppression comment (`// shaderlyn-disable-next-line <ID>`) |

Hover, completion, go to definition and references come from Rider's own shader support (Unity ShaderLab, HLSL and so on).
Shaderlyn does not provide them, so that they do not appear twice at the same place.

## Installation

Search for **Shaderlyn** on the **Marketplace** tab of **Settings > Plugins** and install it. Restart Rider after installing.
JetBrains Marketplace delivers the build that bundles the language server for your OS and CPU.

Where the Marketplace is not available, download the zip for your platform from the [releases page](https://github.com/OmojiP/shaderlyn/releases).

| OS and CPU | File |
| --- | --- |
| Windows (x64, arm64) | `shaderlyn-rider-<version>-windows-x86_64.zip` / `-windows-arm64.zip` |
| macOS (Apple Silicon, Intel) | `shaderlyn-rider-<version>-mac-arm64.zip` / `-mac-x86_64.zip` |
| Linux (x64, arm64) | `shaderlyn-rider-<version>-linux-x86_64.zip` / `-linux-arm64.zip` |

Select **Install Plugin from Disk...** from the gear menu of **Settings > Plugins** and choose the zip.

For a plugin installed from a zip, Rider may show the warning "Cannot update plugin. Choose where it was installed from".
It means that Rider cannot check for updates because it does not know where the plugin came from. It does not affect Shaderlyn.

## Getting started

- For a Unity project, open the project root (the folder that contains `Assets` and `ProjectSettings`). Package headers are resolved automatically
- Unreal Engine `.usf` and `.ush` files are analyzed as Unreal Engine without any configuration. The engine and project locations are found from the opened file (a file inside the engine, or a `.uproject` in the workspace). If they are not found, set `unreal: engine` / `unreal: project` in `.shaderlyn.yaml`
- For other environments (plain HLSL and so on), set `environment` in `.shaderlyn.yaml` ([environments](https://github.com/OmojiP/shaderlyn/blob/main/docs/guide/environments.md))

## Settings

Rule severities and disabling rules are configured in `.shaderlyn.yaml` ([configuration](https://github.com/OmojiP/shaderlyn/blob/main/docs/guide/configuration.md)). The nearest `.shaderlyn.yaml` above the opened file is used.

Plugin settings are under **Settings > Tools > Shaderlyn**.

| Setting | Description | Default |
| --- | --- | --- |
| 言語サーバー > 実行ファイル (Language server > Executable) | The language server (`shaderlyn-lsp`) to start. When empty, the bundled one is used. Changing it restarts the language server | Empty |

If the specified executable does not exist, a notification says so and the bundled one is used.

## Using custom rules

The bundled language server runs only the built-in rules.
To see diagnostics from your own rules in the editor, build a language server that includes them ([writing custom rules, 5-1](https://github.com/OmojiP/shaderlyn/blob/main/docs/custom-rules/tutorial.md#5-1-エディタでも働かせる))
and set its path in the executable setting.

## Documentation

The documentation is written in Japanese. See the [repository](https://github.com/OmojiP/shaderlyn).

## Privacy

The plugin does not collect telemetry and does not connect to the network. Analysis runs in a local process bundled with the plugin.

## License

MIT License

The intrinsic function tables are generated from DirectXShaderCompiler data. Its copyright notice and license are in THIRD-PARTY-NOTICES.txt bundled with the plugin.
