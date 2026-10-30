// Invisibility.shader - GPU compositing for the invisibility AR system.
// Blends the live camera with the stored background per person, with three
// effects per person: 0 = Shimmer, 1 = Neon glow (noise dissolve), 2 = Hard cut.
// Inputs (set by InvisibilityReceiver.cs):
//   _CamTex   live camera frame
//   _BgTex    clean background
//   _MaskTex  RGBA = soft person masks for slots 0..3
//   _Prog     vanish progress per slot (0 visible .. 1 gone)
//   _Eff      effect index per slot
Shader "Custom/InvisibilityAR"
{
    Properties
    {
        _CamTex ("Camera", 2D) = "black" {}
        _BgTex ("Background", 2D) = "black" {}
        _MaskTex ("Person masks (RGBA = slots 0-3)", 2D) = "black" {}
        _Prog ("Vanish progress per slot", Vector) = (0,0,0,0)
        _Eff ("Effect per slot", Vector) = (0,0,0,0)
        _GlowColor ("Glow colour", Color) = (0.16, 0.82, 1, 1)
        _EdgeRadius ("Edge radius (uv)", Float) = 0.012
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        Cull Off
        ZWrite Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _CamTex;
            sampler2D _BgTex;
            sampler2D _MaskTex;
            float4 _Prog;
            float4 _Eff;
            float4 _GlowColor;
            float _EdgeRadius;

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            float comp (float4 v, int i)
            {
                return i == 0 ? v.x : (i == 1 ? v.y : (i == 2 ? v.z : v.w));
            }

            float hash21 (float2 p)
            {
                return frac(sin(dot(p, float2(127.1, 311.7))) * 43758.5453);
            }

            float vnoise (float2 p)
            {
                float2 i = floor(p);
                float2 f = frac(p);
                f = f * f * (3.0 - 2.0 * f);
                float a = hash21(i);
                float b = hash21(i + float2(1, 0));
                float c = hash21(i + float2(0, 1));
                float d = hash21(i + float2(1, 1));
                return lerp(lerp(a, b, f.x), lerp(c, d, f.x), f.y);
            }

            float fbm (float2 p)
            {
                return vnoise(p) * 0.6 + vnoise(p * 2.1) * 0.3 + vnoise(p * 4.3) * 0.1;
            }

            // Edge strength around each slot's mask (one value per channel).
            float4 edgeBand (float2 uv)
            {
                float2 r = float2(_EdgeRadius * 0.75, _EdgeRadius);
                float4 c = tex2D(_MaskTex, uv);
                float4 mx = c;
                float4 mn = c;
                float4 s;
                s = tex2D(_MaskTex, uv + float2( r.x, 0)); mx = max(mx, s); mn = min(mn, s);
                s = tex2D(_MaskTex, uv + float2(-r.x, 0)); mx = max(mx, s); mn = min(mn, s);
                s = tex2D(_MaskTex, uv + float2(0,  r.y)); mx = max(mx, s); mn = min(mn, s);
                s = tex2D(_MaskTex, uv + float2(0, -r.y)); mx = max(mx, s); mn = min(mn, s);
                s = tex2D(_MaskTex, uv + float2( r.x,  r.y)); mx = max(mx, s); mn = min(mn, s);
                s = tex2D(_MaskTex, uv + float2(-r.x,  r.y)); mx = max(mx, s); mn = min(mn, s);
                s = tex2D(_MaskTex, uv + float2( r.x, -r.y)); mx = max(mx, s); mn = min(mn, s);
                s = tex2D(_MaskTex, uv + float2(-r.x, -r.y)); mx = max(mx, s); mn = min(mn, s);
                return saturate((mx - mn) * 1.6);
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float2 uv = i.uv;
                float t = _Time.y;
                float4 band = edgeBand(uv);

                // Ripple: displace the picture near each person's outline.
                float2 rv = float2(
                    vnoise(uv * 22.0 + float2(t * 1.3, t * 0.7)),
                    vnoise(uv * 22.0 + 17.3 + float2(-t * 0.9, t * 1.1))) - 0.5;
                float2 duv = float2(0, 0);
                [unroll]
                for (int k = 0; k < 4; k++)
                {
                    float p = comp(_Prog, k);
                    int e = (int)(comp(_Eff, k) + 0.5);
                    float ts = 4.0 * p * (1.0 - p);
                    float amp = 0.0;
                    if (e == 0) amp = 0.003 + 0.003 * saturate(p * 4.0) + 0.022 * ts;
                    else if (e == 1) amp = 0.0015 + 0.006 * ts;
                    duv += rv * comp(band, k) * amp * 2.0;
                }

                float2 uvd = uv + duv;
                float3 col = tex2D(_CamTex, uvd).rgb;
                float3 bg = tex2D(_BgTex, uvd).rgb;
                float4 mk = tex2D(_MaskTex, uvd);

                // Static noise field that the dissolve eats through.
                float n = saturate((fbm(uv * 7.0) - 0.2) / 0.6);
                float glow = 0.0;

                [unroll]
                for (int j = 0; j < 4; j++)
                {
                    float p = comp(_Prog, j);
                    int e = (int)(comp(_Eff, j) + 0.5);
                    float m = comp(mk, j);
                    float b = comp(band, j);
                    float a = p;
                    if (e == 1)
                    {
                        float w = 0.1;
                        float tt = p * (1.0 + 2.0 * w) - w;
                        a = smoothstep(n - w, n + w, tt);
                    }
                    else if (e == 2)
                    {
                        a = step(0.02, p);
                        m = step(0.5, m);
                    }

                    col = lerp(col, bg, a * m);

                    if (e == 1)
                    {
                        float ts = 4.0 * p * (1.0 - p);
                        float pulse = 0.7 + 0.3 * sin(t * 7.0);
                        float g = b * (0.45 + 0.5 * saturate(p * 4.0) + 0.8 * ts) * pulse;
                        g += 4.0 * a * (1.0 - a) * m * 1.2;   // glowing dissolve front
                        glow += g;
                    }
                }

                col += _GlowColor.rgb * glow;
                return fixed4(saturate(col), 1.0);
            }
            ENDCG
        }
    }
    FallBack Off
}
