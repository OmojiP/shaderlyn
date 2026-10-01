namespace Shaderlyn.Cli.Inspection;

/// <summary>
/// ビューアーの画面を組み立てる JavaScript。
/// </summary>
internal static partial class AnalysisInspector
{
    /// <summary>
    /// 画面の描画処理。
    /// </summary>
    /// <remarks>
    /// <b>選択した要素に対応するソースの範囲を必ず示す。</b>
    /// 構文木だけを見せても「このノードがどのコードのことか」を探す手間が残り、
    /// 結局ソースと突き合わせる作業が人に残る。
    /// </remarks>
    private const string Script = """
        const src = DATA.source;
        const lineStarts = (() => {
          const starts = [0];
          for (let i = 0; i < src.length; i++) { if (src[i] === "\n") starts.push(i + 1); }
          return starts;
        })();

        let highlight = null;

        // 画面に出ている木のノードと、その DOM の対応。
        // ソースをクリックしたときに、そこを含む最も内側のノードを引くために持つ。
        let treeNodes = [];

        function esc(s) {
          return s.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;");
        }

        function shorten(text) {
          const flat = (text || "").replace(/\s+/g, " ").trim();
          return flat.length > 60 ? flat.slice(0, 60) + "…" : flat;
        }

        function renderSource() {
          const host = document.getElementById("source");
          const hs = highlight ? highlight.start : -1;
          const he = highlight ? highlight.start + Math.max(highlight.length, 1) : -1;
          const out = [];

          for (let i = 0; i < lineStarts.length; i++) {
            const from = lineStarts[i];
            const to = i + 1 < lineStarts.length ? lineStarts[i + 1] - 1 : src.length;
            const raw = src.slice(from, to).replace(/\r$/, "");
            let body;

            if (highlight && he > from && hs < to + 1) {
              const a = Math.max(hs - from, 0);
              const b = Math.min(he - from, raw.length);
              body = esc(raw.slice(0, a)) + "<mark>" + esc(raw.slice(a, b) || " ") + "</mark>" + esc(raw.slice(b));
            } else {
              body = esc(raw);
            }

            out.push('<div class="ln" id="L' + (i + 1) + '"><span class="no">' + (i + 1) +
                     '</span><span class="tx">' + body + "</span></div>");
          }

          host.innerHTML = out.join("");
        }

        // ソースのクリック位置を、テキスト全体での文字位置に直す。
        // 行の中身は <mark> で分割されていることがあるので、
        // 行の先頭からクリック位置までの文字数を測って足す。
        function offsetFromPoint(event) {
          const line = event.target.closest(".ln");
          if (!line) { return -1; }

          const index = Number(line.id.slice(1)) - 1;
          const base = lineStarts[index];
          const tx = line.querySelector(".tx");
          if (!tx) { return base; }

          let range = null;

          if (document.caretRangeFromPoint) {
            range = document.caretRangeFromPoint(event.clientX, event.clientY);
          } else if (document.caretPositionFromPoint) {
            const pos = document.caretPositionFromPoint(event.clientX, event.clientY);
            if (pos) {
              range = document.createRange();
              range.setStart(pos.offsetNode, pos.offset);
            }
          }

          // 位置を取れない場合は行の先頭を指す。行が分かれば十分に役に立つ。
          if (!range || !tx.contains(range.startContainer)) { return base; }

          const measure = document.createRange();
          measure.setStart(tx, 0);
          measure.setEnd(range.startContainer, range.startOffset);

          return base + measure.toString().length;
        }

        // その位置を含む、最も内側のノードを探す。
        // 同じ位置を複数のノードが覆っているので、範囲が短いものほど利用者が指したものに近い。
        function findInnermost(offset) {
          let best = null;

          for (const entry of treeNodes) {
            const start = entry.node.start;
            const end = start + Math.max(entry.node.length, 1);

            if (offset < start || offset >= end) { continue; }
            if (best === null || entry.node.length < best.node.length) { best = entry; }
          }

          return best;
        }

        // 折り畳まれている親を開く。開かないと選んだノードが画面に出ない。
        function revealTreeNode(head) {
          for (let el = head.parentElement; el; el = el.parentElement) {
            if (el.tagName === "UL" && el.style.display === "none") {
              el.style.display = "";

              const toggle = el.parentElement
                && el.parentElement.querySelector(":scope > .node > .tog");

              if (toggle) { toggle.textContent = "▾"; }
            }
          }
        }

        // ソースをクリックしたら、対応する木のノードを選ぶ。
        function selectFromSource(event) {
          const offset = offsetFromPoint(event);
          if (offset < 0) { return; }

          const found = findInnermost(offset);
          if (!found) { return; }

          highlight = { start: found.node.start, length: found.node.length };
          renderSource();

          document.querySelectorAll(".node.sel, tbody tr.sel").forEach(n => n.classList.remove("sel"));
          found.head.classList.add("sel");

          revealTreeNode(found.head);
          found.head.scrollIntoView({ block: "center" });
        }

        function select(start, length, element) {
          highlight = { start: start, length: length };
          renderSource();
          document.querySelectorAll(".node.sel, tbody tr.sel").forEach(n => n.classList.remove("sel"));
          if (element) { element.classList.add("sel"); }

          const line = document.getElementById("L" + (src.slice(0, start).split("\n").length));
          if (line) { line.scrollIntoView({ block: "center" }); }
        }

        function treeItem(node) {
          const li = document.createElement("li");
          const head = document.createElement("div");
          head.className = "node";

          const toggle = document.createElement("span");
          toggle.className = "tog";
          toggle.textContent = node.children.length ? "▾" : "";

          const kind = document.createElement("span");
          kind.className = "kind";
          kind.textContent = node.kind;

          const pos = document.createElement("span");
          pos.className = "pos";
          pos.textContent = node.line
            + (node.expanded ? " (マクロ展開)" : "")
            + (node.macroBody ? " 本体 " + node.macroBody : "");

          const snip = document.createElement("span");
          snip.className = "snip";
          snip.textContent = shorten(node.text);

          head.append(toggle, kind, pos);

          // #ifdef の条件は木の中に出す。ここに無いと、
          // 条件付きの宣言が無条件の宣言と同じに見えてしまう。
          if (node.condition) {
            const cond = document.createElement("span");
            cond.className = "cond";
            cond.textContent = "#if " + node.condition;
            head.append(cond);
          }

          head.append(snip);
          li.append(head);

          if (node.children.length) {
            const ul = document.createElement("ul");
            ul.className = "tree";
            node.children.forEach(c => ul.append(treeItem(c)));
            li.append(ul);

            toggle.onclick = e => {
              e.stopPropagation();
              const hidden = ul.style.display === "none";
              ul.style.display = hidden ? "" : "none";
              toggle.textContent = hidden ? "▾" : "▸";
            };
          }

          head.onclick = () => select(node.start, node.length, head);

          // ソースから引けるように覚えておく。
          treeNodes.push({ node: node, head: head });

          return li;
        }

        function treeView(nodes, emptyMessage) {
          // 別のタブを描き直すたびに、前の対応は捨てる。
          // 残すと、画面に無いノードを選んでしまう。
          treeNodes = [];

          if (!nodes.length) {
            const p = document.createElement("p");
            p.className = "empty";
            p.textContent = emptyMessage;
            return p;
          }

          const ul = document.createElement("ul");
          ul.className = "tree root";
          nodes.forEach(n => ul.append(treeItem(n)));
          return ul;
        }

        function tableView(columns, rows, emptyMessage) {
          if (!rows.length) {
            const p = document.createElement("p");
            p.className = "empty";
            p.textContent = emptyMessage;
            return p;
          }

          const table = document.createElement("table");
          const head = document.createElement("thead");
          const headRow = document.createElement("tr");
          columns.forEach(c => { const th = document.createElement("th"); th.textContent = c; headRow.append(th); });
          head.append(headRow);

          const body = document.createElement("tbody");
          rows.forEach(row => {
            const tr = document.createElement("tr");
            row.cells.forEach(cell => {
              const td = document.createElement("td");
              if (cell && cell.html) { td.innerHTML = cell.html; } else { td.textContent = cell; }
              tr.append(td);
            });
            tr.onclick = () => {
              select(row.start, row.length, null);
              document.querySelectorAll("tbody tr.sel").forEach(n => n.classList.remove("sel"));
              tr.classList.add("sel");
            };
            body.append(tr);
          });

          table.append(head, body);
          return table;
        }

        function note(text) {
          const p = document.createElement("p");
          p.className = "note";
          p.textContent = text;
          return p;
        }
        """;
}
