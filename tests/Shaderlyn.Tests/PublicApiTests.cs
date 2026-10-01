using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;

namespace Shaderlyn.Tests;

/// <summary>
/// NuGet で配るライブラリの公開 API を、記録した一覧 (PublicApi.txt) と照合する。
/// </summary>
/// <remarks>
/// <para>
/// <b>公開した型とメンバーは、利用者のコードが触れる約束になる。</b>
/// 型を 1 つ public にしただけでも、次からは消すのも変えるのも破壊的変更になる。
/// レビューで差分の中の <c>public</c> に気づくのは難しいので、一覧の差分として見せる。
/// </para>
/// <para>
/// 意図して変えたときは、環境変数 <c>SHADERLYN_UPDATE_PUBLIC_API=1</c> を付けてこのテストを実行すると
/// 一覧を書き直す。書き直した差分がそのまま公開 API の変更点になる。
/// </para>
/// </remarks>
public sealed class PublicApiTests
{
    /// <summary>NuGet で配るアセンブリ。</summary>
    private static readonly Assembly[] Shipping =
    [
        typeof(Core.Analysis.DiagnosticAnalyzer).Assembly,
        typeof(ShaderLab.Syntax.ShaderLabSyntaxNode).Assembly,
        typeof(Hlsl.Syntax.HlslSyntaxNode).Assembly,
        typeof(Semantics.ShaderCompilation).Assembly,
        typeof(Configuration.ConfigurationLoader).Assembly,
        typeof(Rules.TagAnalyzer).Assembly,
        typeof(Cli.Program).Assembly,
        typeof(LanguageServer.ShaderLanguageServer).Assembly,
        typeof(Testing.ShaderRuleVerifier).Assembly,
    ];

    [Fact]
    public void 公開APIが記録と一致する()
    {
        string actual = Describe();
        string path = Path.Combine(AppContext.BaseDirectory, "PublicApi.txt");
        string expected = File.Exists(path) ? File.ReadAllText(path).ReplaceLineEndings("\n") : "";

        if (Environment.GetEnvironmentVariable("SHADERLYN_UPDATE_PUBLIC_API") == "1")
        {
            File.WriteAllText(SourcePath(), actual);
            return;
        }

        HashSet<string> before = [.. expected.Split('\n', StringSplitOptions.RemoveEmptyEntries)];
        HashSet<string> after = [.. actual.Split('\n', StringSplitOptions.RemoveEmptyEntries)];

        List<string> changes =
        [
            .. before.Except(after).Order(StringComparer.Ordinal).Select(line => "- " + line),
            .. after.Except(before).Order(StringComparer.Ordinal).Select(line => "+ " + line),
        ];

        Assert.True(
            changes.Count == 0,
            "公開 API が tests/Shaderlyn.Tests/PublicApi.txt と違います。"
            + "意図した変更なら SHADERLYN_UPDATE_PUBLIC_API=1 を付けてこのテストを実行し、一覧を書き直してください。"
            + Environment.NewLine + string.Join(Environment.NewLine, changes));
    }

    /// <summary>公開 API を 1 行 1 項目で書き出す。</summary>
    /// <returns>並べ替えた一覧。</returns>
    private static string Describe()
    {
        List<string> lines = [];

        foreach (Type type in Shipping.SelectMany(a => a.GetExportedTypes()))
        {
            string name = type.FullName!;
            lines.Add($"{name} : {Kind(type)}{BaseList(type)}");

            const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic
                | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

            foreach (MemberInfo member in type.GetMembers(Flags))
            {
                if (DescribeMember(type, member) is { } text)
                {
                    lines.Add($"{name} :: {text}");
                }
            }
        }

        lines.Sort(StringComparer.Ordinal);

        StringBuilder builder = new();
        foreach (string line in lines)
        {
            builder.Append(line).Append('\n');
        }

        return builder.ToString();
    }

