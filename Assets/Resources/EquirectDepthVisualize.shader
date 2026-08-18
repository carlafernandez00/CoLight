// Visualises the reconstructed equirectangular DEPTH panorama (RFloat, linear
// meters) as a blue-ramp image for a debug canvas.
//   near = light blue, far = dark blue (normalised by _MaxDepth)
//   unseen texels (depth == 0) = black, so scanned coverage "paints in" as blue
//   against black and is easy to watch initialise.
// Used by EnvironmentMapReconstructor via Graphics.Blit(depthRT, displayRT, mat).
Shader "EquirectDepthVisualize"
{
    Properties
    {
        _MainTex ("Depth (meters)", 2D) = "black" {}
        _MaxDepth ("Max depth (m) mapped to black", Float) = 8.0
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" }

        Pass
        {
            ZWrite Off
            ZTest Always
            Cull Off

            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float _MaxDepth;  // max depth (meters) mapped to black -> used to scale the data

            fixed4 frag(v2f_img i) : SV_Target
            {
                float meters = tex2D(_MainTex, i.uv).r;

                // depth == 0 -> this direction has not been scanned yet
                if (meters <= 0.0)
                    return fixed4(0.0, 0.0, 0.0, 1.0);   // black = unseen

                // Scanned: light blue (near) -> dark blue (far), normalised by _MaxDepth.
                float t = saturate(meters / max(_MaxDepth, 1e-3));   // 0 near .. 1 far
                fixed3 nearCol = fixed3(0.70, 0.90, 1.00);           // light blue
                fixed3 farCol  = fixed3(0.00, 0.10, 0.40);           // dark blue
                return fixed4(lerp(nearCol, farCol, t), 1.0);
            }
            ENDCG
        }
    }
}
