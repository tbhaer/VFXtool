using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// 模块四：材质球冗余数据清理。
/// 材质反复修改 / 更换 Shader 后，.mat 文件的 m_SavedProperties 里会残留
/// 当前 Shader 已不存在的旧属性（贴图 / 浮点 / 颜色 / 整数），其中旧贴图 GUID
/// 会让打包时带入废弃资源，甚至出现引用错误。
/// 本工具通过 Unity 序列化接口，只删除当前 Shader 中不存在的属性记录，
/// 面板上能看到的属性原样保留；支持在 Project 窗口单选材质或选择文件夹。
/// </summary>
public partial class VFXToolboxWindow
{
    private bool mcDirty;               // true=需要重新扫描
    private DefaultAsset mcScanFolder;   // 用户指定的扫描文件夹（ObjectField 选择）；null=未指定
    private bool mcScanAll;              // 本次是否扫的全工程
    private readonly List<string> mcMaterialPaths = new List<string>();
    private readonly List<string> mcFolderPaths = new List<string>();
    private Vector2 mcListScroll;
    private Vector2 mcPreviewScroll;
    private int mcSelectedIndex;

    // 路径 -> 冗余分析结果（只读预览用）
    private readonly Dictionary<string, MaterialDryRun> mcAnalysis = new Dictionary<string, MaterialDryRun>();

    private Material mcPreviewMaterial;
    private Editor mcPreviewEditor;

    /// <summary>单个材质的冗余数据分析结果（不修改文件）。</summary>
    private class MaterialDryRun
    {
        public bool missingShader;
        public string shaderName = "";
        public readonly List<string> staleTextures = new List<string>();
        public readonly List<string> staleInts = new List<string>();
        public readonly List<string> staleFloats = new List<string>();
        public readonly List<string> staleColors = new List<string>();

        public int Total
        {
            get { return staleTextures.Count + staleInts.Count + staleFloats.Count + staleColors.Count; }
        }
    }

    private void InitMaterialCleaner()
    {
    }

    private void OnDisable()
    {
        DestroyPreviewEditor();
    }

    // ================= UI =================

    private void DrawMaterialCleaner()
    {
        EditorGUILayout.LabelField("材质球冗余数据清理", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "清理 .mat 文件中当前 Shader 已不存在的旧属性记录（旧贴图 / 浮点 / 颜色 / 整数）。\n" +
            "· 在下方「扫描文件夹」处选择一个文件夹（或直接点「扫描全工程」），再点对应扫描按钮；\n" +
            "· 右侧预览窗口会列出每个材质将要删除的冗余项，材质面板当前能看到的属性不会被改动；\n" +
            "· 这些旧记录只在“切回原来的 Shader”时才会被 Unity 用来恢复旧参数，当前 Shader 完全不读取，删除不影响材质现在的显示；\n" +
            "· Shader 丢失（粉色）的材质会跳过，需先指定 Shader；清理会重写文件，建议先提交版本管理。",
            MessageType.Info);

        EditorGUILayout.Space(4);

        // ---- 扫描文件夹选择 ----
        EditorGUI.BeginChangeCheck();
        mcScanFolder = (DefaultAsset)EditorGUILayout.ObjectField(
            new GUIContent("扫描文件夹", "留空则扫描全工程"),
            mcScanFolder, typeof(DefaultAsset), false);
        if (EditorGUI.EndChangeCheck() && mcScanFolder != null)
        {
            string fp = AssetDatabase.GetAssetPath(mcScanFolder);
            if (!string.IsNullOrEmpty(fp) && !AssetDatabase.IsValidFolder(fp))
            {
                EditorUtility.DisplayDialog("提示", "请选择一个文件夹（不是单个材质文件）。", "确定");
                mcScanFolder = null;
            }
        }

        // ---- 底部两个扫描按钮 ----
        EditorGUILayout.Space(4);
        EditorGUILayout.BeginHorizontal();
        using (new EditorGUI.DisabledGroupScope(mcScanFolder == null))
        {
            if (GUILayout.Button("扫描选中文件夹（含子目录）", GUILayout.Height(28)))
            {
                mcScanAll = false;
                mcDirty = true;
            }
        }
        if (GUILayout.Button("扫描全工程（Assets）", GUILayout.Width(180), GUILayout.Height(28)))
        {
            mcScanAll = true;
            mcDirty = true;
        }
        EditorGUILayout.EndHorizontal();

        if (mcDirty)
        {
            CollectMaterials();
            mcAnalysis.Clear();
            AnalyzeAllMaterials();
            mcSelectedIndex = mcMaterialPaths.Count > 0 ? 0 : -1;
            DestroyPreviewEditor();
            mcDirty = false;

            if (mcMaterialPaths.Count > 0)
            {
                SetStatus(string.Format(
                    "扫描完成：{0}，找到材质 {1} 个。",
                    mcScanAll ? "全工程" : "选中文件夹", mcMaterialPaths.Count));
            }
            else
            {
                SetStatus("扫描完成：没有找到 .mat 材质文件。");
            }
        }

        int totalStale = 0;
        foreach (MaterialDryRun d in mcAnalysis.Values)
        {
            totalStale += d.Total;
        }

        EditorGUILayout.Space(4);
        string scope = mcFolderPaths.Count > 0
            ? string.Format("（扫描文件夹 {0} 个）", mcFolderPaths.Count)
            : "";
        EditorGUILayout.LabelField(
            string.Format("待处理材质：{0} 个{1}，发现冗余记录 {2} 项",
                mcMaterialPaths.Count, scope, totalStale),
            EditorStyles.boldLabel);

        DrawListAndPreview();

        EditorGUILayout.Space(4);
        EditorGUI.BeginDisabledGroup(mcMaterialPaths.Count == 0);
        if (GUILayout.Button("应用清理", GUILayout.Height(30)))
        {
            if (EditorUtility.DisplayDialog(
                    "确认清理材质冗余数据",
                    "即将重写 " + mcMaterialPaths.Count + " 个材质文件，删除当前 Shader 不存在的旧属性记录。\n\n" +
                    "建议已用 Git / SVN 提交备份。是否继续？",
                    "清理", "取消"))
            {
                CleanMaterials();
            }
        }
        EditorGUI.EndDisabledGroup();
    }

