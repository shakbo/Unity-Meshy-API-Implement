Shader "Hidden/WallCutoutMask" {
  Properties {
    _Color ("Color", Color) = (1,1,1,1)
    _CutoutCenter ("Cutout Center", Vector) = (0,0,0,0)
    _CutoutSize ("Cutout Size", Vector) = (1,1,1,0)
    _MainTex ("Texture", 2D) = "white" {}
  }
  SubShader {
    Tags { "RenderType"="Transparent" }
    LOD 200
    Cull Back
    ZWrite On
    Blend SrcAlpha OneMinusSrcAlpha

    Pass {
      CGPROGRAM
      #pragma vertex vert
      #pragma fragment frag
      #include "UnityCG.cginc"

      struct appdata {
        float4 vertex : POSITION;
        float2 uv : TEXCOORD0;
      };

      struct v2f {
        float4 pos : SV_POSITION;
        float2 uv : TEXCOORD0;
        float3 worldPos : TEXCOORD1;
      };

      sampler2D _MainTex;
      float4 _MainTex_ST;
      fixed4 _Color;
      float3 _CutoutCenter;
      float3 _CutoutSize;

      v2f vert(appdata v)
      {
        v2f o;
        o.pos = UnityObjectToClipPos(v.vertex);
        o.uv = TRANSFORM_TEX(v.uv, _MainTex);
        o.worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
        return o;
      }

      fixed4 frag(v2f i) : SV_Target
      {
        // Transform worldPos back into object space using unity_WorldToObject
        float4 objPos4 = mul(unity_WorldToObject, float4(i.worldPos,1.0));
        float3 objPos = objPos4.xyz;

        // Check if inside cutout bounds (object space aligned)
        bool insideX = abs(objPos.x - _CutoutCenter.x) <= (_CutoutSize.x * 0.5);
        bool insideY = abs(objPos.y - _CutoutCenter.y) <= (_CutoutSize.y * 0.5);
        bool insideZ = abs(objPos.z - _CutoutCenter.z) <= (_CutoutSize.z * 0.5);

        if (insideX && insideY && insideZ)
        {
          discard;
        }

        fixed4 col = tex2D(_MainTex, i.uv) * _Color;
        return col;
      }
      ENDCG
    }
  }
}
