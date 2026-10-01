// 字句・構文解析の境界条件を集めた fixture。
// 見た目が不自然でも、いずれも Unity が受け付ける記述である。

   Shader   "Example/Edge Cases/With Spaces"   {   // 行末コメント
    Properties {
        // 属性を複数並べる
        [NoScaleOffset][Normal] _Bump ("バンプ", 2D) = "bump" {}

        // 属性に引数を伴う
        [KeywordEnum(None, Add, Multiply)] _Blend ("Blend Mode", Float) = 0
        [Enum(Off, 0, On, 1)] _ZWrite ("ZWrite", Float) = 1

        // 指数表記と符号付き
        _Tiny ("Tiny", Float) = 1e-5
        _Negative ("Negative", Range(-1, 1)) = -0.5

        /* 宣言の途中に複数行コメント */ _Mid /* ここにも */ ("Mid", Float) = 0

        // 小文字のキーワード。ShaderLab は大文字小文字を区別しない。
        _Lower ("Lower", range(0, 1)) = 0
    }

    subshader {
        tags { "RenderType" = "Opaque" }

        // レンダーステートの値をプロパティから取る
        Cull [_Cull]
        ZWrite [_ZWrite]
        Blend [_SrcBlend] [_DstBlend]

        // カンマで行をまたぐ継続
        Blend One Zero,
              One One

        pass {
            name "LOWERCASE"

            CGINCLUDE
            // CGINCLUDE の終端も ENDCG である
            float4 _Unused;
            ENDCG

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            ENDHLSL
        }
    }

    Fallback Off
}
