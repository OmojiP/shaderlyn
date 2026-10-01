using Shaderlyn.Core.Syntax;

namespace Shaderlyn.Hlsl.Preprocessing;

/// <summary>
/// 取り込みの展開が読んだ名前の記録。展開結果を、読んだ名前の状態が同じなら使い回すためにある。
/// </summary>
/// <remarks>
/// <para>
/// <b>展開の結果は、マクロの状態のうち、その展開が読んだ名前の状態だけで決まる。</b>
/// マクロ表全体を鍵にすると、ヘッダに関係のないマクロが 1 つ違うだけで展開し直す。
/// バリアントごとの展開では <c>#define _KEYWORD 1</c> の違いがまさにそれで、
/// 取り込む 65,008 回のうち全体の鍵で当たったのは 3,676 回だった。
/// </para>
/// <para>
/// 読みは表の型 (<see cref="TrackedTable{TValue}"/>・<see cref="TrackedNameSet"/>) が必ず知らせる。
/// 知らされた名前を、記録中の取り込みすべてに「読んだときの状態」とともに残す。
/// その取り込み自身が先に書いた名前は残さない。書いたあとの状態は外によらない。
/// </para>
/// </remarks>
internal sealed partial class HlslPreprocessor
{
    /// <summary>「条件の中で定義された名前」の状態を、マクロ表と混ざらないようにずらす値。</summary>
    private const ulong ConditionallyDefinedSalt = 0x5851F42D4C957F2DUL;

    /// <summary>ヘッダの定義の状態を、マクロ表と混ざらないようにずらす値。</summary>
    private const ulong HeaderDefinitionSalt = 0x14057B7EF767814FUL;

    private void NoteMacroRead(string name)
    {
        foreach (IncludeRecording recording in _recordings)
        {
            if (!recording.MacroReads.ContainsKey(name) && !recording.MacroWrites.Contains(name))
            {
                recording.MacroReads.Add(name, MacroFingerprint(name));
            }
        }
    }

    private void NoteConditionallyDefinedRead(string name)
    {
        foreach (IncludeRecording recording in _recordings)
        {
            if (!recording.ConditionallyDefinedReads.ContainsKey(name) && !recording.ConditionallyDefinedWrites.Contains(name))
            {
                recording.ConditionallyDefinedReads.Add(name, ConditionallyDefinedFingerprint(name));
            }
        }
    }

    private void NoteHeaderDefinitionRead(string name)
    {
        foreach (IncludeRecording recording in _recordings)
        {
            if (!recording.HeaderDefinitionReads.ContainsKey(name) && !recording.HeaderDefinitionWrites.Contains(name))
            {
                recording.HeaderDefinitionReads.Add(name, HeaderDefinitionFingerprint(name));
            }
        }
    }

    private void NoteMacroWrite(string name)
    {
        foreach (IncludeRecording recording in _recordings)
        {
            recording.MacroWrites.Add(name);
        }
    }

    private void NoteConditionallyDefinedWrite(string name)
    {
        foreach (IncludeRecording recording in _recordings)
        {
            recording.ConditionallyDefinedWrites.Add(name);
        }
    }

    private void NoteHeaderDefinitionWrite(string name)
    {
        foreach (IncludeRecording recording in _recordings)
        {
            recording.HeaderDefinitionWrites.Add(name);
        }
    }

    /// <summary>マクロ 1 つの今の状態。</summary>
    /// <param name="name">名前。</param>
    /// <returns>名前と定義から求めた値。無い名前も名前ごとに違う値になる。</returns>
    private ulong MacroFingerprint(string name)
        => Mix(NameHash(name) ^ _macroHashes.GetValueOrDefault(name));

