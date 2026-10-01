import * as fs from "fs";
import * as path from "path";
import * as vscode from "vscode";
import {
  LanguageClient,
  LanguageClientOptions,
  ServerOptions,
  TransportKind,
} from "vscode-languageclient/node";

let client: LanguageClient | undefined;

/** 定義済みにしたシンボルを覚えておく workspaceState の鍵の接頭辞。後ろにスクリプトの URI が付く。 */
const DefinedSymbolsKeyPrefix = "shaderlyn.definedSymbols:";

/** サーバが送ってくる、条件が外れて効いていない範囲。行は 0 始まりで、終わりの行は含まない。 */
interface InactiveRegionsParams {
  uri: string;
  regions: { start: { line: number }; end: { line: number } }[];
}

/**
 * 効いていない範囲を、すべての構成を合わせて出すか、選んだ構成で出すか。
 *
 * 選んだ構成では、選ばなかったシンボルを無効として扱う。
 */
type SymbolDisplay = "allConfigurations" | "selectedConfiguration";

/** サーバが返す、選べるシンボルと今の表示の仕方。 */
interface ConditionSymbolsResponse {
  display?: SymbolDisplay;
  symbols: ConditionSymbol[];
}

/**
 * 選べる条件のシンボル 1 つ。
 *
 * `group` は同じ `#pragma` の行で宣言されたものをまとめる番号で、
 * 同じ行のシンボルは**同時には有効にならない**。
 * `requiresOne` が立っている行は、どれか 1 つが**必ず有効**になる
 * (`#pragma multi_compile MODE_A MODE_B` のように `_` を書いていない行)。
 */
interface ConditionSymbol {
  name: string;
  defined: boolean;
  declaredByPragma?: boolean;
  group?: number;
  requiresOne?: boolean;
}

/** workspaceState に覚えておく、ファイルごとの選択。 */
interface SymbolSelection {
  symbols: string[];
  display: SymbolDisplay;
}

export async function activate(context: vscode.ExtensionContext): Promise<void> {
  const server = resolveServerPath(context);

  if (server === undefined) {
    // 起動できないことを表示する
    void vscode.window.showErrorMessage(
      "shaderlyn の言語サーバが見つかりません。" +
        "shaderlyn.serverPath に実行ファイルのパスを設定してください。",
    );
    return;
  }

  const serverOptions: ServerOptions = {
    run: { command: server, transport: TransportKind.stdio },
    debug: { command: server, transport: TransportKind.stdio },
  };

  const clientOptions: LanguageClientOptions = {
    documentSelector: [
      { scheme: "file", language: "shaderlab" },
      { scheme: "file", pattern: "**/*.shader" },
      // HLSL だけを編集することも多い。どの言語として開かれるかは入れている拡張で変わる
      // (組み込みの hlsl、Unity 向け拡張の UnityShader など) ので、言語ではなく拡張子で選ぶ。
      // サーバが HLSL 単体として読む拡張子 (ShaderSourceKinds) と揃えること。
      { scheme: "file", pattern: "**/*.{hlsl,cginc,hlslinc,compute}" },
    ],
    synchronize: {
      // 設定を書き換えたらサーバへ伝える。
      // キーを 1 文字打ち間違えただけで検査が効かないまま運用されるのを避けるため、
      // 設定ファイルの変更は取りこぼさない。
      fileEvents: vscode.workspace.createFileSystemWatcher("**/.shaderlyn.yaml"),
    },
    // 効いていない範囲はこの拡張が表示できる。
    // 表示しないクライアントのために、サーバは申告があったときだけ求める (保存のたびにセマンティックモデルを組むため)。
    initializationOptions: { inactiveRegions: true },
    outputChannelName: "Shaderlyn",
  };

  client = new LanguageClient("shaderlyn", "Shaderlyn", serverOptions, clientOptions);

  const inactive = new InactiveRegionDecorator();
  context.subscriptions.push(inactive);

  // 開始より前に登録する。開いているスクリプトの解析結果は、開始の直後に届く。
  client.onNotification("shaderlyn/inactiveRegions", (params: InactiveRegionsParams) =>
    inactive.update(params),
  );

  await client.start();

  context.subscriptions.push(
    { dispose: () => void client?.stop() },
    vscode.commands.registerCommand("shaderlyn.inspect", showInspector),
    vscode.commands.registerCommand("shaderlyn.selectConditionSymbols", () =>
      selectConditionSymbols(context),
    ),
  );

  await restoreDefinedSymbols(context);
}

