using System.Collections.Immutable;
using Shaderlyn.Hlsl.Syntax;
using Shaderlyn.Semantics.Symbols;

namespace Shaderlyn.Semantics.Programs;

/// <summary>
/// コードブロック 1 つ分から取り出した宣言。
/// </summary>
/// <param name="Uniforms">見つかった uniform。</param>
/// <param name="ConstantBuffers">見つかった定数バッファ。</param>
/// <param name="Structs">見つかった構造体。</param>
/// <param name="FunctionNames">定義または宣言された関数の名前。</param>
/// <param name="FunctionSignatures">関数の名前から、宣言されている形の一覧への対応。</param>
internal readonly record struct DeclarationSet(
    ImmutableArray<UniformSymbol> Uniforms,
    ImmutableArray<ConstantBufferSymbol> ConstantBuffers,
    ImmutableArray<StructDeclarationSyntax> Structs,
    ImmutableHashSet<string> FunctionNames,
    ImmutableDictionary<string, ImmutableArray<FunctionSignature>> FunctionSignatures);

/// <summary>
/// 関数の宣言 1 つ分の、戻り値の型と受け取れる引数の個数。
/// </summary>
/// <param name="ReturnType">戻り値の型名。</param>
/// <param name="MinimumArguments">省略できない引数の個数。</param>
/// <param name="MaximumArguments">受け取れる引数の個数の上限。</param>
/// <param name="Declaration">
/// この形を宣言しているノード。出現条件を引くのに使う。分からない場合は <see langword="null"/>。
/// </param>
/// <remarks>
/// <b>オーバーロードを個数で絞れば、型の優先順位を知らなくても答えられる場合がある。</b>
/// 実引数の個数を受け取れる宣言がすべて同じ型を返すなら、
/// どれが選ばれるかを決めなくても戻り値の型は確定する。
/// 暗黙の型変換の優先順位に踏み込まずに済む範囲がここまでである。
/// </remarks>
public readonly record struct FunctionSignature(
    string ReturnType,
    int MinimumArguments,
    int MaximumArguments,
    FunctionDeclarationSyntax? Declaration = null);

/// <summary>
/// HLSL の構文木からトップレベルの宣言を取り出す。
/// </summary>
/// <remarks>
/// <para>
/// <b>1 回の走査ですべてを集める。</b>
/// 展開後の構文木には URP のヘッダ群が丸ごと含まれており、
/// ノード数は 1 ブロックあたり数十万に達する。
/// 必要な情報ごとに木を走査し直すと、その回数だけ解析時間が伸びる。
/// </para>
/// <para>
/// <b>関数の中は走査しない。</b>
/// ローカル変数は uniform ではなく、拾うとプロパティとの対応判定が壊れる。
/// トップレベルの宣言と、定数バッファ・名前空間の中だけを見る。
/// </para>
/// </remarks>
internal static class UniformCollector
{
    /// <summary>
    /// uniform ではないことを示す修飾子。
    /// </summary>
    /// <remarks>
    /// <c>static</c> はシェーダー内部の変数、<c>groupshared</c> は
    /// コンピュートシェーダーのスレッドグループ共有メモリであり、
    /// どちらもマテリアルから値が入る対象ではない。
    /// <c>const</c> は除外しない。HLSL では <c>const</c> だけの大域変数は
    /// 「書き換えられない uniform」であり、値は外から与えられる。
    /// </remarks>
    private static readonly string[] NonUniformModifiers = ["static", "groupshared"];

    /// <summary>
    /// 構文木からトップレベルの宣言を集める。
    /// </summary>
    /// <param name="root">HLSL の構文木の根。</param>
    /// <returns>集めた宣言。</returns>
    public static DeclarationSet Collect(HlslCompilationUnitSyntax root)
    {
        ArgumentNullException.ThrowIfNull(root);

        Collector collector = new();
        collector.CollectFrom(root.Declarations);
        return collector.ToResult();
    }

    /// <summary>走査中の状態。</summary>
    private sealed class Collector
    {
        private readonly ImmutableArray<UniformSymbol>.Builder _uniforms =
            ImmutableArray.CreateBuilder<UniformSymbol>();

        private readonly ImmutableArray<ConstantBufferSymbol>.Builder _constantBuffers =
            ImmutableArray.CreateBuilder<ConstantBufferSymbol>();

        private readonly ImmutableArray<StructDeclarationSyntax>.Builder _structs =
            ImmutableArray.CreateBuilder<StructDeclarationSyntax>();

        private readonly ImmutableHashSet<string>.Builder _functionNames =
            ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);

        /// <summary>関数の名前から、宣言されている形の一覧への対応。</summary>
        private readonly Dictionary<string, List<FunctionSignature>> _functionSignatures =
            new(StringComparer.Ordinal);

        public DeclarationSet ToResult() => new(
            _uniforms.ToImmutable(),
            _constantBuffers.ToImmutable(),
            _structs.ToImmutable(),
            _functionNames.ToImmutable(),
            _functionSignatures.ToImmutableDictionary(
                pair => pair.Key,
                pair => pair.Value.ToImmutableArray(),
                StringComparer.Ordinal));

