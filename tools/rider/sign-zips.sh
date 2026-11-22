#!/usr/bin/env bash
# Rider プラグインの zip に、作者の署名を付ける (JetBrains の marketplace-zip-signer)。
#
# 使い方: sign-zips.sh <zip のフォルダー> <証明書 (PEM)> <秘密鍵 (PEM)>
#   秘密鍵のパスフレーズは、環境変数 PRIVATE_KEY_PASSWORD で渡す (コマンドラインに出さない)。
#   フォルダーの中の shaderlyn-rider-*.zip を、同じ名前のまま署名したものに置き換え、証明書で確かめる。
#
# 作者が署名していないプラグインは、インストールのときに IDE が警告を出す。JetBrains Marketplace は、上げた後に自分でも署名する。
# リリース (release.yml) はシークレットの鍵で、CI (ci.yml) はその場で作った使い捨ての鍵で、このスクリプトを動かす。
set -euo pipefail

if [ "$#" -ne 3 ]; then
  echo "使い方: $0 <zip のフォルダー> <証明書> <秘密鍵>" >&2
  exit 2
fi

directory="$1"
certificate="$2"
key="$3"

# 道具のバージョンとチェックサム。上げるときは、両方を書き換える。
signer_version="0.1.45"
signer_sha256="fc48b8a4165907e8164fc2a1256ed0d0fe6f7b25acb33ac48660ab13e7f64bb8"

work="$(mktemp -d)"
trap 'rm -rf "${work}"' EXIT

signer="${work}/marketplace-zip-signer-cli.jar"
curl --fail --silent --show-error --location --output "${signer}" \
  "https://github.com/JetBrains/marketplace-zip-signer/releases/download/${signer_version}/marketplace-zip-signer-cli.jar"
echo "${signer_sha256}  ${signer}" | sha256sum --check --quiet

count=0
for zip in "${directory}"/shaderlyn-rider-*.zip; do
  [ -e "${zip}" ] || continue
  signed="${work}/$(basename "${zip}")"

  java -jar "${signer}" sign -in "${zip}" -out "${signed}" \
    -cert-file "${certificate}" -key-file "${key}" -key-pass "${PRIVATE_KEY_PASSWORD:?PRIVATE_KEY_PASSWORD が必要です}"
  java -jar "${signer}" verify -in "${signed}" -cert "${certificate}"

  mv "${signed}" "${zip}"
  echo "署名した: ${zip}"
  count=$((count + 1))
done

if [ "${count}" -eq 0 ]; then
  echo "署名する zip がありません: ${directory}" >&2
  exit 1
fi