/**
 * 条件が外れて効いていない範囲を薄く表示する。
 *
 * `#ifdef` の中が効いているかどうかは、シンボルの定義を追わなければ分からない。
 * 効いていないコードを直しても何も変わらないので、見ただけで区別できるようにする。
 */
class InactiveRegionDecorator implements vscode.Disposable {
  /** URI ごとの、最後に受け取った範囲。エディタを開き直したときに付け直すために持つ。 */
  private readonly regions = new Map<string, vscode.Range[]>();
  private readonly subscriptions: vscode.Disposable[] = [];
  private decoration: vscode.TextEditorDecorationType;

  constructor() {
    this.decoration = InactiveRegionDecorator.createDecoration();

    this.subscriptions.push(
      vscode.window.onDidChangeVisibleTextEditors(editors => editors.forEach(editor => this.apply(editor))),
      vscode.workspace.onDidCloseTextDocument(document => this.regions.delete(document.uri.toString())),
      vscode.workspace.onDidChangeConfiguration(event => {
        if (event.affectsConfiguration("shaderlyn.inactiveRegions")) {
          this.decoration.dispose();
          this.decoration = InactiveRegionDecorator.createDecoration();
          vscode.window.visibleTextEditors.forEach(editor => this.apply(editor));
        }
      }),
    );
  }

  update(params: InactiveRegionsParams): void {
    this.regions.set(
      params.uri,
      params.regions
        .filter(region => region.end.line > region.start.line)
        .map(region => new vscode.Range(region.start.line, 0, region.end.line - 1, Number.MAX_SAFE_INTEGER)),
    );

    vscode.window.visibleTextEditors
      .filter(editor => editor.document.uri.toString() === params.uri)
      .forEach(editor => this.apply(editor));
  }

  dispose(): void {
    this.decoration.dispose();
    this.subscriptions.forEach(subscription => subscription.dispose());
  }

  private apply(editor: vscode.TextEditor): void {
    const configuration = vscode.workspace.getConfiguration("shaderlyn.inactiveRegions");
    const ranges = configuration.get<boolean>("enabled", true)
      ? (this.regions.get(editor.document.uri.toString()) ?? [])
      : [];

    editor.setDecorations(this.decoration, ranges.map(range => editor.document.validateRange(range)));
  }

  private static createDecoration(): vscode.TextEditorDecorationType {
    const opacity = vscode.workspace.getConfiguration("shaderlyn.inactiveRegions").get<number>("opacity", 0.45);

    return vscode.window.createTextEditorDecorationType({
      opacity: String(Math.min(Math.max(opacity, 0.1), 1)),
      isWholeLine: true,
      rangeBehavior: vscode.DecorationRangeBehavior.ClosedClosed,
    });
  }
}

/**
 * 解析の中身をタブで開く。
 *
 * 指摘の理由が分からないとき、原因が構文解析なのかマクロ展開なのか
 * 型判定なのかを切り分ける手段が無いと、再現環境を作るところから始めることになる。
 */
async function showInspector(): Promise<void> {
  const editor = vscode.window.activeTextEditor;

  if (client === undefined || editor === undefined) {
    void vscode.window.showWarningMessage("シェーダーを開いた状態で実行してください。");
    return;
  }

  const response = await client.sendRequest<{ html: string } | null>("shaderlyn/inspect", {
    textDocument: { uri: editor.document.uri.toString() },
  });

  if (response === null) {
    void vscode.window.showWarningMessage("解析の中身を取得できませんでした。");
    return;
  }

  const panel = vscode.window.createWebviewPanel(
    "shaderlyn.inspector",
    "解析の中身: " + editor.document.fileName.split(/[\\/]/).pop(),
    vscode.ViewColumn.Beside,
    // 外部を一切参照しない 1 枚の HTML なので、要るのはスクリプトの実行だけである。
    { enableScripts: true },
  );

  panel.webview.html = response.html;
}

