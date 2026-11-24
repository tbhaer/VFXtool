using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

/// <summary>
/// 模块五：特效资源提取分包（由 Assets/ABC/ExtractAndCopyResources.cs 分析重写）。
/// 选一个特效预制体，把它和依赖的材质 / 贴图 / 模型网格复制到一套独立目录，
/// 并按类型分子目录存放、编号命名，复制出的新预制体引用全部指向新文件，用于分包。
/// 相对原脚本的修复：
///   · 用 PrefabUtility.LoadPrefabContents 编辑预制体，不污染场景；
///   · 模型支持 FBX 等导入资源的子资源 Mesh（按文件内顺序映射），
///     并覆盖 MeshFilter / SkinnedMeshRenderer / ParticleSystemRenderer / 拖尾材质；
///   · 复制贴图 / 模型时克隆原资源的 Importer 设置（法线贴图、sRGB、缩放、动画等不丢）；
///   · 路径统一为 Assets 相对正斜杠；重复执行自动生成新目录不覆盖；
///   · 内嵌在 FBX 里的材质等无法独立复制的资源会跳过并在报告中列出。
/// </summary>
public partial class VFXToolboxWindow
{
    private GameObject exPrefab;
    private DefaultAsset exRootFolderAsset;
    private string exRootFolderPath = "Assets/ExtractedResources";

    private bool exIncludeMaterials = true;
    private bool exIncludeTextures = true;
    private bool exIncludeModels = true;
    private bool exAdvancedFoldout;

    private string exMaterialPrefix = "Material_";
    private string exTexturePrefix = "Texture_";
    private string exModelPrefix = "Model_";
    private string exMaterialFolder = "Material";
    private string exTextureFolder = "Texture";
    private string exModelFolder = "Model";

    private bool exSelectionDirty = true;
    private ExtractAnalysis exAnalysis;
    private Vector2 exPreviewScroll;

    /// <summary>一次依赖分析的结果。</summary>
    private class ExtractAnalysis
    {
        public readonly List<string> materials = new List<string>();
        public readonly List<string> textures = new List<string>();
        public readonly List<string> modelFiles = new List<string>();
        public readonly List<string> warnings = new List<string>();
    }

    private void InitExtractTool()
    {
    }

    // ================= UI =================

