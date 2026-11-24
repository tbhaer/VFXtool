using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

/// <summary>
/// 模块十二：噪波生成（参考 AE Fractal Noise + 多层噪声叠加工具）。
/// 支持多个噪声层，每层独立参数，按 相加/相乘/取大/取小 叠加；GPU 实时预览，导出 PNG。
/// Shader 为项目内资源 VFXNoiseGenerator.shader，Unity 导入时编译，打开标签不卡顿。
/// </summary>
public partial class VFXToolboxWindow
{
    private const int NzMaxLayers = 8;
    private const string NoiseShaderPath = "Assets/Editor/VFXToolbox/VFXNoiseGenerator.shader";

    private static readonly string[] NzFractalNames =
    {
        "基础 Basic（Value）", "湍流平滑 Turbulent Smooth", "湍流基础 Turbulent Basic",
        "动态 Dynamic", "动态递进 Dynamic Progressive", "涂抹 Smeary",
        "最大 Max", "漩涡 Swirly", "岩石 Rocky（Ridged）", "细胞 Voronoi（Worley F1）",
        "Perlin", "细胞边界 Worley F2-F1", "域扭曲 Domain Warp",
        "Simplex", "白噪声 White Noise", "棉团云 Billow",
        "棋盘方格 Checker", "条纹 Gabor", "圆点 Dot Field"
    };

    // 每种噪声的效果说明与推荐参数（索引 = 内部类型 id，与 NzFractalNames 对应）
    private static readonly string[] NzFractalTips =
    {
        "基础 Value 分形：柔和团块状云絮。推荐 Octaves 4~5、Persistence 0.5，插值选“样条”最柔。适合云雾、柔光遮罩、噪声打底。【可无缝循环】",
        "湍流平滑：Value 取绝对值，絮状烟雾/水流纹理。推荐 Octaves 5~6、对比度 1.2~1.5，是溶解遮罩最常用的类型。【可无缝循环】",
        "湍流基础：方块插值取绝对值，尖锐的晶格褶皱。适合碎玻璃、干裂土地、粗糙岩石底面。【可无缝循环】",
        "动态 Dynamic：采样位置被上层结果推移，有拉丝流动感；推移场本身周期，仍可无缝。适合水流、光流、流动岩浆。【可无缝循环】",
        "动态递进：逐层加大域偏移，扭曲比 Dynamic 更强。适合翻滚浓烟、漩涡气流。【可无缝循环】",
        "涂抹 Smeary：每层固定方向错位（常数偏移不破坏周期），产生风向感。适合拉丝、速度线、定向沙尘。【可无缝循环】",
        "最大 Max：各层取最亮值，颗粒分明。适合星点、光斑、水珠/气泡分布。【可无缝循环】",
        "漩涡 Swirly：每层绕纹理中心旋转，旋转破坏环面周期，四边无法相接。适合漩涡、星云、卷云。【不可无缝循环】",
        "岩石 Ridged：山脊状锐利褶皱。适合山脉、裂纹、闪电、坚硬表面。【可无缝循环】",
        "Worley F1：亮心暗边的细胞块。Octaves 设 1 最干净，适合鳞片、碎石地块、细胞组织。【可无缝循环】",
        "Perlin：经典梯度噪声，比 Value 更自然、没有方块感。云、雾、顶点扰动、UV 扰动的通用首选。【可无缝循环】",
        "Worley F2-F1：只保留细胞边界，Octaves 设 1 就是干净的龟裂纹；勾“反相”得到亮线裂缝，可做熔岩裂纹发光底。【可无缝循环】",
        "域扭曲 Domain Warp：两次粗噪声扭曲采样域，烟、云、能量流、流体效果最常用；warp 场已周期化，可无缝。【可无缝循环】",
        "Simplex：比 Perlin 更细腻、没有方向性网格伪影。适合高端云层、柔雾、粒子流线扰动；该算法不做环面周期化。【不可无缝循环】",
        "白噪声：像素级完全随机颗粒。适合胶片颗粒、火花闪烁、静电、抖动贴图；请把 Octaves 设为 1。【可无缝循环】",
        "棉团云 Billow：Perlin 取绝对值，圆滚滚的棉团状云朵。适合爆炸浓烟团块、卡通云、泡沫。【可无缝循环】",
        "棋盘方格：二值方块棋盘，硬边块状遮罩；环面二染色要求每圈格数为偶数，无缝时频率自动取偶。配合阈值软化可得到软边方块。【可无缝循环】",
        "条纹 Gabor：定向正弦条纹加噪声扭曲，木纹、拉丝、水流、指纹。用“条纹角度”控制方向；仅 0°/90° 等轴向条纹可循环，任意角度四边无法相接。【任意角度不可无缝循环】",
        "圆点 Dot Field：在均匀分布的特征点上画圆点，气泡、水珠、鳞甲、铆钉。Octaves 设 1；反相得到黑点阵列，频率控制圆点数量。【可无缝循环】"
    };

    // 下拉显示顺序：可无缝循环的类型在前，不可循环的在最后；内部 id 保持不变以兼容已序列化参数
    private static readonly int[] NzFractalOrder =
    {
        0, 10, 1, 2, 8, 15, 6, 3, 4, 5, 9, 11, 18, 16, 14, 12,
        7, 13, 17
    };