/**
 * 条件のシンボルを選び、その状態で解析し直して表示する。
 *
 * 選べるものは 2 種類ある。
 *
 * - 定義されていないシンボル: HLSL の断片では、`#ifdef _FOO` の `_FOO` を取り込む側の .shader が定義するので、
 *   断片だけを開くとその分岐の中が一度も検査されない。選ぶと `#define 名前 1` と同じ扱いになる。
 * - このファイルが宣言したシンボル: 選ぶと、そのシンボルだけを有効にした構成で効いていない範囲を薄く表示する。
 *   シンボルの分岐はバリアントとして検査済みなので、指摘は変わらない。
 */
async function selectConditionSymbols(context: vscode.ExtensionContext): Promise<void> {
  const editor = vscode.window.activeTextEditor;

  if (client === undefined || editor === undefined) {
    void vscode.window.showWarningMessage("シェーダーを開いた状態で実行してください。");
    return;
  }

  const uri = editor.document.uri.toString();

  const response = await client.sendRequest<ConditionSymbolsResponse | null>(
    "shaderlyn/conditionSymbols",
    { textDocument: { uri } },
  );

  if (response === null) {
    void vscode.window.showWarningMessage("シンボルの一覧を取得できませんでした。");
    return;
  }

  if (response.symbols.length === 0) {
    void vscode.window.showInformationMessage("このファイルの条件には、選べるシンボルがありません。");
    return;
  }

  const declared = response.symbols.filter(symbol => symbol.declaredByPragma === true);
  const others = response.symbols.filter(symbol => symbol.declaredByPragma !== true);

  // シンボルを 1 つも選ばないときに、すべての構成を合わせるか、シンボルをすべて外した構成にするかを決める項目。
  // `multi_compile _ _FOO` の「_FOO を外した構成」を見る手段は、これを外して確定する以外に無い。
  const allConfigurations: vscode.QuickPickItem = {
    label: "$(layers) すべての構成を合わせて表示",
    description: "シンボルを選ばないとき、シンボルで分かれる分岐を薄くしない",
    picked: response.display !== "selectedConfiguration",
  };

  const items: vscode.QuickPickItem[] = [];

  if (declared.length > 0) {
    items.push(
      allConfigurations,
      { label: "シンボル（選ばなかったものは無効として表示）", kind: vscode.QuickPickItemKind.Separator },
      ...declared.map(symbol => ({
        label: symbol.name,
        picked: symbol.defined,
        description: describeGroup(symbol),
      })),
    );
  }

  if (others.length > 0) {
    items.push(
      { label: "定義されていないシンボル（#define して解析）", kind: vscode.QuickPickItemKind.Separator },
      ...others.map(symbol => ({ label: symbol.name, picked: symbol.defined })),
    );
  }

  const picked = await pickSymbols(items, response.symbols, declared.length > 0 ? allConfigurations : undefined);

  // 取り消したときは何も変えない。何も選ばずに確定したときは「すべて外す」である。
  if (picked === undefined) {
    return;
  }

  const symbols = picked.filter(item => item !== allConfigurations).map(item => item.label);
  const declaredNames = new Set(declared.map(symbol => symbol.name));

  // シンボルを選んだなら、その構成で表示する。選んでいなければ「すべての構成」の項目に従う。
  const display: SymbolDisplay =
    declared.length > 0 && (symbols.some(name => declaredNames.has(name)) || !picked.includes(allConfigurations))
      ? "selectedConfiguration"
      : "allConfigurations";

  await client.sendRequest("shaderlyn/setDefinedSymbols", { textDocument: { uri }, symbols, display });
  await context.workspaceState.update(
    DefinedSymbolsKeyPrefix + uri,
    symbols.length > 0 || display === "selectedConfiguration" ? ({ symbols, display } satisfies SymbolSelection) : undefined,
  );
}

