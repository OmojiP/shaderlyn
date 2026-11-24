# JetBrains Marketplace のページの内容

JetBrains Marketplace の Shaderlyn のページのうち、zip から作られない項目の元の内容です。
リリースのワークフローは zip を送るだけなので、ここを変えた場合は、Marketplace のプラグインのページの「Edit Section」から手で反映してください。

| 項目 | 元の内容 |
| --- | --- |
| Description | zip の中の [plugin.xml](../src/main/resources/META-INF/plugin.xml) の `<description>` から作られる (画面で編集しても、次の zip で上書きされる) |
| Getting Started | [getting-started.html](getting-started.html) (HTML で入力する) |
| Documentation URL | `https://github.com/OmojiP/shaderlyn/blob/main/editors/rider/README.md` |
| Bugtracker | `https://github.com/OmojiP/shaderlyn/issues` |
| Source code URL | `https://github.com/OmojiP/shaderlyn` |
| License | MIT |
| Tags | Code Quality |

Visual Studio Marketplace の概要は [Visual Studio 拡張の README](../../visualstudio/README.md) (英語) で、リリースのたびに自動で反映されます。
