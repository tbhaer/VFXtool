using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

/// <summary>
/// VFX 工具箱（Tools / VFX 工具箱）：
///   模块一 渐变贴图：编辑渐变并烘焙成 PNG 导出（横/竖向、Wrap、sRGB、Mipmap 可选）。
///   模块二 批量重命名：见 VFXRenameTool.cs，走 AssetDatabase.RenameAsset，GUID 与引用不变。
///   模块三 通道合并：见 VFXChannelPackerTool.cs，四张源贴图任选通道合成 RGBA 四通道 PNG。
///   模块四 材质清理：见 VFXMaterialCleanerTool.cs，删除材质中当前 Shader 不存在的冗余属性记录。
///   模块五 资源提取：见 VFXExtractTool.cs，预制体依赖按材质/贴图/模型分类复制分包。
///   模块六 通道拆分：见 VFXChannelSplitterTool.cs，一张贴图的 RGBA 通道拆成灰度单图。
///   模块七 序列合拆：见 VFXSequenceTool.cs，序列大图按行列拆单帧 / 多单帧合成大图。
///   模块八 引用替换：见 VFXReferenceRemapperTool.cs，分析预制体/材质依赖并在面板暂存替换后写回。
///   模块九 重复贴图：见 VFXDuplicateTextureTool.cs，像素哈希找重复，重定向引用后删除冗余。
///   模块十 未使用资源：见 VFXUnusedAssetTool.cs，GUID 引用图可达性分析找零引用资源。
///   模块十一 渐变映射：见 VFXGradientMapTool.cs，按明暗把渐变颜色映射到贴图（PS Gradient Map）。
///   模块十二 噪波生成：见 VFXNoiseTool.cs，AE 风格分形噪波生成与导出。
///   模块十三 贴图改色：见 VFXColorAdjustTool.cs，对已有颜色贴图做 HSV/亮度对比度/Gamma/色阶/染色/Alpha 调整后导出。
///   模块十四 贴图拼合：见 VFXAtlasTool.cs，画布自由切片（PS 切片式），贴图拖进格子自动铺满后合成一张 PNG。
/// 主窗口只负责标签切换与公共状态，各模块为同类的 partial 实现。
/// </summary>
public partial class VFXToolboxWindow : EditorWindow
{
    private int tab;
    private static readonly string[] TabNames = { "渐变贴图", "批量重命名", "通道合并", "材质清理", "资源提取", "通道拆分", "序列合拆", "引用替换", "重复贴图", "未使用资源", "渐变映射", "噪波生成", "贴图改色", "内嵌网格改名", "贴图拼合" };
    private Vector2 rootScroll;

    /// <summary>各模块共用的底部状态提示。</summary>
    private string statusMessage = "";

    [MenuItem("Tools/VFX 工具箱")]
    public static void Open()
    {
        VFXToolboxWindow win = GetWindow<VFXToolboxWindow>("VFX 工具箱");
        win.minSize = new Vector2(580, 540);
        win.Show();
    }

    private void OnEnable()
    {
        titleContent = new GUIContent("VFX 工具箱");
        InitGradient();
        InitRenameTool();
        InitPacker();
        InitMaterialCleaner();
        InitExtractTool();
        InitChannelSplitter();
        InitSequenceTool();
        InitReferenceRemapper();
        InitDuplicateFinder();
        InitUnusedScanner();
        InitGradientMap();
        InitNoiseGenerator();
        InitColorAdjust();
        InitEmbeddedMeshTool();
        InitAtlasTool();
    }

    private void OnGUI()
    {
        tab = GUILayout.Toolbar(tab, TabNames, EditorStyles.toolbarButton, GUILayout.Height(24));
        EditorGUILayout.Space(4);

        rootScroll = EditorGUILayout.BeginScrollView(rootScroll);
        switch (tab)
        {
            case 0: DrawGradientTool(); break;
            case 1: DrawRenameTool(); break;
            case 2: DrawPackerTool(); break;
            case 3: DrawMaterialCleaner(); break;
            case 4: DrawExtractTool(); break;
            case 5: DrawChannelSplitterTool(); break;
            case 6: DrawSequenceTool(); break;
            case 7: DrawReferenceRemapper(); break;
            case 8: DrawDuplicateTextureTool(); break;
            case 9: DrawUnusedAssetTool(); break;
            case 10: DrawGradientMapTool(); break;
            case 11: DrawNoiseTool(); break;
            case 12: DrawColorAdjustTool(); break;
            case 13: DrawEmbeddedMeshTool(); break;
            case 14: DrawAtlasTool(); break;
        }
        EditorGUILayout.EndScrollView();

        if (!string.IsNullOrEmpty(statusMessage))
        {
            EditorGUILayout.HelpBox(statusMessage, MessageType.Info);
        }
    }

    // ================= 模块一：渐变贴图 =================

    [SerializeField] private Gradient gradient;
    [SerializeField] private int gradWidth = 256;
    [SerializeField] private int gradHeight = 8;
    [SerializeField] private int gradDirection; // 0 = 水平，1 = 竖直
    [SerializeField] private TextureWrapMode gradWrap = TextureWrapMode.Clamp;
    [SerializeField] private bool gradSRGB = true;
    [SerializeField] private bool gradMipmaps;
    [SerializeField] private string gradAssetName = "Gradient";

    // 渐变识别：从已有渐变贴图反推 Gradient 色标/透明度标
    [SerializeField] private Texture2D gradAnalyzeSource;