    // 数学上无法做到环面四边相接的类型
    private static bool IsFractalNonTileable(int id)
    {
        return id == 7 || id == 13 || id == 17;
    }

    private static readonly string[] NzFractalDisplayNames = BuildFractalDisplayNames();

    private static string[] BuildFractalDisplayNames()
    {
        string[] names = new string[NzFractalOrder.Length];
        for (int i = 0; i < NzFractalOrder.Length; i++)
        {
            int id = NzFractalOrder[i];
            names[i] = IsFractalNonTileable(id) ? "〔不循环〕" + NzFractalNames[id] : NzFractalNames[id];
        }
        return names;
    }

    private static readonly string[] NzInterpNames =
    { "方块 Block", "线性 Linear", "柔线 Soft Linear", "样条 Spline" };

    private static readonly string[] NzBlendNames =
    {
        "相加 Add", "相乘 Multiply", "取大 Max（变亮）", "取小 Min（变暗）",
        "屏幕 Screen", "叠加 Overlay", "柔光 Soft Light", "减去 Subtract",
        "差值 Difference", "排除 Exclusion", "平均 Average"
    };

    // 每种叠加方式的说明（索引与 NzBlendNames 对应），选择后直接显示在下拉框下方
    private static readonly string[] NzBlendTips =
    {
        "相加 Add：两层灰度直接相加，越叠越亮。最常用的云层/光效堆积方式，容易过曝，可用层强度或溢出处理控制。",
        "相乘 Multiply：灰度相乘，越叠越暗。白色不改变下层，适合压暗、脏迹、暗纹与遮罩镂空。",
        "取大 Max（变亮）：逐像素保留更亮的值。适合星点、光斑、水珠、零散高光叠加，不会整体抬亮。",
        "取小 Min（变暗）：逐像素保留更暗的值。适合裂缝、孔洞、零散暗斑的叠加。",
        "屏幕 Screen：1-(1-本层)(1-下层)，像两台投影仪叠加，提亮但比相加柔和、不易过曝。雾、光、烟推荐。",
        "叠加 Overlay：暗处相乘、亮处屏幕，同时增强明暗对比，把纹理“压”进下层，细节融合最常用。",
        "柔光 Soft Light：类似叠加但更温和，本层 50% 灰处不变。适合轻微加肌理，不破坏大关系。",
        "减去 Subtract：下层减去本层，越叠越暗。适合侵蚀、挖空、反向遮罩、做暗沟。",
        "差值 Difference：|下层-本层|，相同处变黑、差异处变亮。可做龟裂纹理、金属拉丝、特殊过渡。",
        "排除 Exclusion：与差值类似但对比更柔和、中间调偏灰，质感更温润。",
        "平均 Average：两层灰度取均值，等量中和。多层纹理等权混合、降低单层突兀感时使用。"
    };

    private static readonly string[] NzLayerTypeNames = { "噪声层", "贴图层" };
    private static readonly string[] NzChannelNames =
    {
        "亮度（灰度）", "R 红通道", "G 绿通道", "B 蓝通道", "A 透明通道"
    };
    private static readonly string[] NzOverflowNames = { "裁剪（Clip）", "柔化（Soft Clamp）", "回绕（Wrap）" };
    private static readonly int[] NzSizePresets = { 128, 256, 512, 1024, 2048 };

    [Serializable]
    public class NoiseLayerParams
    {
        public int layerType;          // 0 = 噪声层，1 = 贴图层
        public int fractal = 1;        // 分形类型（内部 id）
        public int interp = 3;         // 晶格插值
        public int blend = 0;          // 叠加方式（第 0 层忽略）
        public float strength = 1f;    // 强度
        public float contrast = 1f;    // 对比度
        public float seed = 1f;        // Seed
        public float frequency = 6f;   // 旧单轴频率，仅用于一次性迁移到 X/Y
        public float frequencyX = 6f;  // U 方向频率（横向周期数）
        public float frequencyY = 6f;  // V 方向频率（纵向周期数）
        public int freqVersion;        // 参数版本：0 = 需要从旧 frequency 迁移
        public int octaves = 5;        // Octaves
        public float persistence = 0.55f; // Persistence（子影响）
        public bool invert = false;    // 反相
        public bool tile = true;       // 无缝循环（贴图层语义：贴图可循环平铺）
        public float angle;            // 条纹角度（仅 Gabor 条纹使用）
        public float scrollX;          // UV 静态偏移 X（圈；1 平移一整圈）
        public float scrollY;          // UV 静态偏移 Y（圈）

        // 贴图层专用
        public Texture2D texture;      // 源贴图
        public int channel;            // 取值通道：0 亮度 1 R 2 G 3 B 4 A
        public float tilingX = 1f;     // Tiling X
        public float tilingY = 1f;     // Tiling Y

        // 公共色阶调整
        public float brightness;       // 亮度偏移
        public float gamma = 1f;       // Gamma 中间调
        public float blackLevel;       // 色阶黑场
        public float whiteLevel = 1f;  // 色阶白场
        public float threshold;        // 二值化阈值（0 = 不启用）
        public float thresholdSoft = 0.02f; // 阈值边缘软化
    }

    private enum NoiseOverflow
    {
        Clip = 0,      // 裁剪
        SoftClamp = 1, // 柔化（Soft Clamp）
        Wrap = 2       // 回绕（Wrap Back）
    }

