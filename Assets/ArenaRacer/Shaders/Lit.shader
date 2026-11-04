// Flat Lambert lighting with vertex colours. Vertex alpha below 1 marks self-lit parts (lamps, screens).
Shader "ArenaRacer/Lit"
{
    Properties
    {
        _Color ("Tint", Color) = (1, 1, 1, 1)
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" }
        LOD 200

        CGPROGRAM
        #pragma surface surf Lambert fullforwardshadows addshadow noforwardadd
        #pragma target 3.0

        fixed4 _Color;

        struct Input
        {
            float4 color : COLOR;
        };

        void surf (Input IN, inout SurfaceOutput o)
        {
            fixed3 c = IN.color.rgb * _Color.rgb;
            o.Albedo = c;
            o.Emission = c * (1.0 - IN.color.a) * 1.8;
        }
        ENDCG
    }
}
