// Holographic / reflex gun sight shader for VRChat (Unity Built-in Render Pipeline)
//
// The reticle is drawn as if it floats at a depth behind (or in front of) the plane.
// Looking straight on  -> reticle sits in the centre.
// Tilting / moving your eye off-axis -> reticle slides across the plane and
// eventually leaves it (and is clipped), like a real holo sight.
//
// Works with Unity's default Plane/Quad (needs normals + tangents + UVs 0..1).

Shader "Custom/HolographicSight"
{
    Properties
    {
        _MainTex ("Reticle (RGBA, alpha = shape)", 2D) = "white" {}
        [HDR] _Color ("Reticle Colour (HDR for glow)", Color) = (1, 0.1, 0.1, 1)

        _Depth ("Parallax Depth (UV units, flip sign to reverse direction)", Range(-3, 100)) = 10
        _Scale ("Reticle Size (1 = fills plane)", Range(0.05, 50)) = 5

        [Space]
        [Toggle] _UseLuminance ("Use brightness as alpha (for PNGs with black bg, no alpha)", Float) = 0
        [Enum(UnityEngine.Rendering.BlendMode)] _SrcBlend ("Src Blend", Float) = 5 // SrcAlpha
        [Enum(UnityEngine.Rendering.BlendMode)] _DstBlend ("Dst Blend", Float) = 1 // One (additive glow). Use 10 for normal alpha
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 2           // Back
    }

    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" }

        Pass
        {
            Blend [_SrcBlend] [_DstBlend]
            ZWrite Off
            Cull [_Cull]

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _MainTex_ST;
            fixed4 _Color;
            float _Depth;
            float _Scale;
            float _UseLuminance;

            struct appdata
            {
                float4 vertex  : POSITION;
                float3 normal  : NORMAL;
                float4 tangent : TANGENT;
                float2 uv      : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 pos    : SV_POSITION;
                float2 uv     : TEXCOORD0;
                float3 viewTS : TEXCOORD1; // vector from surface to eye, in tangent space
                UNITY_VERTEX_OUTPUT_STEREO
            };

            v2f vert (appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_OUTPUT(v2f, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv  = v.uv;

                // Object-space vector to the eye (per-eye correct in VR)
                float3 viewOS = ObjSpaceViewDir(v.vertex);

                // Build tangent basis
                float3 n = v.normal;
                float3 t = v.tangent.xyz;
                float3 b = cross(n, t) * v.tangent.w;

                o.viewTS = float3(dot(viewOS, t), dot(viewOS, b), dot(viewOS, n));
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float3 v = normalize(i.viewTS);

                // Parallax: where the eye ray hits an image plane at "_Depth" behind the surface.
                // Stays 0 when looking straight on, grows as you tilt.
                float2 offset = (v.xy / max(v.z, 0.05)) * _Depth;

                // Re-centre, shift, scale the reticle UVs
                float2 uv = (i.uv - 0.5 - offset) / _Scale + 0.5;

                // Anything outside the reticle texture is invisible (so it can slide off)
                float2 inside = step(0.0, uv) * step(uv, 1.0);
                float mask = inside.x * inside.y;

                fixed4 tex = tex2D(_MainTex, uv);
                float a = lerp(tex.a, dot(tex.rgb, float3(0.299, 0.587, 0.114)), _UseLuminance);

                fixed4 col;
                col.rgb = tex.rgb * _Color.rgb;
                col.a   = a * _Color.a * mask;

                // Hide when viewed from behind the plane
                col.a *= step(0.0, v.z);

                return col;
            }
            ENDCG
        }
    }
}