    [SerializeField] private int nzWidth = 512;
    [SerializeField] private int nzHeight = 512;
    [SerializeField] private List<NoiseLayerParams> nzLayers = new List<NoiseLayerParams>();
    [SerializeField] private int nzOverflow = (int)NoiseOverflow.Clip;
    [SerializeField] private float nzMasterContrast = 1f;
    [SerializeField] private float nzBrightness;
    [SerializeField] private bool nzInvert;
    [SerializeField] private bool nzAlphaFromNoise;
    [SerializeField] private string nzOutputName = "Noise";

    private readonly bool[] nzLayerFold = new bool[NzMaxLayers];
    private Texture2D nzPreview;
    private bool nzDirty = true;
    private double nzLastBakeTime;
    // 层的增删/排序延迟到下一帧 Layout 之前执行，避免同一事件中控件数变化导致 IMGUI 报错
    private bool nzPendingAdd;
    private int nzPendingAddType; // 待添加层类型：0 噪声层，1 贴图层
    private int nzPendingDelete = -1;
    private int nzPendingMoveFrom = -1;
    private int nzPendingMoveTo = -1;
    private static Shader nzShader;
    // 参数说明框：鼠标悬停任意参数时，在注释框显示该参数的详细说明（L| 层内，G| 整体）
    private string nzLayerParamTip = "将鼠标移到上方任意参数上，这里会显示该参数的详细说明。";
    private string nzGlobalParamTip = "将鼠标移到整体调整的任意参数上，这里会显示该参数的详细说明。";

    // 读取当前悬停控件的提示，前缀匹配时缓存并返回去掉前缀的正文
    private static string PickTooltip(ref string cache, string prefix)
    {
        string tip = GUI.tooltip;
        if (!string.IsNullOrEmpty(tip) && tip.StartsWith(prefix))
        {
            cache = tip.Substring(prefix.Length);
        }
        return cache;
    }

    private void InitNoiseGenerator()
    {
        if (nzLayers == null || nzLayers.Count == 0)
        {
            nzLayers = new List<NoiseLayerParams> { new NoiseLayerParams() };
        }
        // 一次性迁移：旧版本只有单轴 frequency，升级后 X/Y 取相同值
        foreach (NoiseLayerParams l in nzLayers)
        {
            if (l.freqVersion == 0)
            {
                float f = l.frequency > 0f ? l.frequency : 6f;
                l.frequencyX = f;
                l.frequencyY = f;
                l.freqVersion = 1;
            }
        }
    }

    private static Shader GetNoiseShader()
    {
        if (nzShader == null)
        {
            // 使用项目内的 .shader 资源：Unity 在导入时编译，打开标签无需现场编译、不卡顿
            nzShader = Shader.Find("Hidden/VFXToolboxNoiseGenerator");
            if (nzShader == null)
            {
                nzShader = AssetDatabase.LoadAssetAtPath<Shader>(NoiseShaderPath);
            }
        }
        return nzShader;
    }

    /// <summary>一键恢复噪波工具的全部默认参数。</summary>
    private void ResetNoiseParams()
    {
        nzWidth = 512;
        nzHeight = 512;
        nzLayers = new List<NoiseLayerParams> { new NoiseLayerParams() };
        for (int i = 0; i < nzLayerFold.Length; i++)
        {
            nzLayerFold[i] = i == 0;
        }
        nzOverflow = (int)NoiseOverflow.Clip;
        nzMasterContrast = 1f;
        nzBrightness = 0f;
        nzInvert = false;
        nzAlphaFromNoise = false;
        nzOutputName = "Noise";
        nzDirty = true;
        SetStatus("噪波参数已重置为默认值。");
    }

    /// <summary>处理排队的层添加/删除/排序，避免在 IMGUI 事件中途改变控件数量。</summary>
    private void ApplyPendingLayerOps()
    {
        bool changed = false;
        if (nzPendingAdd && nzLayers.Count < NzMaxLayers)
        {
            NoiseLayerParams l = new NoiseLayerParams();
            l.layerType = nzPendingAddType;
            l.blend = 0;
            if (nzPendingAddType == 0)
            {
                l.seed = Mathf.RoundToInt(UnityEngine.Random.value * 100000f);
            }
            else
            {
                // 贴图层默认中性参数，贴图由用户拖入
                l.tilingX = 1f;
                l.tilingY = 1f;
            }
            nzLayers.Add(l);
            nzLayerFold[nzLayers.Count - 1] = true;
            changed = true;
        }
        nzPendingAdd = false;

        if (nzPendingMoveFrom >= 0 && nzPendingMoveTo >= 0
            && nzPendingMoveTo < nzLayers.Count && nzPendingMoveFrom < nzLayers.Count)
        {
            NoiseLayerParams tmp = nzLayers[nzPendingMoveTo];
            nzLayers[nzPendingMoveTo] = nzLayers[nzPendingMoveFrom];
            nzLayers[nzPendingMoveFrom] = tmp;
            changed = true;
        }
        nzPendingMoveFrom = -1;
        nzPendingMoveTo = -1;

        if (nzPendingDelete >= 0 && nzPendingDelete < nzLayers.Count && nzLayers.Count > 1)
        {
            nzLayers.RemoveAt(nzPendingDelete);
            changed = true;
        }
        nzPendingDelete = -1;

        if (changed)
        {
            nzDirty = true;
        }
    }