        public void CollectFrom(ImmutableArray<HlslDeclarationSyntax> declarations)
        {
            foreach (HlslDeclarationSyntax declaration in declarations)
            {
                switch (declaration)
                {
                    case VariableDeclarationSyntax variable:
                        AddUniforms(variable, containingBufferName: null, _uniforms);
                        break;

                    case ConstantBufferDeclarationSyntax buffer:
                        CollectConstantBuffer(buffer);
                        break;

                    // 構造体の外で定義したメソッド (Wave::GetIndex) はグローバルな関数ではない。
                    case FunctionDeclarationSyntax function when !function.NameToken.IsMissing && !function.IsMethodDefinition:
                        _functionNames.Add(function.Name);
                        AddSignature(function);
                        break;

                    case StructDeclarationSyntax structDeclaration:
                        CollectStruct(structDeclaration);
                        break;

                    case NamespaceDeclarationSyntax ns:
                        // 名前空間の中の宣言もトップレベルと同じ扱いになる。
                        // Unity のレイトレーシング関連のコードが名前空間を使っている。
                        CollectFrom(ns.Members);
                        break;
                }
            }
        }

        /// <summary>構造体 1 つ分を処理する。</summary>
        /// <param name="declaration">対象の構造体。</param>
        /// <remarks>
        /// <b>メンバーを uniform として拾ってはならない。</b>
        /// 構造体のフィールドはマテリアルから値が入る対象ではなく、
        /// 頂点入力や補間の受け渡しに使う名前である。
        /// uniform の一覧に混ざると、プロパティとの対応判定が
        /// 無関係な名前と突き合わせられることになる。
        /// 入れ子の構造体だけを辿る。
        /// </remarks>
        private void CollectStruct(StructDeclarationSyntax declaration)
        {
            _structs.Add(declaration);

            foreach (HlslDeclarationSyntax member in declaration.Members)
            {
                if (member is StructDeclarationSyntax nested)
                {
                    CollectStruct(nested);
                }
            }
        }

        /// <summary>関数の宣言の形を記録する。</summary>
        /// <param name="function">対象の関数。</param>
        /// <remarks>
        /// プロトタイプ宣言と定義のように同じ形が 2 度現れるのは普通なので、
        /// 同じものは重ねて持たない。
        /// </remarks>
        private void AddSignature(FunctionDeclarationSyntax function)
        {
            int required = 0;
            int total = 0;

            foreach (ParameterSyntax parameter in function.ParameterList)
            {
                total++;

                if (parameter.DefaultValue is null)
                {
                    required++;
                }
            }

            // 宣言のノードも覚える。条件ごとに違う型を返す関数は、
            // どの宣言がその構成で見えるかを出現条件から決める
            // (ExpressionTypeBinder.EnumerateAssumptions)。
            FunctionSignature signature = new(function.ReturnType.Name, required, total, function);

            if (!_functionSignatures.TryGetValue(function.Name, out List<FunctionSignature>? signatures))
            {
                signatures = _functionSignatures[function.Name] = [];
            }

            if (!signatures.Contains(signature))
            {
                signatures.Add(signature);
            }
        }

        /// <summary>定数バッファ 1 つ分を処理する。</summary>
        /// <param name="buffer">対象の定数バッファ。</param>
        private void CollectConstantBuffer(ConstantBufferDeclarationSyntax buffer)
        {
            ImmutableArray<UniformSymbol>.Builder members = ImmutableArray.CreateBuilder<UniformSymbol>();

            foreach (HlslDeclarationSyntax member in buffer.Members)
            {
                if (member is VariableDeclarationSyntax variable)
                {
                    AddUniforms(variable, buffer.Name, members);
                }
            }

            _uniforms.AddRange(members);
            _constantBuffers.Add(new ConstantBufferSymbol(buffer.Name, buffer, members.ToImmutable()));
        }

        /// <summary>変数宣言 1 件から uniform を作る。</summary>
        /// <param name="declaration">対象の宣言。</param>
        /// <param name="containingBufferName">含まれている定数バッファの名前。</param>
        /// <param name="uniforms">見つかった uniform の追加先。</param>
        private static void AddUniforms(
            VariableDeclarationSyntax declaration,
            string? containingBufferName,
            ImmutableArray<UniformSymbol>.Builder uniforms)
        {
            foreach (string modifier in NonUniformModifiers)
            {
                if (declaration.HasModifier(modifier))
                {
                    return;
                }
            }

            foreach (VariableDeclaratorSyntax declarator in declaration.Variables)
            {
                // 構文エラーから回復するために合成されたトークンには名前が無い。
                // これを uniform として登録すると、空の名前が索引に入り込む。
                if (declarator.NameToken.IsMissing || declarator.Name.Length == 0)
                {
                    continue;
                }

                uniforms.Add(new UniformSymbol(declarator.Name, declaration, declarator, containingBufferName));
            }
        }
    }
}
