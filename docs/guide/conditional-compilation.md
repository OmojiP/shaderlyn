# 条件付きコンパイルの解析

`#ifdef` や `#if` で切り替わるコードを、shaderlyn がどのように解析するかを、入力から順に説明します。条件付きのコードが各段階でどう扱われるか、また解析できない箇所があると何が報告されるかを追えます。

`#pragma` で宣言されたシンボルが関わる分岐は、両側を検査します。構文として一緒に扱える分岐はまとめて解析し、そうでない領域は対象の構成を変えて解析します。どちらの方法になるかはコードの形によって決まります。

`SL0003` は上限などにより調べていない構成があるとき、`SL0004` は構成間の対応を特定できないときに報告されます。詳細はそれぞれ[5.3](#53-上限と-sl0003)と[6.3](#63-突き合わせに失敗した場合sl0004)を参照してください。

末尾の[付録](#付録-実装の詳細)には、同じ流れを実装側から説明しています。本文の例は解析結果を確認したものです。

## 目次

- [なぜ両方の分岐を調べるのか](#なぜ両方の分岐を調べるのか)
- [分岐の解析方法](#分岐の解析方法)
- [解析の流れ](#解析の流れ)
- [1. 字句解析](#1-字句解析)
- [2. シンボルの宣言を読む](#2-シンボルの宣言を読む)
- [3. プリプロセス](#3-プリプロセス)
- [4. 構文解析](#4-構文解析)
- [5. 構成ごとの展開（方法 B）](#5-構成ごとの展開方法-b)
- [6. 条件の索引](#6-条件の索引)
- [7. 名前と型](#7-名前と型)
- [8. ルールとエディタ](#8-ルールとエディタ)
- [解析方法が分かれる例](#解析方法が分かれる例)
- [既知の限界](#既知の限界)
- [付録: 実装の詳細](#付録-実装の詳細)

---

## なぜ両方の分岐を調べるのか

Unity はシェーダーキーワードの組み合わせごとにシェーダーをコンパイルします。
ある組み合わせで無効な `#ifdef` の中身は、別の組み合わせでは有効になります。
既定の 1 通りの構成だけを解析すると、それ以外の構成でしか通らないコードの誤りを見逃します。

shaderlyn は、`#pragma` で宣言されたシンボルが見ている分岐を、**どちらの側も検査します**。

## 分岐の解析方法

条件付きのコードは、領域の形に応じて次の 2 通りで解析します。

| 方法 | やること | 段階 |
| --- | --- | --- |
| 方法 A | 両方の分岐のトークンを並べ、1 回の構文解析で 1 本の木にする。ノードごとに「どの条件のもとで存在するか」を付ける | [3](#3-プリプロセス)・[4](#4-構文解析)・[6](#6-条件の索引) |
| 方法 B | そのシンボルを有効にした構成を別に展開して構文解析し、既定の構成の木と突き合わせる | [5](#5-構成ごとの展開方法-b)・[6](#6-条件の索引) |

方法 B で作る構成を、このページではバリアントと呼びます。
バリアントは 1 つ作るごとに解析の時間が伸び、上限（既定 8）に届くと残りは調べずに [SL0003](../rules/SL0003.md) になります。
また、方法 B の領域どうしの組み合わせは、条件に書かれたものしか調べません（[5.4](#54-条件に書かれていない組み合わせ)）。
どの領域にどちらの方法を使うかは、プリプロセス時に判定します。

## 解析の流れ

コードブロック（`HLSLPROGRAM` や `.compute` のカーネル）ごとに、次の順で処理します。

| 段階 | 入力 → 出力 | 条件の扱い |
| --- | --- | --- |
| [1. 字句解析](#1-字句解析) | テキスト → トークン列 | この段階では条件として扱わない |
| [2. シンボルの宣言を読む](#2-シンボルの宣言を読む) | `#pragma` のトークン → シンボルと、その間の制約 | どの名前がシンボルかが決まる |
| [3. プリプロセス](#3-プリプロセス) | トークン列 → 指令の無いトークン列と、条件の範囲の表 | 領域ごとに、両方の分岐を並べるか（方法 A）を決める。条件は範囲の表に残る |
| [4. 構文解析](#4-構文解析) | トークン列 → 構文木 1 本 | パーサーは条件を扱わない |
| [5. 構成ごとの展開](#5-構成ごとの展開方法-b) | 並べなかったシンボル → バリアントごとに 3〜4 をやり直す | どの構成を作るか、上限に届いたかを決める |
| [6. 条件の索引](#6-条件の索引) | 範囲の表とバリアント → ノードごとの条件 | 範囲とノードを対応させる。バリアントと突き合わせる |
| [7. 名前と型](#7-名前と型) | 木と条件 → 構成ごとの型 | 構成を仮定して型を決める |
| [8. ルールとエディタ](#8-ルールとエディタ) | ここまでの結果 → 指摘、ホバー、`--inspect` | 条件を添えて報告する。条件が分からなければ報告しない |

取り込んだヘッダがシンボルを宣言していた場合は、それを足して 2〜3 をやり直します（[ヘッダに置いた `#pragma`](#ヘッダに置いた-pragma)）。

**構文解析の時点では、マクロも `#if` もトークン列に残っていません。** 条件は「トークン列のどこからどこまでが、どの条件のものか」という表として横に持ち、木ができてからノードに付けます。
次のコードを例に、各段階の出力を示します。

```hlsl
#pragma multi_compile _ _A

#ifdef _A
#define CTYPE float3
#else
#define CTYPE float4
#endif

float Shade()
{
    CTYPE d = 1;
#ifdef _A
    d.x = 2;
#endif
    return d.x;
}
```

| 段階 | 出力 |
| --- | --- |
| 1 | `#` `pragma` `multi_compile` … `#` `ifdef` `_A` … のトークン列。指令もトークンとして並ぶ |
| 2 | シンボル `_A` |
| 3 | トークン列 `float Shade ( ) { float3 d = 1 ; float4 d = 1 ; d . x = 2 ; return d . x ; }`<br>範囲の表: 5〜9 番目は `_A`（複製）、10〜14 番目は `!_A`（複製）、15〜20 番目は `_A`<br>マクロ表の `CTYPE` は既定の構成の `float4` だけ |
| 4 | 関数の中に文が 4 つ並んだ木。`float3 d` と `float4 d` が並んでいても、パーサーは気にしない |
| 5 | バリアントは作らない（どの領域も方法 A で済んだ） |
| 6 | `float3 d = 1;` は `_A`、`float4 d = 1;` は `!_A`、`d.x = 2;` は `_A`、`return d.x;` は条件なし |
| 7 | `return` の `d` は、`_A` のとき `float3`、`!_A` のとき `float4` |
| 8 | ルールは構成ごとに検査する。ホバーは構成ごとの型を並べ、`--inspect` は複製のそれぞれに `#if _A` / `#if !_A` を付ける |

## 1. 字句解析

テキストをトークン列に分けます。`#ifdef _A` は `#`・`ifdef`・`_A` の 3 つのトークンで、この段階では条件として読みません。

`.shader` では、ShaderLab の部分を伏せたテキストからコードブロックを取り出してから分けます（[解析の流れ](../internals/architecture.md)）。

## 2. シンボルの宣言を読む

名前が `shader_feature` / `multi_compile` / `dynamic_branch` の**いずれかで始まる** `#pragma` を、シンボル（Unity の用語ではシェーダーキーワード）の宣言として読みます。
`shader_feature_local`、`multi_compile_local_fragment`、`multi_compile_instancing` も含みます。

```hlsl
#pragma multi_compile _ _A _B        // _A と _B を宣言する
#pragma shader_feature_local _DETAIL // _DETAIL を宣言する
```

- その行の識別子すべてをシンボルとして読みます。下線だけの名前（`_`、`__` …）は「シンボル無しの構成」を表すので除きます
- 同じ行のシンボルは同時に有効になりません。`_` の無い `multi_compile` の行は、どれか 1 つが必ず有効です。この制約は、構成を作るとき（段階 5）とルールが条件を掛け合わせるとき（段階 8）に使います
- `#pragma dynamic_branch` のシンボルは、`if (_A)` のように実行時の分岐で使います。条件で使っていなくても、コードに現れていれば「使われている」と見なします

宣言したシンボルをどこでも使っていなければ [HL0331](../rules/HL0331.md)、宣言していない名前を条件で見ていれば [HL0330](../rules/HL0330.md) が出ます。
宣言していない名前の分岐は決して通らないので、構成は作りません。

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
そのため、1 度プリプロセスしてから宣言を集め直し、ヘッダが宣言していたら、その分も含めてプリプロセスし直します。
ヘッダの宣言は、シェーダー自身が書いた `#pragma` とまったく同じに扱われます。

- `#ifdef _HEADER_QUALITY` は両方の分岐を 1 本の木に並べます（方法 A）。
  `Shade` を呼ぶと [HL0311](../rules/HL0311.md)（`!_HEADER_QUALITY` のとき定義がありません）が出ます
- 並べられない形は、構成として作り直します（方法 B）。
  ヘッダに書かれた `#if` も、**そのヘッダ自身が宣言したシンボル**については条件として読みます
- `_` の無い `#pragma multi_compile MODE_A MODE_B` がヘッダにあれば、
  既定の構成でも `MODE_A` を定義します。拾わないと、どちらも無効という実在しない構成を解析することになります

## 3. プリプロセス

`#include` を取り込み、マクロを展開し、`#if` を評価して、構文解析に渡すトークン列を作ります。
既定の構成（宣言したシンボルが無効で、`_` の無い行は先頭が有効）で行います。
取り込んだヘッダがシンボルを宣言していた場合（[ヘッダに置いた `#pragma`](#ヘッダに置いた-pragma)）と、
並べた分岐で定義したマクロがコードで使われていた場合（[3.5](#35-include-の切り替え)）は、やり直します。

シンボルを見ている `#if` の領域ごとに、**両方の分岐をトークン列に並べられるか**を判断します。
並べられれば方法 A、並べられなければ既定の構成の分岐だけを出力し、そのシンボルを方法 B の候補にします（段階 5）。

この段階の出力は 3 つです。

| 出力 | 内容 |
| --- | --- |
| トークン列 | 指令を取り除き、並べた分岐は両方とも含めたもの |
| 条件の範囲の表 | 「何番目から何番目のトークンは、どの条件のものか」。段階 6 でノードに対応させる |
| マクロ表 | **既定の構成で最後に効いた定義だけ**。別の構成の定義は、条件付きで別に覚えている（3.3） |

### 3.1 条件式を読む

次の形の条件は、「どのシンボルが有効なときに通るか」として読みます。

| 書き方 | 扱い |
| --- | --- |
| `#ifdef X` / `#ifndef X` | シンボルの条件として読む |
| `#if defined(X)` / `#if defined X` | シンボルの条件として読む（括弧は省略可） |
| `#if X` | X が宣言されたシンボルなら、シンボルの条件として読む |
| `#if !X`、`#if A && B`、`#if A \|\| B`、`#if (A)` | シンボルの条件として読む |
| `#if X == 1`、`#if X != 0`、`#if X > 0` | 値として解く。X が宣言されたシンボルなら、その構成を作って検査する（方法 B） |
| `#if X == 2`、`#if defined(_X) && defined(_Y)`（同じ行） | どの構成でも成り立たない。[HL0332](../rules/HL0332.md) を報告し、構成は作らない |
| `#if SHADER_API_D3D11` | 宣言されたシンボルではないので、値として解く |

`#ifdef _A`、`#if _A`、`#if defined(_A)` の 3 つは同じに扱います。Unity は有効なシンボルを値 1 のマクロとして定義するため、
URP 自身も `#if _ALPHATEST_ON` の形を使っています。

**値として比べる書き方は、方法 A で並べられません。** `#if _A == 1` と `#if _A` は通る構成が同じですが、
前者はシンボルの条件として読めないので、`_A` を有効にした構成を別に作ります。

**どの構成でも成り立たない条件は [HL0332](../rules/HL0332.md)（情報）として報告します。**
宣言されたシンボルと定数だけでできた条件について、シンボルの有効・無効の組をすべて試し、どれでも偽なら成り立たないと判断します。
同じ `#pragma` 行のシンボルを両方求める組や、`_` の無い `multi_compile` の行をすべて否定する組は、実在しないので試しません。
`SHADER_API_*` のような環境のマクロを含む条件は、別のプラットフォームで成り立つことがあるので判断しません。

`SHADER_API_*` や `UNITY_VERSION` のような環境のマクロは、1 通りの値を仮に選んで解きます（`--define` で変えられます）。

### 3.2 両方の分岐を並べる（方法 A）

`#ifdef` の領域が文や宣言の単位で閉じていれば、両方の中身をトークン列として並べます。

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

この方法で済んだ領域は、バリアントを作りません。`--max-symbol-variants 0` を指定しても
挙動は変わらず、`SL0003` も出ません。

並べるのは、各分岐で括弧（`{} () []`）が釣り合い、最後のトークンが `;` か `}` で終わる領域です（空の分岐も可）。
こう決めている理由は [4. 構文解析](#4-構文解析) にあります。
入れ子の `#if` は、外側も並べているときだけ並べます。
同じ `#pragma` 行のシンボル（同時に有効にならないもの）のように、外側と合わせて決して通らない分岐は並べません。

`#define` を切り替えている領域は、そのマクロを**条件の中でしか使っていなければ**並べます。
`#ifdef _A` の中で `#define USE_A 1` と書き、`#ifdef USE_A` でしか読まないなら、
`USE_A` は「`_A` が有効な条件」として読めるので、並べても展開結果は変わりません。

### 3.3 条件で中身が変わるマクロ（条件の巻き上げ）

マクロの中身を条件で切り替え、それをコードの中で使っている場合は、
**そのマクロを使う文を、定義ごとに展開し直して並べます**。

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

**複製するのは、条件がノードの存在にしか付けられないためです。**
段階 6 で付ける条件は「そのノードがどの構成で存在するか」で、1 つの宣言の型を構成ごとに変えることはできません。
また、マクロはこの段階で展開されて消えるので、ここで 1 通りにしか展開しなければ、もう一方の定義は木に届きません。
定義ごとに別の文として出力しておけば、3.2 と同じ形になり、そのまま方法 A で扱えます。

関数形式マクロも同じで、引数ごと展開し直します。
別々の `#ifdef` で同じ名前を定義する形（`#ifdef _A` で 1 つ、`#ifdef _B` でもう 1 つ）も複製します。
どの定義も当てはまらない構成のために、定義が無い側も作ります。
定義が 1 つしか無いマクロ（`#ifdef _A` の中だけで定義する）は複製しません。

**複製する範囲は構文解析が決めます。** 記号の数え方では、
`#define BEGIN struct Foo {` のようにマクロが括弧を作る場合に切れ目を取り違えます。
定義ごとに展開したものが文・宣言として読めなければ、次の切れ目で読み直します。
どれも読めなければ複製せず、1 つの構成の値で展開します。

別のマクロの本体を通して使っている場合（`#define COLOR_TYPE CTYPE` を `COLOR_TYPE color = 1;` と使う）も、
使う位置で、本体が参照している `CTYPE` の定義ごとに複製します。本体は 8 段までたどります。

次の場合、マクロの複製では扱えず、そのシンボルは方法 B の対象になります。

| コードの形 | 方法 B になる理由 |
| --- | --- |
| 定義を書き分けた分岐にコードもある | 並べなかった側のコードが、どの木にも載らない |
| 定義を `#elif` や入れ子の `#if` で 3 通り以上に書き分ける、または `#undef` してから定義する | 書き分けの条件を組み立てていない（[既知の限界](#既知の限界) 10） |
| マクロを使う文の中に `#if` がある | 複製すると指令を 2 度処理することになる |
| 1 つの文で、条件によって中身が変わるマクロを 2 つ以上使う（別のマクロの本体を通して参照する場合も含む） | 1 つの文で複製できるのは 1 つのマクロだけ |

複製した文は同じ行に並びますが、どれがどの構成のものかは区別されています。
エディタのホバーは構成ごとの型と展開結果を並べて示し（`CTYPE` の上なら「`_A` のとき `float3`」「`!_A` のとき `float4`」）、
`--inspect` の HLSL タブは複製のそれぞれに `#if _A` / `#if !_A` を付けます。

### 3.4 文の途中で分かれる `#if`（条件の巻き上げ）

分岐の中身が文の途中で切れていて、両方を並べると文にならない領域も、3.3 と同じ考え方で扱います。
**`#if` の手前から文の終わりまでを、分岐ごとに複製して並べます。**

```hlsl
float m
#ifdef _A
    = 1
#else
    = 2
#endif
    ;
```

これは次の 2 文になります（それぞれに条件が付きます）。

```hlsl
float m = 1;   // _A
float m = 2;   // !_A
```

型名だけを分ける形（`#ifdef _A` / `float2` / `#else` / `float4` / `#endif` / `v = 0;`）や、
初期化の要素を条件で足す形（`float4 c = { 1, 2, 3` / `#ifdef _A` / `, 4` / `#endif` / `};`）も同じです。
後者は `_A` の無い構成で要素が 3 個しかないので、[HL0352](../rules/HL0352.md) が `!_A のとき` として報告します。
`#elif` の分岐は、それまでの分岐の否定と自分の条件を掛け合わせた条件で複製します。
`#else` が無ければ、どの分岐も通らない構成のために、分岐の中身を入れない文も作ります。

この形はバリアントを作りません。別々の `#if` が同時に有効なときだけ起きる誤りも、
型を求めるときに構成を仮定して調べます（[7. 名前と型](#7-名前と型)）。

次の場合は複製せず、方法 B になります。

| コードの形 | 方法 B になる理由 |
| --- | --- |
| 複製する単位に `{ }` のブロックが入る（仮引数を条件で足す関数、戻り値の型だけを分ける関数、`if` の頭だけを分ける） | 関数や `if` の本体まで複製すると、どの構成にもあるコードまで条件付きになり、同じ誤りが構成ごとに報告される |
| 分岐の中に、`#elif` / `#else` / `#endif` 以外の指令がある | 複製すると指令を何度も処理することになる |
| 分岐が 5 つ以上になる（`#else` が無いときの、どの分岐も通らない分を含む） | 同じ文が何本も並ぶ |
| 条件をシンボルの条件として読めない、外側の `#if` を並べていない、分岐の中で構成によって定義が変わるマクロを使っている | 3.6 の表の同じ理由を参照 |

複製する文の中で、条件で中身が変わるマクロ（3.3）を使っていても、そのマクロは複製しません。
1 つの単位で複製するのは 1 回までなので、そのマクロのシンボルは方法 B になります。

### 3.5 `#include` の切り替え

`#include` を切り替えている領域も、両方のヘッダをそれぞれの条件のもとで取り込んで並べます。

ただし、ヘッダが定義したマクロをコードの中で使っていた場合は、その展開はどちらか 1 つの構成のものになります。
その時点でそのシンボルを並べる対象から外し、ブロックをプリプロセスし直します（方法 B）。
シェーダー内の `#ifdef` で定義したマクロは、3.3 の複製の対象になります。

次の例では `VALUE_TYPE` を `a.hlsl` / `b.hlsl` が定義しているため、方法 B になります。

```hlsl
#ifdef _A
#include "a.hlsl"       // #define VALUE_TYPE float3
#else
#include "b.hlsl"       // #define VALUE_TYPE float4
#endif

VALUE_TYPE v = Shade();
```

次の例では、ヘッダは関数だけを定義し、`VALUE_TYPE` は別の `#ifdef` で切り替えています。この形は方法 A で扱います。

```hlsl
#ifdef _A
#include "a.hlsl"
#else
#include "b.hlsl"
#endif

#ifdef _A
#define VALUE_TYPE float3
#else
#define VALUE_TYPE float4
#endif

VALUE_TYPE v = Shade();
```

取り込みとコードで使うマクロの定義が同じ分岐にあると、取り込みの切り替えとしては並べられず、方法 B になります。

### 3.6 並べられない領域

並べられなかった理由は、`--inspect` の「シンボルの扱い」タブに、原因の `#if` の位置とともに出ます。

| `--inspect` に表示される理由 | 該当するコードの形 |
| --- | --- |
| 分岐の中身が文・宣言の単位で閉じていない | 3.4 の複製で扱えない形（仮引数を条件で足す、戻り値の型だけを分ける、`if` の頭だけを分ける） |
| 分岐の中で、コードで使うマクロを定義している | 3.3 の表に示した形 |
| 分岐で取り込むファイルを切り替えている | 取り込みと、コードで使うマクロの定義を同じ分岐に書く |
| `#endif` が無い | `#endif` の書き忘れ（[HL0002](../rules/HL0002.md) も出る） |
| 分岐の中で、構成によって定義が変わるマクロを使っている | `#ifdef _B` の中で、`_A` で中身が変わるマクロを使う |
| 条件式をシンボルの条件として読めない | `#if _A == 1`、`#if defined(_A) + 1 > 1` |
| 外側の `#if` を並べていない | 並べられない `#if` の中に `#if` を書く |
| 並べなかった `#if` に続く `#elif` | 最初の分岐が並べられない `#if … #elif … #endif` |
| 並べた分岐で定義したマクロを、コードで使っている | 取り込んだヘッダが定義したマクロをコードで使う（3.5） |

次の例では、左と右で解析方法が異なります。左は方法 B、右は方法 A です。
文の途中で分かれる形（`float m` / `#ifdef _A` / `= 1` / `#else` / `= 2` / `#endif` / `;`）は、3.4 の複製で方法 A になります。

```hlsl
// 仮引数の一部が条件付き                 // 関数全体が条件付き
float Helper(float a                      #ifdef _A
#ifdef _A                                 float Helper(float a, float b) { return a; }
    , float b                             #else
#endif                                    float Helper(float a) { return a; }
    ) { return a; }                       #endif
```

```hlsl
// _B の中で、_A で変わる V を使う        // V の宣言が条件の外にある
#ifdef _B                                 float v = V;
    return V;                             #ifdef _B
#else                                         return v;
    return 0;                             #else
#endif                                        return 0;
                                          #endif
```

ほかに、指令がマクロの展開から出てきた場合と、既定の構成が並べなかった領域をバリアントで展開した場合にも理由が付きます。
これらも「シンボルの扱い」タブに表示されます。

## 4. 構文解析

パーサーは段階 3 のトークン列だけを受け取り、1 本の木を作ります。**条件のことは何も知りません。**
並べた分岐は、条件を外してそのまま続けて読みます。

方法 A で並べる領域を「各分岐が文・宣言として閉じている」ものに限るのはこのためです。
`float m` / `#ifdef _A` / `= 1` / `#else` / `= 2` / `#endif` / `;` を並べると、パーサーには
`float m = 1 = 2 ;` が届き、文になりません。
文ごと分けていれば `float m = 1 ; float m = 2 ;` になり、同じ名前の宣言が 2 つ並ぶだけで木は壊れません。
2 つが同時に存在しないことは、段階 6 の条件で分かります。
文の途中で分かれる領域は、段階 3 で分岐ごとに文を複製してから渡すので（3.4）、パーサーにはこの形で届きます。

## 5. 構成ごとの展開（方法 B）

段階 3 で並べられなかったシンボルについて、そのシンボルを有効にした構成で 3〜4 をやり直します。

### 5.1 対象になるシンボル

展開の対象になるのは、次をすべて満たすシンボルだけです。

- `#pragma` で宣言されている（宣言が無い分岐は決して通らないので、[HL0330](../rules/HL0330.md) の担当）
- 解析しているファイル自身の `#if` が見ている。
  ただし**取り込んだヘッダに書かれた `#if` も、条件によっては対象**です（5.2）
- マクロ展開の結果として現れた名前ではない
- 方法 A で並べられていない

### 5.2 作る構成

| 構成 | 内容 | `--inspect` の表示 |
| --- | --- | --- |
| シンボル 1 つ | 候補のシンボルを 1 つだけ有効にしたもの | `_A` |
| 同時に定義されていないと通らない組 | `#if defined(_A) && defined(_B)` の中や、`#ifdef _A` の中の `#ifdef _B` のように、1 つずつ有効にしただけでは通らない分岐のための組 | `_A+_B` |
| まとめた構成 | 互いに関係しないシンボル（別の関数で分かれているなど）を 1 回の展開で同時に有効にしたもの。1 つずつ展開したときと結果が変わらないことを確かめて使う | `{_A,_B}` |

**すべての組み合わせは作りません。** 総当たりだと構成の数が 2ⁿ になります。
**条件に書かれた組だけ**を作るので、増えるのは書いた数だけです。
入れ子は、外側の条件と掛け合わせた組になります。外側の分岐を読み飛ばしていても、内側の条件は読みます。

```hlsl
#pragma multi_compile _ _A
#pragma multi_compile _ _B

float F(float x)
{
#if defined(_A) && defined(_B)
    if (Missing() > 0)   // ← _A と _B を同時に有効にした構成で検査される
#else
    if (x > 0)
#endif
    {                    // ← if の頭だけを分けているので、方法 B へ回る (3.4)
        return 1;
    }
    return 0;
}
```

`_A` だけ、`_B` だけの構成ではこの分岐を通らないため、論理積を 1 つの構成として作ります。
`HL0310`（`'Missing' は _A && _B のとき宣言されていません`）が報告されます。

組を作るのは、その中のシンボルがすべて宣言されている場合だけです。
否定の項（`!_A`）は定義しないことで満たせるので、組には入れません。論理和（`||`）は、どれか 1 つの構成で足ります。
同じ `#pragma` 行に並べたシンボル（`#pragma multi_compile _ _X _Y` の `_X` と `_Y`）は同時に有効にならないので、その組は作りません。

`_` の無い `#pragma multi_compile MODE_A MODE_B` はどちらかが必ず有効なので、既定の構成で先頭の `MODE_A` を定義して解析します。
`#ifdef MODE_A ... #else` の `#else` は、`MODE_B` を有効にした構成で検査します。

**取り込んだヘッダに書かれた条件**は、次のものだけを 1 つずつの構成にします。

- そのヘッダ自身が宣言したシンボル（[ヘッダに置いた `#pragma`](#ヘッダに置いた-pragma)）
- このファイルが宣言したシンボルのうち、ヘッダの中で両方の分岐を並べられなかったもの。
  ヘッダの `#ifdef` もまず並べてみて、並べられなかったシンボルだけ構成を作ります

シェーダー自身が宣言したシンボルの、ヘッダに書かれた条件を**すべて**構成の候補にはしません。
URP のシェーダーは 40 を超えるシンボルを宣言し、その条件はほとんどがヘッダ側に書かれています。
すべてを候補にすると 1 ブロックあたり数十件になり、上限で落ちた分が `SL0003` として並びます。

### 5.3 上限と SL0003

バリアントの数には上限があります（既定 8、`--max-symbol-variants`）。

```bash
shaderlyn Assets/Shaders --unity-project . --max-symbol-variants 16
```

- **上限が効くのは方法 B の対象だけです。** 方法 A で並べられたシンボルは、いくつあっても上限を消費しません
- まとめた構成（`{_A,_B}`）は、まとめたシンボルの数にかかわらず 1 つ分です
- 上限に達して展開しなかったシンボルと組は [SL0003](../rules/SL0003.md) として報告されます。「調べていない経路が残っている」という意味で、シェーダーの誤りではありません
- `0` を指定すると方法 B を行いません。方法 A で扱えない `#ifdef` のシンボルはすべて `SL0003` になります。
  バリアントにしか無い宣言は見えなくなるので、「宣言が見つからない」ことを根拠にするルールは、その名前については報告を見送ります
- 解析時間は展開する構成の数に比例して伸びます
- 上限の指定は解析キャッシュの鍵に含まれます。値を変えると、キャッシュは使われずに解析し直されます

上限に達したときは、次の順に残します。落とすのは後ろからです。

1. まとめた構成（1 回で何個ものシンボルを調べるので先に割り当てる）
2. このファイルに書かれた条件が見ているシンボル（1 つずつの構成）
3. 条件に書かれた組（論理積・入れ子）
4. 取り込んだヘッダに書かれた条件から作った構成

まとめに入れないのは、`_` の無い行のシンボル、分岐の中でマクロを定義したり取り込みを切り替えたりするシンボル、
同じ関数・構造体などの中で変わるシンボルどうし、条件の組に一緒に書かれたシンボルどうしです。

落とした組は、組のまま `SL0003` として報告されます。

`SL0003` が出た場合、`--inspect` の「シンボルの扱い」タブで、上限に達したシンボルとその条件を確認できます。上限の変更方法は[CLI の使い方](cli-usage.md#シンボルの展開数)を参照してください。方法 B になる条件は[解析方法が分かれる例](#解析方法が分かれる例)にまとめています。

### 5.4 条件に書かれていない組み合わせ

方法 B で作るのは「1 つずつの構成」と「条件に書かれた組」だけです。
**方法 B の領域どうしが、同時に有効なときだけ壊れるコードは調べません。**

```hlsl
#pragma multi_compile _ _A
#pragma multi_compile _ _B

#ifdef _A
float2 Get(float x)      // 戻り値の型だけを分けているので方法 B (3.4)
#else
float4 Get(float x)
#endif
{
    return x;
}

float F(float x)
{
#ifdef _B
    if (Get(x).z > 0)    // _A と _B が同時に有効なときだけ、float2 に z は無い
#else
    if (x > 0)           // if の頭だけを分けているので方法 B (3.4)
#endif
    {
        return 1;
    }
    return 0;
}
```

`_A` だけの構成でも `_B` だけの構成でも誤りは無く、「`_A` かつ `_B`」はどの条件にも書かれていないため、この組は構成として作られません。
**このことは報告されません。** `_A` も `_B` も展開しており、上限にも達していないためです。

**方法 A で扱う領域どうしの組み合わせは調べます。** 型を求めるときに構成を仮定するためです（段階 7）。
文の途中で分かれる形（3.4）もこれにあたり、`#ifdef _A` / `float2` / `#else` / `float4` / `#endif` / `v = 0;` と
`float r` / `#ifdef _B` / `= v.z` / … のように書いた場合は、`_A && _B` のときの `v.z` が [HL0312](../rules/HL0312.md) として報告されます。
方法 B の領域どうしでも、条件に `#if defined(_A) && defined(_B)` と書かれていたり、
`#ifdef _A` の中に `#ifdef _B` が入れ子になっていたりすれば、その組を構成として作って調べます（5.2）。

## 6. 条件の索引

段階 3 の範囲の表と、段階 5 のバリアントを使って、木のノードごとに「どの条件のもとで存在するか」を求めます。
ルールは [`GetConditionMap()`](../custom-rules/api-reference.md#conditionmap-shaderlynsemanticsconditional) で引きます。

### 6.1 範囲からノードへ

方法 A で並べた範囲は、ソース上の位置に直し、その中に収まるノードに条件を付けます。
入れ子の条件は論理積で重なります。

3.3 で複製した文は、どの複製もソース上の同じ位置にあるため、位置では見分けられません。
複製ごとに別のトークンを持たせてあり、ノードの先頭と末尾のトークンがどの複製のものかで条件を付けます。

### 6.2 バリアントにしか無いコードも検査する

既定の構成の木とバリアントの木を突き合わせ、片方にしか無いノードに条件を付けます。
バリアントの木にしか無いノードは「既定の構成の木のどこへ足すか」と一緒に記録され、
ルールはその足したノードも歩きます。方法 B で拾った分岐の中のコードも検査されます。

```hlsl
#ifdef _A
    if (Missing() > 0)   // ← この行の中も検査される (_A のとき 'Missing' は宣言されていません)
#else
    if (x > 0)
#endif
    {
        return 1;
    }
```

そこで宣言された名前も、既定の構成から見えます。

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

### 6.3 突き合わせに失敗した場合（SL0004）

方法 B で展開しても、木の対応が取れないことがあります。
同じ場所が構成によって**別の種類の文**になる場合です。

```hlsl
#pragma multi_compile _ _MODE_A _MODE_B

#if defined(_MODE_A)
#define BODY { v = 1; }
#elif defined(_MODE_B)   // #elif で書き分けているので、3.3 の複製で補えず方法 B になる
#define BODY v = 2;
#else
#define BODY v = 3;
#endif

if (v > 0)
    BODY
```

`if` の本体が、一方ではブロック、もう一方では式文になります。
この箇所は [SL0004](../rules/SL0004.md) として報告されます。

出現条件を根拠にするルール（[HL0311](../rules/HL0311.md) / [HL0313](../rules/HL0313.md) /
[HL0340](../rules/HL0340.md)）は、誤った指摘を出さないために、この箇所では何も報告しません。

**同じ場所が別の形になるだけなら、報告しません。** 両方を「その構成でだけ立つもの」として残すためです。
次の形は方法 B になりますが、`SL0004` にはならず、どちらの形も検査されます。

```hlsl
#define VALUE half(Missing())   // ← !SHADER_DEBUG のときとして検査される
#ifdef SHADER_DEBUG
#undef VALUE                    // #undef してから定義し直すので、3.3 の複製で補えず方法 B になる
#define VALUE 1
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

## 7. 名前と型

名前の宣言が構成によって変わる場合は、構成を仮定して、その構成で見える宣言だけから型を決めます。
仮定するのは、その式に関わる宣言の条件に出てくるシンボルの組み合わせです。

[解析の流れ](#解析の流れ) の例では、`return d.x;` の `d` は `_A` を仮定すると `float3 d`、`!_A` を仮定すると `float4 d` だけが見えるので、
それぞれ `float3` と `float4` になります。`d.x` はどちらでも `float` です。

方法 A で並べた領域どうしなら、別々の `#ifdef` の組み合わせもこの仮定で調べます（[5.4](#54-条件に書かれていない組み合わせ)）。

## 8. ルールとエディタ

- **条件を添えて報告します。** 1 つの構成でしか起きない誤りも直すべき誤りなので報告し、メッセージに構成を添えます（`'Helper' は _A のとき宣言されていません。`）
- **条件が分からない箇所では報告しません。** `SL0004` の箇所がそれです
- **エディタのホバー**は、構成によって変わる型や、マクロの展開結果を構成ごとに並べます。
  `#define` の行の上では、その行の定義と、それが使われる条件を示します
- **`--inspect`** は、解析の中身を 1 つの HTML に書き出します

`--inspect` で条件付きコンパイルについて見られるものは次のとおりです。

| タブ | 内容 |
| --- | --- |
| HLSL | このファイルに書かれた宣言の木。条件付きのノードに `#if 条件` が付く。複製した文にも、それぞれの条件が付く |
| シンボルの扱い | 宣言したシンボルごとに、方法 A で並べたか、方法 B で展開したか（作った構成）、上限で展開しなかったか、条件に現れないか。方法 B になった理由と、原因の `#if` の位置 |

「シンボルの扱い」タブで理由の行を選ぶと、原因の `#if` がソースで示されます。
[3.6 並べられない領域](#36-並べられない領域)に、理由と該当するコードの形を示しています。

## 解析方法が分かれる例

| コードの形 | 段階 | 方法 | 上限を消費 | 中のコードをルールが検査 |
| --- | --- | --- | --- | --- |
| 文・宣言の単位で閉じている | [3.2](#32-両方の分岐を並べる方法-a) | A | しない | する |
| 複合条件（`&&` / `\|\|`）で閉じている | [3.2](#32-両方の分岐を並べる方法-a) | A | しない | する |
| `#define` を切り替えている（条件でしか使わないマクロ） | [3.2](#32-両方の分岐を並べる方法-a) | A | しない | する |
| `#define` を切り替えている（コードで使うマクロ。3.3 の形） | [3.3](#33-条件で中身が変わるマクロ条件の巻き上げ) | A | しない | する（利用者が書いたマクロなら中身も） |
| `#include` を切り替えている（ヘッダのマクロをコードで使わない） | [3.5](#35-include-の切り替え) | A | しない | する |
| 値として比べる（`#if _A == 1`） | [3.1](#31-条件式を読む) | B | する | する |
| 文の途中・型名だけ・初期化の要素で分かれる | [3.4](#34-文の途中で分かれる-if条件の巻き上げ) | A | しない | する |
| 仮引数・戻り値の型・`if` の頭だけで分かれる（関数や `if` の本体を含む） | [3.4](#34-文の途中で分かれる-if条件の巻き上げ) | B | する | する |
| 条件で中身が変わるマクロを、3.3 で補えない形で使う | [3.3](#33-条件で中身が変わるマクロ条件の巻き上げ) | B | する | する |
| ヘッダが定義したマクロをコードで使う | [3.5](#35-include-の切り替え) | B | する | する |
| 構成で中身が変わるマクロを、別のシンボルの `#ifdef` の中で使う | [3.6](#36-並べられない領域) | B | する | する |
| 突き合わせに失敗した | [6.3](#63-突き合わせに失敗した場合sl0004) | B | する | 条件を根拠にするルールは報告しない（[SL0004](../rules/SL0004.md)） |
| 上限に達した | [5.3](#53-上限と-sl0003) | — | — | 既定の構成にある分だけ（[SL0003](../rules/SL0003.md)） |

表は、現在の解析処理がコードの形をどう扱うかをまとめたものです。個々の例と判定理由は[3.3](#33-条件で中身が変わるマクロ条件の巻き上げ)、[3.4](#34-文の途中で分かれる-if条件の巻き上げ)、[3.6](#36-並べられない領域)に示します。

## 既知の限界

実装上そうなっている点を、意図の有無にかかわらず並べます。どれも実際に解析させて確かめています。

| # | 段階 | 内容 |
| --- | --- | --- |
| 1 | 5 | **方法 B の領域どうしの、条件に書かれていない組み合わせは試さない。** 方法 B の `#ifdef _A` と `#ifdef _B` が同時に有効なときだけ壊れるコードは、どの構成でも検査されず、`SL0003` も出ない（[実例](#54-条件に書かれていない組み合わせ)）。方法 A で並べた領域どうしの組み合わせは型を求めて調べる。論理積と入れ子の条件は組として作る |
| 2 | 6 | **Unity や外部パッケージのヘッダのマクロの本体は報告しない。** このファイルと利用者のヘッダが書いたマクロの本体は、本体の位置に報告する |
| 3 | 5 | **シェーダー自身が宣言したシンボルについて、ヘッダに書かれた条件をすべて構成の候補にはしない。** まず両方の分岐を並べ、並べられなかったシンボルだけ構成を作る（[5.2](#52-作る構成)） |
| 4 | 8 | **「無いこと」を根拠にするルール（SL1001 / SL1004 / URP0001 / HL0301 / HL0310）は、取り込んだヘッダの無効な分岐に現れた名前を、解析されなかった場所にあるものとして扱う。** 解析しているファイル自身の分岐は位置で見分け、どの構成でも解析されなかった場所の名前だけを数える。ヘッダは見分けないので、ヘッダの無効な分岐にある名前と同じ名前の誤りは見逃す |
| 5 | 3 | **`#if X == 2` のように 1 以外と比べる条件は通らない。** Unity は有効なシンボルを 1 として定義するので、実機でも通らない。宣言されたシンボルと定数だけでできた条件なら [HL0332](../rules/HL0332.md) として報告する。環境のマクロや別のマクロを含む条件は報告しない |
| 6 | 5 | **`dynamic_branch` のシンボルは、条件で参照されなければバリアントを作らない。** 実行時分岐 (`if (_A)`) として書かれた場合、その両側は元から解析されている |
| 7 | 5 | **上限に達したときに落ちるシンボルは名前順で決まる。** 重要度は見ない |
| 8 | 8 | **`SL0003` は、同じシンボルを見ている条件のうち最初の 1 か所にしか出ない。** 2 つ目以降の `#ifdef` の中も読まれていないが、そこには指摘が出ない |
| 9 | 5 | **`_` の無い `shader_feature` の行は、「どれも無い」構成もあるものとして扱う。** Unity 2019.4 の説明ではそうなっているが、Unity 6 の説明でははっきりしない。違っていれば、実在しない構成の中を検査して誤りを報告する |
| 10 | 3 | **条件で中身が変わるマクロの複製は、`#ifdef … #else … #endif` の 2 通りの書き分けにしか効かない。** `#elif` や入れ子の `#if` で 3 通り以上に書き分けた場合と、`#undef` してから定義し直す場合は、そのシンボルは方法 B になる。別々の `#ifdef` で同じ名前を定義する形（`#ifdef _A` で 1 つ、`#ifdef _B` でもう 1 つ）は複製する |

1 は報告もされない「調べていないのに報告しない」形で、「見送ったことは報告する」という原則から外れる唯一の項目です。

利用者が書いたヘッダについての残りの限界（ヘッダだけを開いたときの解析など）は
[設計判断「残っている課題」](../internals/design-decisions.md#残っている課題) にあります。

---

## 付録: 実装の詳細

ここから先は開発者向けです。穴の洗い出しのために、上の内容を実装の側から、同じ段階の番号で書き出しています。

コードへのリンクは、ファイルとメンバーの名前で示します。行番号はコードを直すたびにずれるので載せていません。
リンク先のファイルでメンバーの名前を検索してください。

段階ごとの入口は次のとおりです。

| 段階 | 入口 |
| --- | --- |
| 1〜4 | [HlslSyntaxTree.Parse](../../src/Shaderlyn.Hlsl/HlslSyntaxTree.cs)（[HlslPreprocessor.Preprocess](../../src/Shaderlyn.Hlsl/Preprocessing/HlslPreprocessor.cs) → [HlslParser](../../src/Shaderlyn.Hlsl/Parsing/HlslParser.cs)）。字句解析はプリプロセッサがファイルを読むときに行う（[HlslLexer](../../src/Shaderlyn.Hlsl/Parsing/HlslLexer.cs)） |
| 2〜5 の繰り返し | [ShaderCompilationBuilder.Build](../../src/Shaderlyn.Semantics/Programs/ShaderCompilationBuilder.cs) |
| 6 | [ConditionMapBuilder.Build](../../src/Shaderlyn.Semantics/Conditional/ConditionMapBuilder.cs)。ルールが `GetConditionMap()` を初めて呼んだときに作る |
| 7 | [ExpressionTypeBinder](../../src/Shaderlyn.Semantics/Programs/ExpressionTypeBinder.cs) |

### 付録 2: 宣言の読み取り

[ShaderSymbols.cs](../../src/Shaderlyn.Hlsl/Preprocessing/ShaderSymbols.cs) の 1 か所だけが判断します。

| 判断 | コード |
| --- | --- |
| 対象の `#pragma`（前方一致） | [ShaderSymbols.DeclaringPragmaPrefixes](../../src/Shaderlyn.Hlsl/Preprocessing/ShaderSymbols.cs)、[IsDeclaringPragmaName](../../src/Shaderlyn.Hlsl/Preprocessing/ShaderSymbols.cs) |
| 引数（その行の識別子トークンすべて） | [ShaderSymbols.EnumerateDeclared](../../src/Shaderlyn.Hlsl/Preprocessing/ShaderSymbols.cs) |
| 除外する引数（下線だけの名前） | [ShaderSymbols.IsNoSymbol](../../src/Shaderlyn.Hlsl/Preprocessing/ShaderSymbols.cs) |

宣言を集める経路は 2 つあります。

| 経路 | 入力 | 使う場面 | コード |
| --- | --- | --- | --- |
| `CollectDeclared(pragmas)` | プリプロセス後の `PragmaDirective` | HL0330 / HL0331、バリアントの候補選び | [ShaderSymbols.CollectDeclared](../../src/Shaderlyn.Hlsl/Preprocessing/ShaderSymbols.cs) |
| `CollectDeclaredFromTokens(tokens)` | 字句解析しただけのトークン列 | プリプロセスの**前**に「どれがシンボルか」を知る必要があるため | [ShaderSymbols.CollectDeclaredFromTokens](../../src/Shaderlyn.Hlsl/Preprocessing/ShaderSymbols.cs) |

`CollectDeclaredFromTokens` は **解析対象のファイル自身の `#pragma` しか見ません**。取り込んだヘッダの宣言は
この時点では読めていません。
そのため、1 度プリプロセスしたあとに `CollectDeclared(pragmas)` で宣言を集め直し、ヘッダが宣言していた場合は
その分も並べる対象と制約に足して**プリプロセスし直します**
（[ShaderCompilationBuilder.TryAddIncludedSymbols](../../src/Shaderlyn.Semantics/Programs/ShaderCompilationBuilder.cs)）。
プリプロセスし直すのは、足したシンボルの `#ifdef` が実際にある場合だけです。

「使われている」と見なす経路も 2 つです。

| 経路 | 内容 | コード |
| --- | --- | --- |
| `ConditionalIdentifiers` | 条件で参照された名前のトークン | [PreprocessResult.ConditionalIdentifiers](../../src/Shaderlyn.Hlsl/Preprocessing/PreprocessorTypes.cs) |
| `CollectRuntimeReferences(codeTokens)` | コード中に識別子として現れた名前（`dynamic_branch` のため）。`#` で始まる行は飛ばす | [ShaderSymbols.CollectRuntimeReferences](../../src/Shaderlyn.Hlsl/Preprocessing/ShaderSymbols.cs) |

### 付録 3: プリプロセス

#### 条件式の読み取り

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
その構成での値を解いて片方の分岐だけを残します（理由は `UnreadableCondition`）。

参照の記録（`RecordConditionalIdentifier`、[定義](../../src/Shaderlyn.Hlsl/Preprocessing/HlslPreprocessorConditionals.cs)）の呼び出しは 4 か所です。

| 呼び出し元 | 形 |
| --- | --- |
| [ReadSymbolAtom](../../src/Shaderlyn.Hlsl/Preprocessing/HlslPreprocessorDirectives.cs) | `#if` の条件式に単独の識別子として現れた名前（宣言されたシンボルでなくても記録する） |
| [ReadDefinedAtom](../../src/Shaderlyn.Hlsl/Preprocessing/HlslPreprocessorDirectives.cs) | `defined(X)` を条件に変換するとき |
| [EvaluateDefinedLine](../../src/Shaderlyn.Hlsl/Preprocessing/HlslPreprocessorConditionals.cs) | `#ifdef X` / `#ifndef X` |
| [ResolveDefinedOperators](../../src/Shaderlyn.Hlsl/Preprocessing/HlslPreprocessorConditionals.cs) | `defined` を値として解くとき |

#### 方法 A の実装

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
| `NotSelfContained` | 上を満たさない（文の途中、仮引数の並び、`,` で終わるなど）。複製する単位にブロックが入らなければ、分岐ごとに文を複製して並べる（次の節） |
| `DefinesMacros` | 領域の中に `#define` / `#undef` がある。定義した名前が条件の中でしか使われていなければ並べる（[CanMerge](../../src/Shaderlyn.Hlsl/Preprocessing/HlslPreprocessorConditionals.cs)） |
| `SwitchesIncludes` | 領域の中に `#include` がある。領域の中の `#define` については `DefinesMacros` と同じ条件で並べる（`MergeSwitchedIncludes`）。領域の終わりは中身を調べずに求める（[FindRegionEnd](../../src/Shaderlyn.Hlsl/Preprocessing/ConditionalRegionScanner.cs)）。求めずに返していたときは、領域の後ろの `#define` まで中身として読み、並べられるものを並べていなかった |
| `Unterminated` | `#endif` が無い |

補足として、

- 内側の `#if` は同じ規則で先に判定し、`Mergeable` でなければ外側もその理由で返す
- 指令の行そのものは数えない（出力に残らないため）
- 入れ子は「**外側も並べているとき**」にだけ並べる（[ChooseKeptCondition](../../src/Shaderlyn.Hlsl/Preprocessing/HlslPreprocessorConditionals.cs)）
- バリアントは、既定の構成が並べた領域だけを並べる（`PreprocessorOptions.MergeOnlyRegions`）。並べてよいかはマクロの状態にも依存するので、構成ごとに判断が変わると、既定の木との違いが「そのシンボルで変わる部分」だけではなくなる。領域は指令の（ファイル, 位置）で持ち、取り込んだヘッダの領域も含める。取り込みの記録にも残すので、使い回したヘッダの分も欠けない
- バリアントは宣言したキーワード (`DeclaredSymbols`) を既定の構成と同じにして展開する。条件付きで覚えるマクロの条件 (`#if !defined(_B)` の中の `#define WNB`) はこれで書くので、空にするとバリアントだけが `#ifdef WNB` を並べず、関係の無いキーワードの否定が条件に付く (HDRP の Lit.shader の `WRITE_NORMAL_BUFFER`)
- 並べなかった条件が、並べた分岐で定義したマクロを見ていたら、その分岐のシンボルは並べずに展開し直す (`NoteMergedMacroConditionUse`)。並べた `#define` はどの構成でも効くので、その場の定義の有無で分岐を選ぶと、定義の無い構成でも定義のある側を読む
- 並べ直しで並べる対象から外したキーワードは、条件がヘッダにしか無くても構成の候補に入れる。並べる対象でないキーワードの条件は、並べられなかったとも記録されない
- 並べずに展開しているキーワードは、その構成では値が決まっている。条件付きで覚えたマクロの条件にそうしたキーワードが現れたら、その値で置き換えてから並べるかを決める（`SymbolCondition.Assume`）。置き換えずに並べると、そのキーワードのときだけの分岐の `#define` がこの構成に漏れる（HDRP の Lit.shader の `_HEIGHTMAP` → `_CONSERVATIVE_DEPTH_OFFSET` → `SV_POSITION_QUALIFIERS`）。条件の巻き上げも、その構成で通らない定義は複製しない
- 並べた分岐で定義したマクロがコードとして展開されたら、そのシンボルを並べずにブロックを展開し直す（`PreprocessResult.MergedMacroConflicts`、[ParseWithoutMergedMacroConflicts](../../src/Shaderlyn.Semantics/Programs/ShaderCompilationBuilder.cs)）。取り込んだヘッダが使っている場合も、取り込みの記録に残した名前から分かる

並べられなかった領域に現れたシンボルは `DeclinedBothBranchSymbols` に記録され、方法 B の候補になります
（[DeclineBothBranchSymbolsIn](../../src/Shaderlyn.Hlsl/Preprocessing/HlslPreprocessorDirectives.cs)、
[CollectMergedSymbols](../../src/Shaderlyn.Semantics/Programs/ShaderCompilationBuilder.cs)）。
1 つの領域でも並べられなければ、そのシンボルは他の領域で並べられていても候補に入ります。

#### 条件の巻き上げ

[HlslPreprocessorHoisting.cs](../../src/Shaderlyn.Hlsl/Preprocessing/HlslPreprocessorHoisting.cs) が行います。

- 定義は、読み飛ばす分岐のものも条件付きで覚える（[RecordConditionalDefinition](../../src/Shaderlyn.Hlsl/Preprocessing/HlslPreprocessorDefines.cs)）。`#undef` は、読み飛ばす分岐のものも含めて、その名前の記録を捨てる。捨てずにいると、`#undef` してから定義し直した中身を「`#undef` せずに定義し直した」（HL0002）と誤って報告していた
- 複製するのは、条件付きの定義が 2 つ以上あり、中身が違い、条件が分かっていて、分岐の数（定義の無い分岐を含む）が 4 以下のマクロ（[GetHoistableDefinitions](../../src/Shaderlyn.Hlsl/Preprocessing/HlslPreprocessorHoisting.cs)）
- 文に書かれた名前が、条件で中身が変わるマクロでなくても、その本体を 8 段までたどって条件で中身が変わるマクロをちょうど 1 つ参照していれば、そのマクロの定義ごとに複製する（[FindHoistableThroughMacros](../../src/Shaderlyn.Hlsl/Preprocessing/HlslPreprocessorHoisting.cs)）。たどった結果はマクロの定義ごとに覚え、マクロ表か条件付きの定義が変わったら捨てる
- 複製は、どれも同じ位置から作られる。位置で条件を引くと `_A` の複製と `!_A` の複製が重なり、両方「常に」になる。そのため複製ごとにトークンを別のインスタンスにし（`HlslSyntaxToken.Duplicate`）、範囲に印を付ける（`ConditionalTokenRange.IsHoisted`）
- `#define` しか無い領域（`#define` / `#else` / `#endif` だけ、[FindHoistableDefinitions](../../src/Shaderlyn.Hlsl/Preprocessing/HlslPreprocessorHoisting.cs)）を `RegionDefinesMacros` で並べなかった場合、そこで定義したマクロのコードでの展開がすべて巻き上げによるものなら、展開を終えた時点でそのシンボルを並べたものとして数え直す（[RestoreHoistedDeclines](../../src/Shaderlyn.Hlsl/Preprocessing/HlslPreprocessorHoisting.cs)）。その領域で求めた同時に有効にする組も、数え直さなかったときだけ記録する。巻き上げ以外の展開は [NoteUnhoistedExpansion](../../src/Shaderlyn.Hlsl/Preprocessing/HlslPreprocessorHoisting.cs) が数える（複製を諦めた箇所、複製しなかった位置での別のマクロの本体やヘッダの中での展開、同じ文で別のマクロを複製している最中の展開）
- 文の途中で分かれる `#if`（`NotSelfContained`）は、`#if` の手前の文の始まりから、`#endif` の後ろの文の切れ目までを、分岐ごとに複製する（[TryHoistRegion](../../src/Shaderlyn.Hlsl/Preprocessing/HlslPreprocessorRegionHoisting.cs)）。複製する単位に、初期化のもの（`=` `,` `{` の直後）でない `{` があれば複製しない。分岐の中に `#elif` / `#else` / `#endif` 以外の指令があるとき、`#elif` の条件を読めないとき、分岐が 5 つ以上になるときも複製しない。並べるかの判断（`FindDeclineReason`）と同じく、外側の並べていない条件と、構成によって定義が変わるマクロの呼び出しも見る
- 文の切れ目の目印（`NoteUnitBoundary`）は、初期化の波括弧を切れ目にしない。切れ目にすると、波括弧の内側からしか複製できない
- 領域の終わりは、中身を調べ終える前に戻る経路（`#else` / `#elif` で閉じていないと分かった場合、入れ子が並べられない場合、`#include`）でも求める（[ConditionalRegionScanner.Scan](../../src/Shaderlyn.Hlsl/Preprocessing/ConditionalRegionScanner.cs)）。複製は領域の後ろから文の切れ目を探す
- 複製した文の後は、目印を次の文の始まりに置き、括弧の深さを数え直す（`MoveUnitAnchorPastUnit`）

#### 並べなかった理由の記録

[BothBranchDecline](../../src/Shaderlyn.Hlsl/Preprocessing/PreprocessorTypes.cs) に、シンボル・理由（`BothBranchDeclineReason`）・ファイル・指令の位置（`DirectiveSpan`）を残します（[AddDeclinedSymbol](../../src/Shaderlyn.Hlsl/Preprocessing/HlslPreprocessorDefines.cs)）。
挙動には使わず、`--inspect` の「シンボルの扱い」タブと、Unity 同梱シェーダーでの計測（[UnityCorpusTests](../../tests/Shaderlyn.Tests/UnityCorpusTests.cs)）が読みます。

| 理由 | `--inspect` の表示 |
| --- | --- |
| `RegionNotSelfContained` | 分岐の中身が文・宣言の単位で閉じていない |
| `RegionDefinesMacros` | 分岐の中で、コードで使うマクロを定義している (定義ごとの複製で補えなかった) |
| `RegionSwitchesIncludes` | 分岐で取り込むファイルを切り替えている |
| `RegionUnterminated` | #endif が無い |
| `CallsConfigurationDependentMacro` | 分岐の中で、構成によって定義が変わるマクロを使っている |
| `UnreadableCondition` | 条件式をシンボルの条件として読めない (値として解いた) |
| `OuterNotMerged` | 外側の #if を並べていない |
| `FollowsUnmergedBranch` | 並べなかった #if に続く #elif |
| `NotInFile` | 指令がマクロの展開から出てきた |
| `OutsideDefaultMergedRegions` | 既定の構成が並べなかった領域 |
| （`MacroConflictSymbols`） | 並べた分岐で定義したマクロを、コードで使っている (取り込むファイルの切り替えなど) |

表示の対応は [AnalysisInspectorSymbols.cs](../../src/Shaderlyn.Cli/Inspection/AnalysisInspectorSymbols.cs) にあります。

#### 構成を決めるもの

1 つの構成は、次の 4 つで決まります。どれを落としても、実在しない構成を解析することになります。

| 決めるもの | 内容 | 適用する場所 |
| --- | --- | --- |
| 環境 | `SHADER_API_*`、`UNITY_VERSION` など。1 通りだけを仮に選ぶ | [SemanticsOptions.DefaultPredefinedMacros](../../src/Shaderlyn.Semantics/SemanticsOptions.cs)、`--define`（[ValueOptions](../../src/Shaderlyn.Cli/CommandLineOptions.cs)） |
| カーネルのマクロ | `#pragma kernel KMain USE_X` がそのカーネルだけに与えるもの | [ProgramBlockExtractor](../../src/Shaderlyn.Semantics/Programs/ProgramBlockExtractor.cs) の `ExtraMacros`、[WithExtraMacros](../../src/Shaderlyn.Semantics/Programs/ShaderCompilationBuilder.cs) |
| シンボル | その構成で有効にするシェーダーキーワード | [ConfigurationMacros](../../src/Shaderlyn.Semantics/Programs/ShaderCompilationBuilder.cs) |
| 宣言から分かる制約 | 同じ行のシンボルは同時に有効にならない。`_` の無い `multi_compile` はどれか 1 つが必ず有効 | [SymbolConstraints](../../src/Shaderlyn.Core/Syntax/SymbolConstraints.cs) |

既定の構成もバリアントも、この 4 つを同じ順で重ねて作ります（`ConfigurationMacros`）。
どれか 1 つでも別々に扱うと、片方の経路にだけ穴が空きます。実際、カーネルのマクロをバリアントに渡し忘れていた不具合がありました。

### 付録 4: 構文解析

[HlslParser](../../src/Shaderlyn.Hlsl/Parsing/HlslParser.cs) は `PreprocessResult.Tokens` だけを受け取ります。条件の範囲の表は渡しません。
条件の巻き上げは、複製した断片が文として閉じているかを、同じパーサーの [IsCompleteUnits](../../src/Shaderlyn.Hlsl/Parsing/HlslParser.cs) に判断させます。
括弧の数と末尾の記号だけでは `float v = ;` を通してしまうためです。

### 付録 5: 構成ごとの展開

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
- `_` の無い `multi_compile` の行は、既定の構成とどのバリアントも、その行のシンボルを 1 つも有効にしていなければ先頭を定義する（[ConfigurationMacros](../../src/Shaderlyn.Semantics/Programs/ShaderCompilationBuilder.cs)）。先頭だけを有効にした構成は既定の構成と同じなので作らない。先頭が無いことを求める分岐には、同じ行の別のシンボルを有効にした構成を作る（[SymbolConstraints.EnumerateRequiredCombinations](../../src/Shaderlyn.Core/Syntax/SymbolConstraints.cs)）。`shader_feature` は対象にしない（[既知の限界](#既知の限界) 9、[ShaderSymbols.IsRequiredSet](../../src/Shaderlyn.Hlsl/Preprocessing/ShaderSymbols.cs)）

**(c) ヘッダの条件の構成。** 取り込んだヘッダに書かれた条件は、次のものだけを 1 つずつの構成にします。

- そのヘッダ自身が宣言したシンボル（`IncludedDeclaredSymbols`）
- このファイルが宣言したシンボルのうち、ヘッダの中で両方の分岐を並べられなかったもの（`declinedInHeaders`）。
  並べられなかった側はどの木にも載らないので、そのシンボルを有効にした構成を作る

**並び順は (a)、(b)、(c) の順にします。** 上限に達したときは後ろから落とします。
(a) は名前順、(b) は中身を並べた文字列の順で、実行のたびに変わりません。

互いに関係しないキーワードは 1 回の展開にまとめます（`PackIndependentSymbols`）。違いがどのキーワードのものかは、重なる `#if` の連なり (`KeywordRegions`) で決め、決められなければ 1 つずつ展開し直します。まとめた構成は上限を 1 つ分だけ使います。

上限は [SemanticsOptions.DefaultMaxSymbolVariants](../../src/Shaderlyn.Semantics/SemanticsOptions.cs)（既定 8）と
[CommandLineOptions.ReadMaxSymbolVariants](../../src/Shaderlyn.Cli/CommandLineOptions.cs) で決まり、キャッシュの鍵にも入ります（[AnalysisCacheKey.AppendOptions](../../src/Shaderlyn.Cli/Caching/AnalysisCacheKey.cs)）。
超過分は、1 つずつの構成は `UnexploredSymbols`、組の構成は `UnexploredSymbolCombinations` に入ります
（[ShaderCompilation.UnexploredSymbols](../../src/Shaderlyn.Semantics/ShaderCompilation.cs)、[UnexploredSymbolCombinations](../../src/Shaderlyn.Semantics/ShaderCompilation.cs)）。

バリアントは、既定の構成と同じシンボルを並べて展開し、有効にしたシンボル（と、それと同時には有効にならないシンボル）だけを固定します（[Expand](../../src/Shaderlyn.Semantics/Programs/ShaderCompilationBuilder.cs)）。並べずに展開すると、既定の木で並べた別のシンボルの分岐までバリアントから消え、突き合わせがその分岐に無関係なシンボルの否定を付けます。

### 付録 6: 条件の索引

[ConditionalMerge.Merge](../../src/Shaderlyn.Semantics/Conditional/ConditionalMerge.cs) が既定の構成の木とバリアントの木を比べ、
[ConditionMapBuilder.Build](../../src/Shaderlyn.Semantics/Conditional/ConditionMapBuilder.cs) が結果を索引にまとめます。

| 結果 | 内容 | コード |
| --- | --- | --- |
| `ConditionalNodes` | ノードとその出現条件 | [ConditionalNode](../../src/Shaderlyn.Semantics/Conditional/ConditionalMerge.cs) |
| `NodeInsertions` | バリアントの木にしか無いノードと、既定の木での挿入先（親） | [NodeInsertion](../../src/Shaderlyn.Semantics/Conditional/ConditionalMerge.cs) |
| `UnmergedLocations` | 対応が取れなかった箇所。[SL0004](../rules/SL0004.md) として報告 | [ConditionMap.UnmergedLocations](../../src/Shaderlyn.Semantics/Conditional/ConditionMap.cs) |

条件の索引は、バリアントが並べた範囲も集めます (`CollectEmittedRegions`)。バリアントにしか無いノードが、その木で並べた分岐の中にあることがあります。

巻き上げで複製した文については、

- 範囲を位置に直さず、トークンで引く。ノードの先頭と末尾のトークンが同じ複製のものなら、その複製の条件が付く（`ConditionMap.GetHoistedCondition`）
- 突き合わせの結果は、一方の木では複製してあり、もう一方の木では同じ位置に複製していない文があるところでだけ使わない（[FindAmbiguousCopies](../../src/Shaderlyn.Semantics/Conditional/ConditionMapBuilder.cs)）。そこでは 1 つの文がどの複製と対応するかが位置から決まらず、取り違えると `multi_compile _A _B` で `float4 d` に `!_B` が付いていた。それ以外の位置では使う。捨てると、`#ifdef _B` の中で複製した文から `_B` の条件が消え、[HL0314](../rules/HL0314.md) を誤って報告していた

#### 出現条件（`SymbolCondition`）

[SymbolCondition](../../src/Shaderlyn.Core/Syntax/SymbolCondition.cs) はシンボルの真偽の論理積の論理和（積和形）です。
特別な値に `Always`（既定値）、`Never`、`Unknown`（表しきれなかった）があり、`And` / `Or` / `Negate` で組み立てます。
`IEquatable<SymbolCondition>` を実装しているので、辞書のキーにできます。

[ConditionMap.GetCondition](../../src/Shaderlyn.Semantics/Conditional/ConditionMap.cs) は次の順で組み立てます。

1. 突き合わせできなかった領域の中なら `Unknown` を返す（「分からない」を「常に」にしない）
2. バリアントとの突き合わせから出た条件を、**親を辿りながら**論理積で重ねる
3. 方法 A で並べた領域の条件を論理積で重ねる
4. 条件の巻き上げで複製した文の中なら、その複製の条件を論理積で重ねる

`IsAlwaysPresent`（[IsAlwaysPresent](../../src/Shaderlyn.Semantics/Conditional/ConditionMap.cs)）は `GetCondition(node).IsAlways` です。

### 付録 7: 名前と型

構成を仮定するのは [ExpressionTypeBinder.EnumerateAssumptions](../../src/Shaderlyn.Semantics/Programs/ExpressionTypeBinder.cs) で、
仮定ごとの型は [GetEvaluatorFor](../../src/Shaderlyn.Semantics/Programs/ExpressionTypeBinder.cs) が返す評価器で求めます。
同じ名前の**関数**を条件ごとに違う型で返す場合も同じように扱います。引数の個数で絞っても型が割れる名前を先に集めておき（`VaryingFunctions`）、仮定ごとに見える宣言だけへ絞ります。

### 付録 8: ルールとエディタ

#### ルールから見えるもの

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

#### シンボルに関わるルール

| ID | 判定の仕方 | コード |
| --- | --- | --- |
| [HL0330](../rules/HL0330.md) | `ConditionalIdentifiers` のうち、宣言に無く、下線+大文字の命名に合い、マクロでもなく、このファイルに書かれたもの。取り込まれる前提の断片 (`.hlsl` など) では報告しない | [ShaderSymbolAnalyzer.ReportUndeclared](../../src/Shaderlyn.Rules/Hlsl/ShaderSymbolAnalyzer.cs)、[IsMaterialSymbol](../../src/Shaderlyn.Rules/Hlsl/ShaderSymbolAnalyzer.cs) |
| [HL0331](../rules/HL0331.md) | 宣言のうち、条件でも実行時の参照でも現れないもの。宣言がこのファイルにある場合だけ | [ReportUnused](../../src/Shaderlyn.Rules/Hlsl/ShaderSymbolAnalyzer.cs) |
| [HL0332](../rules/HL0332.md) | `#if` / `#elif` の条件のうち、宣言されたシンボルと定数だけでできていて、宣言の制約で成り立つシンボルの組のどれでも偽になるもの。このファイルに書かれた条件だけ。判定はプリプロセッサが行い（[NoteIfNeverTrue](../../src/Shaderlyn.Hlsl/Preprocessing/HlslPreprocessorNeverTrue.cs)）、その分岐のためのバリアントは作らない | [ReportNeverTrue](../../src/Shaderlyn.Rules/Hlsl/ShaderSymbolAnalyzer.cs) |
| [SL0003](../rules/SL0003.md) | `UnexploredSymbols` のシンボルごとに 1 件。位置はそのシンボルを参照する最初の条件（このファイルに書かれたもの）。見つからなければファイルの先頭。`UnexploredSymbolCombinations` の組ごとにも 1 件。位置はその組でしか通らない分岐を始めた指令 | [UnexploredSymbolAnalyzer.cs](../../src/Shaderlyn.Rules/Semantics/UnexploredSymbolAnalyzer.cs) |
| [HL0314](../rules/HL0314.md) | 2 つの宣言の出現条件を掛け合わせて成り立つとき。ただし、その条件を満たすバリアントを展開していれば、その木に両方があるときだけ | [RedeclarationAnalyzer.Collides](../../src/Shaderlyn.Rules/Hlsl/RedeclarationAnalyzer.cs) |
| [SL0004](../rules/SL0004.md) | `UnmergedLocations` の各位置 | [UnresolvedConditionAnalyzer.Analyze](../../src/Shaderlyn.Rules/Semantics/UnresolvedConditionAnalyzer.cs) |
| [HL0352](../rules/HL0352.md) | 代入先のメンバーと書いた要素の出現条件に現れるシンボル（12 個まで）の組を仮定し、その構成にあるものだけを数えて比べる。宣言が無い構成と、その木が表さない構成（`GetTreeConfiguration`）は仮定しない | [ValueConversionAnalyzer.CheckInitializerList](../../src/Shaderlyn.Rules/Hlsl/ValueConversionAnalyzer.cs) |
| HL0311 ほか | 条件が `Unknown` の箇所では報告しない | [CallSignatureAnalyzer.AnalyzeCall](../../src/Shaderlyn.Rules/Hlsl/CallSignatureAnalyzer.cs) |

#### エディタ（VS Code 拡張）

エディタでは、構成を選んで表示できます。

| 項目 | 内容 | コード |
| --- | --- | --- |
| `shaderlyn/conditionSymbols` | 切り替え候補になる名前の一覧を返す。`declaredByPragma` はその名前が `#pragma` で宣言されたシンボルか | [OnConditionSymbolsAsync](../../src/Shaderlyn.LanguageServer/ShaderLanguageServer.cs)、[ConditionSymbol](../../src/Shaderlyn.LanguageServer/ConditionSymbols.cs) |
| `shaderlyn/setDefinedSymbols` | 選んだ名前を定義済みとして解析し直す | [OnSetDefinedSymbolsAsync](../../src/Shaderlyn.LanguageServer/ShaderLanguageServer.cs) |
| `shaderlyn/inactiveRegions` | 効いていない領域の通知 | [PublishInactiveRegionsAsync](../../src/Shaderlyn.LanguageServer/ShaderLanguageServer.cs) |
| ホバー | 同じ位置の複製を構成ごとに並べる（[HoverBuilder](../../src/Shaderlyn.LanguageServer/HoverBuilder.cs)）。マクロの上では呼び出し位置の展開結果を構成ごとに、`#define` の行ではその行の定義と条件を示す（[HoverMacros](../../src/Shaderlyn.LanguageServer/HoverMacros.cs)、`PreprocessResult.WrittenDefinitions`） | |

#### `--inspect`

| タブ | 書き出す処理 |
| --- | --- |
| HLSL | [AnalysisInspector.WritePrograms](../../src/Shaderlyn.Cli/Inspection/AnalysisInspector.cs)。ノードごとに `GetCondition` を引き、親と違えば書く |
| シンボルの扱い | [AnalysisInspector.WriteSymbolStates](../../src/Shaderlyn.Cli/Inspection/AnalysisInspectorSymbols.cs)。扱いは、展開した構成 → 上限で展開しなかった → 既定の構成で有効 → 並べなかったが構成は作っていない → 方法 A → 条件に現れない、の順に決める |

### 限界の経緯

[既知の限界](#既知の限界)の 1 は以前「別々の `#ifdef _A` と `#ifdef _B` が同時に有効なときだけ壊れるコードは検査されない」と、方法 A の領域も含めて書いていました。
確かめ直すと、方法 A で並べた領域どうしは構成を仮定して型を求めるので、`_A && _B` のときの誤りも報告されていました。
例も、`_B` だけの構成で報告される誤り（宣言の無い関数の呼び出し）になっていたので、方法 B の領域どうしの例に差し替えました。
その後、文の途中で分かれる `#if` を分岐ごとに複製するようになり（3.4）、差し替えた例（型名だけを分ける形）も方法 A で扱われて HL0312 が報告されるようになったので、関数の戻り値の型と `if` の頭で分ける形に差し替えました。5.2 の論理積の例と 6.2 の例も、同じ理由で `if` の頭で分ける形にしています。

[突き合わせに失敗した場合](#63-突き合わせに失敗した場合sl0004)の例は、以前は `if (v > 0) BODY` でした。
条件で中身が変わるマクロの複製で扱えるようになり `SL0004` にならなくなったので、別のマクロを通す形 (`#define APPLY BODY`) に差し替えました。
別のマクロを通した形も複製で扱えるようになったので、今は `#elif` で 3 通りに書き分ける形にしています。

[既知の限界](#既知の限界)の 3 は以前「取り込んだヘッダの条件はバリアントにしない」でした。
ヘッダが自分で `#pragma multi_compile` を書いている場合、解析しているファイルにそのシンボルの条件が 1 つも無いため、
そのヘッダの分岐は既定の構成の側しか読まれませんでした。
今はヘッダ自身が宣言したシンボルと、ヘッダの中で並べられなかったシンボルを (c) として構成にします
（[SelectVariantSymbols](../../src/Shaderlyn.Semantics/Programs/ShaderCompilationBuilder.cs)）。

5 は以前「`#if X == 1` の形は片方の構成しか見ない」と書いていましたが、誤りでした。
`#if X == 1` の `X` は参照として記録され（[ReadSymbolAtom](../../src/Shaderlyn.Hlsl/Preprocessing/HlslPreprocessorDirectives.cs)）、
バリアントの対象になります。`== 1` / `!= 0` / `> 0` のいずれも、中のコードが検査されることを確かめました。
誤った結論は `HL0310` で検証したためです。当時このルールは、どこかの構成で読み飛ばされた名前をすべて宣言済みとして扱っており、
指標になりませんでした（今は限界 4 の範囲まで狭めてあります）。

以前は「[HL0352](../rules/HL0352.md) は、代入先の成分と初期化の要素とで付いている条件が食い違うとき、判断を見送る」という限界がありました。
出現条件の組ごとに数を比べていたため、`float4 c = { 1, 2, 3` / `#ifdef _A` / `, 4` / `#endif` / `};` のように条件の付いた分が片側にしか無いと、比べる組を決められませんでした。
今は構成を仮定して、その構成にあるメンバーと要素だけを数えて比べるので、`!_A` のときに足りないことを報告します。

すべての構成を 1 つずつ解析した結果と突き合わせるテスト（次の節）で見つかった次の 3 つは、報告するようにしました。

- 条件付きでしか宣言していない変数を、その条件の外で使う → [HL0315](../rules/HL0315.md)
- 同じ名前を条件ごとに違う型で宣言し、ある構成にしかない成分を取り出す → [HL0312](../rules/HL0312.md)。
  構成ごとに型を求める（[ExpressionTypeBinder.EnumerateAssumptions](../../src/Shaderlyn.Semantics/Programs/ExpressionTypeBinder.cs)）。
  ただし両方の分岐を並べられる場合に限る。並べられない場合は限界 1 のまま
- 別々の条件で同じマクロを違う中身で定義する → [HL0002](../rules/HL0002.md)。
  読み飛ばす分岐の `#define` も条件付きで覚え、`defined()` をその定義がある条件として読む
  （[RecordConditionalDefinition](../../src/Shaderlyn.Hlsl/Preprocessing/HlslPreprocessorDefines.cs)）

### 検証

| テスト | 何と何を比べるか | コード |
| --- | --- | --- |
| 並べても並べなくても同じ | 方法 A を使う既定の解析と、方法 A を使わない解析。近似どうしの比較なので、両方に同じ穴があれば見つからない | [BothBranchConsistencyTests](../../tests/Shaderlyn.Tests/BothBranchConsistencyTests.cs) |
| すべての構成との突き合わせ | 既定の解析と、Unity が作る構成をすべて列挙して 1 つずつ解析した結果の和。後者が正解になる | [ConfigurationOracleTests](../../tests/Shaderlyn.Tests/ConfigurationOracleTests.cs) |
| 書き方ごとの方法 | コードの形ごとに、方法 A で扱うか、どの構成を作るか | [VariantShapeTests](../../tests/Shaderlyn.Tests/VariantShapeTests.cs) |

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
- [解析の流れ](../internals/architecture.md) — 開発者向け。ShaderLab を含めた解析全体の中での位置づけ
- [設計判断「条件付きコンパイル」](../internals/design-decisions.md#条件付きコンパイル) — 判断の理由と採らなかった案
