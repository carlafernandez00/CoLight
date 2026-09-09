// Visualises the reconstructed equirectangular COLOR panorama (ARGBHalf, linear
// RGB) on a debug canvas.
//   alpha == 1 -> texel was scanned, show the reconstructed color
//   alpha == 0 -> direction not yet seen, show MAGENTA
// The blit output is opaque, so the panel is always visible on the canvas and
// coverage "paints in" over a magenta background as the user scans.
// Used by EnvironmentMapReconstructor via Graphics.Blit(colorRT, displayRT, mat).
Shader "EquirectColorVisualize"
{
    Properties
    {
        _MainTex ("Color panorama (linear RGB, a = seen)", 2D) = "black" {}
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

            fixed4 frag(v2f_img i) : SV_Target
            {
                float4 c = tex2D(_MainTex, i.uv);

                // The compute writes alpha = 1 on every texel it fills; the panorama is
                // cleared to alpha = 0, so alpha is the coverage mask.
                if (c.a < 0.5)
                    return fixed4(1.0, 0.0, 1.0, 1.0);   // magenta = unseen

                return fixed4(c.rgb, 1.0);
            }
            ENDCG
        }
    }
}
