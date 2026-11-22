using System.ComponentModel.Composition;
using Microsoft.VisualStudio.LanguageServer.Client;
using Microsoft.VisualStudio.Utilities;

namespace Shaderlyn.VisualStudio;

/// <summary>
/// Shaderlyn が解析するファイルの種類 (コンテンツの種類) と、拡張子との対応。
/// </summary>
/// <remarks>
/// 拡張子は、VS Code 拡張の documentSelector と揃える (サーバーの ShaderSourceKinds・各環境の Extensions)。
/// </remarks>
internal static class ShaderContentType
{
    /// <summary>コンテンツの種類の名前。</summary>
    public const string Name = "shaderlyn-shader";

    /// <summary>
    /// HLSL Tools (「C++ によるゲーム開発」ワークロードに入る) のコンテンツの種類の名前。
    /// HLSL Tools が入っていると、.hlsl・.hlsli・.cginc・.compute・.usf・.ush などはこの種類になる。
    /// </summary>
    public const string HlslToolsName = "HLSL";

#pragma warning disable CS0649 // MEF が値を見ずに属性だけを読む。

    [Export]
    [Name(Name)]
    [BaseDefinition(CodeRemoteContentDefinition.CodeRemoteContentTypeName)]
    internal static ContentTypeDefinition? Definition;

    /// <summary>
    /// HLSL Tools の種類に、言語サーバー用の種類を親として加える。
    /// </summary>
    /// <remarks>
    /// Visual Studio の言語サーバーのクライアントは、言語サーバー用の種類から派生した種類のファイルにしか付かない。
    /// 同じ名前の定義は 1 つの種類にまとめられ、親は両方の定義のものになる。
    /// HLSL Tools が入っていなければ、この種類のファイルは無い (拡張子は <see cref="Name"/> に対応付けている)。
    /// </remarks>
    [Export]
    [Name(HlslToolsName)]
    [BaseDefinition(CodeRemoteContentDefinition.CodeRemoteContentTypeName)]
    internal static ContentTypeDefinition? HlslToolsDefinition;

    [Export] [FileExtension(".shader")] [ContentType(Name)] internal static FileExtensionToContentTypeDefinition? Shader;
    [Export] [FileExtension(".hlsl")] [ContentType(Name)] internal static FileExtensionToContentTypeDefinition? Hlsl;
    [Export] [FileExtension(".hlsli")] [ContentType(Name)] internal static FileExtensionToContentTypeDefinition? Hlsli;
    [Export] [FileExtension(".cginc")] [ContentType(Name)] internal static FileExtensionToContentTypeDefinition? Cginc;
    [Export] [FileExtension(".hlslinc")] [ContentType(Name)] internal static FileExtensionToContentTypeDefinition? Hlslinc;
    [Export] [FileExtension(".compute")] [ContentType(Name)] internal static FileExtensionToContentTypeDefinition? Compute;
    [Export] [FileExtension(".usf")] [ContentType(Name)] internal static FileExtensionToContentTypeDefinition? Usf;
    [Export] [FileExtension(".ush")] [ContentType(Name)] internal static FileExtensionToContentTypeDefinition? Ush;
    [Export] [FileExtension(".vert")] [ContentType(Name)] internal static FileExtensionToContentTypeDefinition? Vert;
    [Export] [FileExtension(".frag")] [ContentType(Name)] internal static FileExtensionToContentTypeDefinition? Frag;
    [Export] [FileExtension(".comp")] [ContentType(Name)] internal static FileExtensionToContentTypeDefinition? Comp;
    [Export] [FileExtension(".geom")] [ContentType(Name)] internal static FileExtensionToContentTypeDefinition? Geom;
    [Export] [FileExtension(".tesc")] [ContentType(Name)] internal static FileExtensionToContentTypeDefinition? Tesc;
    [Export] [FileExtension(".tese")] [ContentType(Name)] internal static FileExtensionToContentTypeDefinition? Tese;
    [Export] [FileExtension(".task")] [ContentType(Name)] internal static FileExtensionToContentTypeDefinition? Task;
    [Export] [FileExtension(".rgen")] [ContentType(Name)] internal static FileExtensionToContentTypeDefinition? Rgen;
    [Export] [FileExtension(".rint")] [ContentType(Name)] internal static FileExtensionToContentTypeDefinition? Rint;
    [Export] [FileExtension(".rahit")] [ContentType(Name)] internal static FileExtensionToContentTypeDefinition? Rahit;
    [Export] [FileExtension(".rchit")] [ContentType(Name)] internal static FileExtensionToContentTypeDefinition? Rchit;
    [Export] [FileExtension(".rmiss")] [ContentType(Name)] internal static FileExtensionToContentTypeDefinition? Rmiss;
    [Export] [FileExtension(".rcall")] [ContentType(Name)] internal static FileExtensionToContentTypeDefinition? Rcall;
    [Export] [FileExtension(".glsl")] [ContentType(Name)] internal static FileExtensionToContentTypeDefinition? Glsl;
    [Export] [FileExtension(".gdshader")] [ContentType(Name)] internal static FileExtensionToContentTypeDefinition? Gdshader;
    [Export] [FileExtension(".gdshaderinc")] [ContentType(Name)] internal static FileExtensionToContentTypeDefinition? Gdshaderinc;
    [Export] [FileExtension(".slang")] [ContentType(Name)] internal static FileExtensionToContentTypeDefinition? Slang;
    [Export] [FileExtension(".wgsl")] [ContentType(Name)] internal static FileExtensionToContentTypeDefinition? Wgsl;

#pragma warning restore CS0649
}
