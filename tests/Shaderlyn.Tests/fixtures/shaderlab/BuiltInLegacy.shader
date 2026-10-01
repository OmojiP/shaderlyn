Shader "Example/BuiltInLegacy" {
	Properties {
		_Color ("Main Color", Color) = (1,1,1,1)
		_MainTex ("Base (RGB)", 2D) = "white" {}
		_Shininess ("Shininess", Range (0.01, 1)) = 0.078125
		_Amount ("Amount", Float) = 1
		_IntValue ("Integer", Int) = 3
		_Vec ("Vector", Vector) = (0,0,0,0)
		_Cube ("Cubemap", Cube) = "" {}
		_Volume ("3D Texture", 3D) = "" {}
		_Array ("Texture Array", 2DArray) = "" {}
	}

	/* 複数行コメント。
	   ここに Category が続く。 */
	Category {
		Tags { "Queue"="Transparent" "IgnoreProjector"="True" "RenderType"="Transparent" }
		Blend SrcAlpha OneMinusSrcAlpha
		Cull Off
		Lighting Off
		ZWrite Off

		SubShader {
			LOD 200

			GrabPass { "_GrabTexture" }

			Pass {
				Name "FORWARD"
				Tags { "LightMode" = "ForwardBase" }

				Stencil {
					Ref 2
					Comp Equal
					Pass Keep
					Fail Keep
					ZFail DecrSat
				}

				Offset -1, -1
				ColorMask RGB
				AlphaToMask On
				BlendOp Add, Max
				Blend One OneMinusSrcAlpha, One One

				CGPROGRAM
				#pragma vertex vert
				#pragma fragment frag
				#pragma multi_compile_fwdbase
				#include "UnityCG.cginc"

				sampler2D _MainTex;
				fixed4 _Color;

				struct v2f {
					float4 pos : SV_POSITION;
					float2 uv : TEXCOORD0;
				};

				v2f vert (appdata_base v) {
					v2f o;
					o.pos = UnityObjectToClipPos(v.vertex);
					o.uv = v.texcoord;
					return o;
				}

				fixed4 frag (v2f i) : SV_Target {
					// ENDCG という語をコメントに含めても、ブロックはここで切れてはならない。
					return tex2D(_MainTex, i.uv) * _Color;
				}
				ENDCG
			}

			UsePass "Legacy Shaders/VertexLit/SHADOWCASTER"
		}
	}

	Fallback "Diffuse"
}
