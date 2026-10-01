// HeaderExclusiveType.shader が取り込む。
//
// _ が無いので、既定の構成でも _HIGH_PRECISION が有効になる。
// ヘッダの宣言を拾わなければ、どちらも無効という実在しない構成を解析することになる。
//
// 分岐ごとに同じマクロを違う中身で定義するので、両方の分岐を 1 本の木には並べられない。
// 構成として作らなければ、_LOW_PRECISION の側は一度も読まれない。
#pragma multi_compile _HIGH_PRECISION _LOW_PRECISION

#ifdef _HIGH_PRECISION
#define PRECISE_VECTOR float3
#else
#define PRECISE_VECTOR float2
#endif