    private void DrawExtractTool()
    {
        EditorGUILayout.LabelField("特效资源提取分包", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "把预制体及其材质 / 贴图 / 模型网格复制成一套独立资源并按类型分目录，新预制体只引用新文件，便于分包。\n" +
            "可勾选需要复制的资源类型；Shader、脚本等公共资源保持原引用不复制。",
            MessageType.Info);

        SyncExtractSelection();

        EditorGUI.BeginChangeCheck();
        exPrefab = (GameObject)EditorGUILayout.ObjectField(
            new GUIContent("目标预制体", "仅支持 Project 里的 .prefab 资源"),
            exPrefab, typeof(GameObject), false);

        EditorGUILayout.BeginHorizontal();
        exRootFolderAsset = (DefaultAsset)EditorGUILayout.ObjectField(
            new GUIContent("输出根目录", "复制结果会生成在 根目录/预制体名/ 下"),
            exRootFolderAsset, typeof(DefaultAsset), false);
        if (GUILayout.Button("选择目录", GUILayout.Width(80)))
        {
            string abs = EditorUtility.OpenFolderPanel("选择输出根目录", Application.dataPath, "ExtractedResources");
            if (!string.IsNullOrEmpty(abs))
            {
                string rel = ToAssetsRelative(abs);
                if (rel == null)
                {
                    SetStatus("输出目录必须在工程的 Assets 目录内。");
                }
                else
                {
                    exRootFolderPath = rel;
                    exRootFolderAsset = AssetDatabase.LoadAssetAtPath<DefaultAsset>(rel);
                }
            }
        }
        EditorGUILayout.EndHorizontal();

        if (exRootFolderAsset != null && AssetDatabase.IsValidFolder(AssetDatabase.GetAssetPath(exRootFolderAsset)))
        {
            exRootFolderPath = AssetDatabase.GetAssetPath(exRootFolderAsset);
        }
        EditorGUILayout.LabelField("输出路径：" + exRootFolderPath + "/<预制体名>/", EditorStyles.miniLabel);

        EditorGUILayout.Space(4);
        EditorGUILayout.LabelField("复制资源类型", EditorStyles.boldLabel);
        exIncludeMaterials = EditorGUILayout.ToggleLeft("复制材质（并把预制体材质引用换为新材质）", exIncludeMaterials);
        using (new EditorGUI.DisabledScope(!exIncludeMaterials))
        {
            exIncludeTextures = EditorGUILayout.ToggleLeft("复制材质引用的贴图（并更新为新贴图）", exIncludeTextures);
        }
        exIncludeModels = EditorGUILayout.ToggleLeft("复制模型网格（MeshFilter / 蒙皮网格 / 粒子网格）", exIncludeModels);

        exAdvancedFoldout = EditorGUILayout.Foldout(exAdvancedFoldout, "高级：子目录与命名前缀");
        if (exAdvancedFoldout)
        {
            EditorGUI.indentLevel++;
            exMaterialFolder = EditorGUILayout.TextField("材质子目录", exMaterialFolder);
            exTextureFolder = EditorGUILayout.TextField("贴图片目录", exTextureFolder);
            exModelFolder = EditorGUILayout.TextField("模型子目录", exModelFolder);
            exMaterialPrefix = EditorGUILayout.TextField("材质前缀", exMaterialPrefix);
            exTexturePrefix = EditorGUILayout.TextField("贴图前缀", exTexturePrefix);
            exModelPrefix = EditorGUILayout.TextField("模型前缀", exModelPrefix);
            EditorGUI.indentLevel--;
        }

        bool prefabChanged = EditorGUI.EndChangeCheck();
        if (prefabChanged)
        {
            exAnalysis = null;
        }

        EditorGUILayout.Space(4);
        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("分析依赖", GUILayout.Height(24)))
        {
            AnalyzeCurrentPrefab();
        }
        EditorGUI.BeginDisabledGroup(exPrefab == null || !IsPrefabAsset(exPrefab));
        if (GUILayout.Button("执行提取复制", GUILayout.Height(24)))
        {
            if (AnalyzeCurrentPrefab())
            {
                RunExtraction();
            }
        }
        EditorGUI.EndDisabledGroup();
        EditorGUILayout.EndHorizontal();