    private static string Kind(Type type) => type switch
    {
        { IsEnum: true } => "enum",
        { IsInterface: true } => "interface",
        { IsValueType: true } => "struct",
        _ when typeof(Delegate).IsAssignableFrom(type) => "delegate",
        { IsAbstract: true, IsSealed: true } => "static class",
        { IsAbstract: true } => "abstract class",
        { IsSealed: true } => "sealed class",
        _ => "class",
    };

    private static string BaseList(Type type)
    {
        List<string> bases = [];

        if (type.BaseType is { } baseType && baseType != typeof(object) && baseType != typeof(ValueType)
            && baseType != typeof(Enum) && !typeof(Delegate).IsAssignableFrom(type))
        {
            bases.Add(baseType.ToString());
        }

        bases.AddRange(type.GetInterfaces().Where(i => i.IsPublic || i.IsNestedPublic).Select(i => i.ToString()).Order(StringComparer.Ordinal));

        return bases.Count == 0 ? "" : " (" + string.Join(", ", bases) + ")";
    }

    private static string? DescribeMember(Type type, MemberInfo member)
    {
        bool extensible = !type.IsSealed && !type.IsEnum;

        bool Visible(MethodBase? m) => m is not null && (m.IsPublic || (extensible && (m.IsFamily || m.IsFamilyOrAssembly)));

        switch (member)
        {
            case FieldInfo field when field.IsPublic || (extensible && (field.IsFamily || field.IsFamilyOrAssembly)):
                if (type.IsEnum)
                {
                    return field.IsLiteral ? $"{field.Name} = {field.GetRawConstantValue()}" : null;
                }

                return $"{(field.IsStatic ? "static " : "")}{(field.IsInitOnly ? "readonly " : "")}{field.FieldType} {field.Name}";

            case PropertyInfo property:
                string accessors = string.Concat(
                    Visible(property.GetMethod) ? " get;" : "",
                    Visible(property.SetMethod) ? " set;" : "");
                if (accessors.Length == 0)
                {
                    return null;
                }

                MethodInfo accessor = (Visible(property.GetMethod) ? property.GetMethod : property.SetMethod)!;
                string indexer = property.GetIndexParameters() is { Length: > 0 } index
                    ? "[" + string.Join(", ", index.Select(p => p.ParameterType.ToString())) + "]"
                    : "";
                return $"{Modifiers(accessor)}{property.PropertyType} {property.Name}{indexer} {{{accessors} }}";

            case ConstructorInfo constructor when Visible(constructor):
                return $"{Modifiers(constructor)}.ctor({Parameters(constructor)})";

            case MethodInfo method when Visible(method) && (!method.IsSpecialName || method.Name.StartsWith("op_", StringComparison.Ordinal)):
                string generic = method.IsGenericMethodDefinition
                    ? "<" + string.Join(", ", method.GetGenericArguments().Select(a => a.Name)) + ">"
                    : "";
                return $"{Modifiers(method)}{method.ReturnType} {method.Name}{generic}({Parameters(method)})";

            case EventInfo @event when Visible(@event.AddMethod):
                return $"{Modifiers(@event.AddMethod!)}event {@event.EventHandlerType} {@event.Name}";

            default:
                return null;
        }
    }

    private static string Modifiers(MethodBase method)
    {
        StringBuilder builder = new();
        if (!method.IsPublic)
        {
            builder.Append("protected ");
        }

        if (method.IsStatic)
        {
            builder.Append("static ");
        }
        else if (method.IsAbstract)
        {
            builder.Append("abstract ");
        }
        else if (method.IsVirtual && !method.IsFinal)
        {
            builder.Append("virtual ");
        }

        return builder.ToString();
    }

    private static string Parameters(MethodBase method)
        => string.Join(", ", method.GetParameters().Select(p =>
            (p.IsOut ? "out " : p.ParameterType.IsByRef ? "ref " : "")
            + (p.ParameterType.IsByRef ? p.ParameterType.GetElementType()! : p.ParameterType)
            + (p.HasDefaultValue ? " = " + (p.DefaultValue ?? "null") : "")));

    private static string SourcePath([CallerFilePath] string testFile = "")
        => Path.Combine(Path.GetDirectoryName(testFile)!, "PublicApi.txt");
}