/**
 * 同じ `#pragma` の行で宣言されたことを、項目の説明に書く。
 *
 * 同じ行のシンボルは同時に有効にならないので、選ぶと他が外れる。
 * 説明が無いと、選んだ覚えのないものが外れたように見える。
 */
function describeGroup(symbol: ConditionSymbol): string | undefined {
  if (symbol.group === undefined) {
    return undefined;
  }

  return symbol.requiresOne === true
    ? "同じ行のどれか 1 つが必ず有効（外せません）"
    : "同じ行では 1 つだけ有効にできます";
}

/**
 * 実在する構成だけを選べるようにして、シンボルを選ばせる。
 *
 * Unity の `#pragma multi_compile` は、同じ行に並べたシンボルのうち**1 つだけ**が有効な構成を作る。
 * `_` を書いていない行は、どれか 1 つが**必ず**有効になる。
 * 素の複数選択では、この 2 つをどちらも破れてしまい、
 * 実在しない構成を選べた。
 *
 * 解析の側は `_` の無い行に何も選ばれていなければ先頭のシンボルを有効にするので、
 * 「両方外した」つもりの表示と、実際に解析した構成が食い違っていた。
 */
function pickSymbols(
  items: vscode.QuickPickItem[],
  symbols: ConditionSymbol[],
  allConfigurations: vscode.QuickPickItem | undefined,
): Promise<readonly vscode.QuickPickItem[] | undefined> {
  const byName = new Map(symbols.map(symbol => [symbol.name, symbol]));

  const quickPick = vscode.window.createQuickPick();
  quickPick.items = items;
  quickPick.canSelectMany = true;
  quickPick.title = "条件のシンボルを選ぶ";
  quickPick.placeholder = "シンボルを選ぶと、その構成で効いていない範囲を薄く表示します";

  // 区切りは選べないので、最初の選択から外しておく。
  quickPick.selectedItems = items.filter(
    item => item.kind !== vscode.QuickPickItemKind.Separator && item.picked === true,
  );

  // 直前の選択。同じ行で 2 つ目が選ばれたときに、どちらを残すかを決めるのに使う。
  let previous = new Set(quickPick.selectedItems);

  return new Promise(resolve => {
    quickPick.onDidChangeSelection(selection => {
      const repaired = repairSelection(selection, previous, items, byName, allConfigurations);

      if (repaired !== undefined) {
        // 直した結果もこの通知を呼ぶが、そのときは直すところが無く `undefined` が返る。
        previous = new Set(repaired);
        quickPick.selectedItems = repaired;
        return;
      }

      previous = new Set(selection);
    });

    quickPick.onDidAccept(() => {
      resolve(quickPick.selectedItems);
      quickPick.hide();
    });

    quickPick.onDidHide(() => {
      resolve(undefined);
      quickPick.dispose();
    });

    quickPick.show();
  });
}

/**
 * 実在しない構成になっていれば、選択を直したものを返す。
 *
 * 直す必要が無ければ `undefined` を返す。
 *
 * - 同じ行で 2 つ以上選ばれたら、**今選んだもの**を残して他を外す
 * - どれか 1 つが必ず有効な行が空になったら、**直前に選ばれていたもの**へ戻す
 *
 * ただし、宣言されたシンボルを 1 つも選んでいない状態は「すべての構成を合わせて表示」であって、
 * 構成を 1 つに決めていない。この状態では「外せない行」も働かせない。
 */