        DrawAnalysisPreview();
    }

    private void SyncExtractSelection()
    {
        if (!exSelectionDirty)
        {
            return;
        }
        exSelectionDirty = false;
        if (Selection.activeObject is GameObject go && IsPrefabAsset(go))
        {
            exPrefab = go;
            exAnalysis = null;
        }
    }

    private static bool IsPrefabAsset(GameObject go)
    {
        if (go == null)
        {
            return false;
        }
        string path = AssetDatabase.GetAssetPath(go);
        return !string.IsNullOrEmpty(path) && path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase);
    }

    private void DrawAnalysisPreview()
    {
        if (exPrefab == null || !IsPrefabAsset(exPrefab))
        {
            EditorGUILayout.HelpBox("请指定一个 Project 内的预制体（.prefab）。", MessageType.None);
            return;
        }
        if (exAnalysis == null)
        {
            EditorGUILayout.HelpBox("点“分析依赖”查看将复制的资源。", MessageType.None);
            return;
        }

        EditorGUILayout.Space(4);
        EditorGUILayout.LabelField(
            string.Format("分析结果：材质 {0}，贴图 {1}，模型文件 {2}",
                exIncludeMaterials ? exAnalysis.materials.Count : 0,
                exIncludeMaterials && exIncludeTextures ? exAnalysis.textures.Count : 0,
                exIncludeModels ? exAnalysis.modelFiles.Count : 0),
            EditorStyles.boldLabel);

        exPreviewScroll = EditorGUILayout.BeginScrollView(exPreviewScroll, GUILayout.MinHeight(120), GUILayout.MaxHeight(240));
        if (exIncludeMaterials)
        {
            EditorGUILayout.LabelField("材质（" + exAnalysis.materials.Count + "）", EditorStyles.boldLabel);
            foreach (string p in exAnalysis.materials)
            {
                EditorGUILayout.LabelField("  " + p, EditorStyles.miniLabel);
            }
            if (exIncludeTextures)
            {
                EditorGUILayout.LabelField("贴图（" + exAnalysis.textures.Count + "）", EditorStyles.boldLabel);
                foreach (string p in exAnalysis.textures)
                {
                    EditorGUILayout.LabelField("  " + p, EditorStyles.miniLabel);
                }
            }
        }
        if (exIncludeModels)
        {
            EditorGUILayout.LabelField("模型文件（" + exAnalysis.modelFiles.Count + "）", EditorStyles.boldLabel);
            foreach (string p in exAnalysis.modelFiles)
            {
                EditorGUILayout.LabelField("  " + p, EditorStyles.miniLabel);
            }
        }
        if (exAnalysis.warnings.Count > 0)
        {
            EditorGUILayout.LabelField("注意（" + exAnalysis.warnings.Count + "）", EditorStyles.boldLabel);
            foreach (string w in exAnalysis.warnings)
            {
                EditorGUILayout.LabelField("  " + w, EditorStyles.miniLabel);
            }
        }
        EditorGUILayout.EndScrollView();
    }

    // ================= 依赖分析 =================

    private bool AnalyzeCurrentPrefab()
    {
        if (exPrefab == null || !IsPrefabAsset(exPrefab))
        {
            SetStatus("请先指定有效的预制体资源。");
            return false;
        }

        string prefabPath = AssetDatabase.GetAssetPath(exPrefab);
        GameObject root = PrefabUtility.LoadPrefabContents(prefabPath);
        try
        {
            exAnalysis = BuildAnalysis(root);
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }
        SetStatus(string.Format(
            "分析完成：材质 {0}，贴图 {1}，模型文件 {2}，注意项 {3}。",
            exAnalysis.materials.Count, exAnalysis.textures.Count,
            exAnalysis.modelFiles.Count, exAnalysis.warnings.Count));
        return true;
    }

    private ExtractAnalysis BuildAnalysis(GameObject root)
    {
        ExtractAnalysis a = new ExtractAnalysis();

        // 材质
        List<Material> materials = GatherMaterials(root, a);
        foreach (Material m in materials)
        {
            string p = AssetDatabase.GetAssetPath(m);
            if (!string.IsNullOrEmpty(p) && !a.materials.Contains(p))
            {
                a.materials.Add(p);
            }
        }

        // 贴图（来自材质当前 Shader 的贴图槽）
        if (exIncludeTextures)
        {
            foreach (Material m in materials)
            {
                CollectMaterialTextures(m, a.textures, a.warnings);
            }
        }

        // 模型文件
        if (exIncludeModels)
        {
            foreach (Mesh mesh in GatherMeshes(root))
            {
                string p = AssetDatabase.GetAssetPath(mesh);
                if (string.IsNullOrEmpty(p))
                {
                    continue;
                }
                if (p.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                {
                    if (!a.warnings.Contains("预制体内嵌网格无法复制：" + p))
                    {
                        a.warnings.Add("预制体内嵌网格无法复制：" + p);
                    }
                    continue;
                }
                if (!a.modelFiles.Contains(p))
                {
                    a.modelFiles.Add(p);
                }
            }
        }

        a.materials.Sort(StringComparer.OrdinalIgnoreCase);
        a.textures.Sort(StringComparer.OrdinalIgnoreCase);
        a.modelFiles.Sort(StringComparer.OrdinalIgnoreCase);
        return a;
    }

    private List<Material> GatherMaterials(GameObject root, ExtractAnalysis a)
    {
        List<Material> result = new List<Material>();
        Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
        foreach (Renderer r in renderers)
        {
            if (r.sharedMaterials == null)
            {
                continue;
            }
            foreach (Material m in r.sharedMaterials)
            {
                AddMaterial(m, result, a);
            }

            ParticleSystemRenderer psr = r as ParticleSystemRenderer;
            if (psr != null)
            {
                AddMaterial(psr.trailMaterial, result, a);
            }
        }
        return result;
    }

    private static void AddMaterial(Material m, List<Material> result, ExtractAnalysis a)
    {
        if (m == null || result.Contains(m))
        {
            return;
        }
        string path = AssetDatabase.GetAssetPath(m);
        if (string.IsNullOrEmpty(path))
        {
            return;
        }
        if (!path.EndsWith(".mat", StringComparison.OrdinalIgnoreCase))
        {
            string w = "内嵌材质（位于 " + Path.GetFileName(path) + " 内）将保持原引用：" + m.name;
            if (!a.warnings.Contains(w))
            {
                a.warnings.Add(w);
            }
            return;
        }
        result.Add(m);
    }

    private static void CollectMaterialTextures(Material m, List<string> textures, List<string> warnings)
    {
        Shader shader = m.shader;
        if (shader == null)
        {
            return;
        }
        int count = ShaderUtil.GetPropertyCount(shader);
        for (int i = 0; i < count; i++)
        {
            if (ShaderUtil.GetPropertyType(shader, i) != ShaderUtil.ShaderPropertyType.TexEnv)
            {
                continue;
            }
            string propName = ShaderUtil.GetPropertyName(shader, i);
            Texture tex = m.GetTexture(propName);
            if (tex == null)
            {
                continue;
            }
            string path = AssetDatabase.GetAssetPath(tex);
            if (string.IsNullOrEmpty(path))
            {
                continue;
            }
            if (!textures.Contains(path))
            {
                textures.Add(path);
            }
        }
    }

    private static List<Mesh> GatherMeshes(GameObject root)
    {
        List<Mesh> meshes = new List<Mesh>();

        foreach (MeshFilter mf in root.GetComponentsInChildren<MeshFilter>(true))
        {
            if (mf.sharedMesh != null && !meshes.Contains(mf.sharedMesh))
            {
                meshes.Add(mf.sharedMesh);
            }
        }
        foreach (SkinnedMeshRenderer smr in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            if (smr.sharedMesh != null && !meshes.Contains(smr.sharedMesh))
            {
                meshes.Add(smr.sharedMesh);
            }
        }
        foreach (ParticleSystemRenderer psr in root.GetComponentsInChildren<ParticleSystemRenderer>(true))
        {
            AddMesh(psr.mesh, meshes);
            foreach (Mesh m in GetParticleMeshList(psr))
            {
                AddMesh(m, meshes);
            }
        }
        return meshes;
    }

    /// <summary>粒子渲染器的多网格列表在该版本不是公开 API，通过序列化字段 m_Meshes 读取。</summary>
    private static List<Mesh> GetParticleMeshList(ParticleSystemRenderer psr)
    {
        List<Mesh> list = new List<Mesh>();
        SerializedObject so = new SerializedObject(psr);
        SerializedProperty prop = so.FindProperty("m_Meshes");
        if (prop != null && prop.isArray)
        {
            for (int i = 0; i < prop.arraySize; i++)
            {
                Mesh m = prop.GetArrayElementAtIndex(i).objectReferenceValue as Mesh;
                if (m != null && !list.Contains(m))
                {
                    list.Add(m);
                }
            }
        }
        return list;
    }

    private void RemapParticleMeshList(ParticleSystemRenderer psr, string modelDir,
        Dictionary<string, string> copiedFiles, Dictionary<Mesh, Mesh> meshMap,
        ExtractCounters counters, List<string> warnings)
    {
        SerializedObject so = new SerializedObject(psr);
        SerializedProperty prop = so.FindProperty("m_Meshes");
        if (prop == null || !prop.isArray)
        {
            return;
        }
        bool changed = false;
        for (int i = 0; i < prop.arraySize; i++)
        {
            SerializedProperty el = prop.GetArrayElementAtIndex(i);
            Mesh src = el.objectReferenceValue as Mesh;
            if (src == null)
            {
                continue;
            }
            Mesh nm;
            if (TryCopyMesh(src, modelDir, copiedFiles, meshMap, counters, warnings, out nm) && nm != null)
            {
                el.objectReferenceValue = nm;
                changed = true;
            }
        }
        if (changed)
        {
            so.ApplyModifiedPropertiesWithoutUndo();
        }
    }

    private static void AddMesh(Mesh m, List<Mesh> meshes)
    {
        if (m != null && !meshes.Contains(m))
        {
            meshes.Add(m);
        }
    }

    // ================= 执行复制 =================

    private void RunExtraction()
    {
        string prefabPath = AssetDatabase.GetAssetPath(exPrefab);
        string prefabName = exPrefab.name;

        // 输出目录（重复执行自动换新目录，不覆盖旧结果）
        string baseFolder = AssetDatabase.GenerateUniqueAssetPath(ToAssetPath(exRootFolderPath, prefabName));
        try
        {
            EnsureFolder(baseFolder);
            string materialDir = exIncludeMaterials ? EnsureFolder(ToAssetPath(baseFolder, SafeSegment(exMaterialFolder))) : "";
            string textureDir = exIncludeMaterials && exIncludeTextures ? EnsureFolder(ToAssetPath(baseFolder, SafeSegment(exTextureFolder))) : "";
            string modelDir = exIncludeModels ? EnsureFolder(ToAssetPath(baseFolder, SafeSegment(exModelFolder))) : "";

            GameObject root = PrefabUtility.LoadPrefabContents(prefabPath);
            ExtractCounters counters = new ExtractCounters();
            try
            {
                // 源资源路径 -> 新资源路径（文件级去重）
                Dictionary<string, string> copiedFiles = new Dictionary<string, string>();
                Dictionary<Material, Material> materialMap = new Dictionary<Material, Material>();
                Dictionary<Mesh, Mesh> meshMap = new Dictionary<Mesh, Mesh>();
                List<string> warnings = new List<string>();

                // 1) 材质 + 贴图
                if (exIncludeMaterials)
                {
                    foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true))
                    {
                        Material[] mats = r.sharedMaterials;
                        bool changed = false;
                        for (int i = 0; i < mats.Length; i++)
                        {
                            Material nm;
                            if (TryCopyMaterial(mats[i], materialDir, textureDir, copiedFiles, materialMap, counters, warnings, out nm))
                            {
                                mats[i] = nm;
                                changed = true;
                            }
                        }
                        if (changed)
                        {
                            r.sharedMaterials = mats;
                        }

                        ParticleSystemRenderer psr = r as ParticleSystemRenderer;
                        if (psr != null && psr.trailMaterial != null)
                        {
                            Material nm;
                            if (TryCopyMaterial(psr.trailMaterial, materialDir, textureDir, copiedFiles, materialMap, counters, warnings, out nm))
                            {
                                psr.trailMaterial = nm;
                            }
                        }
                    }
                }

                // 2) 模型网格
                if (exIncludeModels)
                {
                    foreach (MeshFilter mf in root.GetComponentsInChildren<MeshFilter>(true))
                    {
                        Mesh nm;
                        if (TryCopyMesh(mf.sharedMesh, modelDir, copiedFiles, meshMap, counters, warnings, out nm))
                        {
                            mf.sharedMesh = nm;
                        }
                    }
                    foreach (SkinnedMeshRenderer smr in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                    {
                        Mesh nm;
                        if (TryCopyMesh(smr.sharedMesh, modelDir, copiedFiles, meshMap, counters, warnings, out nm))
                        {
                            smr.sharedMesh = nm;
                        }
                    }
                    foreach (ParticleSystemRenderer psr in root.GetComponentsInChildren<ParticleSystemRenderer>(true))
                    {
                        Mesh nm;
                        if (TryCopyMesh(psr.mesh, modelDir, copiedFiles, meshMap, counters, warnings, out nm))
                        {
                            psr.mesh = nm;
                        }
                        RemapParticleMeshList(psr, modelDir, copiedFiles, meshMap, counters, warnings);
                    }
                }

                // 3) 保存新预制体
                string newPrefabPath = AssetDatabase.GenerateUniqueAssetPath(ToAssetPath(baseFolder, prefabName + ".prefab"));
                PrefabUtility.SaveAsPrefabAsset(root, newPrefabPath);

                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();

                Object newPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(newPrefabPath);
                if (newPrefab != null)
                {
                    EditorGUIUtility.PingObject(newPrefab);
                    Selection.activeObject = newPrefab;
                }

                string summary = string.Format(
                    "提取完成：{0}\n新预制体：{1}\n材质 {2}，贴图 {3}，模型文件 {4}，跳过/注意 {5} 项。",
                    baseFolder, newPrefabPath, counters.materials, counters.textures, counters.modelFiles, warnings.Count);
                SetStatus(summary);
                Debug.Log("[VFX 工具箱] " + summary + "\n" + string.Join("\n", warnings));
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
                EditorUtility.ClearProgressBar();
            }
        }
        catch (Exception e)
        {
            AssetDatabase.Refresh();
            SetStatus("提取失败：" + e.Message);
            Debug.LogWarning("[VFX 工具箱] 资源提取失败：\n" + e);
        }
    }

    private class ExtractCounters
    {
        public int materials;
        public int textures;
        public int modelFiles;
        public int materialCounter;
        public int textureCounter;
        public int modelCounter;
    }

    private bool TryCopyMaterial(Material src, string materialDir, string textureDir,
        Dictionary<string, string> copiedFiles, Dictionary<Material, Material> materialMap,
        ExtractCounters counters, List<string> warnings, out Material newMaterial)
    {
        newMaterial = null;
        if (src == null)
        {
            return false;
        }
        if (materialMap.TryGetValue(src, out newMaterial))
        {
            return newMaterial != null;
        }

        string srcPath = AssetDatabase.GetAssetPath(src);
        if (string.IsNullOrEmpty(srcPath) || !srcPath.EndsWith(".mat", StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add("内嵌材质保持原引用：" + src.name);
            materialMap[src] = null;
            return false;
        }

        string dstPath;
        if (!TryCopyFileAsset(srcPath, materialDir, exMaterialPrefix, counters.materialCounter, copiedFiles, out dstPath))
        {
            return false;
        }
        counters.materialCounter++;
        counters.materials++;

        newMaterial = AssetDatabase.LoadAssetAtPath<Material>(dstPath);
        materialMap[src] = newMaterial;
        if (newMaterial == null)
        {
            return false;
        }

        // 复制并重新指向贴图
        if (exIncludeTextures && !string.IsNullOrEmpty(textureDir))
        {
            Shader shader = newMaterial.shader;
            if (shader != null)
            {
                int propCount = ShaderUtil.GetPropertyCount(shader);
                for (int i = 0; i < propCount; i++)
                {
                    if (ShaderUtil.GetPropertyType(shader, i) != ShaderUtil.ShaderPropertyType.TexEnv)
                    {
                        continue;
                    }
                    string propName = ShaderUtil.GetPropertyName(shader, i);
                    Texture srcTex = src.GetTexture(propName);
                    if (srcTex == null)
                    {
                        continue;
                    }
                    string texPath = AssetDatabase.GetAssetPath(srcTex);
                    if (string.IsNullOrEmpty(texPath))
                    {
                        continue;
                    }
                    string texDst;
                    Texture newTex;
                    if (copiedFiles.TryGetValue(texPath, out texDst))
                    {
                        newTex = AssetDatabase.LoadAssetAtPath<Texture>(texDst);
                    }
                    else
                    {
                        if (!TryCopyFileAsset(texPath, textureDir, exTexturePrefix, counters.textureCounter, copiedFiles, out texDst))
                        {
                            continue;
                        }
                        counters.textureCounter++;
                        counters.textures++;
                        newTex = AssetDatabase.LoadAssetAtPath<Texture>(texDst);
                    }
                    if (newTex != null)
                    {
                        newMaterial.SetTexture(propName, newTex);
                    }
                }
                EditorUtility.SetDirty(newMaterial);
            }
        }
        return true;
    }

    private bool TryCopyMesh(Mesh src, string modelDir,
        Dictionary<string, string> copiedFiles, Dictionary<Mesh, Mesh> meshMap,
        ExtractCounters counters, List<string> warnings, out Mesh newMesh)
    {
        newMesh = null;
        if (src == null)
        {
            return false;
        }
        if (meshMap.TryGetValue(src, out newMesh))
        {
            return newMesh != null;
        }

        string srcPath = AssetDatabase.GetAssetPath(src);
        if (string.IsNullOrEmpty(srcPath))
        {
            return false;
        }
        if (srcPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add("预制体内嵌网格保持原引用：" + src.name);
            meshMap[src] = null;
            return false;
        }

        string dstPath;
        if (!copiedFiles.TryGetValue(srcPath, out dstPath))
        {
            if (!TryCopyFileAsset(srcPath, modelDir, exModelPrefix, counters.modelCounter, copiedFiles, out dstPath))
            {
                return false;
            }
            counters.modelCounter++;
            counters.modelFiles++;
        }

        // 子资源 Mesh 按文件内顺序映射（FBX 内含多个网格时也能对上）
        Object[] srcAll = AssetDatabase.LoadAllAssetsAtPath(srcPath);
        Object[] dstAll = AssetDatabase.LoadAllAssetsAtPath(dstPath);
        int index = -1;
        for (int i = 0; i < srcAll.Length; i++)
        {
            if (srcAll[i] is Mesh && srcAll[i] == src)
            {
                index = i;
                break;
            }
        }
        if (index < 0)
        {
            // 独立 Mesh 资源
            newMesh = AssetDatabase.LoadAssetAtPath<Mesh>(dstPath);
        }
        else
        {
            int meshOrdinal = -1;
            for (int i = 0; i <= index && i < srcAll.Length; i++)
            {
                if (srcAll[i] is Mesh)
                {
                    meshOrdinal++;
                }
            }
            int seen = -1;
            foreach (Object o in dstAll)
            {
                Mesh m = o as Mesh;
                if (m == null)
                {
                    continue;
                }
                seen++;
                if (seen == meshOrdinal)
                {
                    newMesh = m;
                    break;
                }
            }
        }

        meshMap[src] = newMesh;
        return newMesh != null;
    }

    /// <summary>
    /// 复制任意资源文件到目标目录（前缀 + 两位编号），并克隆同类型 Importer 设置。
    /// 同一源文件只复制一次。
    /// </summary>
    private bool TryCopyFileAsset(string srcPath, string dstDir, string prefix, int counter,
        Dictionary<string, string> copiedFiles, out string dstPath)
    {
        dstPath = null;
        if (string.IsNullOrEmpty(srcPath) || !srcPath.StartsWith("Assets", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (copiedFiles.TryGetValue(srcPath, out dstPath))
        {
            return true;
        }

        EnsureFolder(dstDir);
        string ext = Path.GetExtension(srcPath);
        string desired = ToAssetPath(dstDir, prefix + (counter + 1).ToString("D2") + ext);
        dstPath = AssetDatabase.GenerateUniqueAssetPath(desired);

        EditorUtility.DisplayProgressBar("提取复制资源", srcPath, 0.5f);

        if (!AssetDatabase.CopyAsset(srcPath, dstPath))
        {
            return false;
        }
        AssetDatabase.ImportAsset(dstPath, ImportAssetOptions.ForceSynchronousImport);
        CopyImporterSettings(srcPath, dstPath);

        copiedFiles[srcPath] = dstPath;
        return true;
    }

    /// <summary>把源资源的导入器设置（贴图类型 / 法线 / sRGB / 模型缩放动画等）克隆到新资源。</summary>
    private static void CopyImporterSettings(string srcPath, string dstPath)
    {
        AssetImporter src = AssetImporter.GetAtPath(srcPath);
        AssetImporter dst = AssetImporter.GetAtPath(dstPath);
        if (src == null || dst == null || src.GetType() != dst.GetType())
        {
            return;
        }

        SerializedObject srcSO = new SerializedObject(src);
        SerializedObject dstSO = new SerializedObject(dst);
        SerializedProperty it = dstSO.GetIterator();
        bool enterChildren = true;
        while (it.NextVisible(enterChildren))
        {
            enterChildren = false;
            SerializedProperty sp = srcSO.FindProperty(it.propertyPath);
            if (sp != null && sp.propertyType == it.propertyType)
            {
                dstSO.CopyFromSerializedProperty(sp);
            }
        }
        dstSO.ApplyModifiedPropertiesWithoutUndo();
        dst.SaveAndReimport();
    }

    // ================= 路径工具 =================

    private static string ToAssetPath(string folder, string leaf)
    {
        return (folder.TrimEnd('/') + "/" + leaf).Replace('\\', '/');
    }

    private static string SafeSegment(string s)
    {
        if (string.IsNullOrEmpty(s))
        {
            return "Misc";
        }
        foreach (char c in Path.GetInvalidPathChars())
        {
            s = s.Replace(c, '_');
        }
        foreach (char c in Path.GetInvalidFileNameChars())
        {
            s = s.Replace(c, '_');
        }
        return s.Trim('/');
    }

    /// <summary>逐级创建 Assets 下的文件夹（已存在则直接返回）。</summary>
    private static string EnsureFolder(string assetFolderPath)
    {
        assetFolderPath = assetFolderPath.Replace('\\', '/').TrimEnd('/');
        if (AssetDatabase.IsValidFolder(assetFolderPath))
        {
            return assetFolderPath;
        }

        string parent = Path.GetDirectoryName(assetFolderPath).Replace('\\', '/');
        string leaf = Path.GetFileName(assetFolderPath);
        if (!string.IsNullOrEmpty(parent) && !AssetDatabase.IsValidFolder(parent))
        {
            EnsureFolder(parent);
        }
        AssetDatabase.CreateFolder(parent, leaf);
        return assetFolderPath;
    }
}