    private Texture2D gradPreview;

    // ================= 渐变模板库（可导出/导入，跨项目使用） =================
    [Serializable]
    private class GradColorKeyDto { public float t, r, g, b; }
    [Serializable]
    private class GradAlphaKeyDto { public float t, a; }
    [Serializable]
    private class GradientPreset
    {
        public string name = "未命名";
        public int mode; // 0 = Blend, 1 = Fixed
        public List<GradColorKeyDto> colorKeys = new List<GradColorKeyDto>();
        public List<GradAlphaKeyDto> alphaKeys = new List<GradAlphaKeyDto>();
    }
    [Serializable]
    private class GradientPresetLibrary
    {
        public int version = 1;
        public List<GradientPreset> presets = new List<GradientPreset>();
    }

    [SerializeField] private List<GradientPreset> gradPresets = new List<GradientPreset>();
    [SerializeField] private string gradNewPresetName = "渐变模板";
    private Vector2 gradPresetScroll;
    private bool gradPresetFoldout = true;

    private void InitGradient()
    {
        if (gradient == null)
        {
            gradient = new Gradient();
            gradient.SetKeys(
                new[]
                {
                    new GradientColorKey(Color.white, 0f),
                    new GradientColorKey(Color.black, 1f)
                },
                new[]
                {
                    new GradientAlphaKey(1f, 0f),
                    new GradientAlphaKey(1f, 1f)
                });
        }
    }

    private void DrawGradientTool()
    {
        EditorGUILayout.LabelField("渐变贴图导出", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "用 Unity 原生渐变编辑器调整颜色与透明度，烘焙成 PNG 保存。\n" +
            "常用于粒子颜色渐变、渐变遮罩、过渡贴图等。",
            MessageType.None);

        // —— 从已有渐变贴图识别（反推色标，填入下方渐变栏） ——
        EditorGUILayout.LabelField("从贴图识别渐变", EditorStyles.boldLabel);
        EditorGUILayout.BeginHorizontal();
        gradAnalyzeSource = (Texture2D)EditorGUILayout.ObjectField(
            new GUIContent("渐变贴图", "拖入一张沿水平/竖直方向变化的渐变贴图，按“方向”取样识别"),
            gradAnalyzeSource, typeof(Texture2D), false);
        if (GUILayout.Button("使用选中", GUILayout.Width(70)))
        {
            Texture2D picked = Selection.activeObject as Texture2D;
            if (picked != null)
            {
                gradAnalyzeSource = picked;
            }
        }
        if (GUILayout.Button("识别生成到渐变栏", GUILayout.Width(130)))
        {
            AnalyzeGradientFromTexture();
        }
        EditorGUILayout.EndHorizontal();
        EditorGUILayout.HelpBox(
            "识别会沿当前“方向”采样（横向贴图选水平 X，纵向贴图选竖直 Y），自动提取颜色与透明度关键帧（最多各 8 个）。\n" +
            "识别后可直接在渐变栏微调，并点渐变编辑器里的 Presets 保存，或导出 PNG 用到其他地方。",
            MessageType.None);

        EditorGUILayout.Space(2);
        EditorGUI.BeginChangeCheck();
        gradient = EditorGUILayout.GradientField(new GUIContent("渐变", "点击打开 Unity 原生 Gradient Editor"), gradient);
        gradDirection = EditorGUILayout.Popup("方向", gradDirection, new[] { "水平 X", "竖直 Y" });
        gradWidth = EditorGUILayout.IntField("宽度", Mathf.Max(1, gradWidth));
        gradHeight = EditorGUILayout.IntField("高度", Mathf.Max(1, gradHeight));
        gradWrap = (TextureWrapMode)EditorGUILayout.EnumPopup("Wrap Mode", gradWrap);
        gradSRGB = EditorGUILayout.Toggle(new GUIContent("sRGB", "颜色贴图勾选；纯数据渐变可取消"), gradSRGB);
        gradMipmaps = EditorGUILayout.Toggle("生成 Mipmap", gradMipmaps);
        gradAssetName = EditorGUILayout.TextField("导出文件名", gradAssetName);
        DrawVFXExportMode();
        bool changed = EditorGUI.EndChangeCheck();

        EditorGUILayout.Space(6);
        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("刷新预览", GUILayout.Height(26)) || changed || gradPreview == null)
        {
            BakeGradientPreview();
        }
        GUI.enabled = gradPreview != null;
        if (GUILayout.Button("导出 PNG…", GUILayout.Height(26)))
        {
            ExportGradientPng();
        }
        GUI.enabled = true;
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.Space(6);
        if (gradPreview != null)
        {
            Rect r = GUILayoutUtility.GetAspectRect(8f, GUILayout.Width(position.width - 28f));
            if (r.height > 96f)
            {
                r = new Rect(r.x, r.y, r.width, 96f);
            }
            EditorGUI.DrawRect(r, new Color(0f, 0f, 0f, 0.35f));
            GUI.DrawTexture(r, gradPreview, ScaleMode.StretchToFill, true);
            EditorGUILayout.LabelField(gradPreview.width + " × " + gradPreview.height + " RGBA32", EditorStyles.miniLabel);
        }