    private void DrawNoiseTool()
    {
        // 先处理上一帧排队的层增删/排序（此时还没有任何 Layout 控件）
        ApplyPendingLayerOps();

        // 参数变化时在任何 Layout 控件之前重渲染预览；拖动滑块时限流约 15 帧/秒，避免卡顿
        if (nzDirty)
        {
            double nowT = Time.realtimeSinceStartup;
            if (nzPreview != null && nowT - nzLastBakeTime < 0.06)
            {
                Repaint();
            }
            else
            {
                BakeNoisePreview();
                nzLastBakeTime = nowT;
                nzDirty = false;
            }
        }

        EditorGUILayout.LabelField("噪波生成（多层 Fractal Noise）", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "可添加噪声层或贴图层，按 相加/相乘/取大/取小 叠加；参数调整即时刷新预览。\n" +
            "类型下拉中可无缝循环的在前，标注〔不循环〕的漩涡/Simplex/任意角度条纹无法四边相接。\n" +
            "每层提供强度、对比度、亮度、Gamma、黑白场、阈值软化与 UV 偏移；贴图层支持 Tiling 与通道选择。",
            MessageType.None);

        EditorGUI.BeginChangeCheck();

        // —— 尺寸 ——
        EditorGUILayout.LabelField("输出尺寸", EditorStyles.boldLabel);
        EditorGUILayout.BeginHorizontal();
        foreach (int s in NzSizePresets)
        {
            if (GUILayout.Button(s.ToString()))
            {
                nzWidth = s;
                nzHeight = s;
            }
        }
        EditorGUILayout.EndHorizontal();
        EditorGUILayout.BeginHorizontal();
        nzWidth = EditorGUILayout.IntField("宽", Mathf.Clamp(nzWidth, 8, 8192));
        nzHeight = EditorGUILayout.IntField("高", Mathf.Clamp(nzHeight, 8, 8192));
        EditorGUILayout.EndHorizontal();

        // —— 噪声层 ——
        EditorGUILayout.Space(4);
        for (int idx = 0; idx < nzLayers.Count; idx++)
        {
            DrawNoiseLayer(idx);
        }

        EditorGUILayout.Space(2);
        EditorGUILayout.BeginHorizontal();
        using (new EditorGUI.DisabledScope(nzLayers.Count >= NzMaxLayers))
        {
            if (GUILayout.Button("＋ 添加噪声层", GUILayout.Height(24)))
            {
                nzPendingAdd = true;
                nzPendingAddType = 0;
            }
            if (GUILayout.Button("＋ 添加贴图层", GUILayout.Height(24)))
            {
                nzPendingAdd = true;
                nzPendingAddType = 1;
            }
        }
        EditorGUILayout.EndHorizontal();

        // —— 总调整 ——
        EditorGUILayout.Space(4);
        EditorGUILayout.LabelField("整体调整", EditorStyles.boldLabel);
        nzMasterContrast = EditorGUILayout.Slider(
            new GUIContent("总对比度", "G|所有层混合完成后，再围绕中灰整体拉一次对比；不影响各层之间的混合关系"),
            nzMasterContrast, 0.1f, 8f);
        nzBrightness = EditorGUILayout.Slider(
            new GUIContent("总亮度", "G|最终结果整体加亮/压暗，在总对比度之后处理"),
            nzBrightness, -2f, 2f);
        nzOverflow = EditorGUILayout.Popup(
            new GUIContent("溢出处理", "G|层相加后灰度超出 0~1 时怎么办：裁剪=硬切到黑白；柔化=平滑压缩高光；回绕=小数部分循环（可做周期性条带）"),
            nzOverflow, NzOverflowNames);
        EditorGUILayout.BeginHorizontal();
        nzInvert = EditorGUILayout.Toggle(
            new GUIContent("整体反相", "G|对最终结果做一次黑白反转（在各层反相之后）"),
            nzInvert);
        nzAlphaFromNoise = EditorGUILayout.Toggle(
            new GUIContent("Alpha 来自噪波", "G|透明通道写入最终灰度，导出的 PNG 可直接当遮罩/溶解贴图用；关闭时 Alpha 全不透明"),
            nzAlphaFromNoise);
        EditorGUILayout.EndHorizontal();

        nzOutputName = EditorGUILayout.TextField(
            new GUIContent("导出文件名", "G|导出 PNG 的默认文件名（不含扩展名）"),
            nzOutputName);
        DrawVFXExportMode();

        // 跟随鼠标的整体参数说明框
        EditorGUILayout.HelpBox(PickTooltip(ref nzGlobalParamTip, "G|"), MessageType.None);

        if (EditorGUI.EndChangeCheck())
        {
            nzDirty = true;
        }

        EditorGUILayout.Space(4);
        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("重置参数", GUILayout.Height(26)))
        {
            ResetNoiseParams();
        }
        if (GUILayout.Button("刷新预览", GUILayout.Height(26)))
        {
            nzDirty = true;
            nzLastBakeTime = 0;
        }
        if (GUILayout.Button("导出 PNG…", GUILayout.Height(26)))
        {
            ExportNoisePng();
        }
        EditorGUILayout.EndHorizontal();

