# 条件付きコンパイルの扱い

`#ifdef` で分かれるコードを、shaderlyn がどう解析しているかをまとめたページです。
`SL0003` / `SL0004` が出たときや、`--max-symbol-variants` を調整するときに読んでください。
末尾の[付録](#付録-実装の詳細)は開発者向けで、同じ内容をコードの側から書いています。

このページの記述は、実際に解析させて確かめたものです。

## 目次

- [前提: 無効な分岐も検査する](#前提-無効な分岐も検査する)
- [何をシンボルと見なすか](#何をシンボルと見なすか)
- [条件の書き方](#条件の書き方)
- [方法 A: トークンを並べて 1 本の木にする](#方法-a-トークンを並べて-1-本の木にする)
- [方法 B: 構成ごとに解析して突き合わせる](#方法-b-構成ごとに解析して突き合わせる)
- [上限と報告](#上限と報告)
- [突き合わせに失敗した場合](#突き合わせに失敗した場合)
- [既知の限界](#既知の限界)
- [まとめ](#まとめ)
- [付録: 実装の詳細](#付録-実装の詳細)

---

## 前提: 無効な分岐も検査する

Unity はシェーダーキーワードの組み合わせごとにシェーダーをコンパイルします。
ある組み合わせで無効な `#ifdef` の中身は、別の組み合わせでは有効になります。
既定の 1 通りの構成だけを解析すると、それ以外の構成でしか通らないコードの誤りを見逃します。

shaderlyn は、`#pragma` で宣言されたシンボルが見ている分岐を、**どちらの側も検査します**。
そのための方法が 2 つあり、まず方法 A を試し、扱えない領域だけを方法 B で扱います。

## 何をシンボルと見なすか

名前が `shader_feature` / `multi_compile` / `dynamic_branch` の**いずれかで始まる** `#pragma` を、シンボル（Unity の用語ではシェーダーキーワード）の宣言として読みます。
`shader_feature_local`、`multi_compile_local_fragment`、`multi_compile_instancing` も含みます。

```hlsl
#pragma multi_compile _ _A _B        // _A と _B を宣言する
#pragma shader_feature_local _DETAIL // _DETAIL を宣言する
```

- その行の識別子すべてをシンボルとして読みます。下線だけの名前（`_`、`__` …）は「シンボル無しの構成」を表すので除きます
- 取り込んだヘッダに書かれた宣言も、シェーダー自身の宣言と同じに扱います（[ヘッダに置いた `#pragma`](#ヘッダに置いた-pragma)）
- `#pragma dynamic_branch` のシンボルは、`if (_A)` のように実行時の分岐で使います。条件で使っていなくても、コードに現れていれば「使われている」と見なします

宣言したシンボルをどこでも使っていなければ [HL0331](../rules/HL0331.md)、宣言していない名前を条件で見ていれば [HL0330](../rules/HL0330.md) が出ます。

## 条件の書き方

次の形の条件は、「どのシンボルが有効なときに通るか」として読みます。

| 書き方 | 扱い |
| --- | --- |
| `#ifdef X` / `#ifndef X` | シンボルの条件として読む |
| `#if defined(X)` / `#if defined X` | シンボルの条件として読む（括弧は省略可） |
| `#if X` | X が宣言されたシンボルなら、シンボルの条件として読む |
| `#if !X`、`#if A && B`、`#if A \|\| B`、`#if (A)` | シンボルの条件として読む |
| `#if X == 1`、`#if VERSION >= 600` | 値として解く。X が宣言されたシンボルなら、その構成も作って検査する（方法 B） |
| `#if SHADER_API_D3D11` | 宣言されたシンボルではないので、値として解く |

`#ifdef _A`、`#if _A`、`#if defined(_A)` の 3 つは同じに扱います。Unity は有効なシンボルを値 1 のマクロとして定義するため、
URP 自身も `#if _ALPHATEST_ON` の形を使っています。

`SHADER_API_*` や `UNITY_VERSION` のような環境のマクロは、1 通りの値を仮に選んで解きます（`--define` で変えられます）。

## 方法 A: トークンを並べて 1 本の木にする

`#ifdef` の領域が文や宣言の単位で閉じていれば、両方の中身をトークン列として並べ、
1 回の構文解析で 1 本の木にします。各ノードには「どの条件のもとで存在するか」が付きます。

```hlsl
#ifdef _A
    return Helper();     // ← この中のコードも、ルールがそのまま検査する
#else
    return 0;
#endif
```

**複合条件もこの方法で扱えます。** 条件は「`_A` かつ `_B`」という形のまま保持されるので、
組み合わせを試す必要がありません。

```hlsl
#if defined(_A) && defined(_B)
    return Helper();     // ← 中のコードは検査される
#endif
```

この方法で済んだ領域は、バリアントの展開（方法 B）を必要としません。`--max-symbol-variants 0` を指定しても
挙動は変わらず、`SL0003` も出ません。

並べるのは、各分岐で括弧（`{} () []`）が釣り合い、最後のトークンが `;` か `}` で終わる領域です（空の分岐も可）。
入れ子の `#if` は、外側も並べているときだけ並べます。
同じ `#pragma` 行のシンボル（同時に有効にならないもの）のように、外側と合わせて決して通らない分岐は並べません。

### 条件で中身が変わるマクロ

マクロの中身を条件で切り替え、それをコードの中で使っている場合は、
**その文を定義ごとに展開し直して並べます**。

```hlsl
#ifdef _A
#define CTYPE float3
#else
#define CTYPE float4
#endif

CTYPE color = Load(uv);
```

展開すると、こうなります（それぞれに条件が付きます）。

```hlsl
float3 color = Load(uv);   // _A
float4 color = Load(uv);   // !_A
```

関数形式マクロも同じで、引数ごと展開し直します。

複製した文は同じ行に並びますが、どれがどの構成のものかは区別されています。
エディタのホバーは構成ごとの型と展開結果を並べて示し（`CTYPE` の上なら「`_A` のとき `float3`」「`!_A` のとき `float4`」）、
`--inspect` の HLSL タブは複製のそれぞれに `#if _A` / `#if !_A` を付けます。
使う文をすべて複製できたなら、そのシンボルについて方法 B の展開は行いません。

**複製する範囲は構文解析が決めます。** 記号の数え方では、
`#define BEGIN struct Foo {` のようにマクロが括弧を作る場合に切れ目を取り違えます。
定義ごとに展開したものが文・宣言として読めなければ、次の切れ目で読み直します。
どれも読めなければ複製せず、今までどおり 1 つの構成の値で展開します。

`#include` を切り替えている領域も、両方のヘッダをそれぞれの条件のもとで処理します。
ただしヘッダが定義したマクロをコードの中で使っていた場合は、
その時点で方法 B へ切り替えて、そのブロックを展開し直します。

## 方法 B: 構成ごとに解析して突き合わせる

次のような領域は、トークンを並べても構文として成立しないため、方法 A では扱えません。

| 領域の形 | 例 |
| --- | --- |
| 文の途中で切れている | `x = 1` / `x = 2` と書き、`;` を `#endif` の外に置く |
| 仮引数が条件付き | `float Helper(float a` / `, float b` / `)` |
| `#endif` が無い | 領域の終わりが分からない |

`#define` を切り替えている領域は、そのマクロを**条件の中でしか使っていなければ**方法 A で扱います。
`#ifdef _A` の中で `#define USE_A 1` と書き、`#ifdef USE_A` でしか読まないなら、
`USE_A` は「`_A` が有効な条件」として読めるので、並べても展開結果は変わりません。

コードの中で使っているマクロと、`#include` の切り替えも方法 A で扱います
（[条件で中身が変わるマクロ](#条件で中身が変わるマクロ)）。

この場合は、**そのシンボルを有効にした構成を別に展開して構文解析**し、
既定の構成の木と突き合わせます。突き合わせの結果は出現条件の索引
（[`GetConditionMap()`](../custom-rules/api-reference.md)）に入り、バリアントにしか無いノードもそこへ加わります。

展開の対象になるのは、次をすべて満たすシンボルだけです。

- `#pragma` で宣言されている（宣言が無い分岐は決して通らないので、[HL0330](../rules/HL0330.md) の担当）
- 解析しているファイル自身の `#if` が見ている。
  ただし**取り込んだヘッダに書かれた `#if` も、条件によっては対象**です
  （[ヘッダに置いた `#pragma`](#ヘッダに置いた-pragma)）
- マクロ展開の結果として現れた名前ではない
- 方法 A で合流できていない

作る構成は 2 種類です。

| 構成 | 内容 |
| --- | --- |
| シンボル 1 つ | 候補のシンボルを 1 つだけ有効にしたもの |
| 同時に定義されていないと通らない組 | `#if defined(_A) && defined(_B)` の中や、`#ifdef _A` の中の `#ifdef _B` のように、1 つずつ有効にしただけでは通らない分岐のための組 |

**すべての組み合わせは作りません。** 総当たりだと構成の数が 2ⁿ になります。
**条件に書かれた組だけ**を作るので、増えるのは書いた数だけです。
入れ子は、外側の条件と掛け合わせた組になります。外側の分岐を読み飛ばしていても、内側の条件は読みます。

```hlsl
#pragma multi_compile _ _A
#pragma multi_compile _ _B

float4 c;
#if defined(_A) && defined(_B)
    c = float2(1, 2)     // ← _A と _B を同時に有効にした構成で検査される
#else
    c = 1
#endif
    ;                    // ← ; が領域の外にあるので、方法 B へ回る
```

`_A` だけ、`_B` だけの構成ではこの分岐を通らないため、以前はこのコードが 1 度も解析されませんでした。
論理積を 1 つの構成として作ることで、`HL0350`（`float4` に `float2` は入らない）が報告されます。

組を作るのは、その中のシンボルがすべて宣言されている場合だけです。
否定の項（`!_A`）は定義しないことで満たせるので、組には入れません。論理和（`||`）は、どれか 1 つの構成で足ります。
同じ `#pragma` 行に並べたシンボル（`#pragma multi_compile _ _X _Y` の `_X` と `_Y`）は同時に有効にならないので、その組は作りません。
`_` の無い `#pragma multi_compile MODE_A MODE_B` はどちらかが必ず有効なので、既定の構成で先頭の `MODE_A` を定義して解析します。
`#ifdef MODE_A ... #else` の `#else` は、`MODE_B` を有効にした構成で検査します。
上限（次の節）に達したときは、次の順に残します。落とすのは後ろからです。

1. このファイルに書かれた条件が見ているシンボル（1 つずつの構成）
2. 条件に書かれた組（論理積・入れ子）
3. 取り込んだヘッダに書かれた条件から作った構成（[ヘッダに置いた `#pragma`](#ヘッダに置いた-pragma)）

落とした組は、組のまま `SL0003` として報告されます。

### バリアントにしか無いコードも検査する

突き合わせのとき、バリアントの木にしか無いノードは「既定の構成の木のどこへ足すか」と一緒に記録されます。
ルールはその足したノードも歩くため、方法 B で拾った分岐の中のコードも検査されます。

```hlsl
float4 c;
#ifdef _A
    c = (half4)1      // ← この行の中も検査される
#else
    c = 1
#endif
    ;
```

そこで宣言された名前も、既定の構成から見えます。
`--max-symbol-variants 0` にすると見えなくなり、「宣言が見つからない」ことを根拠にするルールが
誤って反応しないよう、`SL0003` とともに検査が見送られます。

マクロの展開で生まれたコードも、**そのマクロをこのファイルが書いているなら**報告します。
位置は呼び出し位置ではなく<b>本体</b>を指します。そこが直す場所であり、
同じマクロを何度使っても報告は 1 か所にまとまります。

```hlsl
#ifdef _A
#define HELPER_DECL float Helper() { return Undefined(); }   // ← ここに報告する
#else
#define HELPER_DECL
#endif
HELPER_DECL
```

**Unity や外部パッケージのヘッダのマクロの中身は報告しません。** 利用者に直しようがないためです。
利用者が書いて取り込んだヘッダ (共通の `.hlsl` など) のマクロは、このファイルのマクロと同じく本体の位置に報告します。
実引数として渡したトークンは呼び出し位置に書かれたものなので、そちらの位置で報告します。

### ヘッダに置いた `#pragma`

共通の関数を `Common.hlsl` に切り出し、`#pragma multi_compile` もそこに置く書き方があります。
この場合、シェーダー側には宣言が 1 つも書かれていません。

```hlsl
// Common.hlsl
#pragma multi_compile _ _HEADER_QUALITY

#ifdef _HEADER_QUALITY
float3 Shade(float3 n) { return n * 2; }
#endif
```

ヘッダの `#pragma` は、**取り込んでみるまで分かりません**。
そのため、1 度展開してから宣言を集め直し、ヘッダが宣言していたら、その分も含めて展開し直します。
ヘッダの宣言は、シェーダー自身が書いた `#pragma` とまったく同じに扱われます。

- `#ifdef _HEADER_QUALITY` は両方の分岐を 1 本の木に並べます（方法 A）。
  `Shade` を呼ぶと [HL0311](../rules/HL0311.md)（`!_HEADER_QUALITY` のとき定義がありません）が出ます
- 並べられない形（分岐ごとに同じマクロを違う中身で定義するなど）は、構成として作り直します（方法 B）。
  ヘッダに書かれた `#if` も、**そのヘッダ自身が宣言したシンボル**については条件として読みます
- `_` の無い `#pragma multi_compile MODE_A MODE_B` がヘッダにあれば、
  既定の構成でも `MODE_A` を定義します。拾わないと、どちらも無効という実在しない構成を解析することになります

逆に、`.shader` でシンボルを宣言し、共通の `.hlsl` の中だけでその機能を切り替える書き方もあります。
この場合も、ヘッダの `#ifdef` はまず両方の分岐を並べます（方法 A）。
**並べられなかった分岐があるシンボルだけ**、そのシンボルを有効にした構成を作ります（方法 B）。

シェーダー自身が宣言したシンボルの、ヘッダに書かれた条件を**すべて**構成の候補にはしません。
URP のシェーダーは 40 を超えるシンボルを宣言し、その条件はほとんどがヘッダ側に書かれています。
すべてを候補にすると 1 ブロックあたり数十件になり、上限で落ちた分が `SL0003` として並びます。

## 上限と報告

方法 B の展開には上限があります（既定 8、`--max-symbol-variants`）。

```bash
shaderlyn Assets/Shaders --unity-project . --max-symbol-variants 16
```

- **上限が効くのは方法 B の対象だけです。** 方法 A で合流できたシンボルは、いくつあっても上限を消費しません
- 上限に達して展開しなかったシンボルと組は [SL0003](../rules/SL0003.md) として報告されます。「調べていない経路が残っている」という意味で、シェーダーの誤りではありません
- `0` を指定すると方法 B を行いません。方法 A で扱えない `#ifdef` のシンボルはすべて `SL0003` になります
- 解析時間は展開するシンボルの数に比例して伸びます
- 上限の指定は解析キャッシュの鍵に含まれます。値を変えると、キャッシュは使われずに解析し直されます

宣言したシンボルを条件で使っていない場合は [HL0331](../rules/HL0331.md) が出ます。
使わない宣言を消せば、展開すべき構成も減ります。

### 条件に書かれていない組み合わせは調べません

作るのは「1 つずつの構成」と「条件に書かれた組 (論理積と入れ子)」だけです。
別々の `#if` で見ているシンボルが、**同時に有効なときだけ**壊れるコードは調べません。

```hlsl
#pragma multi_compile _ _A
#pragma multi_compile _ _B

float4 Offset()
{
    float4 v = 0;
#ifdef _A
    v.x = 1;
#endif
#ifdef _B
    v = v.x + Missing();   // _A と _B が同時に有効なときだけ通る組み合わせ
#endif
    return v;
}
```

条件のどこにも「`_A` かつ `_B`」とは書かれていないため、この組は構成として作られません。
**このことは報告されません。** `_A` も `_B` も展開しており、上限にも達していないためです。

すべての組み合わせを試すと構成の数が 2ⁿ になり、実用的な時間で終わりません。
組み合わせに依存する箇所は、条件に `#if defined(_A) && defined(_B)` と書くか、
`#ifdef _A` の中に `#ifdef _B` を入れ子にすると調べられます。

## 突き合わせに失敗した場合

方法 B で展開しても、木の対応が取れないことがあります。
同じ場所が構成によって**別の種類の文**になる場合です。

```hlsl
#ifdef SHADER_DEBUG
#define BODY { v = 1; }
#else
#define BODY v = 2;
#endif

if (v > 0)
    BODY
```

`if` の本体が、一方ではブロック、もう一方では式文になります。
この箇所は [SL0004](../rules/SL0004.md) として報告されます。

出現条件を根拠にするルール（[HL0311](../rules/HL0311.md) / [HL0313](../rules/HL0313.md) /
[HL0340](../rules/HL0340.md)）は、誤った指摘を出さないために、この箇所では何も報告しません。

**同じ場所が別の形になるだけなら、報告しません。** 両方を「その構成でだけ立つもの」として残すためです。
次の形は `SL0004` になりません。

```hlsl
#ifdef SHADER_DEBUG
#define VALUE 1
#else
#define VALUE half(2)
#endif
half4 frag() : SV_Target { half v = VALUE; return v; }
```

分けられるのは文と宣言で、しかもその単位自身のトークンが構成で変わっている場合です。
上の `if` の例では、変わっているのは本体の文の種類で、`if` 文自身のトークンは変わりません。
`if` 文ごと分けると、どの構成にもあるコード（本体）まで条件付きになってしまうため、そうはしません。

なお、`#if` が文の単位をまたいでいても、構成ごとの木が同じ形に収まるなら突き合わせは成功します。
次の形も `SL0004` になりません。

```hlsl
#ifdef _TOP
    if (a < b)
#else
    y = c - y;
    if (y < d)
#endif
    {
        // ここは既定の構成の木にあるので、中のコードは検査される
    }
```

## 既知の限界

実装上そうなっている点を、意図の有無にかかわらず並べます。

| # | 内容 |
| --- | --- |
| 1 | **条件に書かれていない組み合わせは試さない。** 別々の `#ifdef _A` と `#ifdef _B` が同時に有効なときだけ壊れるコードは、どの構成でも検査されず、`SL0003` も出ない（[実例](#条件に書かれていない組み合わせは調べません)）。論理積と入れ子の条件は組として作る |
| 2 | **Unity や外部パッケージのヘッダのマクロの本体は報告しない。** このファイルと利用者のヘッダが書いたマクロの本体は、本体の位置に報告する |
| 3 | **シェーダー自身が宣言したシンボルについて、ヘッダに書かれた条件をすべて構成の候補にはしない。** まず両方の分岐を並べ、並べられなかったシンボルだけ構成を作る（[ヘッダに置いた `#pragma`](#ヘッダに置いた-pragma)） |
| 4 | **[HL0352](../rules/HL0352.md) は、片側にしか条件が無いときに判断を見送る。** メンバー（`;` で閉じる）は並べられ、初期化の要素（`,` で区切る）は並べられないため扱いが分かれる |
| 5 | **「無いこと」を根拠にするルール（SL1001 / SL1004 / URP0001 / HL0301 / HL0310）は、取り込んだヘッダの無効な分岐に現れた名前を、解析されなかった場所にあるものとして扱う。** 解析しているファイル自身の分岐は位置で見分け、どの構成でも解析されなかった場所の名前だけを数える。ヘッダは見分けないので、ヘッダの無効な分岐にある名前と同じ名前の誤りは見逃す |
| 6 | **`#if X == 2` のように 1 以外と比べる条件は通らない。** バリアントは値 1 を定義するため。Unity も有効なシンボルを 1 として定義するので、実機でも通らない |
| 7 | **`dynamic_branch` のシンボルは、条件で参照されなければバリアントを作らない。** 実行時分岐 (`if (_A)`) として書かれた場合、その両側は元から解析されている |
| 8 | **上限に達したときに落ちるシンボルは名前順で決まる。** 重要度は見ない |
| 9 | **`SL0003` は、同じシンボルを見ている条件のうち最初の 1 か所にしか出ない。** 2 つ目以降の `#ifdef` の中も読まれていないが、そこには指摘が出ない |
| 10 | **`_` の無い `shader_feature` の行は、「どれも無い」構成もあるものとして扱う。** Unity 2019.4 の説明ではそうなっているが、Unity 6 の説明でははっきりしない。違っていれば、実在しない構成の中を検査して誤りを報告する |

1 は報告もされない「調べていないのに報告しない」形で、「見送ったことは報告する」という原則から外れる唯一の項目です。

利用者が書いたヘッダについての残りの限界（ヘッダだけを開いたときの解析など）は
[設計判断「残っている課題」](../internals/design-decisions.md#残っている課題) にあります。

## まとめ

| 書き方 | 扱い | 上限を消費 | 中のコードをルールが検査 |
| --- | --- | --- | --- |
| 文・宣言の単位で閉じている | 方法 A | しない | する |
| 複合条件（`&&` / `\|\|`）で閉じている | 方法 A | しない | する |
| 文の途中で切れている | 方法 B | する | する |
| 仮引数が条件付き | 方法 B | する | する |
| `#define` を切り替えている（条件でしか使わないマクロ） | 方法 A | しない | する |
| `#define` を切り替えている（コードで使うマクロ） | 方法 A | しない | する（利用者が書いたマクロなら中身も） |
| `#include` を切り替えている | 方法 A | しない | する（利用者が書いたマクロなら中身も） |
| 突き合わせに失敗した | [SL0004](../rules/SL0004.md) | する | 条件を根拠にするルールは報告しない |
| 上限に達した | [SL0003](../rules/SL0003.md) | — | 既定の構成にある分だけ |

---

## 付録: 実装の詳細

ここから先は開発者向けです。穴の洗い出しのために、上の内容を実装の側から書き出しています。

コードへのリンクは、ファイルとメンバーの名前で示します。行番号はコードを直すたびにずれるので載せていません。
リンク先のファイルでメンバーの名前を検索してください。

### 宣言の読み取り

[ShaderSymbols.cs](../../src/Shaderlyn.Hlsl/Preprocessing/ShaderSymbols.cs) の 1 か所だけが判断します。

| 判断 | コード |
| --- | --- |
| 対象の `#pragma`（前方一致） | [ShaderSymbols.DeclaringPragmaPrefixes](../../src/Shaderlyn.Hlsl/Preprocessing/ShaderSymbols.cs)、[IsDeclaringPragmaName](../../src/Shaderlyn.Hlsl/Preprocessing/ShaderSymbols.cs) |
| 引数（その行の識別子トークンすべて） | [ShaderSymbols.EnumerateDeclared](../../src/Shaderlyn.Hlsl/Preprocessing/ShaderSymbols.cs) |
| 除外する引数（下線だけの名前） | [ShaderSymbols.IsNoSymbol](../../src/Shaderlyn.Hlsl/Preprocessing/ShaderSymbols.cs) |

宣言を集める経路は 2 つあります。

| 経路 | 入力 | 使う場面 | コード |
| --- | --- | --- | --- |
| `CollectDeclared(pragmas)` | 展開後の `PragmaDirective` | HL0330 / HL0331、バリアントの候補選び | [ShaderSymbols.CollectDeclared](../../src/Shaderlyn.Hlsl/Preprocessing/ShaderSymbols.cs) |
| `CollectDeclaredFromTokens(tokens)` | 字句解析しただけのトークン列 | 展開の**前**に「どれがシンボルか」を知る必要があるため | [ShaderSymbols.CollectDeclaredFromTokens](../../src/Shaderlyn.Hlsl/Preprocessing/ShaderSymbols.cs) |

`CollectDeclaredFromTokens` は **解析対象のファイル自身の `#pragma` しか見ません**。取り込んだヘッダの宣言は
この時点では読めていません。
そのため、1 度展開したあとに `CollectDeclared(pragmas)` で宣言を集め直し、ヘッダが宣言していた場合は
その分も並べる対象と制約に足して**展開し直します**
（[ShaderCompilationBuilder.TryAddIncludedSymbols](../../src/Shaderlyn.Semantics/Programs/ShaderCompilationBuilder.cs)）。
展開し直すのは、足したシンボルの `#ifdef` が実際にある場合だけです。

「使われている」と見なす経路も 2 つです。

| 経路 | 内容 | コード |
| --- | --- | --- |
| `ConditionalIdentifiers` | 条件で参照された名前のトークン | [PreprocessResult.ConditionalIdentifiers](../../src/Shaderlyn.Hlsl/Preprocessing/PreprocessorTypes.cs) |
| `CollectRuntimeReferences(codeTokens)` | コード中に識別子として現れた名前（`dynamic_branch` のため）。`#` で始まる行は飛ばす | [ShaderSymbols.CollectRuntimeReferences](../../src/Shaderlyn.Hlsl/Preprocessing/ShaderSymbols.cs) |

### 条件式の読み取り

[HlslPreprocessorDirectives.cs](../../src/Shaderlyn.Hlsl/Preprocessing/HlslPreprocessorDirectives.cs) が条件式を `SymbolCondition` へ変換します。
入口は [TryReadDefinedCondition](../../src/Shaderlyn.Hlsl/Preprocessing/HlslPreprocessorDirectives.cs) で、
`||` → `&&` → 単項 → 原子の順に下ります。

| 書き方 | コード |
| --- | --- |
| `#ifdef X` / `#ifndef X` | [EvaluateDefinedLine](../../src/Shaderlyn.Hlsl/Preprocessing/HlslPreprocessorConditionals.cs) |
| `#if defined(X)` / `#if defined X` | [ReadDefinedAtom](../../src/Shaderlyn.Hlsl/Preprocessing/HlslPreprocessorDirectives.cs) |
| `#if X`（宣言されたシンボルのときだけ） | [ReadSymbolAtom](../../src/Shaderlyn.Hlsl/Preprocessing/HlslPreprocessorDirectives.cs) |
| `!`、`&&`、`\|\|`、括弧 | [ReadOrExpression](../../src/Shaderlyn.Hlsl/Preprocessing/HlslPreprocessorDirectives.cs)、[ReadUnaryExpression](../../src/Shaderlyn.Hlsl/Preprocessing/HlslPreprocessorDirectives.cs) |
| `#if X == 1` など値として解く形 | 上記が `null` を返す。X が宣言されたシンボルなら [ReadSymbolAtom](../../src/Shaderlyn.Hlsl/Preprocessing/HlslPreprocessorDirectives.cs) が参照として記録する |

「宣言されたシンボル」の判定には `BothBranchSymbols`（次の節）を使います。変換できない形は
[DeclineBothBranchSymbolsIn](../../src/Shaderlyn.Hlsl/Preprocessing/HlslPreprocessorDirectives.cs) を通り、
その構成での値を解いて片方の分岐だけを残します。

参照の記録（`RecordConditionalIdentifier`、[定義](../../src/Shaderlyn.Hlsl/Preprocessing/HlslPreprocessorConditionals.cs)）の呼び出しは 4 か所です。

| 呼び出し元 | 形 |
| --- | --- |
| [ReadSymbolAtom](../../src/Shaderlyn.Hlsl/Preprocessing/HlslPreprocessorDirectives.cs) | `#if` の条件式に単独の識別子として現れた名前（宣言されたシンボルでなくても記録する） |
| [ReadDefinedAtom](../../src/Shaderlyn.Hlsl/Preprocessing/HlslPreprocessorDirectives.cs) | `defined(X)` を条件に変換するとき |
| [EvaluateDefinedLine](../../src/Shaderlyn.Hlsl/Preprocessing/HlslPreprocessorConditionals.cs) | `#ifdef X` / `#ifndef X` |
| [ResolveDefinedOperators](../../src/Shaderlyn.Hlsl/Preprocessing/HlslPreprocessorConditionals.cs) | `defined` を値として解くとき |

### 方法 A の実装

並べる対象のシンボル（`BothBranchSymbols`）は、コードブロックごとに、そのブロックの `#pragma` から自動で拾います
（[ShaderCompilation.Build](../../src/Shaderlyn.Semantics/Programs/ShaderCompilationBuilder.cs)、[CollectBlockSymbols](../../src/Shaderlyn.Semantics/Programs/ShaderCompilationBuilder.cs)）。
CLI から対象を変える手段はありません（`--keep-both-branches` は廃止しました）。
`SemanticsOptions.BothBranchSymbols` は残っており、次の 2 か所が使います。

| 利用箇所 | 用途 |
| --- | --- |
| 言語サーバー（[AnalysisSession.cs](../../src/Shaderlyn.LanguageServer/AnalysisSession.cs)） | エディタで「選んだ構成で表示」するとき、空にして構成を固定する |
| テスト | 方法 B の経路を確実に通す。[BothBranchConsistencyTests](../../tests/Shaderlyn.Tests/BothBranchConsistencyTests.cs) は既定と空の両方で解析し、指摘が同じことを確かめる |

並べてよい形かは [ConditionalRegionScanner.cs](../../src/Shaderlyn.Hlsl/Preprocessing/ConditionalRegionScanner.cs) が領域を先読みして判定します
（[Scan](../../src/Shaderlyn.Hlsl/Preprocessing/ConditionalRegionScanner.cs)、
[ClassifyBranch](../../src/Shaderlyn.Hlsl/Preprocessing/ConditionalRegionScanner.cs)、
判定の種類は [ConditionalRegionLayout](../../src/Shaderlyn.Hlsl/Preprocessing/ConditionalRegionScanner.cs)）。

| 判定 | 条件 |
| --- | --- |
| `Mergeable` | 各分岐で括弧（`{} () []`）が釣り合い、最後のトークンが `;` か `}`。空の分岐も可 |
| `NotSelfContained` | 上を満たさない（文の途中、仮引数の並び、`,` で終わるなど） |
| `DefinesMacros` | 領域の中に `#define` / `#undef` がある。定義した名前が条件の中でしか使われていなければ並べる（[CanMerge](../../src/Shaderlyn.Hlsl/Preprocessing/HlslPreprocessorConditionals.cs)） |
| `SwitchesIncludes` | 領域の中に `#include` がある |
| `Unterminated` | `#endif` が無い |

補足として、

- 内側の `#if` は同じ規則で先に判定し、`Mergeable` でなければ外側もその理由で返す
- 指令の行そのものは数えない（出力に残らないため）
- 入れ子は「**外側も並べているとき**」にだけ並べる（[ChooseKeptCondition](../../src/Shaderlyn.Hlsl/Preprocessing/HlslPreprocessorConditionals.cs)）
- バリアントは、既定の構成が並べた領域だけを並べる（`PreprocessorOptions.MergeOnlyRegions`）。並べてよいかはマクロの状態にも依存するので、構成ごとに判断が変わると、既定の木との違いが「そのシンボルで変わる部分」だけではなくなる。領域は指令の（ファイル, 位置）で持ち、取り込んだヘッダの領域も含める。取り込みの記録にも残すので、使い回したヘッダの分も欠けない
- バリアントは宣言したキーワード (`DeclaredSymbols`) を既定の構成と同じにして展開する。条件付きで覚えるマクロの条件 (`#if !defined(_B)` の中の `#define WNB`) はこれで書くので、空にするとバリアントだけが `#ifdef WNB` を並べず、関係の無いキーワードの否定が条件に付く (HDRP の Lit.shader の `WRITE_NORMAL_BUFFER`)
- 並べなかった条件が、並べた分岐で定義したマクロを見ていたら、その分岐のシンボルは並べずに展開し直す (`NoteMergedMacroConditionUse`)。並べた `#define` はどの構成でも効くので、その場の定義の有無で分岐を選ぶと、定義の無い構成でも定義のある側を読む
- 並べ直しで並べる対象から外したキーワードは、条件がヘッダにしか無くても構成の候補に入れる。並べる対象でないキーワードの条件は、並べられなかったとも記録されない
- 条件の索引 (`ConditionMapBuilder`) は、バリアントが並べた範囲も集める。バリアントにしか無いノードが、その木で並べた分岐の中にあることがある
- 互いに関係しないキーワードは 1 回の展開にまとめる。違いがどのキーワードのものかは、重なる `#if` の連なり (`KeywordRegions`) で決め、決められなければ 1 つずつ展開し直す
- 並べずに展開しているキーワードは、その構成では値が決まっている。条件付きで覚えたマクロの条件にそうしたキーワードが現れたら、その値で置き換えてから並べるかを決める（`SymbolCondition.Assume`）。置き換えずに並べると、そのキーワードのときだけの分岐の `#define` がこの構成に漏れる（HDRP の Lit.shader の `_HEIGHTMAP` → `_CONSERVATIVE_DEPTH_OFFSET` → `SV_POSITION_QUALIFIERS`）。条件の巻き上げも、その構成で通らない定義は複製しない
- 並べた分岐で定義したマクロがコードとして展開されたら、そのシンボルを並べずにブロックを展開し直す（`PreprocessResult.MergedMacroConflicts`、[ParseWithoutMergedMacroConflicts](../../src/Shaderlyn.Semantics/Programs/ShaderCompilationBuilder.cs)）。取り込んだヘッダが使っている場合も、取り込みの記録に残した名前から分かる

条件の巻き上げ（[HlslPreprocessorHoisting.cs](../../src/Shaderlyn.Hlsl/Preprocessing/HlslPreprocessorHoisting.cs)）について、

- 複製は、どれも同じ位置から作られる。位置で条件を引くと `_A` の複製と `!_A` の複製が重なり、両方「常に」になる。そのため複製ごとにトークンを別のインスタンスにし（`HlslSyntaxToken.Duplicate`）、範囲に印を付ける（`ConditionalTokenRange.IsHoisted`）。条件の索引はこの範囲だけを位置ではなくトークンで引く。ノードの先頭と末尾のトークンが同じ複製のものなら、その複製の条件が付く（`ConditionMap.GetHoistedCondition`）
- `#define` しか無い領域を `RegionDefinesMacros` で並べなかった場合、そこで定義したマクロのコードでの展開がすべて巻き上げによるものなら、展開を終えた時点でそのシンボルを並べたものとして数え直す（`RestoreHoistedDeclines`）。展開し直したバリアントには複製の片方しか無く、どちらの複製と対応するかが位置からは決まらないためである。取り違えると、`multi_compile _A _B` で `float4 d` に `!_B` が付いていた。巻き上げを諦めた箇所、別のマクロの本体やヘッダの中での展開、`#elif` や `#undef` を含む領域は対象にしない
- それでもバリアントを作る場合、複製の中のノードには突き合わせの結果を付けず、バリアントの木から足すこともしない（`ConditionMapBuilder.IsInsideAny`）

並べられなかった領域に現れたシンボルは `DeclinedBothBranchSymbols` に記録され、方法 B の候補になります
（[DeclineBothBranchSymbolsIn](../../src/Shaderlyn.Hlsl/Preprocessing/HlslPreprocessorDirectives.cs)、
[CollectMergedSymbols](../../src/Shaderlyn.Semantics/Programs/ShaderCompilationBuilder.cs)）。
1 つの領域でも並べられなければ、そのシンボルは他の領域で並べられていても候補に入ります。

### 構成を決めるもの

1 つの構成は、次の 4 つで決まります。どれを落としても、実在しない構成を解析することになります。

| 決めるもの | 内容 | 適用する場所 |
| --- | --- | --- |
| 環境 | `SHADER_API_*`、`UNITY_VERSION` など。1 通りだけを仮に選ぶ | [SemanticsOptions.DefaultPredefinedMacros](../../src/Shaderlyn.Semantics/SemanticsOptions.cs)、`--define`（[ValueOptions](../../src/Shaderlyn.Cli/CommandLineOptions.cs)） |
| カーネルのマクロ | `#pragma kernel KMain USE_X` がそのカーネルだけに与えるもの | [ProgramBlockExtractor](../../src/Shaderlyn.Semantics/Programs/ProgramBlockExtractor.cs) の `ExtraMacros`、[WithExtraMacros](../../src/Shaderlyn.Semantics/Programs/ShaderCompilationBuilder.cs) |
| シンボル | その構成で有効にするシェーダーキーワード | [ConfigurationMacros](../../src/Shaderlyn.Semantics/Programs/ShaderCompilationBuilder.cs) |
| 宣言から分かる制約 | 同じ行のシンボルは同時に有効にならない。`_` の無い `multi_compile` はどれか 1 つが必ず有効 | [SymbolConstraints](../../src/Shaderlyn.Core/Syntax/SymbolConstraints.cs) |

既定の構成もバリアントも、この 4 つを同じ順で重ねて作ります（`ConfigurationMacros`）。
どれか 1 つでも別々に扱うと、片方の経路にだけ穴が空きます。実際、カーネルのマクロをバリアントに渡し忘れていた不具合がありました。

### 方法 B の実装

`SelectVariantSymbols`（[ShaderCompilationBuilder.cs](../../src/Shaderlyn.Semantics/Programs/ShaderCompilationBuilder.cs)）が構成の一覧を作ります。

**(a) シンボル 1 つの構成。** 次を**すべて**満たす名前を選びます。

- `#pragma` で宣言されている
- 方法 A で並べられていない
- マクロ展開の結果として現れた名前ではない
- **解析対象のファイル自身**に書かれた条件が参照している（取り込んだヘッダの条件は (c) で扱う）

**(b) 同時に定義されていないと通らない組の構成**（[SelectVariantCombinations](../../src/Shaderlyn.Semantics/Programs/ShaderCompilationBuilder.cs)）。

- 組の元は、両方の分岐を並べなかった分岐ごとに、**外側の分岐の条件をすべて掛け合わせて**拾う（[RecordRequiredCombinations](../../src/Shaderlyn.Hlsl/Preprocessing/HlslPreprocessorConditionals.cs)、[EnumerateRequiredCombinations](../../src/Shaderlyn.Core/Syntax/SymbolCondition.cs)）。外側を読み飛ばしていても、入れ子の条件は読む
- 条件の中でシンボルとして読むのは、ブロックが宣言しているシンボルだけ（`PreprocessorOptions.DeclaredSymbols`）。並べる対象（`BothBranchSymbols`）とは別に渡すので、並べない設定で展開しても組は変わらない
- 組の中のシンボルは**すべて宣言されている**必要がある。方法 A で並べたシンボルが混ざってもよい（並べた `#ifdef _A` の中の、並べなかった `#ifdef _B` は、`_A` の構成でも `_B` の構成でも通らない）。すべて並べたシンボルなら既定の構成の木に載っているので作らない
- 同じ行どうしの組を作らないこと、方法 A で外側と合わせて成り立たない分岐を並べないことは、[SymbolConstraints](../../src/Shaderlyn.Core/Syntax/SymbolConstraints.cs)（`PreprocessorOptions.SymbolConstraints`）で判断する。ルールが条件を掛け合わせて「成り立つか」を見るときも `ConditionMap.IsPossible` を通す。全 Pass をまとめた索引では、どの Pass でも同じ行に並んでいる組だけを排他とする
- `_` の無い `multi_compile` の行は、既定の構成とどのバリアントも、その行のシンボルを 1 つも有効にしていなければ先頭を定義する（[ConfigurationMacros](../../src/Shaderlyn.Semantics/Programs/ShaderCompilationBuilder.cs)）。先頭だけを有効にした構成は既定の構成と同じなので作らない。先頭が無いことを求める分岐には、同じ行の別のシンボルを有効にした構成を作る（[SymbolConstraints.EnumerateRequiredCombinations](../../src/Shaderlyn.Core/Syntax/SymbolConstraints.cs)）。`shader_feature` は対象にしない（[既知の限界](#既知の限界) 10、[ShaderSymbols.IsRequiredSet](../../src/Shaderlyn.Hlsl/Preprocessing/ShaderSymbols.cs)）

**(c) ヘッダの条件の構成。** 取り込んだヘッダに書かれた条件は、次のものだけを 1 つずつの構成にします。

- そのヘッダ自身が宣言したシンボル（`IncludedDeclaredSymbols`）
- このファイルが宣言したシンボルのうち、ヘッダの中で両方の分岐を並べられなかったもの（`declinedInHeaders`）。
  並べられなかった側はどの木にも載らないので、そのシンボルを有効にした構成を作る

**並び順は (a)、(b)、(c) の順にします。** 上限に達したときは後ろから落とします。
(a) は名前順、(b) は中身を並べた文字列の順で、実行のたびに変わりません。

上限は [SemanticsOptions.DefaultMaxSymbolVariants](../../src/Shaderlyn.Semantics/SemanticsOptions.cs)（既定 8）と
[CommandLineOptions.ReadMaxSymbolVariants](../../src/Shaderlyn.Cli/CommandLineOptions.cs) で決まり、キャッシュの鍵にも入ります（[AnalysisCacheKey.AppendOptions](../../src/Shaderlyn.Cli/Caching/AnalysisCacheKey.cs)）。
超過分は、1 つずつの構成は `UnexploredSymbols`、組の構成は `UnexploredSymbolCombinations` に入ります
（[ShaderCompilation.UnexploredSymbols](../../src/Shaderlyn.Semantics/ShaderCompilation.cs)、[UnexploredSymbolCombinations](../../src/Shaderlyn.Semantics/ShaderCompilation.cs)）。

[ConditionalMerge.Merge](../../src/Shaderlyn.Semantics/Conditional/ConditionalMerge.cs) が既定の構成の木とバリアントの木を比べ、
[ConditionMapBuilder.Build](../../src/Shaderlyn.Semantics/Conditional/ConditionMapBuilder.cs) が結果を索引にまとめます。
バリアントは、既定の構成と同じシンボルを並べて展開し、有効にしたシンボル（と、それと同時には有効にならないシンボル）だけを固定します（[Expand](../../src/Shaderlyn.Semantics/Programs/ShaderCompilationBuilder.cs)）。並べずに展開すると、既定の木で並べた別のシンボルの分岐までバリアントから消え、突き合わせがその分岐に無関係なシンボルの否定を付けます。

| 結果 | 内容 | コード |
| --- | --- | --- |
| `ConditionalNodes` | ノードとその出現条件 | [ConditionalNode](../../src/Shaderlyn.Semantics/Conditional/ConditionalMerge.cs) |
| `NodeInsertions` | バリアントの木にしか無いノードと、既定の木での挿入先（親） | [NodeInsertion](../../src/Shaderlyn.Semantics/Conditional/ConditionalMerge.cs) |
| `UnmergedLocations` | 対応が取れなかった箇所。[SL0004](../rules/SL0004.md) として報告 | [ConditionMap.UnmergedLocations](../../src/Shaderlyn.Semantics/Conditional/ConditionMap.cs) |

### 出現条件（`SymbolCondition`）

[SymbolCondition](../../src/Shaderlyn.Core/Syntax/SymbolCondition.cs) はシンボルの真偽の論理積の論理和（積和形）です。
特別な値に `Always`（既定値）、`Never`、`Unknown`（表しきれなかった）があり、`And` / `Or` / `Negate` で組み立てます。
`IEquatable<SymbolCondition>` を実装しているので、辞書のキーにできます。

[ConditionMap.GetCondition](../../src/Shaderlyn.Semantics/Conditional/ConditionMap.cs) は次の順で組み立てます。

1. 突き合わせできなかった領域の中なら `Unknown` を返す（「分からない」を「常に」にしない）
2. バリアントとの突き合わせから出た条件を、**親を辿りながら**論理積で重ねる
3. 方法 A で並べた領域の条件を論理積で重ねる
4. 条件の巻き上げで複製した文の中なら、その複製の条件を論理積で重ねる

`IsAlwaysPresent`（[IsAlwaysPresent](../../src/Shaderlyn.Semantics/Conditional/ConditionMap.cs)）は `GetCondition(node).IsAlways` です。

### ルールから見えるもの

ルールの書き方は [ルール API リファレンス](../custom-rules/api-reference.md) にあります。ここでは実装の場所だけを示します。

| API | 内容 | コード |
| --- | --- | --- |
| `Programs` | 既定の構成のコードブロックごとの解析結果 | [ShaderCompilation.cs](../../src/Shaderlyn.Semantics/ShaderCompilation.cs) |
| `SymbolVariants` | バリアントの解析結果（`EnabledSymbols` に有効にしたシンボル名。論理積の構成では 2 つ以上） | [SymbolVariants](../../src/Shaderlyn.Semantics/ShaderCompilation.cs) |
| `UnexploredSymbols` | 上限に達して、1 つだけ有効にする構成を作らなかったシンボル | [UnexploredSymbols](../../src/Shaderlyn.Semantics/ShaderCompilation.cs) |
| `UnexploredSymbolCombinations` | 上限に達して、同時に有効にする構成を作らなかった組と、その組でしか通らない分岐を始めた指令の位置 | [UnexploredSymbolCombinations](../../src/Shaderlyn.Semantics/ShaderCompilation.cs) |
| `GetConditionMap()` | 出現条件の索引 | [ConditionMap.cs](../../src/Shaderlyn.Semantics/Conditional/ConditionMap.cs) |
| `GetInsertedChildren(node)` | そのノードの子として足すノード | [GetInsertedChildren](../../src/Shaderlyn.Semantics/Conditional/ConditionMap.cs) |
| `EnumerateOwnNodes(program)` | 走査用。宣言とその子孫に加え、**足すノードとその子孫**も返す | [EnumerateOwnNodes](../../src/Shaderlyn.Semantics/ShaderCompilation.cs)、[EnumerateInserted](../../src/Shaderlyn.Semantics/ShaderCompilation.cs) |

`EnumerateOwnNodes` が返す足すノードには、`IsReportable` を満たすものだけが含まれます。
利用者のファイル (このファイルと、利用者が書いて取り込んだヘッダ) に直接書かれたノードと、
**それらのファイルが書いたマクロの本体から来たノード**です（`HlslSyntaxToken.MacroDefinitionSpan` / `MacroDefinitionSource`）。
Unity や外部パッケージのヘッダのノードと、それらのマクロが作ったノードは除きます。

「無いこと」を根拠にするルールが使う、解析されなかった場所の名前は
[ShaderCompilation.UnanalyzedIdentifiers](../../src/Shaderlyn.Semantics/ShaderCompilation.cs) にあります
（ファイル自身の分は `SkippedRootIdentifiers`、ヘッダの分は `SkippedIncludedIdentifiers`）。

### シンボルに関わるルール

| ID | 判定の仕方 | コード |
| --- | --- | --- |
| [HL0330](../rules/HL0330.md) | `ConditionalIdentifiers` のうち、宣言に無く、下線+大文字の命名に合い、マクロでもなく、このファイルに書かれたもの。取り込まれる前提の断片 (`.hlsl` など) では報告しない | [ShaderSymbolAnalyzer.ReportUndeclared](../../src/Shaderlyn.Rules/Hlsl/ShaderSymbolAnalyzer.cs)、[IsMaterialSymbol](../../src/Shaderlyn.Rules/Hlsl/ShaderSymbolAnalyzer.cs) |
| [HL0331](../rules/HL0331.md) | 宣言のうち、条件でも実行時の参照でも現れないもの。宣言がこのファイルにある場合だけ | [ReportUnused](../../src/Shaderlyn.Rules/Hlsl/ShaderSymbolAnalyzer.cs) |
| [SL0003](../rules/SL0003.md) | `UnexploredSymbols` のシンボルごとに 1 件。位置はそのシンボルを参照する最初の条件（このファイルに書かれたもの）。見つからなければファイルの先頭。`UnexploredSymbolCombinations` の組ごとにも 1 件。位置はその組でしか通らない分岐を始めた指令 | [UnexploredSymbolAnalyzer.cs](../../src/Shaderlyn.Rules/Semantics/UnexploredSymbolAnalyzer.cs) |
| [HL0314](../rules/HL0314.md) | 2 つの宣言の出現条件を掛け合わせて成り立つとき。ただし、その条件を満たすバリアントを展開していれば、その木に両方があるときだけ | [RedeclarationAnalyzer.Collides](../../src/Shaderlyn.Rules/Hlsl/RedeclarationAnalyzer.cs) |
| [SL0004](../rules/SL0004.md) | `UnmergedLocations` の各位置 | [UnresolvedConditionAnalyzer.Analyze](../../src/Shaderlyn.Rules/Semantics/UnresolvedConditionAnalyzer.cs) |
| [HL0352](../rules/HL0352.md) | 片側にしか条件が無い初期化の要素では判断を見送る | [ValueConversionAnalyzer.CheckInitializerList](../../src/Shaderlyn.Rules/Hlsl/ValueConversionAnalyzer.cs) |
| HL0311 ほか | 条件が `Unknown` の箇所では報告しない | [CallSignatureAnalyzer.AnalyzeCall](../../src/Shaderlyn.Rules/Hlsl/CallSignatureAnalyzer.cs) |

### エディタ（VS Code 拡張）

エディタでは、構成を選んで表示できます。

| 項目 | 内容 | コード |
| --- | --- | --- |
| `shaderlyn/conditionSymbols` | 切り替え候補になる名前の一覧を返す。`declaredByPragma` はその名前が `#pragma` で宣言されたシンボルか | [OnConditionSymbolsAsync](../../src/Shaderlyn.LanguageServer/ShaderLanguageServer.cs)、[ConditionSymbol](../../src/Shaderlyn.LanguageServer/ConditionSymbols.cs) |
| `shaderlyn/setDefinedSymbols` | 選んだ名前を定義済みとして解析し直す | [OnSetDefinedSymbolsAsync](../../src/Shaderlyn.LanguageServer/ShaderLanguageServer.cs) |
| `shaderlyn/inactiveRegions` | 効いていない領域の通知 | [PublishInactiveRegionsAsync](../../src/Shaderlyn.LanguageServer/ShaderLanguageServer.cs) |

### 限界の経緯

[既知の限界](#既知の限界)の 3 は以前「取り込んだヘッダの条件はバリアントにしない」でした。
ヘッダが自分で `#pragma multi_compile` を書いている場合、解析しているファイルにそのシンボルの条件が 1 つも無いため、
そのヘッダの分岐は既定の構成の側しか読まれませんでした。
今はヘッダ自身が宣言したシンボルと、ヘッダの中で並べられなかったシンボルを (c) として構成にします
（[SelectVariantSymbols](../../src/Shaderlyn.Semantics/Programs/ShaderCompilationBuilder.cs)）。

6 は以前「`#if X == 1` の形は片方の構成しか見ない」と書いていましたが、誤りでした。
`#if X == 1` の `X` は参照として記録され（[ReadSymbolAtom](../../src/Shaderlyn.Hlsl/Preprocessing/HlslPreprocessorDirectives.cs)）、
バリアントの対象になります。`== 1` / `!= 0` / `> 0` のいずれも、中のコードが検査されることを確かめました。
誤った結論は `HL0310` で検証したためです。当時このルールは、どこかの構成で読み飛ばされた名前をすべて宣言済みとして扱っており、
指標になりませんでした（今は限界 5 の範囲まで狭めてあります）。

すべての構成を 1 つずつ解析した結果と突き合わせるテスト（次の節）で見つかった次の 3 つは、報告するようにしました。

- 条件付きでしか宣言していない変数を、その条件の外で使う → [HL0315](../rules/HL0315.md)
- 同じ名前を条件ごとに違う型で宣言し、ある構成にしかない成分を取り出す → [HL0312](../rules/HL0312.md)。
  構成ごとに型を求める（[ExpressionTypeBinder.EnumerateAssumptions](../../src/Shaderlyn.Semantics/Programs/ExpressionTypeBinder.cs)）。
  ただし両方の分岐を並べられる場合に限る。並べられない場合は限界 1 のまま。
  同じ名前の**関数**を条件ごとに違う型で返す場合も同じように扱う。
  引数の個数で絞っても型が割れる名前を先に集めておき
  （`VaryingFunctions`）、仮定ごとに見える宣言だけへ絞る
- 別々の条件で同じマクロを違う中身で定義する → [HL0002](../rules/HL0002.md)。
  読み飛ばす分岐の `#define` も条件付きで覚え、`defined()` をその定義がある条件として読む
  （[RecordConditionalDefinition](../../src/Shaderlyn.Hlsl/Preprocessing/HlslPreprocessorDefines.cs)）

### 検証

| テスト | 何と何を比べるか | コード |
| --- | --- | --- |
| 並べても並べなくても同じ | 方法 A を使う既定の解析と、方法 A を使わない解析。近似どうしの比較なので、両方に同じ穴があれば見つからない | [BothBranchConsistencyTests](../../tests/Shaderlyn.Tests/BothBranchConsistencyTests.cs) |
| すべての構成との突き合わせ | 既定の解析と、Unity が作る構成をすべて列挙して 1 つずつ解析した結果の和。後者が正解になる | [ConfigurationOracleTests](../../tests/Shaderlyn.Tests/ConfigurationOracleTests.cs) |

**すべての構成との突き合わせ**は次のように行います。

- 構成は、実装（`ShaderSymbols`）を使わずにテストの側で列挙する。`_` の無い `multi_compile` はどれか 1 つ、それ以外は「無し」も含む。実装と同じ読み方をすると、読み方の誤りが両方に入って見えなくなる
- 1 つの構成は、構成を固定した解析（`SemanticsOptions.FixedSymbolConfiguration`、方法 A も方法 B も使わない）で解析する。構成を固定すると、シンボルで外れた分岐の名前を「見えていないだけかもしれない」と扱わない
- 比べるのはルールと位置だけで、メッセージは比べない。「どこまで調べたか」の報告（SL0002 / SL0003 / SL0004 / TOOL0004）と、構成をまたいで「どこにも無い」ことを判断するルール（SL1001 / SL1002 / SL1004 / HL0330 / HL0331）は比べない
- 既定の解析が条件付きで言う HL0311 / HL0313 / HL0315 は、構成を固定した解析の HL0310 / HL0312 と同じ誤りとして比べる
- 見逃すと分かっているシェーダーは、理由を添えて `KnownGaps` に登録する。直ったら外す（外し忘れるとテストが落ちる）

## 関連

- [CLI の使い方](cli-usage.md#シンボルの展開数) — `--max-symbol-variants`
- [SL0003](../rules/SL0003.md) — 調べていないシンボルの経路がある
- [SL0004](../rules/SL0004.md) — 条件を追えなかった箇所がある
- [ルール API リファレンス](../custom-rules/api-reference.md) — `GetConditionMap()` と `SymbolCondition`
- [解析の流れ](../internals/architecture.md) — 開発者向け。解析全体の中での位置づけ
- [設計判断「条件付きコンパイル」](../internals/design-decisions.md#条件付きコンパイル) — 判断の理由と採らなかった案
