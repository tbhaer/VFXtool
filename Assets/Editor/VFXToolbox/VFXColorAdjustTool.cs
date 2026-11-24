using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

/// <summary>
/// 模块十三：贴图改色（颜色调整）。
/// 在“保留原图明暗与纹理”的前提下，对一张已经有颜色的贴图做整体调色：
/// HSV（色相/饱和度/明度）、亮度、对比度、Gamma、输入色阶（黑白场）、反相、
/// 染色 Tint（相乘/罩染/屏幕三种混合）、Alpha 倍增与反转。GPU 实时预览，导出 PNG。
/// 与模块十一“渐变映射”的区别：渐变映射按灰度重新套颜色，本模块保留原图色彩关系只做调整。
/// GPU 处理，不要求源贴图开启 Read/Write。
/// </summary>
public partial class VFXToolboxWindow
{
    [SerializeField] private Texture2D caSource;

    // HSV
    [SerializeField] private float caHue;          // -180..180 度
    [SerializeField] private float caSaturation = 1f; // 0..2，1 不变
    [SerializeField] private float caValue = 1f;      // 明度倍增 0..2

    // 基础调整
    [SerializeField] private float caBrightness;   // -1..1 加性
    [SerializeField] private float caContrast = 1f;   // 0..2，1 不变
    [SerializeField] private float caGamma = 1f;      // 0.1..3

    // 单层非破坏滤镜：在现有调色之前或之后按滤镜语义应用，强度可混合原图。
    [SerializeField] private int caFilterMode;
    [SerializeField] private float caFilterAmount = 1f;
    [SerializeField] private float caFilterSize = 3f;
    [SerializeField] private int caPosterizeLevels = 5;
    [SerializeField] private float caFilterSeed;
    [SerializeField] private float caFilterAngle;
    [SerializeField] private Vector2 caFilterCenter = new Vector2(0.5f, 0.5f);
    [SerializeField] private float caDistortFrequency = 8f;
    [SerializeField] private float caDistortRadius = 0.5f;
    [SerializeField] private bool caPinch;

    // 色阶 / 反相
    [SerializeField] private bool caUseLevels;
    [SerializeField] private float caInBlack;     // 0..1
    [SerializeField] private float caInWhite = 1f;    // 0..1
    [SerializeField] private bool caInvert;

    // 染色
    [SerializeField] private bool caUseTint;
    [SerializeField] private Color caTintColor = Color.white;
    [SerializeField] private float caTintStrength = 0.5f; // 0..1
    [SerializeField] private int caTintMode;      // 0 相乘 1 罩染 2 屏幕

    // Alpha
    [SerializeField] private float caAlphaMul = 1f;   // 0..2
    [SerializeField] private bool caInvertAlpha;

    // 黑底处理
    [SerializeField] private int caBgMode;            // 0 关 1 扣黑 2 加黑底
    [SerializeField] private int caBgBase;            // 0 最大通道 1 亮度
    [SerializeField] private float caBgThreshold;     // 0..1
    [SerializeField] private float caBgSoftness;       // 0..1，0=线性辉光
    [SerializeField] private bool caBgUnmult = true;
    [SerializeField] private bool caBgOpaque = true;

    // 预览 / 导出
    [SerializeField] private bool caShowOriginal;
    [SerializeField] private string caOutputName = "ColorAdjust";

    // 折叠分组
    [SerializeField] private bool caFoldHsv = true;
    [SerializeField] private bool caFoldBasic = true;
    [SerializeField] private bool caFoldFilters = true;
    [SerializeField] private bool caFoldLevels;
    [SerializeField] private bool caFoldTint;
    [SerializeField] private bool caFoldAlpha;
    [SerializeField] private bool caFoldBg;

    private Texture2D caPreview;
    private static Shader caShader;

    private static readonly string[] CaTintNames = { "相乘 Multiply", "罩染 Overlay", "屏幕 Screen" };
    private static readonly string[] CaFilterNames =
    {
        "无", "黑白 Grayscale", "棕褐 Sepia", "高斯模糊 Gaussian Blur", "锐化 Sharpen",
        "边缘检测 Edge", "浮雕 Emboss", "像素化 Pixelate", "色调分离 Posterize", "暗角 Vignette", "胶片颗粒 Film Grain",
        "方框模糊 Box Blur", "动感模糊 Motion Blur", "旋转模糊 Radial Blur", "缩放模糊 Zoom Blur",
        "波浪 Wave", "扭曲 Twirl", "膨胀 / 收缩 Bulge-Pinch", "波纹 Ripple", "色差 Chromatic Aberration"
    };
    private static readonly string[] CaBgModeNames = { "关闭", "扣黑（黑底 → 透明）", "加黑底（透明 → 黑底）" };
    private static readonly string[] CaBgBaseNames = { "最大通道（推荐彩色特效）", "亮度 Luma" };