    /// <summary>左侧材质列表 + 右侧预览窗口。</summary>
    private void DrawListAndPreview()
    {
        float paneHeight = 300f;
        float listWidth = Mathf.Max(180f, (position.width - 30f) * 0.42f);

        EditorGUILayout.BeginHorizontal();

        // ---------- 左侧：材质列表 ----------
        Rect listArea = GUILayoutUtility.GetRect(listWidth, paneHeight, GUILayout.Width(listWidth));
        GUI.Box(listArea, GUIContent.none, EditorStyles.helpBox);
        Rect listView = new Rect(listArea.x + 3f, listArea.y + 3f, listArea.width - 6f, listArea.height - 6f);
        mcListScroll = GUI.BeginScrollView(listView, mcListScroll,
            new Rect(0f, 0f, listView.width - 16f, Mathf.Max(mcMaterialPaths.Count * 24f + 8f, listView.height)));

        if (mcMaterialPaths.Count == 0)
        {
            string msg = "尚未扫描。\n\n在上部选择一个文件夹，点「扫描选中文件夹」；\n或直接点「扫描全工程」扫描整个 Assets。";
            GUI.Label(new Rect(6f, 6f, listView.width - 20f, 60f), msg, EditorStyles.wordWrappedMiniLabel);
        }
        else
        {
            float btnW = listView.width - 16f;
            for (int i = 0; i < mcMaterialPaths.Count; i++)
            {
                string path = mcMaterialPaths[i];
                MaterialDryRun dry = GetAnalysis(path);
                Rect row = new Rect(2f, 3f + i * 24f, btnW, 21f);

                GUIStyle style = i == mcSelectedIndex ? "OL SelectedRow" : "OL Row";
                if (GUI.Button(row, GUIContent.none, style))
                {
                    mcSelectedIndex = i;
                    DestroyPreviewEditor();
                }

                string label = Path.GetFileName(path);
                if (dry != null)
                {
                    if (dry.missingShader)
                    {
                        label += "  [Shader丢失]";
                    }
                    else if (dry.Total > 0)
                    {
                        label += "  [" + dry.Total + "]";
                    }
                    else
                    {
                        label += "  [干净]";
                    }
                }
                Color old = GUI.color;
                if (dry != null && dry.missingShader)
                {
                    GUI.color = new Color(1f, 0.6f, 0.6f);
                }
                else if (dry != null && dry.Total > 0)
                {
                    GUI.color = new Color(1f, 0.85f, 0.55f);
                }
                GUI.Label(new Rect(row.x + 6f, row.y + 3f, row.width - 10f, 17f), label, EditorStyles.miniLabel);
                GUI.color = old;
            }
        }
        GUI.EndScrollView();

        // ---------- 右侧：预览窗口 ----------
        float previewWidth = position.width - 30f - listWidth - 6f;
        Rect previewArea = GUILayoutUtility.GetRect(previewWidth, paneHeight, GUILayout.Width(previewWidth));
        GUI.Box(previewArea, GUIContent.none, EditorStyles.helpBox);

        if (mcSelectedIndex < 0 || mcSelectedIndex >= mcMaterialPaths.Count)
        {
            GUI.Label(new Rect(previewArea.x + 8f, previewArea.y + 8f, previewArea.width - 16f, 30f),
                "在左侧选择一个材质查看清理预览。", EditorStyles.wordWrappedMiniLabel);
        }
        else
        {
            DrawPreviewPane(previewArea, mcMaterialPaths[mcSelectedIndex]);
        }

        EditorGUILayout.EndHorizontal();
    }