function repairSelection(
  selection: readonly vscode.QuickPickItem[],
  previous: Set<vscode.QuickPickItem>,
  items: vscode.QuickPickItem[],
  byName: Map<string, ConditionSymbol>,
  allConfigurations: vscode.QuickPickItem | undefined,
): vscode.QuickPickItem[] | undefined {
  const selected = new Set(selection);
  let changed = false;

  const groups = new Map<number, vscode.QuickPickItem[]>();

  for (const item of items) {
    const group = byName.get(item.label)?.group;

    if (group === undefined) {
      continue;
    }

    const members = groups.get(group) ?? [];
    members.push(item);
    groups.set(group, members);
  }

  // 構成を 1 つに決めているか。決めていなければ、行を空のままにしておける。
  //
  // 「すべての構成を合わせて表示」を外したときも構成を 1 つに決めたことになる。
  // ここを見ないと、それを外して行も空にした「シンボルが 1 つも無い構成」を選べてしまう。
  // そんな構成は `_` の無い行には存在せず、解析は先頭のシンボルを補って別の構成を見ることになる。
  const choosingConfiguration =
    (allConfigurations !== undefined && !selected.has(allConfigurations)) ||
    [...groups.values()].some(members => members.some(item => selected.has(item)));

  for (const members of groups.values()) {
    const chosen = members.filter(item => selected.has(item));

    if (chosen.length > 1) {
      // 今選んだものを残す。前から選ばれていたものが外れる。
      const added = chosen.find(item => !previous.has(item)) ?? chosen[chosen.length - 1];

      for (const item of chosen) {
        if (item !== added) {
          selected.delete(item);
          changed = true;
        }
      }

      continue;
    }

    const requiresOne = members.some(item => byName.get(item.label)?.requiresOne === true);

    if (choosingConfiguration && requiresOne && chosen.length === 0) {
      // 外せない行である。直前に選ばれていたものへ戻す。無ければ先頭を選ぶ。
      // 解析もこの行には先頭のシンボルを補うので、補ったものを画面にも出す。
      const restored = members.find(item => previous.has(item)) ?? members[0];
      selected.add(restored);
      changed = true;
    }
  }

  return changed ? items.filter(item => selected.has(item)) : undefined;
}

/**
 * 前に選んだシンボルをサーバへ送り直す。
 *
 * サーバは選択を覚えているが、ウィンドウを再読み込みするとサーバごと起動し直して消える。
 * 選び直させないよう、エディタ側で覚えておいたものを起動のたびに送る。
 */
async function restoreDefinedSymbols(context: vscode.ExtensionContext): Promise<void> {
  if (client === undefined) {
    return;
  }

  for (const key of context.workspaceState.keys()) {
    if (!key.startsWith(DefinedSymbolsKeyPrefix)) {
      continue;
    }

    const saved = context.workspaceState.get<string[] | SymbolSelection>(key);

    // 表示を切り替えられるようになる前は、シンボルの配列だけを覚えていた。
    const selection: SymbolSelection | undefined = Array.isArray(saved)
      ? { symbols: saved, display: "allConfigurations" }
      : saved;

    if (selection !== undefined && (selection.symbols.length > 0 || selection.display === "selectedConfiguration")) {
      await client.sendRequest("shaderlyn/setDefinedSymbols", {
        textDocument: { uri: key.slice(DefinedSymbolsKeyPrefix.length) },
        symbols: selection.symbols,
        display: selection.display,
      });
    }
  }
}

export async function deactivate(): Promise<void> {
  await client?.stop();
  client = undefined;
}

/**
 * 言語サーバの実行ファイルを決める。
 *
 * 設定で明示されていればそれを使い、無ければ拡張に同梱したものを探す。
 * 同梱は OS ごとに別のファイルになる。
 */
function resolveServerPath(context: vscode.ExtensionContext): string | undefined {
  const configured = vscode.workspace
    .getConfiguration("shaderlyn")
    .get<string>("serverPath");

  if (configured !== undefined && configured.length > 0) {
    return fs.existsSync(configured) ? configured : undefined;
  }

  const executable = process.platform === "win32" ? "shaderlyn-lsp.exe" : "shaderlyn-lsp";

  // 配布物は OS ごとに分けて同梱する。
  // 開発中は 1 つしか要らないので、直下に置いたものも見る。
  const candidates = [
    path.join(context.extensionPath, "server", process.platform, executable),
    path.join(context.extensionPath, "server", executable),
  ];

  return candidates.find(candidate => fs.existsSync(candidate));
}
