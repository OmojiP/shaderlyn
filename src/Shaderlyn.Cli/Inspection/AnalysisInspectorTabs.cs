namespace Shaderlyn.Cli.Inspection;

/// <summary>
/// ビューアーのタブを組み立てる JavaScript。
/// </summary>
internal static partial class AnalysisInspector
{
    /// <summary>タブの定義と初期化。</summary>
    private const string TabsScript = """
        const tabs = [];

        tabs.push({
          label: "ShaderLab 構文木",
          build: () => treeView([DATA.shaderLab], "構文木がありません。"),
        });

        DATA.programs.forEach((program, index) => {
          tabs.push({
            label: "HLSL " + (index + 1),
            build: () => {
              const box = document.createElement("div");
              box.append(note(program.label + " — このファイルに書かれた宣言のみを表示しています。" +
                              "include したヘッダの中身は含みません。"));
              box.append(note("「マクロ展開」と付いたノードは、展開の結果として現れたものです。" +
                              "位置は展開元のマクロを呼び出した場所を指すため、同じ行が重なって見えます。" +
                              "「本体」が付いているものは、このファイル自身が書いたマクロから来たもので、" +
                              "その行が報告の位置になります。"));
              if (program.hoistedUnits) {
                box.append(note("条件で中身が変わるマクロを使う文を " + program.hoistedUnits +
                                " か所で定義ごとに複製し、それぞれに条件を付けています。" +
                                "同じ位置に複数の形が並ぶのはこのためです。" +
                                (program.hoistGiveUps ? "文にならず複製を諦めた箇所が " +
                                 program.hoistGiveUps + " か所あります。" : "")));
              }
              if (program.truncated) {
                box.append(note("ノード数の上限に達したため、途中で打ち切っています。"));
              }
              box.append(treeView(program.declarations, "このブロックに宣言はありません。"));
              return box;
            },
          });
        });

        tabs.push({
          label: "uniform",
          build: () => {
            const rows = [];
            DATA.programs.forEach(program => {
              program.uniforms.forEach(u => rows.push({
                start: u.start,
                length: u.length,
                cells: [u.name, u.type + (u.isArray ? "[]" : ""), u.buffer || "—", program.label],
              }));
            });
            const box = document.createElement("div");
            box.append(note("このファイルに書かれた宣言のみを表示しています。"));
            box.append(tableView(["名前", "型", "定数バッファ", "ブロック"], rows, "uniform はありません。"));
            return box;
          },
        });

        tabs.push({
          label: "式の型",
          build: () => {
            const box = document.createElement("div");
            box.append(note("ルールが「この式は何型か」をどう判定したかです。" +
                            "型が空欄の式は判定できておらず、型を根拠にするルールは何も報告しません。"));

            const known = DATA.expressions.filter(e => e.type).length;
            box.append(note("判定できた式: " + known + " / " + DATA.expressions.length));

            box.append(tableView(
              ["式", "型", "構文", "位置"],
              DATA.expressions.map(e => ({
                start: e.start,
                length: e.length,
                cells: [
                  { html: "<code>" + esc(e.text) + "</code>" },
                  e.type ? { html: "<b>" + esc(e.type) + "</b>" } : "—",
                  e.kind + (e.expanded ? " (マクロ展開)" : ""),
                  e.line,
                ],
              })),
              "式がありません。"));

            return box;
          },
        });

        tabs.push({
          label: "マクロ",
          build: () => {
            const box = document.createElement("div");
            box.append(note("展開の時点で定義されていたマクロです。" +
                            "呼び出しをトークン列から探すか構文木から探すかは、" +
                            "名前が関数形式マクロとして定義されているかで決まります。"));
            box.append(tableView(
              ["名前", "形式", "本体"],
              DATA.macros.map(m => ({
                start: 0,
                length: 0,
                cells: [
                  m.name,
                  m.isFunctionLike ? "関数形式 (" + m.parameters + ")" : "オブジェクト形式",
                  { html: "<code>" + esc(m.body.length > 120 ? m.body.slice(0, 120) + "…" : m.body) + "</code>" },
                ],
              })),
              "マクロはありません。"));
            return box;
          },
        });

        tabs.push({
          label: "Properties",
          build: () => tableView(
            ["名前", "種類"],
            DATA.properties.map(p => ({ start: p.start, length: p.length, cells: [p.name, p.kind] })),
            "Properties はありません。"),
        });

        tabs.push({
          label: "トークン",
          build: () => {
            const box = document.createElement("div");
            box.append(note("マクロ展開前のトークンです。" +
                            "「この識別子は使わない」という形のルールはこれを見て判断します。"));
            box.append(tableView(
              ["種別", "文字列", "位置"],
              DATA.tokens.map(t => ({
                start: t.start,
                length: t.length,
                cells: [t.kind, { html: "<code>" + esc(t.text) + "</code>" }, t.line],
              })),
              "トークンがありません。"));
            return box;
          },
        });

        tabs.push({
          label: "include",
          build: () => {
            const box = document.createElement("div");
            box.append(note(DATA.hasCompleteDependencies
              ? "依存関係はすべて解決できています。"
              : "解決できていない依存関係があります。この状態では一部の検査を見送ります (SL0002)。"));

            const rows = DATA.resolvedIncludes.map(path => ({
              start: 0,
              length: 0,
              cells: [DATA.unresolvedIncludes.includes(path) ? "未解決" : "解決", path],
            }));

            box.append(tableView(["状態", "パス"], rows, "include はありません。"));
            return box;
          },
        });

        tabs.push({
          label: "診断 (" + DATA.diagnostics.length + ")",
          build: () => tableView(
            ["ID", "重要度", "位置", "メッセージ"],
            DATA.diagnostics.map(d => ({
              start: d.start,
              length: d.length,
              cells: [
                d.id,
                { html: '<span class="sev-' + d.severity + '">' + d.severity + "</span>" },
                d.line,
                d.message,
              ],
            })),
            "指摘はありません。"),
        });

        function showTab(index) {
          const bar = document.getElementById("tabs");
          [...bar.children].forEach((b, i) => b.setAttribute("aria-selected", String(i === index)));
          const panel = document.getElementById("panel");
          panel.replaceChildren(tabs[index].build());
          panel.scrollTop = 0;
        }

        (function init() {
          document.getElementById("title").textContent =
            DATA.shaderName || DATA.filePath.split(/[\/]/).pop();
          document.getElementById("meta").textContent =
            DATA.filePath + " / " + DATA.profile + " / 埋め込みブロック " + DATA.programs.length + " 件";

          const bar = document.getElementById("tabs");
          tabs.forEach((tab, index) => {
            const button = document.createElement("button");
            button.textContent = tab.label;
            button.onclick = () => showTab(index);
            bar.append(button);
          });

          renderSource();

          // ソースをクリックしたら、対応する木のノードを選ぶ。
          // 木からソースへは前から辿れたが、逆が無いと
          // 「この行は木のどこか」を人が目で探すことになる。
          document.getElementById("source").addEventListener("click", selectFromSource);

          showTab(0);
        })();
        """;
}
