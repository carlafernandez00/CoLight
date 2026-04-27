// Visualises the Meta Environment Depth texture (Texture2DArray, left-eye slice 0)
// as a greyscale image. White = near, black = far.
// Used by EnvironmentDepthReader to drive a RawImage debug preview.
Shader "DepthVisualize"
{
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

            // Declared as Texture2DArray to match the format EnvironmentDepthManager sets globally.
            UNITY_DECLARE_TEX2DARRAY(_EnvironmentDepthTexture);

            fixed4 frag(v2f_img i) : SV_Target
            {
                // Sample left-eye slice (index 0). Raw depth: ~1 = near, ~0 = far (reversed-Z).
                float d = UNITY_SAMPLE_TEX2DARRAY(_EnvironmentDepthTexture, float3(i.uv.x, i.uv.y, 0.0)).r;
                return fixed4(d, d, d, 1.0);
            }
            ENDCG
        }
    }
}
