// 構文が壊れた入力。エラー回復とラウンドトリップの検証用。
// このファイルは意図的に不正であり、修正してはならない。
Shader "Example/Broken"
{
    Properties
    {
        _Good ("正しい宣言", Float) = 1

        これは まったく プロパティではない

        _AfterGarbage ("ゴミの後でも解析が再開されること", Float) = 2

        _MissingParen ("閉じ括弧が無い", Float = 3

        _NoDefault ("既定値が無い", Float) =

        _Recovered ("ここも解析されること", Color) = (1, 0, 0, 1)
    }

    SubShader
    {
        Tags { "RenderType" = }

        Cull

        Pass
        {
            Name "壊れた Pass"
            @ # $ %
        }
    }