    private const string CaShaderSource = @"
Shader ""Hidden/VFXToolboxColorAdjust""
{
    Properties
    {
        _MainTex (""Tex"", 2D) = ""white"" {}
    }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        Blend Off
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #pragma target 3.0
            #include ""UnityCG.cginc""
            sampler2D _MainTex;

            float _HueShift;
            float _Saturation;
            float _Value;
            float _Brightness;
            float _Contrast;
            float _Gamma;
            float _FilterMode;
            float _FilterAmount;
            float _FilterSize;
            float _PosterizeLevels;
            float _FilterSeed;
            float _FilterAngle;
            float4 _FilterCenter;
            float _DistortFrequency;
            float _DistortRadius;
            float _Pinch;
            float4 _MainTex_TexelSize;
            float _Invert;
            float _UseLevels;
            float _InBlack;
            float _InWhite;
            float _UseTint;
            float4 _TintColor;
            float _TintStrength;
            float _TintMode;
            float _AlphaMul;
            float _InvertAlpha;
            float _BGMode;       // 0 关闭 1 扣黑(黑底转透明) 2 加黑底(透明转黑底)
            float _BGBase;       // 扣黑亮度来源 0 最大通道 1 亮度Luma
            float _BGThreshold;  // 黑场阈值
            float _BGSoftness;   // 边缘柔化宽度
            float _BGUnmult;     // 扣黑后还原纯色边缘(去黑)
            float _BGOpaque;     // 加黑底后输出不透明

            float3 rgb2hsv(float3 c)
            {
                float4 K = float4(0.0, -1.0 / 3.0, 2.0 / 3.0, -1.0);
                float4 p = lerp(float4(c.bg, K.wz), float4(c.gb, K.xy), step(c.b, c.g));
                float4 q = lerp(float4(p.xyw, c.r), float4(c.r, p.yzx), step(p.x, c.r));
                float d = q.x - min(q.w, q.y);
                float e = 1.0e-10;
                return float3(abs(q.z + (q.w - q.y) / (6.0 * d + e)), d / (q.x + e), q.x);
            }

            float3 hsv2rgb(float3 c)
            {
                float3 K = float3(1.0, 2.0 / 3.0, 1.0 / 3.0);
                float3 p = abs(frac(c.xxx + K) * 6.0 - 3.0);
                return c.z * lerp(K.xxx, saturate(p - K.xxx), c.y);
            }

            float3 sampleRgb(float2 uv)
            {
                return tex2D(_MainTex, saturate(uv)).rgb;
            }

            float sampleAlpha(float2 uv)
            {
                return tex2D(_MainTex, saturate(uv)).a;
            }

            fixed4 frag(v2f_img i) : SV_Target
            {
                float2 sampleUV = i.uv;
                float2 center = _FilterCenter.xy;
                float2 aspect = float2(_MainTex_TexelSize.z / max(1.0, _MainTex_TexelSize.w), 1.0);
                float2 centered = (sampleUV - center) * aspect;
                float radius = max(0.001, _DistortRadius);
                float amount = saturate(_FilterAmount);

                // Photoshop 风格空间扭曲：采样坐标先变形，再读取图像。
                if (_FilterMode > 14.5 && _FilterMode < 15.5) // Wave
                {
                    sampleUV.y += sin((sampleUV.x - center.x) * max(1.0, _DistortFrequency) * 6.2831853)
                        * _FilterSize * _MainTex_TexelSize.y * amount;
                }
                else if (_FilterMode > 15.5 && _FilterMode < 16.5) // Twirl
                {
                    float r = length(centered);
                    float falloff = saturate(1.0 - r / radius);
                    float angle = _FilterAmount * 6.2831853 * falloff * falloff;
                    float s = sin(angle), co = cos(angle);
                    float2 p = centered;
                    centered = float2(p.x * co - p.y * s, p.x * s + p.y * co);
                    sampleUV = center + centered / aspect;
                }
                else if (_FilterMode > 16.5 && _FilterMode < 17.5) // Bulge / Pinch
                {
                    float r = length(centered);
                    if (r < radius)
                    {
                        float falloff = 1.0 - r / radius;
                        float strength = (0.85 * amount) * (_Pinch > 0.5 ? 1.0 : -1.0);
                        float scale = 1.0 + strength * falloff * falloff;
                        sampleUV = center + centered * scale / aspect;
                    }
                }
                else if (_FilterMode > 17.5 && _FilterMode < 18.5) // Ripple
                {
                    float r = length(centered);
                    float wave = sin(r * max(1.0, _DistortFrequency) * 6.2831853);
                    float offset = wave * _FilterSize * _MainTex_TexelSize.y * amount;
                    sampleUV += (r > 1.0e-4 ? centered / r : float2(0, 0)) * offset / aspect;
                }

                sampleUV = saturate(sampleUV);
                if (_FilterMode > 6.5 && _FilterMode < 7.5)
                {
                    float blockPixels = max(1.0, _FilterSize);
                    float2 blockUV = _MainTex_TexelSize.xy * blockPixels;
                    float2 blockCenter = floor(i.uv / blockUV) * blockUV + blockUV * 0.5;
                    sampleUV = saturate(lerp(i.uv, blockCenter, amount));
                }

                fixed4 src = tex2D(_MainTex, sampleUV);
                float3 c = src.rgb;
                float alpha = src.a;

                // 空间滤镜先处理原始图像，随后再经过色阶、HSV 和染色等调色步骤。
                if (_FilterAmount > 0.001 && _FilterMode > 2.5 && _FilterMode < 6.5)
                {
                    float2 d = _MainTex_TexelSize.xy * max(1.0, _FilterSize);
                    float3 filtered = c;
                    if (_FilterMode < 3.5) // Gaussian blur, 3x3 kernel
                    {
                        filtered = c * 4.0;
                        filtered += sampleRgb(sampleUV + float2(-d.x, 0)) * 2.0;
                        filtered += sampleRgb(sampleUV + float2( d.x, 0)) * 2.0;
                        filtered += sampleRgb(sampleUV + float2(0, -d.y)) * 2.0;
                        filtered += sampleRgb(sampleUV + float2(0,  d.y)) * 2.0;
                        filtered += sampleRgb(sampleUV + float2(-d.x, -d.y));
                        filtered += sampleRgb(sampleUV + float2( d.x, -d.y));
                        filtered += sampleRgb(sampleUV + float2(-d.x,  d.y));
                        filtered += sampleRgb(sampleUV + float2( d.x,  d.y));
                        filtered /= 16.0;
                        alpha = (alpha * 4.0
                            + sampleAlpha(sampleUV + float2(-d.x, 0)) * 2.0
                            + sampleAlpha(sampleUV + float2( d.x, 0)) * 2.0
                            + sampleAlpha(sampleUV + float2(0, -d.y)) * 2.0
                            + sampleAlpha(sampleUV + float2(0,  d.y)) * 2.0
                            + sampleAlpha(sampleUV + float2(-d.x, -d.y))
                            + sampleAlpha(sampleUV + float2( d.x, -d.y))
                            + sampleAlpha(sampleUV + float2(-d.x,  d.y))
                            + sampleAlpha(sampleUV + float2( d.x,  d.y))) / 16.0;
                    }
                    else if (_FilterMode < 4.5) // Sharpen
                    {
                        float3 neighbors = sampleRgb(sampleUV + float2(-d.x, 0))
                            + sampleRgb(sampleUV + float2(d.x, 0))
                            + sampleRgb(sampleUV + float2(0, -d.y))
                            + sampleRgb(sampleUV + float2(0, d.y));
                        filtered = c * 5.0 - neighbors;
                    }
                    else if (_FilterMode < 5.5) // Edge detect
                    {
                        float3 gx = sampleRgb(sampleUV + float2(d.x, -d.y))
                            + 2.0 * sampleRgb(sampleUV + float2(d.x, 0))
                            + sampleRgb(sampleUV + float2(d.x, d.y))
                            - sampleRgb(sampleUV + float2(-d.x, -d.y))
                            - 2.0 * sampleRgb(sampleUV + float2(-d.x, 0))
                            - sampleRgb(sampleUV + float2(-d.x, d.y));
                        float3 gy = sampleRgb(sampleUV + float2(-d.x, d.y))
                            + 2.0 * sampleRgb(sampleUV + float2(0, d.y))
                            + sampleRgb(sampleUV + float2(d.x, d.y))
                            - sampleRgb(sampleUV + float2(-d.x, -d.y))
                            - 2.0 * sampleRgb(sampleUV + float2(0, -d.y))
                            - sampleRgb(sampleUV + float2(d.x, -d.y));
                        float edge = saturate(length(gx) + length(gy));
                        filtered = float3(edge, edge, edge);
                    }
                    else // Emboss
                    {
                        float3 diagonal = sampleRgb(sampleUV + float2(d.x, d.y))
                            - sampleRgb(sampleUV + float2(-d.x, -d.y));
                        filtered = saturate(0.5 + diagonal * 1.5);
                    }
                    c = lerp(c, filtered, _FilterAmount);
                    if (_FilterMode < 3.5) alpha = lerp(src.a, alpha, _FilterAmount);
                }
                else if (_FilterAmount > 0.001 && _FilterMode > 10.5 && _FilterMode < 14.5)
                {
                    float2 texel = _MainTex_TexelSize.xy;
                    float3 filtered = c;
                    if (_FilterMode < 11.5) // Box Blur, 3x3 uniform kernel
                    {
                        filtered = 0;
                        [unroll] for (int by = -1; by <= 1; by++)
                        {
                            [unroll] for (int bx = -1; bx <= 1; bx++)
                                filtered += sampleRgb(sampleUV + float2(bx, by) * texel * _FilterSize);
                        }
                        filtered /= 9.0;
                        float blurredAlpha = 0;
                        [unroll] for (int bay = -1; bay <= 1; bay++)
                        {
                            [unroll] for (int bax = -1; bax <= 1; bax++)
                                blurredAlpha += sampleAlpha(sampleUV + float2(bax, bay) * texel * _FilterSize);
                        }
                        alpha = lerp(alpha, blurredAlpha / 9.0, _FilterAmount);
                    }
                    else if (_FilterMode < 12.5) // Motion Blur
                    {
                        float2 direction = float2(cos(_FilterAngle), sin(_FilterAngle)) * texel * _FilterSize;
                        filtered = 0;
                        [unroll] for (int m = -4; m <= 4; m++)
                            filtered += sampleRgb(sampleUV + direction * (m / 4.0));
                        filtered /= 9.0;
                        float blurredAlpha = 0;
                        [unroll] for (int ma = -4; ma <= 4; ma++)
                            blurredAlpha += sampleAlpha(sampleUV + direction * (ma / 4.0));
                        alpha = lerp(alpha, blurredAlpha / 9.0, _FilterAmount);
                    }
                    else if (_FilterMode < 13.5) // Radial / Spin Blur
                    {
                        float2 uvCenter = (sampleUV - center) * aspect;
                        filtered = 0;
                        [unroll] for (int r = -4; r <= 4; r++)
                        {
                            float angle = (r / 4.0) * _FilterSize * 0.0015;
                            float s = sin(angle), co = cos(angle);
                            float2 p = uvCenter;
                            float2 rotated = float2(p.x * co - p.y * s, p.x * s + p.y * co);
                            filtered += sampleRgb(center + rotated / aspect);
                        }
                        filtered /= 9.0;
                        float blurredAlpha = 0;
                        [unroll] for (int ra = -4; ra <= 4; ra++)
                        {
                            float angle = (ra / 4.0) * _FilterSize * 0.0015;
                            float s = sin(angle), co = cos(angle);
                            float2 p = uvCenter;
                            float2 rotated = float2(p.x * co - p.y * s, p.x * s + p.y * co);
                            blurredAlpha += sampleAlpha(center + rotated / aspect);
                        }
                        alpha = lerp(alpha, blurredAlpha / 9.0, _FilterAmount);
                    }
                    else // Zoom Blur
                    {
                        float2 direction = (center - sampleUV) * min(1.0, _FilterSize * 0.006);
                        filtered = 0;
                        [unroll] for (int z = 0; z < 9; z++)
                            filtered += sampleRgb(sampleUV + direction * (z / 8.0));
                        filtered /= 9.0;
                        float blurredAlpha = 0;
                        [unroll] for (int za = 0; za < 9; za++)
                            blurredAlpha += sampleAlpha(sampleUV + direction * (za / 8.0));
                        alpha = lerp(alpha, blurredAlpha / 9.0, _FilterAmount);
                    }
                    c = lerp(c, filtered, _FilterAmount);
                }

                // 输入色阶：黑场/白场重新映射明暗分布
                if (_UseLevels > 0.5)
                {
                    float range = max(1.0e-4, _InWhite - _InBlack);
                    c = saturate((c - _InBlack) / range);
                }

                // 反相
                if (_Invert > 0.5) c = 1.0 - c;

                // Gamma
                c = pow(max(c, 0.0), 1.0 / max(1.0e-3, _Gamma));

                // HSV：色相旋转、明度（饱和度单独按亮度插值，见下）
                float3 hsv = rgb2hsv(c);
                hsv.x = frac(hsv.x + _HueShift);
                hsv.z = saturate(hsv.z * _Value);
                c = hsv2rgb(hsv);

                // 饱和度：0=按亮度去色成灰、1=原色、>1 向外增艳。
                // 不直接缩放 HSV 的 S（那样纯红去色会得到白色），改用原色与亮度灰插值，符合 PS 直觉。
                float luma = dot(c, float3(0.299, 0.587, 0.114));
                c = lerp(float3(luma, luma, luma), c, _Saturation);

                // 亮度（加性）
                c = c + _Brightness;

                // 对比度（围绕中灰 0.5）
                c = saturate((c - 0.5) * _Contrast + 0.5);

                // 染色
                if (_UseTint > 0.5)
                {
                    float3 t = _TintColor.rgb;
                    float3 tinted;
                    if (_TintMode < 0.5) tinted = c * t;                       // 相乘：保留明暗的染色
                    else if (_TintMode < 1.5) tinted = t;                      // 罩染：趋向染色，强度控制浓度
                    else tinted = 1.0 - (1.0 - c) * (1.0 - t);                 // 屏幕：提亮式染色
                    c = lerp(c, tinted, _TintStrength);
                }

                // 色调与镜头类滤镜在基础调色后应用。
                if (_FilterAmount > 0.001)
                {
                    if (_FilterMode > 0.5 && _FilterMode < 1.5)
                    {
                        float gray = dot(c, float3(0.299, 0.587, 0.114));
                        c = lerp(c, float3(gray, gray, gray), _FilterAmount);
                    }
                    else if (_FilterMode > 1.5 && _FilterMode < 2.5)
                    {
                        float3 sepia = float3(
                            dot(c, float3(0.393, 0.769, 0.189)),
                            dot(c, float3(0.349, 0.686, 0.168)),
                            dot(c, float3(0.272, 0.534, 0.131)));
                        c = lerp(c, sepia, _FilterAmount);
                    }
                    else if (_FilterMode > 7.5 && _FilterMode < 8.5)
                    {
                        float levels = max(2.0, _PosterizeLevels);
                        float3 posterized = floor(saturate(c) * (levels - 1.0) + 0.5) / (levels - 1.0);
                        c = lerp(c, posterized, _FilterAmount);
                    }
                    else if (_FilterMode > 8.5 && _FilterMode < 9.5)
                    {
                        float2 centered = (i.uv - 0.5) * float2(_MainTex_TexelSize.z / max(1.0, _MainTex_TexelSize.w), 1.0);
                        float vignette = 1.0 - smoothstep(0.25, 0.95, length(centered) * 1.3);
                        c *= lerp(1.0, vignette, _FilterAmount);
                    }
                    else if (_FilterMode > 9.5 && _FilterMode < 10.5)
                    {
                        float noise = frac(sin(dot(i.uv * (_FilterSeed + 1.0), float2(12.9898, 78.233))) * 43758.5453);
                        c += (noise - 0.5) * 0.18 * _FilterAmount;
                    }
                    else if (_FilterMode > 18.5 && _FilterMode < 19.5) // Chromatic Aberration
                    {
                        float2 radial = normalize((i.uv - center) * aspect + 1.0e-6) / aspect;
                        float2 offset = radial * _MainTex_TexelSize.xy * _FilterSize * _FilterAmount;
                        float red = tex2D(_MainTex, saturate(sampleUV + offset)).r;
                        float blue = tex2D(_MainTex, saturate(sampleUV - offset)).b;
                        c.r = saturate(c.r + red - src.r);
                        c.b = saturate(c.b + blue - src.b);
                    }
                }

                c = saturate(c);
                float a = alpha;

                // 黑底处理：扣黑（黑底转透明）/ 加黑底（透明转黑底，Additive 粒子常用）
                if (_BGMode > 0.5 && _BGMode < 1.5)
                {
                    // 按亮度生成 Alpha：彩色特效用最大通道（纯色不被压暗），也可选人眼亮度
                    float bl = _BGBase > 0.5
                        ? dot(c, float3(0.299, 0.587, 0.114))
                        : max(c.r, max(c.g, c.b));
                    float na;
                    if (_BGSoftness > 0.001)
                    {
                        // 硬边缘色块素材：在阈值上方按柔化宽度羽化 Alpha
                        float hi = min(1.0, _BGThreshold + _BGSoftness);
                        na = smoothstep(_BGThreshold, max(hi, _BGThreshold + 1.0e-4), bl);
                    }
                    else
                    {
                        // 柔和辉光素材（默认）：线性黑场映射，阈值为 0 时 Alpha=亮度，完整保留光晕衰减
                        na = saturate((bl - _BGThreshold) / max(1.0 - _BGThreshold, 1.0e-4));
                    }
                    na = bl <= _BGThreshold ? 0.0 : na;
                    if (_BGUnmult > 0.5)
                    {
                        // Unmult：除以“原始亮度”(黑底预乘的逆运算)还原纯色边缘。
                        // 分母必须用未被 smoothstep 重塑的原始亮度，否则软化过渡带会把暗色放大成白色描边。
                        float denom = max(bl, _BGThreshold + 1.0e-3);
                        c = bl > _BGThreshold ? saturate(c / denom) : float3(0.0, 0.0, 0.0);
                    }
                    a = na;
                }
                else if (_BGMode > 1.5)
                {
                    // 合成到黑背景（预乘）：透明区域变黑
                    c = c * a;
                    if (_BGOpaque > 0.5) a = 1.0;
                }

                a *= _AlphaMul;
                if (_InvertAlpha > 0.5) a = 1.0 - a;
                return fixed4(c, saturate(a));
            }
            ENDCG
        }
    }
}";

    private void InitColorAdjust()
    {
        // 序列化字段已给默认值，这里仅保证染色颜色非空
        if (caTintColor == default(Color))
        {
            caTintColor = Color.white;
        }
    }

    private static Shader GetColorAdjustShader()
    {
        if (caShader == null)
        {
            caShader = ShaderUtil.CreateShaderAsset(CaShaderSource);
        }
        return caShader;
    }

    private void DrawColorAdjustTool()
    {
        EditorGUILayout.LabelField("贴图改色（颜色调整）", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "对已有颜色的贴图在保留明暗纹理的前提下整体调色：色相/饱和度/明度、亮度、对比度、Gamma、色阶、反相、染色、Alpha。\n" +
            "勾选“显示原图”可与结果对比；调好后导出为新的 PNG（不覆盖源贴图）。",
            MessageType.None);

        EditorGUI.BeginChangeCheck();

        EditorGUILayout.BeginHorizontal();
        caSource = (Texture2D)EditorGUILayout.ObjectField(
            new GUIContent("源贴图", "拖入一张已经有颜色、需要改色的贴图"),
            caSource, typeof(Texture2D), false);
        if (GUILayout.Button("使用选中", GUILayout.Width(70)))
        {
            Texture2D picked = Selection.activeObject as Texture2D;
            if (picked != null)
            {
                caSource = picked;
            }
        }
        EditorGUILayout.EndHorizontal();

        // —— HSV ——
        caFoldHsv = EditorGUILayout.Foldout(caFoldHsv, "色相 / 饱和度 / 明度（HSV）", true);
        if (caFoldHsv)
        {
            EditorGUI.indentLevel++;
            caHue = EditorGUILayout.Slider(
                new GUIContent("色相 Hue", "整体色相旋转，单位度。如 +180 红变青；做整体变色（红火改蓝火）"), caHue, -180f, 180f);
            caSaturation = EditorGUILayout.Slider(
                new GUIContent("饱和度 S", "0=完全去色变灰，1=不变，>1 增艳"), caSaturation, 0f, 2f);
            caValue = EditorGUILayout.Slider(
                new GUIContent("明度 V", "整体明暗倍增，1=不变，<1 压暗、>1 提亮"), caValue, 0f, 2f);
            EditorGUI.indentLevel--;
        }

        // —— 基础 ——
        caFoldBasic = EditorGUILayout.Foldout(caFoldBasic, "亮度 / 对比度 / Gamma", true);
        if (caFoldBasic)
        {
            EditorGUI.indentLevel++;
            caBrightness = EditorGUILayout.Slider(
                new GUIContent("亮度", "加性偏移，正=整体提亮、负=压暗"), caBrightness, -1f, 1f);
            caContrast = EditorGUILayout.Slider(
                new GUIContent("对比度", "围绕中灰拉伸，1=不变，>1 明暗对比更强、<1 更灰平"), caContrast, 0f, 2f);
            caGamma = EditorGUILayout.Slider(
                new GUIContent("Gamma", "调中间调过渡，1=不变，<1 整体变亮、>1 变暗（主要影响中间灰）"), caGamma, 0.1f, 3f);
            EditorGUI.indentLevel--;
        }

        caFoldFilters = EditorGUILayout.Foldout(caFoldFilters, "滤镜 Filters", true);
        if (caFoldFilters)
        {
            EditorGUI.indentLevel++;
            caFilterMode = EditorGUILayout.Popup(
                new GUIContent("滤镜", "可与 HSV、色阶、染色等参数叠加；选择“无”仅关闭滤镜"),
                caFilterMode, CaFilterNames);
            using (new EditorGUI.DisabledScope(caFilterMode == 0))
            {
                caFilterAmount = EditorGUILayout.Slider(
                    new GUIContent("滤镜强度", "0 保留原图，1 完全应用滤镜；空间滤镜可用中间值混合"),
                    caFilterAmount, 0f, 1f);
                if ((caFilterMode >= 3 && caFilterMode <= 7) ||
                    (caFilterMode >= 11 && caFilterMode <= 15) ||
                    caFilterMode == 18 || caFilterMode == 19)
                {
                    caFilterSize = EditorGUILayout.Slider(
                        new GUIContent("半径 / 强度", "控制取样半径、方块尺寸或变形强度；不同滤镜单位不同"),
                        caFilterSize, 1f, 32f);
                }
                if (caFilterMode == 8)
                {
                    caPosterizeLevels = EditorGUILayout.IntSlider(
                        new GUIContent("色阶数量", "减少每个颜色通道的级数，产生海报化色块效果"),
                        caPosterizeLevels, 2, 16);
                }
                if (caFilterMode == 10)
                {
                    caFilterSeed = EditorGUILayout.Slider(
                        new GUIContent("颗粒变化", "改变胶片颗粒的分布"), caFilterSeed, 0f, 100f);
                }
                if (caFilterMode == 12)
                {
                    caFilterAngle = EditorGUILayout.Slider(
                        new GUIContent("方向角", "动感模糊的方向，0° 为水平，90° 为垂直"), caFilterAngle, -180f, 180f);
                }
                if ((caFilterMode >= 13 && caFilterMode <= 18) || caFilterMode == 19)
                {
                    caFilterCenter = EditorGUILayout.Vector2Field("中心位置", caFilterCenter);
                    caFilterCenter.x = Mathf.Clamp01(caFilterCenter.x);
                    caFilterCenter.y = Mathf.Clamp01(caFilterCenter.y);
                }
                if (caFilterMode == 15 || caFilterMode == 18)
                {
                    caDistortFrequency = EditorGUILayout.Slider(
                        new GUIContent("波纹频率", "控制波浪或波纹的疏密"), caDistortFrequency, 1f, 32f);
                }
                if (caFilterMode >= 16 && caFilterMode <= 18)
                {
                    caDistortRadius = EditorGUILayout.Slider(
                        new GUIContent("影响半径", "扭曲影响范围，占贴图短边的比例"), caDistortRadius, 0.05f, 1f);
                }
                if (caFilterMode == 17)
                {
                    caPinch = EditorGUILayout.Toggle("收缩 Pinch", caPinch);
                }
            }
            EditorGUI.indentLevel--;
        }

        // —— 色阶 / 反相 ——
        caFoldLevels = EditorGUILayout.Foldout(caFoldLevels, "色阶 / 反相", true);
        if (caFoldLevels)
        {
            EditorGUI.indentLevel++;
            caUseLevels = EditorGUILayout.Toggle(
                new GUIContent("启用输入色阶", "按黑场/白场重新映射明暗分布（对应 PS 色阶的输入滑块）"), caUseLevels);
            using (new EditorGUI.DisabledScope(!caUseLevels))
            {
                caInBlack = EditorGUILayout.Slider(
                    new GUIContent("黑场", "亮度低于此值的像素压成纯黑，向右拉增强暗部"), caInBlack, 0f, 1f);
                caInWhite = EditorGUILayout.Slider(
                    new GUIContent("白场", "亮度高于此值的像素提到纯白，向左拉增强亮部"), caInWhite, 0f, 1f);
            }
            caInvert = EditorGUILayout.Toggle(
                new GUIContent("反相 RGB", "颜色取反（正片变负片）"), caInvert);
            EditorGUI.indentLevel--;
        }

        // —— 染色 ——
        caFoldTint = EditorGUILayout.Foldout(caFoldTint, "染色 Tint（统一色调）", true);
        if (caFoldTint)
        {
            EditorGUI.indentLevel++;
            caUseTint = EditorGUILayout.Toggle(
                new GUIContent("启用染色", "给整张贴图罩一个统一色调（偏火红、偏冰蓝等）"), caUseTint);
            using (new EditorGUI.DisabledScope(!caUseTint))
            {
                caTintMode = EditorGUILayout.Popup(
                    new GUIContent("染色方式", "相乘=保留明暗的染色（最常用）；罩染=趋向纯色；屏幕=提亮式染色"),
                    caTintMode, CaTintNames);
                caTintColor = EditorGUILayout.ColorField(
                    new GUIContent("染色颜色", "要罩上的颜色"), caTintColor);
                caTintStrength = EditorGUILayout.Slider(
                    new GUIContent("染色强度", "0=不染，1=完全应用染色"), caTintStrength, 0f, 1f);
            }
            EditorGUI.indentLevel--;
        }

        // —— 黑底处理 ——
        caFoldBg = EditorGUILayout.Foldout(caFoldBg, "黑底处理（扣黑 / 加黑底）", true);
        if (caFoldBg)
        {
            EditorGUI.indentLevel++;
            caBgMode = EditorGUILayout.Popup(
                new GUIContent("模式", "扣黑=黑底特效图按亮度转成透明；加黑底=把透明区域合成成黑色背景（Additive 粒子常用）"),
                caBgMode, CaBgModeNames);
            if (caBgMode == 1)
            {
                caBgBase = EditorGUILayout.Popup(
                    new GUIContent("亮度依据", "最大通道：彩色特效边缘更完整（推荐）；亮度：按人眼明度，纯色会偏暗"),
                    caBgBase, CaBgBaseNames);
                caBgThreshold = EditorGUILayout.Slider(
                    new GUIContent("黑场阈值", "亮度低于此值视为纯黑背景、完全透明；背景不够纯黑时向右拉"),
                    caBgThreshold, 0f, 1f);
                caBgSoftness = EditorGUILayout.Slider(
                    new GUIContent("边缘柔化", "0=按亮度线性生成透明（推荐柔和辉光特效，完整保留光晕衰减）；硬边缘色块贴图可调大来羽化锯齿边缘"),
                    caBgSoftness, 0f, 1f);
                caBgUnmult = EditorGUILayout.Toggle(
                    new GUIContent("还原纯色边缘", "扣黑后把被黑底压暗的半透明边缘还原成纯色（Unmult 去黑），特效边缘不易发灰"),
                    caBgUnmult);
            }
            else if (caBgMode == 2)
            {
                caBgOpaque = EditorGUILayout.Toggle(
                    new GUIContent("输出不透明", "勾选后整图不透明、透明区填黑（Additive 黑底贴图）；取消则保留 Alpha，仅把 RGB 预乘到黑"),
                    caBgOpaque);
            }
            EditorGUI.indentLevel--;
        }

        // —— Alpha ——
        caFoldAlpha = EditorGUILayout.Foldout(caFoldAlpha, "Alpha 透明通道", true);
        if (caFoldAlpha)
        {
            EditorGUI.indentLevel++;
            caAlphaMul = EditorGUILayout.Slider(
                new GUIContent("Alpha 倍增", "整体透明度倍增，1=不变，<1 更透明、0=全透"), caAlphaMul, 0f, 2f);
            caInvertAlpha = EditorGUILayout.Toggle(
                new GUIContent("反转 Alpha", "透明区域变不透明、不透明变透明（抠反相遮罩用）"), caInvertAlpha);
            EditorGUI.indentLevel--;
        }

        EditorGUILayout.Space(4);
        EditorGUILayout.BeginHorizontal();
        caShowOriginal = GUILayout.Toggle(caShowOriginal,
            new GUIContent("显示原图", "勾选时预览显示未调整的原图，用于前后对比"), "Button");
        if (GUILayout.Button("重置调整", GUILayout.Width(90)))
        {
            ResetColorAdjust();
        }
        if (GUILayout.Button("刷新预览", GUILayout.Width(90)))
        {
            caPreview = null;
        }
        EditorGUILayout.EndHorizontal();

        caOutputName = EditorGUILayout.TextField("导出文件名",
            string.IsNullOrEmpty(caOutputName) && caSource != null ? caSource.name + "_Recolor" : caOutputName);
        DrawVFXExportMode();

        EditorGUILayout.Space(4);
        EditorGUILayout.BeginHorizontal();
        GUI.enabled = caSource != null;
        if (GUILayout.Button("导出 PNG…", GUILayout.Height(26)))
        {
            ExportColorAdjustPng();
        }
        GUI.enabled = true;
        EditorGUILayout.EndHorizontal();

        bool changed = EditorGUI.EndChangeCheck();

        // 实时预览：源或参数变化即重渲染（预览降采样到长边 512，保证拖动流畅）
        if (caSource != null && (changed || caPreview == null))
        {
            BakeColorAdjustPreview();
        }
        else if (caSource == null && caPreview != null)
        {
            DestroyImmediate(caPreview);
            caPreview = null;
        }

        if (caPreview != null)
        {
            EditorGUILayout.Space(6);
            float aspect = (float)caPreview.height / Mathf.Max(1, caPreview.width);
            float w = Mathf.Min(360f, position.width - 60f);
            float h = w * aspect;
            const float maxH = 300f;
            if (h > maxH)
            {
                h = maxH;
                w = h / Mathf.Max(0.0001f, aspect);
            }
            Rect r = GUILayoutUtility.GetRect(w, h, GUILayout.Width(w), GUILayout.Height(h));
            EditorGUI.DrawRect(r, new Color(0f, 0f, 0f, 0.35f));
            // 棋盘底，便于观察透明区域
            DrawChecker(r);
            GUI.DrawTexture(r, caPreview, ScaleMode.ScaleToFit, true);
            string tag = caShowOriginal ? "原图" : "调整结果";
            EditorGUILayout.LabelField(caSource.width + " × " + caSource.height + " RGBA32（预览：" + tag + "）", EditorStyles.miniLabel);
        }
    }

    private void ResetColorAdjust()
    {
        caHue = 0f;
        caSaturation = 1f;
        caValue = 1f;
        caBrightness = 0f;
        caContrast = 1f;
        caGamma = 1f;
        caFilterMode = 0;
        caFilterAmount = 1f;
        caFilterSize = 3f;
        caPosterizeLevels = 5;
        caFilterSeed = 0f;
        caFilterAngle = 0f;
        caFilterCenter = new Vector2(0.5f, 0.5f);
        caDistortFrequency = 8f;
        caDistortRadius = 0.5f;
        caPinch = false;
        caUseLevels = false;
        caInBlack = 0f;
        caInWhite = 1f;
        caInvert = false;
        caUseTint = false;
        caTintColor = Color.white;
        caTintStrength = 0.5f;
        caTintMode = 0;
        caAlphaMul = 1f;
        caInvertAlpha = false;
        caBgMode = 0;
        caBgBase = 0;
        caBgThreshold = 0f;
        caBgSoftness = 0f;
        caBgUnmult = true;
        caBgOpaque = true;
        caShowOriginal = false;
        caPreview = null;
    }

    private static Texture2D caChecker;
    private const int CaCheckerCell = 16; // 每个棋盘格的屏幕像素大小（固定，不随预览缩放变化）

    /// <summary>
    /// 在指定矩形内画棋盘格底，用于观察透明区域。格子按固定屏幕像素重复平铺、点采样保持锐利，
    /// 不能把单张棋盘纹理拉伸铺满（那样格子会被放大成模糊的大块灰雾）。
    /// </summary>
    private static void DrawChecker(Rect r)
    {
        const int n = 16; // 纹理 16×16，内含 2×2 个棋盘格
        if (caChecker == null)
        {
            caChecker = new Texture2D(n, n, TextureFormat.RGBA32, false);
            caChecker.hideFlags = HideFlags.HideAndDontSave;
            Color[] px = new Color[n * n];
            Color light = new Color(0.60f, 0.60f, 0.60f, 1f);
            Color dark = new Color(0.45f, 0.45f, 0.45f, 1f);
            for (int y = 0; y < n; y++)
            {
                for (int x = 0; x < n; x++)
                {
                    bool isDark = ((x / (n / 2)) + (y / (n / 2))) % 2 == 0;
                    px[y * n + x] = isDark ? dark : light;
                }
            }
            caChecker.SetPixels(px);
            caChecker.Apply(false);
            caChecker.wrapMode = TextureWrapMode.Repeat;
            caChecker.filterMode = FilterMode.Point; // 锐利无模糊
        }

        // 每遍纹理横跨 2 个格（2*Cell 屏幕像素），按矩形尺寸算 UV 重复次数，使格子保持固定大小
        float repsX = r.width / (2f * CaCheckerCell);
        float repsY = r.height / (2f * CaCheckerCell);
        GUI.DrawTextureWithTexCoords(r, caChecker, new Rect(0f, 0f, repsX, repsY), true);
    }

    private void BakeColorAdjustPreview()
    {
        if (caSource == null)
        {
            return;
        }

        int w = caSource.width;
        int h = caSource.height;
        int pw = w, ph = h;
        int longSide = Mathf.Max(w, h);
        if (longSide > 512)
        {
            float scale = 512f / longSide;
            pw = Mathf.Max(1, Mathf.RoundToInt(w * scale));
            ph = Mathf.Max(1, Mathf.RoundToInt(h * scale));
        }

        if (caPreview != null)
        {
            DestroyImmediate(caPreview);
            caPreview = null;
        }
        caPreview = RenderColorAdjust(pw, ph);
    }

    private Texture2D RenderColorAdjust(int targetW, int targetH)
    {
        Shader sh = GetColorAdjustShader();
        if (sh == null || !sh.isSupported)
        {
            SetStatus("内部改色 Shader 创建/编译失败，请检查控制台。");
            return null;
        }

        bool srgb = IsSourceSRGB(caSource);
        RenderTexture rt = new RenderTexture(targetW, targetH, 0, RenderTextureFormat.ARGB32,
            srgb ? RenderTextureReadWrite.sRGB : RenderTextureReadWrite.Linear);
        rt.hideFlags = HideFlags.HideAndDontSave;
        rt.Create();

        Material mat = new Material(sh);
        mat.hideFlags = HideFlags.HideAndDontSave;
        Texture2D output;
        RenderTexture prev = RenderTexture.active;
        try
        {
            if (caShowOriginal)
            {
                Graphics.Blit(caSource, rt); // 对比原图
            }
            else
            {
                mat.SetFloat("_HueShift", caHue / 360f);
                mat.SetFloat("_Saturation", caSaturation);
                mat.SetFloat("_Value", caValue);
                mat.SetFloat("_Brightness", caBrightness);
                mat.SetFloat("_Contrast", caContrast);
                mat.SetFloat("_Gamma", Mathf.Max(0.05f, caGamma));
                mat.SetFloat("_FilterMode", caFilterMode);
                mat.SetFloat("_FilterAmount", caFilterAmount);
                mat.SetFloat("_FilterSize", caFilterSize);
                mat.SetFloat("_PosterizeLevels", caPosterizeLevels);
                mat.SetFloat("_FilterSeed", caFilterSeed);
                mat.SetFloat("_FilterAngle", caFilterAngle * Mathf.Deg2Rad);
                mat.SetVector("_FilterCenter", new Vector4(caFilterCenter.x, caFilterCenter.y, 0f, 0f));
                mat.SetFloat("_DistortFrequency", caDistortFrequency);
                mat.SetFloat("_DistortRadius", caDistortRadius);
                mat.SetFloat("_Pinch", caPinch ? 1f : 0f);
                mat.SetFloat("_Invert", caInvert ? 1f : 0f);
                mat.SetFloat("_UseLevels", caUseLevels ? 1f : 0f);
                mat.SetFloat("_InBlack", Mathf.Min(caInBlack, caInWhite - 0.001f));
                mat.SetFloat("_InWhite", caInWhite);
                mat.SetFloat("_UseTint", caUseTint ? 1f : 0f);
                mat.SetColor("_TintColor", caTintColor);
                mat.SetFloat("_TintStrength", caTintStrength);
                mat.SetFloat("_TintMode", caTintMode);
                mat.SetFloat("_AlphaMul", caAlphaMul);
                mat.SetFloat("_InvertAlpha", caInvertAlpha ? 1f : 0f);
                mat.SetFloat("_BGMode", caBgMode);
                mat.SetFloat("_BGBase", caBgBase);
                mat.SetFloat("_BGThreshold", caBgThreshold);
                mat.SetFloat("_BGSoftness", caBgSoftness);
                mat.SetFloat("_BGUnmult", caBgUnmult ? 1f : 0f);
                mat.SetFloat("_BGOpaque", caBgOpaque ? 1f : 0f);
                Graphics.Blit(caSource, rt, mat);
            }

            RenderTexture.active = rt;
            output = new Texture2D(targetW, targetH, TextureFormat.RGBA32, false);
            output.ReadPixels(new Rect(0, 0, targetW, targetH), 0, 0);
            output.Apply(false);
        }
        finally
        {
            RenderTexture.active = prev;
            rt.Release();
            Object.DestroyImmediate(rt);
            Object.DestroyImmediate(mat);
        }
        return output;
    }

    private void ExportColorAdjustPng()
    {
        if (caSource == null)
        {
            SetStatus("请先指定要改色的源贴图。");
            return;
        }

        string fileName = string.IsNullOrEmpty(caOutputName)
            ? (caSource.name + "_Recolor")
            : caOutputName;
        string absPath = ResolveVFXExportPath("导出改色 PNG", fileName, "png",
            AssetDatabase.GetAssetPath(caSource), true);
        if (string.IsNullOrEmpty(absPath))
        {
            return;
        }

        Texture2D tex = RenderColorAdjust(caSource.width, caSource.height);
        if (tex == null)
        {
            return;
        }
        byte[] png = tex.EncodeToPNG();
        DestroyImmediate(tex);
        if (png == null || png.Length == 0)
        {
            SetStatus("导出失败：PNG 编码为空。");
            return;
        }

        File.WriteAllBytes(absPath, png);
        AssetDatabase.Refresh();

        string rel = ToAssetsRelative(absPath);
        if (rel != null)
        {
            string srcPath = AssetDatabase.GetAssetPath(caSource);
            TextureImporter srcImporter = AssetImporter.GetAtPath(srcPath) as TextureImporter;
            TextureImporter importer = AssetImporter.GetAtPath(rel) as TextureImporter;
            if (importer != null)
            {
                importer.textureType = srcImporter != null ? srcImporter.textureType : TextureImporterType.Default;
                importer.sRGBTexture = IsSourceSRGB(caSource);
                importer.mipmapEnabled = srcImporter != null && srcImporter.mipmapEnabled;
                importer.wrapMode = srcImporter != null ? srcImporter.wrapMode : TextureWrapMode.Clamp;
                importer.filterMode = srcImporter != null ? srcImporter.filterMode : FilterMode.Bilinear;
                if (srcImporter != null)
                {
                    importer.alphaSource = srcImporter.alphaSource;
                    importer.alphaIsTransparency = srcImporter.alphaIsTransparency;
                }
                else
                {
                    importer.alphaSource = TextureImporterAlphaSource.FromInput;
                }
                importer.SaveAndReimport();
            }
            Texture2D loaded = AssetDatabase.LoadAssetAtPath<Texture2D>(rel);
            if (loaded != null)
            {
                EditorGUIUtility.PingObject(loaded);
            }
            SetStatus("改色贴图已导出：" + rel);
        }
        else
        {
            SetStatus("已导出到项目外（未导入 Unity）：" + absPath);
        }
    }
}