    /// <summary>右侧预览：材质球 + 冗余项明细。</summary>
    private void DrawPreviewPane(Rect area, string path)
    {
        Material mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        MaterialDryRun dry = GetAnalysis(path);

        Rect content = new Rect(area.x + 8f, area.y + 6f, area.width - 16f, area.height - 12f);
        mcPreviewScroll = GUI.BeginScrollView(content, mcPreviewScroll,
            new Rect(0f, 0f, content.width - 16f, 520f));

        float x = 4f;
        float w = content.width - 24f;
        float y = 2f;

        // 标题信息
        GUI.Label(new Rect(x, y, w, 18f), Path.GetFileName(path), EditorStyles.boldLabel);
        y += 20f;
        GUI.Label(new Rect(x, y, w, 16f), "路径：" + path, EditorStyles.miniLabel);
        y += 17f;
        GUI.Label(new Rect(x, y, w, 16f),
            "Shader：" + (dry != null && !string.IsNullOrEmpty(dry.shaderName) ? dry.shaderName : (mat != null && mat.shader != null ? mat.shader.name : "缺失")),
            EditorStyles.miniLabel);
        y += 20f;

        // 材质球预览
        if (mat != null)
        {
            EnsurePreviewEditor(mat);
            if (mcPreviewEditor != null)
            {
                float sphereH = Mathf.Min(170f, w * 0.55f);
                Rect sphereRect = new Rect(x, y, w, sphereH);
                mcPreviewEditor.OnPreviewGUI(sphereRect, EditorStyles.helpBox);
                y += sphereH + 8f;
            }
        }

        if (dry == null)
        {
            GUI.Label(new Rect(x, y, w, 20f), "分析中…", EditorStyles.miniLabel);
        }
        else if (dry.missingShader)
        {
            Rect warn = new Rect(x, y, w, 34f);
            EditorGUI.HelpBox(warn, "该材质 Shader 丢失（粉色），清理时会跳过。请先重新指定 Shader。", MessageType.Warning);
            y += 38f;
        }
        else if (dry.Total == 0)
        {
            Rect ok = new Rect(x, y, w, 26f);
            EditorGUI.HelpBox(ok, "没有冗余数据，该材质无需清理。", MessageType.Info);
            y += 30f;
        }
        else
        {
            Rect sum = new Rect(x, y, w, 26f);
            EditorGUI.HelpBox(sum, "以下 " + dry.Total + " 项是当前 Shader 不存在的旧记录，点“应用清理”后会被删除：", MessageType.Warning);
            y += 30f;

            y = DrawStaleGroup(x, y, w, "冗余贴图（旧贴图引用）", dry.staleTextures, new Color(1f, 0.75f, 0.55f));
            y = DrawStaleGroup(x, y, w, "冗余浮点", dry.staleFloats, new Color(0.85f, 0.85f, 0.85f));
            y = DrawStaleGroup(x, y, w, "冗余颜色", dry.staleColors, new Color(0.85f, 0.85f, 0.85f));
            y = DrawStaleGroup(x, y, w, "冗余整数", dry.staleInts, new Color(0.85f, 0.85f, 0.85f));
        }

        GUI.EndScrollView();
    }

    private static float DrawStaleGroup(float x, float y, float w, string title, List<string> items, Color color)
    {
        if (items.Count == 0)
        {
            return y;
        }
        GUI.Label(new Rect(x, y, w, 17f), title + "（" + items.Count + "）", EditorStyles.boldLabel);
        y += 18f;
        Color old = GUI.color;
        GUI.color = color;
        foreach (string item in items)
        {
            GUI.Label(new Rect(x + 14f, y, w - 14f, 16f), item, EditorStyles.miniLabel);
            y += 16f;
        }
        GUI.color = old;
        y += 3f;
        return y;
    }

    private void EnsurePreviewEditor(Material mat)
    {
        if (mcPreviewMaterial == mat && mcPreviewEditor != null)
        {
            return;
        }
        DestroyPreviewEditor();
        mcPreviewMaterial = mat;
        mcPreviewEditor = Editor.CreateEditor(mat);
    }

