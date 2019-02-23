Shader "Unlit/BlendShader"
{
	Properties
	{
		_MainTex ("External Camera", 2D) = "white" {}
		_Overlay ("Holograms", 2D) = "white" {}
	}
	SubShader
	{
//		// No culling or depth
//		Cull Off ZWrite Off ZTest Always

		Pass
		{
		
		    
			CGPROGRAM
			#pragma vertex vert
			#pragma fragment frag
			#include "UnityCG.cginc"
		

			struct v2f
			{
				float2 uv : TEXCOORD0;
				float4 vertex : SV_POSITION;
			};

			v2f vert (appdata_base v)
			{
				v2f o;
				o.vertex = UnityObjectToClipPos(v.vertex);
				o.uv = v.texcoord;
				
				return o;
			}
			
			fixed4 linearToGamma(fixed4 col){
			    return pow(col, 1.0/2.4);
			    
			}
			
			
			sampler2D _MainTex;
			// overlay is the in game holograms
			sampler2D _Overlay;

			fixed4 frag (v2f i) : SV_Target
			{
			    // we need to gamma correct the holograms but not the ext camera
			    // then output everything in linear color space so the rendertexture doesn't do conversion on the whole thing
			    
//			    float baseX = i.uv.x*0.6666+0.3333;
//			    fixed4 colBase = tex2D(_MainTex, float2(baseX,i.uv.y));
				fixed4 colBase = tex2D(_MainTex, i.uv);
				
//				float uu = ((i.uv.x-0.5)*1.2 + 0.5)+0.14;
//			    float vv = ((i.uv.y-0.5)*1.2 + 0.5);
//				fixed4 colOverlay = (uu>0.99 || vv>0.99 || uu<0.01 || vv<0.01) ? fixed4(0.0,0.0,0.0,0.0) : linearToGamma(tex2D(_Overlay, float2(uu,vv)));
                fixed4 colOverlay = linearToGamma(tex2D(_Overlay, i.uv));
				
				
				fixed4 blend = lerp (colBase, colOverlay, colOverlay.a*0.75f);
				
				return blend;
			}
			ENDCG
		}
	}
}


