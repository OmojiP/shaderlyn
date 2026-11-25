#!/usr/bin/env bash
# 言語サーバーを起動し、initialize・shutdown・exit を送って、応答と終了コードを確認する。
#
# 使い方: lsp-smoke.sh <言語サーバーの実行ファイル> [実行ファイルの前に付けるコマンド (dotnet など)]
#   Native AOT で組んだ言語サーバーが、その OS と CPU で起動してプロトコルに応えられることを確かめる (release.yml・ci.yml)。
#   解析そのものは CLI で確かめているので、ここでは起動と応答だけを見る。
set -euo pipefail

if [ "$#" -lt 1 ]; then
  echo "使い方: $0 <言語サーバーの実行ファイル> [前に付けるコマンド]" >&2
  exit 2
fi

server="$1"
shift

# 本文は ASCII だけなので、文字数がそのままバイト数になる。
message() { printf 'Content-Length: %d\r\n\r\n%s' "${#1}" "$1"; }

# macOS のランナーには timeout がないので、ある場合だけ使う。
run_with_timeout() {
  if command -v timeout > /dev/null; then
    timeout 120 "$@"
  else
    "$@"
  fi
}

output="$(mktemp)"
trap 'rm -f "${output}"' EXIT

set +e
{
  message '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"processId":null,"rootUri":null,"capabilities":{}}}'
  message '{"jsonrpc":"2.0","method":"initialized","params":{}}'
  message '{"jsonrpc":"2.0","id":2,"method":"shutdown"}'
  message '{"jsonrpc":"2.0","method":"exit"}'
} | run_with_timeout "$@" "${server}" > "${output}"
code=$?
set -e

if [ "${code}" -ne 0 ]; then
  echo "::error::言語サーバーが終了コード ${code} で終わりました。"
  exit 1
fi

if ! grep -q '"capabilities"' "${output}"; then
  echo "::error::言語サーバーが initialize に応えませんでした。"
  head -c 2000 "${output}"
  exit 1
fi

if ! grep -q '"id":2' "${output}"; then
  echo "::error::言語サーバーが shutdown に応えませんでした。"
  exit 1
fi

echo "言語サーバーは起動し、initialize と shutdown に応えました。"