    private void DestroyPreviewEditor()
    {
        if (mcPreviewEditor != null)
        {
            UnityEngine.Object.DestroyImmediate(mcPreviewEditor);
            mcPreviewEditor = null;
        }
        mcPreviewMaterial = null;
    }

    // ================= 冗余分析（只读） =================

    private MaterialDryRun GetAnalysis(string path)
    {
        MaterialDryRun dry;
        if (mcAnalysis.TryGetValue(path, out dry))
        {
            return dry;
        }

        dry = AnalyzeMaterial(path);
        mcAnalysis[path] = dry;
        return dry;
    }

    private static MaterialDryRun AnalyzeMaterial(string path)
    {
        MaterialDryRun result = new MaterialDryRun();
        Material mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat == null)
        {
            result.missingShader = true;
            return result;
        }

        Shader shader = mat.shader;
        if (shader == null || shader.name == "Hidden/InternalErrorShader")
        {
            result.missingShader = true;
            return result;
        }
        result.shaderName = shader.name;

        HashSet<string> validProps = new HashSet<string>();
        int propCount = shader.GetPropertyCount();
        for (int i = 0; i < propCount; i++)
        {
            validProps.Add(shader.GetPropertyName(i));
        }

        SerializedObject so = new SerializedObject(mat);
        SerializedProperty saved = so.FindProperty("m_SavedProperties");
        if (saved != null)
        {
            AnalyzeArray(saved, "m_TexEnvs", validProps, result.staleTextures, true);
            AnalyzeArray(saved, "m_Ints", validProps, result.staleInts, false);
            AnalyzeArray(saved, "m_Floats", validProps, result.staleFloats, false);
            AnalyzeArray(saved, "m_Colors", validProps, result.staleColors, false);
        }
        return result;
    }

    private static void AnalyzeArray(SerializedProperty savedProperties, string arrayName,
        HashSet<string> validProps, List<string> output, bool isTexture)
    {
        SerializedProperty array = savedProperties.FindPropertyRelative(arrayName);
        if (array == null || !array.isArray)
        {
            return;
        }

        for (int i = 0; i < array.arraySize; i++)
        {
            SerializedProperty element = array.GetArrayElementAtIndex(i);
            SerializedProperty nameProp = element != null ? element.FindPropertyRelative("first") : null;
            if (nameProp == null)
            {
                continue;
            }
            string propName = nameProp.stringValue;
            if (validProps.Contains(propName))
            {
                continue;
            }

            if (isTexture)
            {
                string texName = "空";
                SerializedProperty texProp = element.FindPropertyRelative("second.m_Texture");
                if (texProp != null && texProp.objectReferenceValue != null)
                {
                    texName = texProp.objectReferenceValue.name;
                }
                output.Add(propName + "  →  " + texName);
            }
            else
            {
                SerializedProperty valueProp = element.FindPropertyRelative("second");
                string value = "?";
                if (valueProp != null)
                {
                    switch (valueProp.propertyType)
                    {
                        case SerializedPropertyType.Float:
                            value = valueProp.floatValue.ToString("0.###");
                            break;
                        case SerializedPropertyType.Integer:
                            value = valueProp.intValue.ToString();
                            break;
                        case SerializedPropertyType.Color:
                            Color c = valueProp.colorValue;
                            value = string.Format("({0:0.##}, {1:0.##}, {2:0.##}, {3:0.##})", c.r, c.g, c.b, c.a);
                            break;
                        default:
                            value = valueProp.ToString();
                            break;
                    }
                }
                output.Add(propName + "  =  " + value);
            }
        }
    }

    // ================= 收集选择 =================

    private void CollectMaterials()
    {
        mcMaterialPaths.Clear();
        mcFolderPaths.Clear();
        HashSet<string> done = new HashSet<string>();

        string[] searchFolders;
        if (mcScanAll)
        {
            searchFolders = new[] { "Assets" };
            mcFolderPaths.Add("Assets");
        }
        else
        {
            string folderPath = mcScanFolder != null ? AssetDatabase.GetAssetPath(mcScanFolder) : null;
            if (string.IsNullOrEmpty(folderPath) || !AssetDatabase.IsValidFolder(folderPath))
            {
                return; // 没选文件夹，且不是全工程，不扫
            }
            searchFolders = new[] { folderPath };
            mcFolderPaths.Add(folderPath);
        }

        foreach (string guid in AssetDatabase.FindAssets("t:Material", searchFolders))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (!string.IsNullOrEmpty(path)
                && path.EndsWith(".mat", StringComparison.OrdinalIgnoreCase)
                && done.Add(path))
            {
                mcMaterialPaths.Add(path);
            }
        }

        mcMaterialPaths.Sort(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>选择变化后立即分析全部材质，保证列表标记与统计完整。</summary>
    private void AnalyzeAllMaterials()
    {
        bool progress = mcMaterialPaths.Count > 30;
        for (int i = 0; i < mcMaterialPaths.Count; i++)
        {
            string path = mcMaterialPaths[i];
            if (progress)
            {
                EditorUtility.DisplayProgressBar("分析材质", path, (float)i / Mathf.Max(1, mcMaterialPaths.Count));
            }
            mcAnalysis[path] = AnalyzeMaterial(path);
        }
        if (progress)
        {
            EditorUtility.ClearProgressBar();
        }
    }

    // ================= 清理 =================

    private void CleanMaterials()
    {
        int processed = 0;
        int skippedShader = 0;
        int removedTex = 0;
        int removedFloat = 0;
        int removedColor = 0;
        int removedInt = 0;
        List<string> details = new List<string>();

        try
        {
            for (int i = 0; i < mcMaterialPaths.Count; i++)
            {
                string path = mcMaterialPaths[i];
                EditorUtility.DisplayProgressBar("清理材质冗余数据", path, (float)i / Mathf.Max(1, mcMaterialPaths.Count));

                Material mat = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (mat == null)
                {
                    continue;
                }

                Shader shader = mat.shader;
                if (shader == null || shader.name == "Hidden/InternalErrorShader")
                {
                    skippedShader++;
                    details.Add("[跳过-Shader丢失] " + path);
                    continue;
                }

                int before = removedTex + removedFloat + removedColor + removedInt;

                SerializedObject so = new SerializedObject(mat);
                SerializedProperty saved = so.FindProperty("m_SavedProperties");
                if (saved != null)
                {
                    HashSet<string> validProps = new HashSet<string>();
                    int propCount = shader.GetPropertyCount();
                    for (int p = 0; p < propCount; p++)
                    {
                        validProps.Add(shader.GetPropertyName(p));
                    }

                    removedTex += CleanSavedArray(saved, "m_TexEnvs", validProps);
                    removedInt += CleanSavedArray(saved, "m_Ints", validProps);
                    removedFloat += CleanSavedArray(saved, "m_Floats", validProps);
                    removedColor += CleanSavedArray(saved, "m_Colors", validProps);
                    so.ApplyModifiedPropertiesWithoutUndo();
                }

                int removed = removedTex + removedFloat + removedColor + removedInt - before;
                EditorUtility.SetDirty(mat);
                processed++;
                details.Add(string.Format("{0}  删除 {1} 项", path, removed));
            }

            AssetDatabase.SaveAssets();
        }
        catch (Exception e)
        {
            EditorUtility.ClearProgressBar();
            SetStatus("清理中断：" + e.Message);
            Debug.LogWarning("[VFX 工具箱] 材质清理中断：\n" + e);
            return;
        }

        EditorUtility.ClearProgressBar();

        string summary = string.Format(
            "材质清理完成：处理 {0} 个，跳过（Shader 丢失）{1} 个；删除冗余记录 贴图 {2} / 整数 {3} / 浮点 {4} / 颜色 {5} 项。",
            processed, skippedShader, removedTex, removedInt, removedFloat, removedColor);
        SetStatus(summary);
        Debug.Log("[VFX 工具箱] " + summary + "\n" + string.Join("\n", details));

        mcAnalysis.Clear();
        DestroyPreviewEditor();
        Repaint();
    }

    /// <summary>
    /// 删除 m_SavedProperties 下某个数组里当前 Shader 不存在的属性元素。
    /// 数组元素均为 (first=属性名, second=值) 的序列化对。
    /// </summary>
    private static int CleanSavedArray(SerializedProperty savedProperties, string arrayName, HashSet<string> validProps)
    {
        SerializedProperty array = savedProperties.FindPropertyRelative(arrayName);
        if (array == null || !array.isArray)
        {
            return 0;
        }

        int removed = 0;
        for (int i = array.arraySize - 1; i >= 0; i--)
        {
            SerializedProperty element = array.GetArrayElementAtIndex(i);
            SerializedProperty nameProp = element != null ? element.FindPropertyRelative("first") : null;
            if (nameProp == null)
            {
                continue;
            }

            string propName = nameProp.stringValue;
            if (!validProps.Contains(propName))
            {
                array.DeleteArrayElementAtIndex(i);
                removed++;
            }
        }
        return removed;
    }
}