        DrawGradientPresetLibrary();
    }

    // ================= 渐变模板库 UI =================

    private void DrawGradientPresetLibrary()
    {
        EditorGUILayout.Space(8);
        gradPresetFoldout = EditorGUILayout.Foldout(gradPresetFoldout,
            "渐变模板库（导出 .json 即可跨项目/跨机器使用）", true);
        if (!gradPresetFoldout)
        {
            return;
        }

        EditorGUILayout.HelpBox(
            "跨项目迁移流程：① 点“读取 Unity 已保存的全部预设”，把 Gradient Editor 里存过的预设一次性全部读入；\n" +
            "② 点“导出模板库”存成一个 .json（建议放云盘/共享目录）；③ 在别的项目打开本工具点“导入模板库”即可全部读回。\n" +
            "Unity 自带 Gradient Editor 的 Presets 只存在本机偏好目录，换电脑/团队带不走。",
            MessageType.None);

        // 一次性读取 Unity 已保存的全部渐变预设（批量）
        if (GUILayout.Button("读取 Unity 已保存的全部预设（批量导入到下方列表）", GUILayout.Height(24)))
        {
            ImportAllUnityGradientPresets();
        }

        EditorGUILayout.Space(2);

        // 新增单个模板（把当前渐变栏存下来）
        EditorGUILayout.BeginHorizontal();
        gradNewPresetName = EditorGUILayout.TextField("存当前渐变", gradNewPresetName);
        if (GUILayout.Button("保存为模板", GUILayout.Width(90), GUILayout.Height(18)))
        {
            SaveCurrentGradientAsPreset();
        }
        EditorGUILayout.EndHorizontal();

        // 模板列表
        if (gradPresets.Count == 0)
        {
            EditorGUILayout.LabelField("（还没有模板：调好渐变后点上面“保存当前渐变为模板”）", EditorStyles.miniLabel);
        }
        else
        {
            gradPresetScroll = EditorGUILayout.BeginScrollView(gradPresetScroll, EditorStyles.helpBox, GUILayout.MinHeight(90), GUILayout.MaxHeight(200));
            for (int i = 0; i < gradPresets.Count; i++)
            {
                GradientPreset p = gradPresets[i];
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("应用", EditorStyles.miniButton, GUILayout.Width(44)))
                {
                    ApplyPresetToGradient(p);
                }
                EditorGUILayout.LabelField(p.name, EditorStyles.label, GUILayout.MinWidth(120));
                GUILayout.FlexibleSpace();
                GUILayout.Label(p.colorKeys.Count + " 色标 / " + p.alphaKeys.Count + " 透明标", EditorStyles.miniLabel);
                if (GUILayout.Button("×", EditorStyles.miniButton, GUILayout.Width(22)))
                {
                    gradPresets.RemoveAt(i);
                    i--;
                    continue;
                }
                EditorGUILayout.EndHorizontal();
            }
            EditorGUILayout.EndScrollView();
        }

        // 导出 / 导入
        DrawVFXExportMode();
        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("导出模板库…", GUILayout.Height(22)))
        {
            ExportGradientPresetLibrary();
        }
        if (GUILayout.Button("导入模板库…", GUILayout.Height(22)))
        {
            ImportGradientPresetLibrary();
        }
        EditorGUILayout.EndHorizontal();

        // 写回 Unity 自带 Gradient Editor 的 Presets 面板
        if (GUILayout.Button("同步全部到 Unity 预设（Gradient Editor 底部 Presets）", GUILayout.Height(22)))
        {
            SyncPresetsToUnityLibrary();
        }
        EditorGUILayout.HelpBox(
            "“应用”只填到上方渐变栏；点“同步全部到 Unity 预设”才会把列表里的渐变写进 Gradient Editor 底部的 Presets（重名更新、新增追加，不删原有）。",
            MessageType.None);
    }

    private void SaveCurrentGradientAsPreset()
    {
        if (gradient == null)
        {
            SetStatus("渐变栏为空，无法保存。");
            return;
        }
        string name = string.IsNullOrWhiteSpace(gradNewPresetName) ? "渐变模板" : gradNewPresetName.Trim();

        // 重名直接覆盖
        GradientPreset existing = gradPresets.Find(x => x.name == name);
        GradientPreset preset = existing ?? new GradientPreset { name = name };
        preset.mode = (int)gradient.mode;
        preset.colorKeys.Clear();
        preset.alphaKeys.Clear();
        foreach (GradientColorKey k in gradient.colorKeys)
        {
            preset.colorKeys.Add(new GradColorKeyDto { t = k.time, r = k.color.r, g = k.color.g, b = k.color.b });
        }
        foreach (GradientAlphaKey k in gradient.alphaKeys)
        {
            preset.alphaKeys.Add(new GradAlphaKeyDto { t = k.time, a = k.alpha });
        }
        if (existing == null)
        {
            gradPresets.Add(preset);
        }
        SetStatus("已保存渐变模板：" + name + "（颜色 " + preset.colorKeys.Count + " / 透明 " + preset.alphaKeys.Count + "）");
    }

    private void ApplyPresetToGradient(GradientPreset preset)
    {
        if (preset == null)
        {
            return;
        }
        gradient = BuildGradientFromPreset(preset);
        BakeGradientPreview();
        SetStatus("已应用渐变模板：" + preset.name);
    }

    /// <summary>从模板 DTO 重建一个可直接使用的 UnityEngine.Gradient。</summary>
    private static Gradient BuildGradientFromPreset(GradientPreset preset)
    {
        Gradient g = new Gradient();
        g.mode = (GradientMode)Mathf.Clamp(preset.mode, 0, 1);
        GradientColorKey[] colors = new GradientColorKey[preset.colorKeys.Count];
        GradientAlphaKey[] alphas = new GradientAlphaKey[preset.alphaKeys.Count];
        for (int i = 0; i < preset.colorKeys.Count; i++)
        {
            GradColorKeyDto c = preset.colorKeys[i];
            colors[i] = new GradientColorKey(new Color(c.r, c.g, c.b, 1f), c.t);
        }
        for (int i = 0; i < preset.alphaKeys.Count; i++)
        {
            GradAlphaKeyDto a = preset.alphaKeys[i];
            alphas[i] = new GradientAlphaKey(a.a, a.t);
        }
        if (colors.Length == 0)
        {
            colors = new[]
            {
                new GradientColorKey(Color.white, 0f),
                new GradientColorKey(Color.black, 1f)
            };
        }
        if (alphas.Length == 0)
        {
            alphas = new[]
            {
                new GradientAlphaKey(1f, 0f),
                new GradientAlphaKey(1f, 1f)
            };
        }
        g.SetKeys(colors, alphas);
        return g;
    }

    /// <summary>
    /// 把工具列表里的全部渐变写回 Unity 自带 Gradient Editor 的默认预设库（Preferences/Presets/Default.gradients），
    /// 重名更新、新增追加，不删除库里原有的其他预设；写完保存到磁盘并刷新视图。
    /// </summary>
    private void SyncPresetsToUnityLibrary()
    {
        if (gradPresets == null || gradPresets.Count == 0)
        {
            SetStatus("列表为空，没有可同步的渐变。");
            return;
        }

        try
        {
            Assembly asm = typeof(Editor).Assembly;
            const BindingFlags StaticAny = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            const BindingFlags InstAny = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

            Type tLib = asm.GetType("UnityEditor.GradientPresetLibrary");
            Type tHelperOpen = asm.GetType("UnityEditor.ScriptableObjectSaveLoadHelper`1");
            Type tHelper = tHelperOpen.MakeGenericType(tLib);
            Type tSaveType = asm.GetType("UnityEditor.SaveType");
            if (tLib == null || tHelperOpen == null || tSaveType == null)
            {
                SetStatus("同步失败：当前 Unity 版本缺少渐变预设库内部类型。");
                return;
            }

            // helper = new ScriptableObjectSaveLoadHelper<GradientPresetLibrary>("gradients", SaveType.Text)
            ConstructorInfo ctor = tHelper.GetConstructor(InstAny, null, new[] { typeof(string), tSaveType }, null);
            object helper = ctor.Invoke(new object[] { "gradients", Enum.ToObject(tSaveType, 1) }); // SaveType.Text = 1

            // 默认库路径（不含扩展名）
            Type tLocations = asm.GetType("UnityEditor.PresetLibraryLocations");
            PropertyInfo piPath = tLocations.GetProperty("defaultPresetLibraryPath", StaticAny);
            string libPath = (string)piPath.GetValue(null, null);

            // PresetLibraryManager 单例（ScriptableSingleton<PresetLibraryManager>.instance）
            Type tMgr = asm.GetType("UnityEditor.PresetLibraryManager");
            PropertyInfo piInstance = tMgr.BaseType.GetProperty("instance", StaticAny);
            object mgr = piInstance.GetValue(null, null);
            if (mgr == null)
            {
                SetStatus("同步失败：拿不到 PresetLibraryManager 单例，请先打开一次渐变编辑器再试。");
                return;
            }

            MethodInfo miGet = FindGenericMethod(tMgr, "GetLibrary");
            MethodInfo miCreate = FindGenericMethod(tMgr, "CreateLibrary");
            MethodInfo miSave = FindGenericMethod(tMgr, "SaveLibrary");
            MethodInfo getLib = miGet.MakeGenericMethod(tLib);
            object lib = getLib.Invoke(mgr, new object[] { helper, libPath });
            if (lib == null && miCreate != null)
            {
                lib = miCreate.MakeGenericMethod(tLib).Invoke(mgr, new object[] { helper, libPath });
            }
            if (lib == null)
            {
                SetStatus("同步失败：无法加载或创建默认渐变预设库。");
                return;
            }

            MethodInfo miCount = tLib.GetMethod("Count", InstAny, null, Type.EmptyTypes, null);
            MethodInfo miGetName = tLib.GetMethod("GetName", InstAny, null, new[] { typeof(int) }, null);
            MethodInfo miAdd = tLib.GetMethod("Add", InstAny, null, new[] { typeof(object), typeof(string) }, null);
            MethodInfo miReplace = tLib.GetMethod("Replace", InstAny, null, new[] { typeof(int), typeof(object) }, null);

            int existingCount = (int)miCount.Invoke(lib, null);
            Dictionary<string, int> nameIndex = new Dictionary<string, int>();
            for (int i = 0; i < existingCount; i++)
            {
                string n = miGetName.Invoke(lib, new object[] { i }) as string;
                if (!string.IsNullOrEmpty(n))
                {
                    nameIndex[n] = i;
                }
            }

            int added = 0;
            int updated = 0;
            foreach (GradientPreset p in gradPresets)
            {
                Gradient g = BuildGradientFromPreset(p);
                if (nameIndex.TryGetValue(p.name, out int idx))
                {
                    miReplace.Invoke(lib, new object[] { idx, g });
                    updated++;
                }
                else
                {
                    miAdd.Invoke(lib, new object[] { g, p.name });
                    nameIndex[p.name] = existingCount;
                    existingCount++;
                    added++;
                }
            }

            miSave.MakeGenericMethod(tLib).Invoke(mgr, new object[] { helper, lib, libPath });
            UnityEditorInternal.InternalEditorUtility.RepaintAllViews();
            SetStatus(string.Format(
                "已同步到 Unity 渐变预设库（{0}）：新增 {1} 个，重名更新 {2} 个。重新打开 Gradient Editor 即可在底部 Presets 看到。",
                libPath, added, updated));
        }
        catch (Exception e)
        {
            string inner = e.InnerException != null ? (" / " + e.InnerException.Message) : "";
            SetStatus("同步到 Unity 预设失败：" + e.Message + inner);
        }
    }

    private static MethodInfo FindGenericMethod(Type type, string name)
    {
        foreach (MethodInfo m in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            if (m.Name == name && m.IsGenericMethodDefinition)
            {
                return m;
            }
        }
        return null;
    }

    private void ExportGradientPresetLibrary()
    {
        if (gradPresets.Count == 0)
        {
            SetStatus("当前没有可导出的模板，先在上方保存几个。");
            return;
        }
        string lastDir = EditorPrefs.GetString("VFXGradLibDir", "");
        string startDir = string.IsNullOrEmpty(lastDir) ? GetVFXAbsoluteAssetPath("Assets") : lastDir;
        string absPath = ResolveVFXExportPath("导出渐变模板库", "GradientPresets", "json", startDir, false);
        if (string.IsNullOrEmpty(absPath))
        {
            return;
        }

        GradientPresetLibrary lib = new GradientPresetLibrary();
        lib.presets.AddRange(gradPresets);
        string json = JsonUtility.ToJson(lib, true);
        try
        {
            File.WriteAllText(absPath, json, new System.Text.UTF8Encoding(false));
        }
        catch (Exception e)
        {
            SetStatus("导出失败：" + e.Message);
            return;
        }
        EditorPrefs.SetString("VFXGradLibDir", Path.GetDirectoryName(absPath));
        SetStatus(string.Format("已导出 {0} 个渐变模板到：{1}", gradPresets.Count, absPath));
    }

    private void ImportGradientPresetLibrary()
    {
        string lastDir = EditorPrefs.GetString("VFXGradLibDir", "");
        string absPath = EditorUtility.OpenFilePanel("导入渐变模板库", lastDir, "json");
        if (string.IsNullOrEmpty(absPath))
        {
            return;
        }
        GradientPresetLibrary lib;
        try
        {
            lib = JsonUtility.FromJson<GradientPresetLibrary>(File.ReadAllText(absPath));
        }
        catch (Exception e)
        {
            SetStatus("导入失败：" + e.Message);
            return;
        }
        if (lib == null || lib.presets == null || lib.presets.Count == 0)
        {
            SetStatus("文件里没有渐变模板：" + absPath);
            return;
        }

        // 追加合并：重名覆盖
        int added = 0;
        foreach (GradientPreset p in lib.presets)
        {
            int idx = gradPresets.FindIndex(x => x.name == p.name);
            if (idx >= 0)
            {
                gradPresets[idx] = p;
            }
            else
            {
                gradPresets.Add(p);
                added++;
            }
        }
        EditorPrefs.SetString("VFXGradLibDir", Path.GetDirectoryName(absPath));
        SetStatus(string.Format("已从 {0} 导入：新增 {1} 个，重名覆盖 {2} 个。",
            Path.GetFileName(absPath), added, lib.presets.Count - added));
    }

    // ================= 读取 Unity 自带 Gradient Editor 已保存的预设 =================

    /// <summary>
    /// 扫描 Unity 偏好目录下所有 *.gradients 库（Gradient Editor 底部 Presets 的实际存储），
    /// 一次性把里面的全部渐变读入工具列表。
    /// </summary>
    private void ImportAllUnityGradientPresets()
    {
        string presetsDir;
        try
        {
            presetsDir = Path.Combine(UnityEditorInternal.InternalEditorUtility.unityPreferencesFolder, "Presets");
        }
        catch
        {
            presetsDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Unity", "Editor-5.x", "Preferences", "Presets");
        }

        if (!Directory.Exists(presetsDir))
        {
            SetStatus("没有找到 Unity 渐变预设目录（" + presetsDir + "）。请先在 Gradient Editor 里保存过至少一个预设。");
            return;
        }

        string[] files;
        try
        {
            files = Directory.GetFiles(presetsDir, "*.gradients", SearchOption.AllDirectories);
        }
        catch (Exception e)
        {
            SetStatus("扫描渐变预设目录失败：" + e.Message);
            return;
        }

        if (files.Length == 0)
        {
            SetStatus("预设目录里没有 .gradients 文件：" + presetsDir);
            return;
        }

        int added = 0;
        int overwritten = 0;
        int totalRead = 0;
        foreach (string file in files)
        {
            List<GradientPreset> parsed;
            try
            {
                parsed = ParseGradientsFile(file);
            }
            catch (Exception e)
            {
                SetStatus("解析失败 " + Path.GetFileName(file) + "：" + e.Message);
                continue;
            }

            string fileBase = Path.GetFileNameWithoutExtension(file);
            for (int i = 0; i < parsed.Count; i++)
            {
                GradientPreset p = parsed[i];
                if (string.IsNullOrWhiteSpace(p.name))
                {
                    p.name = fileBase + "_" + (i + 1);
                }
                totalRead++;
                int idx = gradPresets.FindIndex(x => x.name == p.name);
                if (idx >= 0)
                {
                    gradPresets[idx] = p;
                    overwritten++;
                }
                else
                {
                    gradPresets.Add(p);
                    added++;
                }
            }
        }

        if (totalRead == 0)
        {
            SetStatus("在 " + files.Length + " 个 .gradients 文件里没有读到任何渐变（可能预设是空的）。目录：" + presetsDir);
            return;
        }
        SetStatus(string.Format(
            "已从 {0} 个 Unity 预设库读取 {1} 个渐变：新增 {2} 个，重名覆盖 {3} 个。现在可点“导出模板库”备份。",
            files.Length, totalRead, added, overwritten));
    }

    /// <summary>
    /// 解析 Unity 的 .gradients（ScriptableObject YAML）。
    /// 颜色键：keyN.rgb + ctimeN(0..65535)；透明度键：keyN.a + atimeN；数量取 m_NumColorKeys / m_NumAlphaKeys。
    /// </summary>
    private static List<GradientPreset> ParseGradientsFile(string path)
    {
        string[] lines = File.ReadAllLines(path);
        List<GradientPreset> result = new List<GradientPreset>();

        GradientPreset current = null;
        Color[] keyColor = new Color[8];
        float[] keyAlpha = new float[8];
        int[] ctime = new int[8];
        int[] atime = new int[8];
        int mode;
        int numColor;
        int numAlpha;

        const NumberStyles ns = NumberStyles.Float;
        IFormatProvider inv = CultureInfo.InvariantCulture;

        ResetBuffers();

        void ResetBuffers()
        {
            for (int i = 0; i < 8; i++)
            {
                keyColor[i] = Color.black;
                keyAlpha[i] = 1f;
                ctime[i] = 0;
                atime[i] = 0;
            }
            mode = 0;
            numColor = 0;
            numAlpha = 0;
        }

        void Commit()
        {
            if (current == null)
            {
                return;
            }
            int nc = Mathf.Clamp(numColor, 0, 8);
            int na = Mathf.Clamp(numAlpha, 0, 8);
            for (int i = 0; i < nc; i++)
            {
                float t = Mathf.Clamp01(ctime[i] / 65535f);
                Color c = keyColor[i];
                c.a = 1f;
                current.colorKeys.Add(new GradColorKeyDto { t = t, r = c.r, g = c.g, b = c.b });
            }
            for (int i = 0; i < na; i++)
            {
                float t = Mathf.Clamp01(atime[i] / 65535f);
                current.alphaKeys.Add(new GradAlphaKeyDto { t = t, a = Mathf.Clamp01(keyAlpha[i]) });
            }
            current.mode = mode == 1 ? 1 : 0;
            // 极端兜底：文件缺计数时，保证至少两端有键
            if (current.colorKeys.Count == 0)
            {
                current.colorKeys.Add(new GradColorKeyDto { t = 0f, r = 1, g = 1, b = 1 });
                current.colorKeys.Add(new GradColorKeyDto { t = 1f, r = 0, g = 0, b = 0 });
            }
            if (current.alphaKeys.Count == 0)
            {
                current.alphaKeys.Add(new GradAlphaKeyDto { t = 0f, a = 1 });
                current.alphaKeys.Add(new GradAlphaKeyDto { t = 1f, a = 1 });
            }
            result.Add(current);
            current = null;
        }

        Regex headRx = new Regex(@"^\s*-\s*m_Name\s*:\s*(.*?)\s*$");
        Regex keyRx = new Regex(
            @"^\s*key(\d+)\s*:\s*\{\s*r\s*:\s*([-+0-9.eE]+)\s*,\s*g\s*:\s*([-+0-9.eE]+)\s*,\s*b\s*:\s*([-+0-9.eE]+)\s*,\s*a\s*:\s*([-+0-9.eE]+)\s*\}");
        Regex ctimeRx = new Regex(@"^\s*ctime(\d+)\s*:\s*(-?\d+)");
        Regex atimeRx = new Regex(@"^\s*atime(\d+)\s*:\s*(-?\d+)");
        Regex modeRx = new Regex(@"^\s*m_Mode\s*:\s*(-?\d+)");
        Regex numColorRx = new Regex(@"^\s*m_NumColorKeys\s*:\s*(\d+)");
        Regex numAlphaRx = new Regex(@"^\s*m_NumAlphaKeys\s*:\s*(\d+)");

        foreach (string raw in lines)
        {
            Match head = headRx.Match(raw);
            if (head.Success)
            {
                Commit();
                current = new GradientPreset { name = head.Groups[1].Value.Trim().Trim('"') };
                ResetBuffers();
                continue;
            }

            if (current == null)
            {
                continue;
            }

            Match mk = keyRx.Match(raw);
            if (mk.Success)
            {
                int idx = int.Parse(mk.Groups[1].Value, CultureInfo.InvariantCulture);
                if (idx >= 0 && idx < 8)
                {
                    float r = float.Parse(mk.Groups[2].Value, ns, inv);
                    float g = float.Parse(mk.Groups[3].Value, ns, inv);
                    float b = float.Parse(mk.Groups[4].Value, ns, inv);
                    float a = float.Parse(mk.Groups[5].Value, ns, inv);
                    keyColor[idx] = new Color(r, g, b, 1f);
                    keyAlpha[idx] = a;
                }
                continue;
            }

            Match mc = ctimeRx.Match(raw);
            if (mc.Success)
            {
                int idx = int.Parse(mc.Groups[1].Value, CultureInfo.InvariantCulture);
                if (idx >= 0 && idx < 8)
                {
                    ctime[idx] = int.Parse(mc.Groups[2].Value, CultureInfo.InvariantCulture);
                }
                continue;
            }

            Match ma = atimeRx.Match(raw);
            if (ma.Success)
            {
                int idx = int.Parse(ma.Groups[1].Value, CultureInfo.InvariantCulture);
                if (idx >= 0 && idx < 8)
                {
                    atime[idx] = int.Parse(ma.Groups[2].Value, CultureInfo.InvariantCulture);
                }
                continue;
            }

            Match mm = modeRx.Match(raw);
            if (mm.Success)
            {
                mode = int.Parse(mm.Groups[1].Value, CultureInfo.InvariantCulture);
                continue;
            }
            Match mnc = numColorRx.Match(raw);
            if (mnc.Success)
            {
                numColor = int.Parse(mnc.Groups[1].Value, CultureInfo.InvariantCulture);
                continue;
            }
            Match mna = numAlphaRx.Match(raw);
            if (mna.Success)
            {
                numAlpha = int.Parse(mna.Groups[1].Value, CultureInfo.InvariantCulture);
            }
        }
        Commit();

        return result;
    }

    private void BakeGradientPreview()
    {
        if (gradPreview != null)
        {
            DestroyImmediate(gradPreview);
            gradPreview = null;
        }
        gradPreview = BakeGradientTexture();
    }

    private Texture2D BakeGradientTexture()
    {
        int w = Mathf.Clamp(gradWidth, 1, 8192);
        int h = Mathf.Clamp(gradHeight, 1, 2048);
        Texture2D tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
        Color[] colors = new Color[w * h];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                float t = gradDirection == 0
                    ? (w > 1 ? (float)x / (w - 1) : 0f)
                    : (h > 1 ? (float)y / (h - 1) : 0f);
                colors[y * w + x] = gradient.Evaluate(t);
            }
        }
        tex.SetPixels(colors);
        tex.Apply(false);
        tex.wrapMode = gradWrap;
        tex.filterMode = FilterMode.Bilinear;
        return tex;
    }

    // ================= 渐变识别（贴图 → Gradient） =================

    /// <summary>沿当前方向采样来源贴图，反推颜色/透明度关键帧并写入渐变栏。</summary>
    private void AnalyzeGradientFromTexture()
    {
        if (gradAnalyzeSource == null)
        {
            SetStatus("请先拖入要识别的渐变贴图。");
            return;
        }

        bool horizontal = gradDirection == 0;
        int axisLength = Mathf.Clamp(horizontal ? gradAnalyzeSource.width : gradAnalyzeSource.height, 8, 512);
        int bands = Mathf.Clamp(horizontal ? gradAnalyzeSource.height : gradAnalyzeSource.width, 1, 64);
        int sampleW = horizontal ? axisLength : bands;
        int sampleH = horizontal ? bands : axisLength;

        // RT 颜色空间跟随源贴图：sRGB 贴图走 sRGB RT，采样解码 + 写回编码，读到的就是作者所见的 gamma 颜色
        bool srgb = IsSourceSRGB(gradAnalyzeSource);
        RenderTexture rt = new RenderTexture(sampleW, sampleH, 0, RenderTextureFormat.ARGB32,
            srgb ? RenderTextureReadWrite.sRGB : RenderTextureReadWrite.Linear);
        rt.hideFlags = HideFlags.HideAndDontSave;
        rt.Create();

        Color[] samples;
        RenderTexture prev = RenderTexture.active;
        Texture2D read = new Texture2D(sampleW, sampleH, TextureFormat.RGBA32, false);
        try
        {
            Graphics.Blit(gradAnalyzeSource, rt); // 拉伸重采样，Bilinear 顺带降噪
            RenderTexture.active = rt;
            read.ReadPixels(new Rect(0, 0, sampleW, sampleH), 0, 0);
            read.Apply(false);
            Color[] pixels = read.GetPixels();

            // 沿垂直于渐变的方向做平均，弱化噪点/边缘瑕疵
            samples = new Color[axisLength];
            for (int i = 0; i < axisLength; i++)
            {
                Color acc = Color.clear;
                for (int b = 0; b < bands; b++)
                {
                    int px = horizontal ? i : b;
                    int py = horizontal ? b : i;
                    acc += pixels[py * sampleW + px];
                }
                samples[i] = acc / bands;
            }
        }
        catch (Exception e)
        {
            SetStatus("识别失败：" + e.Message);
            return;
        }
        finally
        {
            RenderTexture.active = prev;
            rt.Release();
            DestroyImmediate(rt);
            DestroyImmediate(read);
        }

        List<GradientColorKey> colorKeys = ExtractColorKeys(samples);
        List<GradientAlphaKey> alphaKeys = ExtractAlphaKeys(samples);
        if (colorKeys.Count < 2 || alphaKeys.Count < 2)
        {
            SetStatus("识别失败：有效色标不足，请确认贴图方向设置正确。");
            return;
        }

        Gradient g = new Gradient();
        g.mode = GradientMode.Blend;
        g.SetKeys(colorKeys.ToArray(), alphaKeys.ToArray());
        gradient = g;
        BakeGradientPreview();
        SetStatus(string.Format(
            "已从 {0} 识别渐变：颜色关键帧 {1} 个、透明度关键帧 {2} 个（方向：{3}）。",
            gradAnalyzeSource.name, colorKeys.Count, alphaKeys.Count, horizontal ? "水平" : "竖直"));
    }

    /// <summary>
    /// 迭代提取颜色关键帧：从两端开始，每次加入与当前分段线性插值偏差最大的采样点，
    /// 直到偏差小于阈值或达到 8 个（Gradient 上限）。
    /// </summary>
    private static List<GradientColorKey> ExtractColorKeys(Color[] samples)
    {
        const float threshold = 0.025f;
        const int maxKeys = 8;
        int n = samples.Length;

        List<int> indices = new List<int> { 0, n - 1 };
        bool[] locked = new bool[n];
        locked[0] = true;
        locked[n - 1] = true;

        while (indices.Count < maxKeys)
        {
            int best = -1;
            float bestError = threshold;
            for (int seg = 0; seg < indices.Count - 1; seg++)
            {
                int a = indices[seg];
                int b = indices[seg + 1];
                Color ca = samples[a];
                Color cb = samples[b];
                for (int i = a + 1; i < b; i++)
                {
                    if (locked[i])
                    {
                        continue;
                    }
                    float t = (float)(i - a) / Mathf.Max(1, b - a);
                    Color expected = Color.Lerp(ca, cb, t);
                    Color actual = samples[i];
                    float error = Mathf.Abs(expected.r - actual.r)
                        + Mathf.Abs(expected.g - actual.g)
                        + Mathf.Abs(expected.b - actual.b);
                    error /= 3f;
                    if (error > bestError)
                    {
                        bestError = error;
                        best = i;
                    }
                }
            }
            if (best < 0)
            {
                break;
            }
            indices.Add(best);
            locked[best] = true;
            indices.Sort();
        }

        List<GradientColorKey> keys = new List<GradientColorKey>(indices.Count);
        foreach (int i in indices)
        {
            Color c = samples[i];
            c.a = 1f; // 颜色键不携带透明度
            keys.Add(new GradientColorKey(c, n > 1 ? (float)i / (n - 1) : 0f));
        }
        return keys;
    }

    /// <summary>迭代提取透明度关键帧，逻辑同颜色提取，阈值作用于 alpha 单通道。</summary>
    private static List<GradientAlphaKey> ExtractAlphaKeys(Color[] samples)
    {
        const float threshold = 0.015f;
        const int maxKeys = 8;
        int n = samples.Length;

        List<int> indices = new List<int> { 0, n - 1 };
        bool[] locked = new bool[n];
        locked[0] = true;
        locked[n - 1] = true;

        while (indices.Count < maxKeys)
        {
            int best = -1;
            float bestError = threshold;
            for (int seg = 0; seg < indices.Count - 1; seg++)
            {
                int a = indices[seg];
                int b = indices[seg + 1];
                for (int i = a + 1; i < b; i++)
                {
                    if (locked[i])
                    {
                        continue;
                    }
                    float t = (float)(i - a) / Mathf.Max(1, b - a);
                    float expected = Mathf.Lerp(samples[a].a, samples[b].a, t);
                    float error = Mathf.Abs(expected - samples[i].a);
                    if (error > bestError)
                    {
                        bestError = error;
                        best = i;
                    }
                }
            }
            if (best < 0)
            {
                break;
            }
            indices.Add(best);
            locked[best] = true;
            indices.Sort();
        }

        List<GradientAlphaKey> keys = new List<GradientAlphaKey>(indices.Count);
        foreach (int i in indices)
        {
            keys.Add(new GradientAlphaKey(Mathf.Clamp01(samples[i].a), n > 1 ? (float)i / (n - 1) : 0f));
        }
        return keys;
    }

    private void ExportGradientPng()
    {        string fileName = string.IsNullOrEmpty(gradAssetName) ? "Gradient" : gradAssetName;
        string absPath = ResolveVFXExportPath("导出渐变 PNG", fileName, "png", null, false);
        if (string.IsNullOrEmpty(absPath))
        {
            return;
        }

        Texture2D tex = BakeGradientTexture();
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
            TextureImporter importer = AssetImporter.GetAtPath(rel) as TextureImporter;
            if (importer != null)
            {
                importer.textureType = TextureImporterType.Default;
                importer.sRGBTexture = gradSRGB;
                importer.mipmapEnabled = gradMipmaps;
                importer.wrapMode = gradWrap;
                importer.SaveAndReimport();
            }
            Texture2D loaded = AssetDatabase.LoadAssetAtPath<Texture2D>(rel);
            if (loaded != null)
            {
                EditorGUIUtility.PingObject(loaded);
            }
            SetStatus("已导出并导入：" + rel);
        }
        else
        {
            SetStatus("已导出到项目外（未导入 Unity）：" + absPath);
        }
    }

    // ================= 公共工具 =================

    /// <summary>设置底部状态提示。</summary>
    private void SetStatus(string msg)
    {
        statusMessage = msg;
        Debug.Log("[VFX 工具箱] " + msg);
    }

    /// <summary>把绝对路径转成以 Assets 开头的相对路径；不在项目内返回 null。</summary>
    private static string ToAssetsRelative(string absPath)
    {
        string dataPath = Application.dataPath.Replace('\\', '/');
        string p = absPath.Replace('\\', '/');
        if (p.StartsWith(dataPath, StringComparison.OrdinalIgnoreCase))
        {
            return "Assets" + p.Substring(dataPath.Length);
        }
        return null;
    }
}