    /// <summary>「条件の中で定義された名前」1 つの今の状態。</summary>
    /// <param name="name">名前。<see cref="TrackedNameSet.CountName"/> なら、集合が空かどうか。</param>
    /// <returns>名前とあるかどうかから求めた値。</returns>
    private ulong ConditionallyDefinedFingerprint(string name)
    {
        bool present = name == TrackedNameSet.CountName
            ? _conditionallyDefinedMacros.RawCount > 0
            : _conditionallyDefinedMacros.RawContains(name);

        return Mix(NameHash(name) ^ ConditionallyDefinedSalt ^ (present ? 1UL : 0UL));
    }

    /// <summary>ヘッダの定義 1 つの今の状態。</summary>
    /// <param name="name">名前。</param>
    /// <returns>名前と定義がある条件から求めた値。</returns>
    private ulong HeaderDefinitionFingerprint(string name)
        => Mix(NameHash(name) ^ HeaderDefinitionSalt
               ^ (_headerDefinitions.RawTryGetValue(name, out SymbolCondition condition) ? HashHeaderDefinition(name, condition) : 0UL));

    /// <summary>読んだ名前の状態を、今の状態で 1 つの値にまとめる。</summary>
    /// <param name="reads">読んだ名前。</param>
    /// <returns>各名前の状態の排他的論理和。</returns>
    private ulong DigestReads(IncludeReadSet reads)
    {
        ulong digest = 0;

        foreach (string name in reads.Macros)
        {
            digest ^= MacroFingerprint(name);
        }

        foreach (string name in reads.ConditionallyDefined)
        {
            digest ^= ConditionallyDefinedFingerprint(name);
        }

        foreach (string name in reads.HeaderDefinitions)
        {
            digest ^= HeaderDefinitionFingerprint(name);
        }

        return digest;
    }

    /// <summary>記録を、使い回すときに照合する形にする。</summary>
    /// <param name="recording">終えた記録。</param>
    /// <returns>読んだ名前と、読んだときの状態をまとめた値。</returns>
    private static IncludeReadSet ToReadSet(IncludeRecording recording)
    {
        ulong digest = 0;

        foreach (ulong value in recording.MacroReads.Values)
        {
            digest ^= value;
        }

        foreach (ulong value in recording.ConditionallyDefinedReads.Values)
        {
            digest ^= value;
        }

        foreach (ulong value in recording.HeaderDefinitionReads.Values)
        {
            digest ^= value;
        }

        return new IncludeReadSet(
            [.. recording.MacroReads.Keys],
            [.. recording.ConditionallyDefinedReads.Keys],
            [.. recording.HeaderDefinitionReads.Keys],
            digest);
    }

    /// <summary>
    /// 使い回す展開が読んだ名前を、記録中の外側の取り込みへ足す。
    /// </summary>
    /// <param name="reads">使い回す展開が読んだ名前。</param>
    /// <remarks>
    /// <b>使い回した中身は展開し直さないので、読みも知らされない。</b>
    /// 足さないと、外側の記録は内側が読んだ名前に依存していないことになり、
    /// 内側の名前だけが違う状態で外側ごと使い回してしまう。
    /// 当て直す前 (今の状態) の値で足す。それが内側を展開した場合に読まれる値である。
    /// </remarks>
    private void NoteReusedReads(IncludeReadSet reads)
    {
        if (_recordings.Count == 0)
        {
            return;
        }

        foreach (string name in reads.Macros)
        {
            NoteMacroRead(name);
        }

        foreach (string name in reads.ConditionallyDefined)
        {
            NoteConditionallyDefinedRead(name);
        }

        foreach (string name in reads.HeaderDefinitions)
        {
            NoteHeaderDefinitionRead(name);
        }
    }

    private static ulong NameHash(string name)
        => (ulong)(uint)StringComparer.Ordinal.GetHashCode(name) * 0xD6E8FEB86659FD93UL;

    /// <summary>64 ビットの値をよく混ぜる (splitmix64 の仕上げ)。</summary>
    /// <param name="value">混ぜる値。</param>
    /// <returns>混ぜた値。</returns>
    private static ulong Mix(ulong value)
    {
        value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
        value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
        return value ^ (value >> 31);
    }
}
