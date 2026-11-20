using Microsoft.VisualStudio.Extensibility;

namespace Shaderlyn.VisualStudio;

/// <summary>
/// Visual Studio に登録する拡張。
/// </summary>
[VisualStudioContribution]
internal sealed class ShaderlynExtension : Extension
{
    /// <inheritdoc/>
    public override ExtensionConfiguration ExtensionConfiguration => new()
    {
        Metadata = new(
            id: "Shaderlyn.VisualStudio",
            version: ExtensionAssemblyVersion,
            publisherName: "OmojiP",
            displayName: "Shaderlyn",
            description: "Unity・Unreal Engine・Godot・Slang・WGSL などのシェーダーの誤りを、エディタで指摘します。"),
    };
}
