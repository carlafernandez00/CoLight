// Visualises the reconstructed equirectangular DEPTH panorama (RFloat, linear
// meters) as a greyscale image for a debug canvas.
//   near = white, far = black (normalised by _MaxDepth)
//   unseen texels (depth == 0) = dark blue, so "not scanned yet" reads distinctly
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
                    return fixed4(0.0, 0.0, 0.3, 1.0);   // dark blue = unseen

                float g = saturate(meters / max(_MaxDepth, 1e-3));
                g = 1.0 - g;                             // near = white, far = black
                return fixed4(g, g, g, 1.0);
            }
            ENDCG
        }
    }
}
