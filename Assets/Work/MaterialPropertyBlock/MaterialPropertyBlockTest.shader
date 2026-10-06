// MaterialPropertyBlock 实测用着色器（内置渲染管线 / Unity 2022.3）
//
// 两个关键设计：
//  1) _Color / _Value 用 UNITY_DEFINE_INSTANCED_PROP 声明 —— 它们是"每实例属性"。
//     勾上材质上的 Enable GPU Instancing 后，Unity 才会把 MaterialPropertyBlock 里的值
//     按实例塞进实例数据缓冲，一次 draw 画多个实例。
//  2) _NonInstanced 是普通 uniform（故意不声明成实例化属性）——
//     往 MaterialPropertyBlock 里塞它，Unity 会关掉该物体的实例化（官方文档原话：
//     "Don't put non-instanced properties in the MaterialPropertyBlock, because this disables instancing"）。
//     测试场景 8 就是用来验证这一条的。
//
// 没勾 Enable GPU Instancing 时，UNITY_ACCESS_INSTANCED_PROP 会退化成普通 uniform 读取，
// 同一份着色器、同一份材质照样能跑 —— 于是"实例化开/关"成了两个场景之间唯一的变量。

Shader "Customer/MaterialPropertyBlock/MaterialPropertyBlockTest"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _Color ("Tint（实例化属性）", Color) = (1,1,1,1)
        _Value ("Value（实例化属性）", Float) = 1
        _NonInstanced ("Non Instanced（非实例化，放进 MaterialPropertyBlock 会关掉实例化）", Float) = 0
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Transparent"
            "IgnoreProjector" = "True"
            "RenderType" = "Transparent"
            "PreviewType" = "Plane"
        }

        Cull Off
        Lighting Off
        ZWrite Off
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #pragma multi_compile_instancing

            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _MainTex_ST;

            // 非实例化属性：只是为了演示"放进 MaterialPropertyBlock 会关掉实例化"
            float _NonInstanced;

            UNITY_INSTANCING_BUFFER_START(Props)
                UNITY_DEFINE_INSTANCED_PROP(float4, _Color)
                UNITY_DEFINE_INSTANCED_PROP(float, _Value)
            UNITY_INSTANCING_BUFFER_END(Props)

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
                float4 color : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                float4 color : COLOR;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            v2f vert (appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_OUTPUT(v2f, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);

                float4 tint = UNITY_ACCESS_INSTANCED_PROP(Props, _Color);
                float value = UNITY_ACCESS_INSTANCED_PROP(Props, _Value);

                // _NonInstanced 参与一下计算，免得被编译器优化掉
                o.color = v.color * tint * value * (1.0 + _NonInstanced * 0.0);
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                fixed4 c = tex2D(_MainTex, i.uv) * i.color;
                c.rgb *= c.a;
                return c;
            }
            ENDCG
        }
    }

    Fallback Off
}
