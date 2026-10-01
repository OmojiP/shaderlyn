// HeaderDeclaredSymbol.shader が取り込む。宣言をヘッダ側に置いた形。
// シェーダーには #pragma が 1 つも無いので、ヘッダの宣言を拾わなければ構成は 1 つしか無い。
#pragma multi_compile _ _HEADER_QUALITY

// 誤りがあるのは _HEADER_QUALITY を有効にした側である。
// 既定の構成 (どちらも無効) は正しいので、この分岐を読まなければ何も見つからない。
#ifdef _HEADER_QUALITY
float2 Shade(float3 n) { return n.xy; }
#else
float3 Shade(float3 n) { return n; }
#endif
