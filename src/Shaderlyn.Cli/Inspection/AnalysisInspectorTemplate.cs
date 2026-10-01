namespace Shaderlyn.Cli.Inspection;

/// <summary>
/// ビューアーの HTML の雛形。
/// </summary>
/// <remarks>
/// 別ファイルに分けているのは、C# の処理と HTML/CSS/JavaScript が
/// 1 つのファイルに混ざると、どちらも読みにくくなるためである。
/// </remarks>
internal static partial class AnalysisInspector
{
    /// <summary>
    /// 雛形。<c>/*DATA*/</c> を解析結果の JSON に置き換えて使う。
    /// </summary>
    private const string HtmlTemplate = """
        <!doctype html>
        <html lang="ja">
        <head>
        <meta charset="utf-8">
        <title>シェーダー解析ビューアー</title>
        <style>
        :root {
          color-scheme: light dark;
          --bg: #ffffff; --fg: #1c1c1e; --muted: #6b6b70; --line: #e2e2e6;
          --panel: #f7f7f9; --accent: #2f6feb; --mark: #ffe58a; --markfg: #1c1c1e;
          --error: #c0392b; --warning: #b7791f; --info: #2f6feb;
        }
        @media (prefers-color-scheme: dark) {
          :root {
            --bg: #16171a; --fg: #e6e6ea; --muted: #9a9aa2; --line: #2c2e33;
            --panel: #1d1f24; --accent: #6ea8ff; --mark: #6b5a10; --markfg: #ffffff;
            --error: #ff7b6b; --warning: #ffc668; --info: #6ea8ff;
          }
        }
        * { box-sizing: border-box; }
        body {
          margin: 0; background: var(--bg); color: var(--fg);
          font: 13px/1.6 "Segoe UI", "Yu Gothic UI", system-ui, sans-serif;
        }
        header {
          padding: 10px 16px; border-bottom: 1px solid var(--line); background: var(--panel);
          display: flex; gap: 16px; align-items: baseline; flex-wrap: wrap;
        }
        header h1 { font-size: 14px; margin: 0; font-weight: 600; }
        header .meta { color: var(--muted); font-size: 12px; }
        main { display: flex; height: calc(100vh - 46px); }
        #source { width: 50%; overflow: auto; border-right: 1px solid var(--line); }
        #side { width: 50%; display: flex; flex-direction: column; overflow: hidden; }
        .code { font-family: Consolas, "Cascadia Mono", monospace; font-size: 12.5px; }
        .ln { display: flex; white-space: pre; }
        .ln:hover { background: var(--panel); }
        .ln .no {
          flex: 0 0 4.5em; text-align: right; padding-right: 12px;
          color: var(--muted); user-select: none; border-right: 1px solid var(--line);
        }
        .ln .tx { padding-left: 12px; }
        mark { background: var(--mark); color: var(--markfg); border-radius: 2px; }
        nav { display: flex; gap: 4px; padding: 8px 12px 0; border-bottom: 1px solid var(--line); flex-wrap: wrap; }
        nav button {
          font: inherit; background: none; border: 1px solid transparent; border-bottom: none;
          color: var(--muted); padding: 6px 10px; cursor: pointer; border-radius: 6px 6px 0 0;
        }
        nav button[aria-selected="true"] {
          color: var(--fg); background: var(--panel); border-color: var(--line);
        }
        #panel { flex: 1; overflow: auto; padding: 12px 16px; }
        ul.tree { list-style: none; margin: 0; padding-left: 14px; }
        ul.tree.root { padding-left: 0; }
        .node { display: flex; align-items: baseline; gap: 6px; cursor: pointer; padding: 1px 4px; border-radius: 4px; }
        .node:hover { background: var(--panel); }
        .node.sel { background: var(--panel); outline: 1px solid var(--accent); }
        .tog { width: 1em; color: var(--muted); user-select: none; }
        .kind { font-weight: 600; }
        .pos { color: var(--muted); font-size: 11px; }
        .snip { color: var(--muted); font-family: Consolas, monospace; font-size: 11px; }

        /* #ifdef の条件。無条件のノードに埋もれないよう、色で区別する。 */
        .cond {
          color: #b58900; border: 1px solid #b5890055; border-radius: 3px;
          padding: 0 4px; font-family: Consolas, monospace; font-size: 11px;
        }
        table { border-collapse: collapse; width: 100%; font-size: 12px; }
        th, td { text-align: left; padding: 4px 8px; border-bottom: 1px solid var(--line); vertical-align: top; }
        th { color: var(--muted); font-weight: 600; position: sticky; top: -12px; background: var(--bg); }
        tbody tr { cursor: pointer; }
        tbody tr:hover { background: var(--panel); }
        .sev-error { color: var(--error); font-weight: 600; }
        .sev-warning { color: var(--warning); font-weight: 600; }
        .sev-info { color: var(--info); font-weight: 600; }
        .note { color: var(--muted); margin: 0 0 10px; }
        .empty { color: var(--muted); padding: 8px 0; }
        code { font-family: Consolas, monospace; }
        </style>
        </head>
        <body>
        <header>
          <h1 id="title"></h1>
          <span class="meta" id="meta"></span>
        </header>
        <main>
          <div id="source" class="code"></div>
          <div id="side">
            <nav id="tabs"></nav>
            <div id="panel"></div>
          </div>
        </main>
        <script>
        const DATA = /*DATA*/;
        /*SCRIPT*/
        </script>
        </body>
        </html>
        """;
}