        if (nzPreview != null)
        {
            EditorGUILayout.Space(6);
            float aspect = (float)nzPreview.height / Mathf.Max(1, nzPreview.width);
            float w = Mathf.Min(320f, position.width - 60f);
            float h = w * aspect;
            const float maxH = 260f;
            if (h > maxH)
            {
                h = maxH;
                w = h / Mathf.Max(0.0001f, aspect);
            }
            Rect r = GUILayoutUtility.GetRect(w, h, GUILayout.Width(w), GUILayout.Height(h));
            EditorGUI.DrawRect(r, new Color(0f, 0f, 0f, 0.35f));
            GUI.DrawTexture(r, nzPreview, ScaleMode.ScaleToFit, true);
            EditorGUILayout.LabelField("实时预览（导出尺寸 " + nzWidth + " × " + nzHeight + "）", EditorStyles.miniLabel);
        }
    }

    private void DrawNoiseLayer(int idx)
    {
        NoiseLayerParams l = nzLayers[idx];
        bool isTexture = l.layerType == 1;
        EditorGUILayout.BeginVertical(EditorStyles.helpBox);

        string title;
        if (isTexture)
        {
            title = "贴图层 " + (idx + 1) + "：" + (l.texture != null ? l.texture.name : "未指定贴图");
        }
        else
        {
            int fname = Mathf.Clamp(l.fractal, 0, NzFractalNames.Length - 1);
            title = "噪声层 " + (idx + 1) + "：" + NzFractalNames[fname];
        }

        EditorGUILayout.BeginHorizontal();
        nzLayerFold[idx] = EditorGUILayout.Foldout(nzLayerFold[idx], title, true);
        using (new EditorGUI.DisabledScope(idx == 0))
        {
            if (GUILayout.Button("↑", GUILayout.Width(24)))
            {
                nzPendingMoveFrom = idx;
                nzPendingMoveTo = idx - 1;
            }
            if (GUILayout.Button("↓", GUILayout.Width(24)))
            {
                nzPendingMoveFrom = idx;
                nzPendingMoveTo = idx + 1;
            }
        }
        using (new EditorGUI.DisabledScope(nzLayers.Count <= 1))
        {
            if (GUILayout.Button("删", GUILayout.Width(24)))
            {
                nzPendingDelete = idx;
            }
        }
        EditorGUILayout.EndHorizontal();

        if (nzLayerFold[idx])
        {
            EditorGUI.indentLevel++;
            using (new EditorGUI.DisabledScope(idx == 0))
            {
                l.blend = EditorGUILayout.Popup(
                    new GUIContent("叠加方式", "L|本层与下面已叠加结果的混合方式；第 0 层是底层，不参与叠加"),
                    l.blend, NzBlendNames);
            }
            // 叠加方式像“类型”一样，在下拉框下方固定显示当前模式的说明
            if (idx == 0)
            {
                EditorGUILayout.HelpBox("底层：作为整组纹理的基础（强度 0 时为中性灰），不与任何层叠加。", MessageType.None);
            }
            else if (l.blend >= 0 && l.blend < NzBlendTips.Length)
            {
                EditorGUILayout.HelpBox(NzBlendTips[l.blend], MessageType.None);
            }
            l.layerType = EditorGUILayout.Popup(
                new GUIContent("层类型", "L|噪声层=程序生成的分形噪声；贴图层=采样一张外部贴图"),
                l.layerType, NzLayerTypeNames);

            if (isTexture)
            {
                // —— 贴图层专用参数 ——
                EditorGUILayout.BeginHorizontal();
                l.texture = (Texture2D)EditorGUILayout.ObjectField(
                    new GUIContent("贴图", "L|拖入要叠加的贴图。采样时按 Tiling 平铺并自动折回 UV，但贴图内容本身要能四边循环，平铺后才看不到缝"),
                    l.texture, typeof(Texture2D), false);
                if (GUILayout.Button("使用选中", GUILayout.Width(70)))
                {
                    if (Selection.activeObject is Texture2D pickedTex)
                    {
                        l.texture = pickedTex;
                    }
                }
                EditorGUILayout.EndHorizontal();
                if (l.texture == null)
                {
                    EditorGUILayout.HelpBox("未指定贴图：该层按中性灰处理，不影响结果。", MessageType.Warning);
                }
                l.channel = EditorGUILayout.Popup(
                    new GUIContent("取值通道", "L|从贴图哪个通道取灰度：亮度按人眼权重 0.299/0.587/0.114 加权；也可只取 R/G/B/A（如遮罩取 A）"),
                    l.channel, NzChannelNames);
                l.tilingX = EditorGUILayout.Slider(
                    new GUIContent("Tiling X", "L|贴图横向（U）平铺次数，支持小数；2 表示一张图宽度内重复两遍"),
                    l.tilingX, 0.1f, 32f);
                l.tilingY = EditorGUILayout.Slider(
                    new GUIContent("Tiling Y", "L|贴图纵向（V）平铺次数，支持小数"),
                    l.tilingY, 0.1f, 32f);
            }
            else
            {
                // —— 噪声层专用参数（下拉按可循环在前排序，内部 id 不变） ——
                int curOrder = Array.IndexOf(NzFractalOrder, l.fractal);
                if (curOrder < 0)
                {
                    curOrder = 0;
                }
                int pickedType = EditorGUILayout.Popup(
                    new GUIContent("类型", "L|分形噪声算法；可无缝循环的排在前面，〔不循环〕的漩涡/Simplex/任意角度条纹四边无法相接"),
                    curOrder, NzFractalDisplayNames);
                if (pickedType != curOrder)
                {
                    l.fractal = NzFractalOrder[pickedType];
                }
                if (l.fractal >= 0 && l.fractal < NzFractalTips.Length)
                {
                    EditorGUILayout.HelpBox(NzFractalTips[l.fractal], MessageType.None);
                }
                l.interp = EditorGUILayout.Popup(
                    new GUIContent("插值方式", "L|噪声晶格之间的过渡曲线：方块=硬边像素感，线性=较硬，柔线=较柔和，样条=最柔最自然（湍流基础固定为方块）"),
                    l.interp, NzInterpNames);
                EditorGUILayout.BeginHorizontal();
                l.seed = EditorGUILayout.FloatField(
                    new GUIContent("Seed", "L|随机种子：相同参数下换一个种子得到另一张图案，不影响纹理密度"),
                    l.seed);
                if (GUILayout.Button("随机", GUILayout.Width(60)))
                {
                    l.seed = Mathf.RoundToInt(UnityEngine.Random.value * 100000f);
                    GUI.FocusControl(null);
                }
                EditorGUILayout.EndHorizontal();
                l.frequencyX = EditorGUILayout.Slider(
                    new GUIContent("频率 X", "L|横向（U）一张图内的周期数（颗粒数量），越大横向越细密；开启无缝后自动取整保证四边相接"),
                    l.frequencyX, 1f, 128f);
                l.frequencyY = EditorGUILayout.Slider(
                    new GUIContent("频率 Y", "L|纵向（V）一张图内的周期数；X/Y 设不同可做拉丝、水流、木纹等单向拉伸纹理，无缝仍成立"),
                    l.frequencyY, 1f, 128f);
                l.octaves = EditorGUILayout.IntSlider(
                    new GUIContent("Octaves", "L|分形叠加层数，每层频率翻倍、细节更丰富，层数越多计算越慢；细胞/细胞边界/棋盘/圆点/白噪声建议设 1"),
                    l.octaves, 1, 8);
                l.persistence = EditorGUILayout.Slider(
                    new GUIContent("Persistence", "L|每层细节相对上一层的强度衰减：越大高频细节越扎眼，越小越平滑，常用 0.45~0.6"),
                    l.persistence, 0.05f, 2f);
                if (l.fractal == 17)
                {
                    l.angle = EditorGUILayout.Slider(
                        new GUIContent("条纹角度", "L|Gabor 条纹方向角；0°/90° 等轴向可以无缝，任意斜角四边无法相接"),
                        l.angle, -180f, 180f);
                }
            }

            // —— 公共：强度与色阶调整 ——
            EditorGUILayout.LabelField("强度 / 色阶调整", EditorStyles.miniBoldLabel);
            l.strength = EditorGUILayout.Slider(
                new GUIContent("强度", "L|该层参与混合的程度：0 完全不起作用，1 完全混合；噪声底层为 0 时是中性灰"),
                l.strength, 0f, 2f);
            l.contrast = EditorGUILayout.Slider(
                new GUIContent("对比度", "L|围绕中灰 0.5 拉开或压缩明暗：>1 黑白更分明，<1 发灰；在层混合之前处理"),
                l.contrast, 0.1f, 8f);
            l.brightness = EditorGUILayout.Slider(
                new GUIContent("亮度偏移", "L|整体加亮或压暗该层（相当于 PS 色阶的输出偏移），在层混合之前处理"),
                l.brightness, -1f, 1f);
            l.gamma = EditorGUILayout.Slider(
                new GUIContent("Gamma", "L|中间调明暗：小于 1 提亮暗部（雾更透），大于 1 压暗，1 不变；不改变纯黑纯白"),
                l.gamma, 0.2f, 5f);
            l.blackLevel = EditorGUILayout.Slider(
                new GUIContent("黑场", "L|PS 色阶输入黑场：低于此灰度的值压成纯黑，用来把暗部压沉"),
                l.blackLevel, 0f, 1f);
            l.whiteLevel = EditorGUILayout.Slider(
                new GUIContent("白场", "L|PS 色阶输入白场：高于此灰度的值提成纯白；黑白场越接近，对比越硬"),
                Mathf.Max(l.whiteLevel, 0.01f), 0.01f, 1f);
            l.threshold = EditorGUILayout.Slider(
                new GUIContent("阈值", "L|灰度二值化分界：0 关闭；设 0.5 时暗于 0.5 变黑、亮于 0.5 变白，配合软化做溶解和硬边遮罩"),
                l.threshold, 0f, 1f);
            using (new EditorGUI.DisabledScope(l.threshold <= 0f))
            {
                l.thresholdSoft = EditorGUILayout.Slider(
                    new GUIContent("阈值软化", "L|二值化边缘的过渡宽度：0 为硬边，调大得到羽化/溶解边缘；阈值为 0 时不起作用"),
                    l.thresholdSoft, 0f, 0.25f);
            }

            // —— 公共：UV 偏移 ——
            EditorGUILayout.LabelField("UV 偏移", EditorStyles.miniBoldLabel);
            l.scrollX = EditorGUILayout.Slider(
                new GUIContent("偏移 X", "L|沿 U 方向平移纹理，单位为一圈：任意整数（含 5）平移一整圈回到原位，无缝层任意值都保持循环"),
                l.scrollX, -5f, 5f);
            l.scrollY = EditorGUILayout.Slider(
                new GUIContent("偏移 Y", "L|沿 V 方向平移纹理，单位为一圈：任意整数（含 5）平移一整圈回到原位，无缝层任意值都保持循环"),
                l.scrollY, -5f, 5f);

            EditorGUILayout.BeginHorizontal();
            l.invert = EditorGUILayout.Toggle(
                new GUIContent("反相", "L|黑白反转，亮变暗、暗变亮；细胞边界反相后得到亮线裂缝"),
                l.invert);
            string tileLabel = isTexture ? "贴图可循环" : "无缝循环";
            string tileTip = isTexture
                ? "L|声明贴图内容自身可以四边循环平铺，导出 PNG 的 Wrap Mode 会设为 Repeat；贴图本身有缝时不要勾选"
                : "L|频率自动取整并做环面周期包裹，导出 PNG 四边无缝、Wrap Mode = Repeat；漩涡/Simplex/斜条纹无法真正无缝";
            l.tile = EditorGUILayout.Toggle(new GUIContent(tileLabel, tileTip), l.tile);
            EditorGUILayout.EndHorizontal();

            // 跟随鼠标的参数说明框（与“类型”说明同款样式）
            EditorGUILayout.HelpBox(PickTooltip(ref nzLayerParamTip, "L|"), MessageType.None);
            EditorGUI.indentLevel--;
        }

        EditorGUILayout.EndVertical();
    }

    private void ConfigureNoiseMaterial(Material mat)
    {
        int count = Mathf.Min(nzLayers.Count, NzMaxLayers);
        float[] fractal = new float[NzMaxLayers];
        float[] interp = new float[NzMaxLayers];
        float[] strength = new float[NzMaxLayers];
        float[] contrast = new float[NzMaxLayers];
        float[] seed = new float[NzMaxLayers];
        float[] frequencyX = new float[NzMaxLayers];
        float[] frequencyY = new float[NzMaxLayers];
        float[] octaves = new float[NzMaxLayers];
        float[] persistence = new float[NzMaxLayers];
        float[] invert = new float[NzMaxLayers];
        float[] tile = new float[NzMaxLayers];
        float[] blend = new float[NzMaxLayers];
        float[] angle = new float[NzMaxLayers];
        float[] scrollX = new float[NzMaxLayers];
        float[] scrollY = new float[NzMaxLayers];
        float[] layerType = new float[NzMaxLayers];
        float[] channel = new float[NzMaxLayers];
        float[] tilingX = new float[NzMaxLayers];
        float[] tilingY = new float[NzMaxLayers];
        float[] bright = new float[NzMaxLayers];
        float[] gamma = new float[NzMaxLayers];
        float[] black = new float[NzMaxLayers];
        float[] white = new float[NzMaxLayers];
        float[] threshold = new float[NzMaxLayers];
        float[] thresholdSoft = new float[NzMaxLayers];

        for (int i = 0; i < NzMaxLayers; i++)
        {
            if (i < count)
            {
                NoiseLayerParams l = nzLayers[i];
                fractal[i] = l.fractal;
                interp[i] = l.interp;
                strength[i] = l.strength;
                contrast[i] = l.contrast;
                seed[i] = l.seed;
                frequencyX[i] = l.frequencyX;
                frequencyY[i] = l.frequencyY;
                octaves[i] = l.octaves;
                persistence[i] = l.persistence;
                invert[i] = l.invert ? 1f : 0f;
                tile[i] = l.tile ? 1f : 0f;
                blend[i] = l.blend;
                angle[i] = l.angle;
                scrollX[i] = l.scrollX;
                scrollY[i] = l.scrollY;
                layerType[i] = l.layerType;
                channel[i] = l.channel;
                tilingX[i] = l.tilingX;
                tilingY[i] = l.tilingY;
                bright[i] = l.brightness;
                gamma[i] = l.gamma;
                black[i] = l.blackLevel;
                white[i] = l.whiteLevel;
                threshold[i] = l.threshold;
                thresholdSoft[i] = l.thresholdSoft;
            }
            else
            {
                strength[i] = 1f;
                frequencyX[i] = 6f;
                frequencyY[i] = 6f;
                octaves[i] = 1;
                persistence[i] = 0.5f;
                interp[i] = 3f;
                tilingX[i] = 1f;
                tilingY[i] = 1f;
                gamma[i] = 1f;
                white[i] = 1f;
                thresholdSoft[i] = 0.02f;
            }
        }

        mat.SetInt("_LayerCount", count);
        mat.SetFloatArray("_LFractal", fractal);
        mat.SetFloatArray("_LInterp", interp);
        mat.SetFloatArray("_LStrength", strength);
        mat.SetFloatArray("_LContrast", contrast);
        mat.SetFloatArray("_LSeed", seed);
        mat.SetFloatArray("_LFrequencyX", frequencyX);
        mat.SetFloatArray("_LFrequencyY", frequencyY);
        mat.SetFloatArray("_LOctaves", octaves);
        mat.SetFloatArray("_LPersistence", persistence);
        mat.SetFloatArray("_LInvert", invert);
        mat.SetFloatArray("_LTile", tile);
        mat.SetFloatArray("_LBlend", blend);
        mat.SetFloatArray("_LAngle", angle);
        mat.SetFloatArray("_LScrollX", scrollX);
        mat.SetFloatArray("_LScrollY", scrollY);
        mat.SetFloatArray("_LType", layerType);
        mat.SetFloatArray("_LChannel", channel);
        mat.SetFloatArray("_LTilingX", tilingX);
        mat.SetFloatArray("_LTilingY", tilingY);
        mat.SetFloatArray("_LBright", bright);
        mat.SetFloatArray("_LGamma", gamma);
        mat.SetFloatArray("_LBlack", black);
        mat.SetFloatArray("_LWhite", white);
        mat.SetFloatArray("_LThreshold", threshold);
        mat.SetFloatArray("_LThresholdSoft", thresholdSoft);

        // 8 个贴图槽全部绑定：未使用的槽给 WhiteTexture，避免采样未定义纹理
        for (int i = 0; i < NzMaxLayers; i++)
        {
            Texture tex = (i < count && nzLayers[i].layerType == 1 && nzLayers[i].texture != null)
                ? nzLayers[i].texture
                : Texture2D.whiteTexture;
            mat.SetTexture("_LTex" + i, tex);
        }

        mat.SetFloat("_NzContrast", nzMasterContrast);
        mat.SetFloat("_NzBrightness", nzBrightness);
        mat.SetFloat("_NzOverflow", (int)nzOverflow);
        mat.SetFloat("_NzInvert", nzInvert ? 1f : 0f);
        mat.SetFloat("_NzAlpha", nzAlphaFromNoise ? 1f : 0f);
    }

    private Texture2D RenderNoise(int targetW, int targetH)
    {
        Shader sh = GetNoiseShader();
        if (sh == null || !sh.isSupported)
        {
            SetStatus("找不到噪波 Shader：" + NoiseShaderPath + "，请确认文件存在。");
            return null;
        }

        // 噪波是数据纹理，固定线性空间，避免 sRGB 转换改变灰度数值
        RenderTexture rt = new RenderTexture(targetW, targetH, 0, RenderTextureFormat.ARGB32,
            RenderTextureReadWrite.Linear);
        rt.hideFlags = HideFlags.HideAndDontSave;
        rt.Create();

        Material mat = new Material(sh);
        mat.hideFlags = HideFlags.HideAndDontSave;
        Texture2D output;
        RenderTexture prev = RenderTexture.active;
        try
        {
            ConfigureNoiseMaterial(mat);
            mat.SetVector("_NzSize", new Vector4(targetW, targetH));
            Graphics.Blit(Texture2D.whiteTexture, rt, mat);

            RenderTexture.active = rt;
            output = new Texture2D(targetW, targetH, TextureFormat.RGBA32, false, true);
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

    private void BakeNoisePreview()
    {
        int pw = Mathf.Clamp(nzWidth, 8, 8192);
        int ph = Mathf.Clamp(nzHeight, 8, 8192);
        // 实时预览固定长边 320，拖动参数时保持流畅；导出仍按完整尺寸
        int longSide = Mathf.Max(pw, ph);
        if (longSide > 320)
        {
            float scale = 320f / longSide;
            pw = Mathf.Max(1, Mathf.RoundToInt(pw * scale));
            ph = Mathf.Max(1, Mathf.RoundToInt(ph * scale));
        }

        if (nzPreview != null)
        {
            DestroyImmediate(nzPreview);
            nzPreview = null;
        }
        nzPreview = RenderNoise(pw, ph);
    }

    private void ExportNoisePng()
    {
        if (nzLayers == null || nzLayers.Count == 0)
        {
            SetStatus("请至少保留一个噪声层。");
            return;
        }

        int w = Mathf.Clamp(nzWidth, 8, 8192);
        int h = Mathf.Clamp(nzHeight, 8, 8192);
        string fileName = string.IsNullOrEmpty(nzOutputName) ? "Noise" : nzOutputName;
        string absPath = ResolveVFXExportPath("导出噪波 PNG", fileName, "png", null, false);
        if (string.IsNullOrEmpty(absPath))
        {
            return;
        }

        Texture2D tex = RenderNoise(w, h);
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

        // 所有层都无缝时才使用 Repeat 包裹，否则 Clamp 避免把接缝平铺出来
        bool allTile = true;
        foreach (NoiseLayerParams l in nzLayers)
        {
            if (!l.tile)
            {
                allTile = false;
                break;
            }
        }

        string rel = ToAssetsRelative(absPath);
        if (rel != null)
        {
            TextureImporter importer = AssetImporter.GetAtPath(rel) as TextureImporter;
            if (importer != null)
            {
                importer.textureType = TextureImporterType.Default;
                importer.sRGBTexture = false; // 数据贴图
                importer.mipmapEnabled = true;
                importer.wrapMode = allTile ? TextureWrapMode.Repeat : TextureWrapMode.Clamp;
                importer.alphaSource = nzAlphaFromNoise
                    ? TextureImporterAlphaSource.FromInput
                    : TextureImporterAlphaSource.None;
                importer.alphaIsTransparency = false;
                importer.SaveAndReimport();
            }
            Texture2D loaded = AssetDatabase.LoadAssetAtPath<Texture2D>(rel);
            if (loaded != null)
            {
                EditorGUIUtility.PingObject(loaded);
            }
            SetStatus("噪波图已导出：" + rel + (allTile ? "（无缝，Wrap Mode = Repeat）" : "（含非无缝层，Wrap Mode = Clamp）"));
        }
        else
        {
            SetStatus("已导出到项目外（未导入 Unity）：" + absPath);
        }
    }
}
