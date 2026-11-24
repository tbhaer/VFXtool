// VFX 工具箱 - 噪波生成器 Shader（多层分形噪波）
Shader "Hidden/VFXToolboxNoiseGenerator"
{
    Properties
    {
        _MainTex ("Dummy", 2D) = "white" {}
    }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"

            #define NZ_MAX_LAYERS 8

            int _LayerCount;
            float _LFractal[NZ_MAX_LAYERS];
            float _LInterp[NZ_MAX_LAYERS];
            float _LStrength[NZ_MAX_LAYERS];
            float _LContrast[NZ_MAX_LAYERS];
            float _LSeed[NZ_MAX_LAYERS];
            float _LFrequencyX[NZ_MAX_LAYERS]; // U 方向频率（一张图内横向周期数）
            float _LFrequencyY[NZ_MAX_LAYERS]; // V 方向频率
            float _LOctaves[NZ_MAX_LAYERS];
            float _LPersistence[NZ_MAX_LAYERS];
            float _LInvert[NZ_MAX_LAYERS];
            float _LTile[NZ_MAX_LAYERS];
            float _LBlend[NZ_MAX_LAYERS];
            float _LAngle[NZ_MAX_LAYERS];
            float _LScrollX[NZ_MAX_LAYERS]; // UV 静态偏移（圈；1 = 平移一整圈回到原位）
            float _LScrollY[NZ_MAX_LAYERS];
            float _LType[NZ_MAX_LAYERS];    // 0 = 噪声层，1 = 贴图层
            float _LChannel[NZ_MAX_LAYERS]; // 贴图取值：0 亮度 1 R 2 G 3 B 4 A
            float _LTilingX[NZ_MAX_LAYERS]; // 贴图 Tiling X
            float _LTilingY[NZ_MAX_LAYERS];
            float _LBright[NZ_MAX_LAYERS];  // 亮度偏移
            float _LGamma[NZ_MAX_LAYERS];   // Gamma 中间调
            float _LBlack[NZ_MAX_LAYERS];   // 色阶黑场
            float _LWhite[NZ_MAX_LAYERS];   // 色阶白场
            float _LThreshold[NZ_MAX_LAYERS];   // 二值化阈值（0 = 不启用）
            float _LThresholdSoft[NZ_MAX_LAYERS]; // 阈值边缘软化宽度

            // 贴图层纹理（未使用的槽位由 C# 绑定 WhiteTexture）
            sampler2D _LTex0;
            sampler2D _LTex1;
            sampler2D _LTex2;
            sampler2D _LTex3;
            sampler2D _LTex4;
            sampler2D _LTex5;
            sampler2D _LTex6;
            sampler2D _LTex7;

            float _NzContrast;
            float _NzBrightness;
            float _NzOverflow;
            float _NzInvert;
            float _NzAlpha;
            float2 _NzSize; // 输出像素尺寸（白噪声按像素随机用）

            float hash21(int2 p, float seed)
            {
                float n = dot(float2(p), float2(127.1, 311.7)) + seed * 74.7;
                return frac(sin(n) * 43758.5453123);
            }

            // 环面包裹：X/Y 周期可以不同（非等比频率）
            int2 wrapCell(int2 c, int2 period)
            {
                return ((c % period) + period) % period;
            }

            // 周期值噪波，interp 选择晶格插值方式（AE Noise Type）
            float vnoise(float2 p, int2 period, float seed, int interp)
            {
                int2 i = int2(floor(p));
                float2 f = frac(p);
                float2 u;
                if (interp == 0)           u = floor(f + 0.5);                 // Block
                else if (interp == 1)      u = f;                              // Linear
                else if (interp == 2)      u = f * f * (3.0 - 2.0 * f);        // Soft Linear
                else                       u = f * f * f * (f * (f * 6.0 - 15.0) + 10.0); // Spline

                int2 i00 = wrapCell(i, period);
                int2 i10 = wrapCell(i + int2(1, 0), period);
                int2 i01 = wrapCell(i + int2(0, 1), period);
                int2 i11 = wrapCell(i + int2(1, 1), period);
                float a = hash21(i00, seed);
                float b = hash21(i10, seed);
                float c = hash21(i01, seed);
                float d = hash21(i11, seed);
                float x1 = lerp(a, b, u.x);
                float x2 = lerp(c, d, u.x);
                return lerp(x1, x2, u.y) * 2.0 - 1.0;
            }

            float2 grad2(int2 p, float seed)
            {
                float h = hash21(p, seed);
                float ang = h * 6.28318530718;
                return float2(cos(ang), sin(ang));
            }

            // 周期经典 Perlin 噪波
            float pnoise(float2 p, int2 period, float seed)
            {
                int2 i = int2(floor(p));
                float2 f = frac(p);
                float2 u = f * f * f * (f * (f * 6.0 - 15.0) + 10.0);
                int2 i00 = wrapCell(i, period);
                int2 i10 = wrapCell(i + int2(1, 0), period);
                int2 i01 = wrapCell(i + int2(0, 1), period);
                int2 i11 = wrapCell(i + int2(1, 1), period);
                float n00 = dot(grad2(i00, seed), f);
                float n10 = dot(grad2(i10, seed), f - float2(1, 0));
                float n01 = dot(grad2(i01, seed), f - float2(0, 1));
                float n11 = dot(grad2(i11, seed), f - float2(1, 1));
                float x1 = lerp(n00, n10, u.x);
                float x2 = lerp(n01, n11, u.x);
                return lerp(x1, x2, u.y) * 1.5;
            }

            // 2D Simplex（Ashima / Gustavson 实现），比 Perlin 更细腻、无网格方向性
            float2 mod289(float2 x) { return x - floor(x * (1.0 / 289.0)) * 289.0; }
            float3 mod289(float3 x) { return x - floor(x * (1.0 / 289.0)) * 289.0; }
            float3 permute(float3 x) { return mod289(((x * 34.0) + 1.0) * x); }

            float snoise(float2 v)
            {
                const float4 C = float4(0.211324865405187, 0.366025403784439,
                                        -0.577350269189626, 0.024390243902439);
                float2 i = floor(v + dot(v, C.yy));
                float2 x0 = v - i + dot(i, C.xx);
                float2 i1 = (x0.x > x0.y) ? float2(1.0, 0.0) : float2(0.0, 1.0);
                float4 x12 = x0.xyxy + C.xxzz;
                x12.xy -= i1;
                i = mod289(i);
                float3 p = permute(permute(i.y + float3(0.0, i1.y, 1.0))
                                 + i.x + float3(0.0, i1.x, 1.0));
                float3 m = max(0.5 - float3(dot(x0, x0), dot(x12.xy, x12.xy), dot(x12.zw, x12.zw)), 0.0);
                m = m * m;
                m = m * m;
                float3 x = 2.0 * frac(p * C.www) - 1.0;
                float3 h = abs(x) - 0.5;
                float3 ox = floor(x + 0.5);
                float3 a0 = x - ox;
                m *= 1.79284291400159 - 0.85373472095314 * (a0 * a0 + h * h);
                float3 g;
                g.x = a0.x * x0.x + h.x * x0.y;
                g.yz = a0.yz * x12.xz + h.yz * x12.yw;
                return 130.0 * dot(m, g);
            }

            // 周期 Voronoi（Worley）：mode 0 = F1（亮心暗边），1 = F2-F1（裂纹），2 = 圆点
            // jitter：特征点在晶格内的抖动幅度，1.0 = 可落在晶格内任意位置（偏移 ±0.5）
            float voronoi(float2 p, int2 period, float seed, float jitter, int mode)
            {
                int2 ip = int2(floor(p));
                float2 fp = frac(p);
                float f1 = 1e9;
                float f2 = 1e9;
                // 特征点偏移最大 ±jitter/2（jitter=1 时 ±0.5），3×3 邻域并不充分：当本格点很近、
                // 周围点又被随机推到远端时，真正的 F2（罕见时连 F1）会落在 3×3 之外，漏掉后整格
                // 细胞边界算错，在晶格线上出现规则方形硬接缝。枚举 ±2 共 5×5 个影像后，窗外点
                // 距离至少 1.5，严格保证 F1/F2 都在范围内。
                // 环面周期 P<5 时同一个物理细胞会在 ±2 影像窗里出现多个“影像”，只保留最近影像：
                // |偏移| <= P/2 才参与（等价环面最短距离 min(c,P-c)）；偶数周期的对径细胞其 ±P/2
                // 两个影像等距，两个都保留。非等比频率时 X/Y 各自过滤。
                // 细胞坐标用边界 ±P 修正而不是整数取模（避免负数取模在小周期/旧编译器上的隐患），
                // 因 |偏移|<=2、ip∈[0,P-1]，一次修正即可落回 [0,P-1]。固定 5×5 上界兼容老编译器。
                // 用非负循环计数 ky/kx（有符号偏移 = k-2），小周期用 if 跳过多余影像；
                // 不用负循环初值/continue，兼容 ps_3.0 的循环与整数模拟
                // 固定枚举 ±2 共 5×5=25 个环面影像，全部参与 F1/F2 比较，不做“最近影像”过滤：
                // 特征点在晶格内有 ±0.5 抖动、会越过格线；周期较小(P<=4)时同一物理细胞会从环面
                // 两侧出现(如 P=3 时同时有 +1 与 -2 两个影像)，漏掉对侧影像会在边界算错距离产生接缝。
                // 全部影像参与、取最近，结果即环面真值(已逐像素验证)。细胞坐标用边界 ±P 修正而非取模，
                // 避开负数取模在小周期/旧编译器的隐患；非负循环计数 ky/kx 兼容 ps_3.0。
                for (int ky = 0; ky < 5; ky++)
                {
                    int oy = ky - 2;
                    int cy = ip.y + oy;
                    cy = cy < 0 ? cy + period.y : cy;
                    cy = cy >= period.y ? cy - period.y : cy;
                    for (int kx = 0; kx < 5; kx++)
                    {
                        int ox = kx - 2;
                        int cx = ip.x + ox;
                        cx = cx < 0 ? cx + period.x : cx;
                        cx = cx >= period.x ? cx - period.x : cx;
                        int2 cell = int2(cx, cy);
                        float2 rnd = float2(hash21(cell, seed), hash21(cell, seed + 3.7));
                        float2 pt = float2(ox, oy) + (rnd - 0.5) * jitter;
                        float d2 = dot(pt - fp, pt - fp);
                        if (d2 < f1) { f2 = f1; f1 = d2; }
                        else if (d2 < f2) { f2 = d2; }
                    }
                }
                float d1 = sqrt(f1);
                if (mode == 0)
                {
                    return saturate(1.0 - d1 * 1.35) * 2.0 - 1.0;
                }
                if (mode == 2)
                {
                    float disc = 1.0 - smoothstep(0.26, 0.34, d1);
                    return disc * 2.0 - 1.0;
                }
                float edge = sqrt(f2) - d1;
                return saturate(edge * 1.7) * 2.0 - 1.0;
            }

            float2 rot2(float2 p, float angle)
            {
                float ca = cos(angle);
                float sa = sin(angle);
                return mul(float2x2(ca, -sa, sa, ca), p);
            }

            // 每层的后处理：对比度 → 色阶黑白场 → Gamma → 亮度偏移 → 阈值化 → 反相，输入输出均为 0..1
            float applyLayer(float v01, int L)
            {
                // 对比度（围绕中灰 0.5 缩放）
                v01 = (v01 - 0.5) * _LContrast[L] + 0.5;
                // PS 色阶：输入黑场/白场重映射到 0..1
                float range = _LWhite[L] - _LBlack[L];
                v01 = saturate((v01 - _LBlack[L]) / max(range, 0.001));
                // Gamma 中间调（1 = 不变；<1 提亮，>1 压暗）
                float g = _LGamma[L];
                if (abs(g - 1.0) > 0.001) v01 = pow(v01, g);
                // 亮度偏移
                v01 += _LBright[L];
                // 阈值化：threshold > 0 时在阈值处二值化，soft 控制过渡带宽度（0 = 硬边）
                float th = _LThreshold[L];
                if (th > 0.0001)
                {
                    float soft = max(_LThresholdSoft[L], 0.0);
                    v01 = smoothstep(th - soft, th + soft, v01);
                }
                if (_LInvert[L] > 0.5) v01 = 1.0 - v01;
                return v01;
            }

            // 按通道选择从贴图颜色取灰度：0 亮度 1 R 2 G 3 B 4 A
            float textureGray(float4 c, int channel)
            {
                if (channel == 1) return c.r;
                if (channel == 2) return c.g;
                if (channel == 3) return c.b;
                if (channel == 4) return c.a;
                return dot(c.rgb, float3(0.299, 0.587, 0.114));
            }

            // 采样第 L 层绑定的贴图（给默认初值，避免编译器误判未初始化）
            float4 sampleLayerTexture(int L, float2 uv)
            {
                float4 c = tex2D(_LTex7, uv);
                if (L == 0) c = tex2D(_LTex0, uv);
                else if (L == 1) c = tex2D(_LTex1, uv);
                else if (L == 2) c = tex2D(_LTex2, uv);
                else if (L == 3) c = tex2D(_LTex3, uv);
                else if (L == 4) c = tex2D(_LTex4, uv);
                else if (L == 5) c = tex2D(_LTex5, uv);
                else if (L == 6) c = tex2D(_LTex6, uv);
                return c;
            }

            // 单层噪波，返回 0..1
            float evalLayer(int L, float2 uv)
            {
                float tile = _LTile[L];
                // 无缝模式：X/Y 频率分别严格取整，子层频率固定 2 倍，周期为整数晶格，保证首尾严格相接
                float Sx = tile > 0.5 ? floor(_LFrequencyX[L] + 0.5) : _LFrequencyX[L];
                float Sy = tile > 0.5 ? floor(_LFrequencyY[L] + 0.5) : _LFrequencyY[L];
                int octaves = (int)_LOctaves[L];
                int fractal = (int)_LFractal[L];
                int interp = fractal == 2 ? 0 : (int)_LInterp[L];

                // UV 静态偏移，单位为“圈”（周期为 1 的相位平移）；frac 折回 [0,1) 保证循环。
                float2 nzPhase = float2(_LScrollX[L], _LScrollY[L]);

                // 贴图层：按 Tiling 平铺采样外部贴图（贴图自身需可循环平铺才能四边相接）
                if (_LType[L] > 0.5)
                {
                    float2 t = uv * float2(_LTilingX[L], _LTilingY[L]) + nzPhase;
                    float4 c = sampleLayerTexture(L, frac(t));
                    return applyLayer(textureGray(c, (int)_LChannel[L]), L);
                }

                uv = frac(uv + nzPhase);

                // 白噪声（14）：按输出像素完全随机（按尺寸取模保证可无缝）
                if (fractal == 14)
                {
                    int2 sz = int2(_NzSize);
                    int2 pc = int2(uv * _NzSize);
                    pc = ((pc % sz) + sz) % sz;
                    return applyLayer(hash21(pc, _LSeed[L]), L);
                }

                // 棋盘方格（16）：整数晶格的二值棋盘；环面二染色要求每圈格数为偶数，无缝时取偶
                if (fractal == 16)
                {
                    // 环面二染色要求 X、Y 两个方向的格数都为偶数
                    float Scx = tile > 0.5 ? Sx - fmod(Sx, 2.0) : Sx;
                    float Scy = tile > 0.5 ? Sy - fmod(Sy, 2.0) : Sy;
                    float2 ci = floor(uv * float2(Scx, Scy));
                    float chk = fmod(ci.x + ci.y, 2.0);
                    return applyLayer(chk, L);
                }

                // 条纹 Gabor（17）：定向正弦条纹 + 噪声扭曲，木纹/拉丝/水流
                if (fractal == 17)
                {
                    float ang = _LAngle[L] * 0.01745329;
                    float2 dir = float2(cos(ang), sin(ang));
                    float2 p = uv * float2(Sx, Sy);
                    float w = 0.0;
                    if (tile > 0.5)
                    {
                        // warp 场独立整数频率（X/Y 分别取），保证扭曲部分周期（条纹本体仅 0°/90° 等轴向无缝）
                        int2 wf = int2(max(1, (int)floor(Sx * 0.5 + 0.5)),
                                       max(1, (int)floor(Sy * 0.5 + 0.5)));
                        w = vnoise(uv * float2(wf.x, wf.y) + float2(5.3, -2.7), wf, _LSeed[L], 3);
                    }
                    else
                    {
                        w = vnoise(p * 0.5 + float2(5.3, -2.7), int2(100000, 100000), _LSeed[L], 3);
                    }
                    float2 q = p + float2(w, -w) * 0.35;
                    float bands = sin(dot(q, dir) * 6.2831853) * 0.5 + 0.5;
                    return applyLayer(bands, L);
                }

                // 圆点（18）：Worley 特征点处画均匀圆点，气泡/鳞甲/水珠
                if (fractal == 18)
                {
                    int2 periodD = tile > 0.5 ? int2((int)Sx, (int)Sy) : int2(100000, 100000);
                    float n = voronoi(uv * float2(Sx, Sy), periodD, _LSeed[L], 1.0, 2);
                    return applyLayer(n * 0.5 + 0.5, L);
                }

                float2 base = uv * float2(Sx, Sy);

                // 域扭曲 Domain Warp（12）：用粗尺度噪波两次扭曲采样域，再做常规分形
                if (fractal == 12)
                {
                    float lseed = _LSeed[L];
                    float2 w = 0.0;
                    if (tile > 0.5)
                    {
                        // 无缝：warp 场用独立的整数频率 wf（≈S/2，X/Y 分别取）并按 wf 包裹，
                        // warp 场自身严格周期，扭曲后的采样域仍环面连续
                        int2 wf = int2(max(1, (int)floor(Sx * 0.5 + 0.5)),
                                       max(1, (int)floor(Sy * 0.5 + 0.5)));
                        float2 wp = uv * float2(wf.x, wf.y);
                        w.x = vnoise(wp + float2(13.2, 7.1), wf, lseed + 3.1, interp);
                        w.y = vnoise(wp + float2(-5.7, 11.9), wf, lseed + 7.3, interp);
                        base += w * 1.5;
                        w.x = vnoise(wp + float2(21.4, -9.3), wf, lseed + 11.9, interp);
                        w.y = vnoise(wp + float2(-13.1, 3.7), wf, lseed + 17.5, interp);
                        base += w * 0.75;
                    }
                    else
                    {
                        w.x = vnoise(base * 0.5 + float2(13.2, 7.1), int2(100000, 100000), lseed + 3.1, interp);
                        w.y = vnoise(base * 0.5 + float2(-5.7, 11.9), int2(100000, 100000), lseed + 7.3, interp);
                        base += w * 1.5;
                        w.x = vnoise(base * 0.5 + float2(21.4, -9.3), int2(100000, 100000), lseed + 11.9, interp);
                        w.y = vnoise(base * 0.5 + float2(-13.1, 3.7), int2(100000, 100000), lseed + 17.5, interp);
                        base += w * 0.75;
                    }
                }

                float amp = 1.0;
                float freq = 1.0;
                float sum = 0.0;
                float norm = 0.0;
                float maxV = -1.0;
                float2 warpOffset = 0.0;

                // 动态循环（最多 8 次），不要全展开：配合外层 8 个噪声层，全展开会生成上千个
                // 重复分支体（含 sin/cos 与 Voronoi 9 格搜索），直接撑爆 fxc 编译器进程
                for (int o = 0; o < 8; o++)
                {
                    if (o >= octaves) break;
                    // 无缝时 X/Y 周期 = S*2^o（整数）；非无缝给一个足够大的周期
                    int2 period = tile > 0.5
                        ? int2((int)(Sx * freq), (int)(Sy * freq))
                        : int2(100000, 100000);
                    float2 sp = base * freq;
                    float seed = _LSeed[L] + o * 17.17;

                    // 动态/动态递进：上层采样位置被前一层结果推移
                    if (fractal == 3 || fractal == 4) sp += warpOffset;
                    // 涂抹：每层固定方向错位
                    if (fractal == 5) sp += float2(0.42 * o, -0.31 * o);
                    // 漩涡：每层绕中心旋转
                    if (fractal == 7)
                    {
                        float2 c = 0.5 * float2(Sx, Sy) * freq;
                        sp = rot2(sp - c, o * 0.5) + c;
                    }

                    float n = 0.0;
                    if (fractal == 9)        n = voronoi(sp, period, seed, 1.0, 0); // Worley F1
                    else if (fractal == 11)  n = voronoi(sp, period, seed, 1.0, 1); // Worley F2-F1
                    else if (fractal == 10)  n = pnoise(sp, period, seed);           // Perlin
                    else if (fractal == 13)  n = snoise(sp);                         // Simplex（不做周期包裹）
                    else if (fractal == 15)  n = abs(pnoise(sp, period, seed)) * 2.0 - 1.0; // Billow
                    else                     n = vnoise(sp, period, seed, interp);   // Value 系列

                    if (fractal == 1 || fractal == 2)
                    {
                        n = abs(n) * 2.0 - 1.0;
                        sum += n * amp;
                        norm += amp;
                    }
                    else if (fractal == 8)
                    {
                        float r = 1.0 - abs(n);
                        r *= r;
                        sum += (r * 2.0 - 1.0) * amp;
                        norm += amp;
                    }
                    else if (fractal == 6)
                    {
                        maxV = max(maxV, n);
                    }
                    else
                    {
                        sum += n * amp;
                        norm += amp;
                    }

                    if (fractal == 3)      warpOffset += float2(n, -n) * 0.35;
                    else if (fractal == 4) warpOffset += float2(n, -n) * (0.12 * (o + 1));

                    amp *= _LPersistence[L];
                    freq *= 2.0;
                }

                float v = fractal == 6 ? maxV : (sum / max(norm, 0.0001));
                return applyLayer(v * 0.5 + 0.5, L);
            }

            fixed4 frag(v2f_img i) : SV_Target
            {
                float acc = 0.0;
                for (int L = 0; L < NZ_MAX_LAYERS; L++)
                {
                    if (L >= _LayerCount) break;
                    float v = evalLayer(L, i.uv);
                    float s = _LStrength[L];
                    if (L == 0)
                    {
                        // 底层强度：0 = 中性灰 0.5，1 = 完整噪声
                        acc = lerp(0.5, v, s);
                    }
                    else
                    {
                        // 混合模式（0..3 与旧版数学等价，兼容已序列化参数）；强度 s 统一为
                        // “从原结果到混合结果”的插值量，s=0 该层不起作用，s=1 完全混合
                        int blend = (int)_LBlend[L];
                        float a = acc;
                        float b = v;
                        float mixed;
                        if (blend == 0)      mixed = a + b;                                   // 相加
                        else if (blend == 1) mixed = a * b;                                   // 相乘
                        else if (blend == 2) mixed = max(a, b);                               // 取大（变亮）
                        else if (blend == 3) mixed = min(a, b);                               // 取小（变暗）
                        else if (blend == 4) mixed = 1.0 - (1.0 - a) * (1.0 - b);             // 屏幕：柔和提亮
                        else if (blend == 5) mixed = a < 0.5 ? 2.0 * a * b
                                                           : 1.0 - 2.0 * (1.0 - a) * (1.0 - b); // 叠加：对比融合
                        else if (blend == 6) mixed = a + (2.0 * b - 1.0) * a * (1.0 - a);     // 柔光：温和对比
                        else if (blend == 7) mixed = a - b;                                   // 减去：压暗侵蚀
                        else if (blend == 8) mixed = abs(a - b);                              // 差值
                        else if (blend == 9) mixed = a + b - 2.0 * a * b;                     // 排除：柔和差值
                        else                 mixed = (a + b) * 0.5;                           // 平均
                        acc = lerp(a, mixed, s);
                    }
                }

                float v = (acc - 0.5) * _NzContrast + 0.5 + _NzBrightness;
                if (_NzInvert > 0.5) v = 1.0 - v;

                if (_NzOverflow < 0.5)
                {
                    v = saturate(v);
                }
                else if (_NzOverflow < 1.5)
                {
                    v = saturate(v);
                    v = v * v * (3.0 - 2.0 * v);
                }
                else
                {
                    v = frac(v);
                }

                fixed a = _NzAlpha > 0.5 ? (fixed)v : 1.0;
                return fixed4(v, v, v, a);
            }
            ENDCG
        }
    }
}
