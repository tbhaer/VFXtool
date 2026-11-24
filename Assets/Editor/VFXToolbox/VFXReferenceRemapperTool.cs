using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

/// <summary>
/// 模块八：引用依赖检查与替换（分包排错用）。
/// 选中一个预制体或材质，分析它引用的材质 / 贴图 / 模型 / 动画 / 音频等资源：
///   · 左侧列出当前引用，右侧可指定替换资源——替换只暂存在面板，点“应用替换”才写回原文件；
///   · Shader 只展示不提供替换；
///   · 预制体模式下，材质行带“复制到本地”按钮，可把外部材质复制一份到指定目录并自动作为替换项；
///   · 另附只读的完整依赖清单（含 Shader、材质内部贴图等间接依赖）用于排查跨项目引用。
/// 预制体通过遍历所有组件的序列化 Object 引用进行重映射，材质直接改写贴图槽并保存。
/// </summary>
public partial class VFXToolboxWindow
{
    private Object rpTarget;
    private DefaultAsset rpCopyFolder;
    private string rpCopyPrefix = "Copy_";
    private readonly List<RefRow> rpRows = new List<RefRow>();
    private readonly List<string> rpDependencyPaths = new List<string>();
    private Vector2 rpRowScroll;
    private Vector2 rpDepScroll;
    private bool rpDepsFoldout = true;
    private static readonly string[] RpCategoryOrder =
    {
        "材质", "贴图/精灵", "模型网格", "动画", "音频", "动画控制器", "预制体", "字体", "其他", "Shader"
    };

    [Serializable]
    private class RefRow
    {
        public string category;
        public string displayName;
        public string assetPath;
        public string subAssetName;     // 子资源（如 FBX 里的 Mesh）名，主资源为空
        public string propertyName;     // 材质模式下的 shader 属性名
        public Object source;
        public Object replacement;
        public bool canRemap = true;
        public Material ownerMaterial;  // 材质下的贴图片：所属材质
        public List<RefRow> children;   // 材质下挂的贴图
    }

    private void InitReferenceRemapper()
    {
    }

    // ================= UI =================

    private void DrawReferenceRemapper()
    {
        EditorGUILayout.LabelField("引用依赖检查与替换", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "选中预制体或材质进行依赖分析。材质下列出它引用的贴图，可直接在对应行替换。\n" +
            "右侧指定替换资源或点“复制”生成本地副本，替换只暂存在面板，点“应用替换”才写回原文件；\n" +
            "复制文件按“前缀_01、前缀_02”自动续号，不会覆盖已有文件。Shader 只列出不替换。",
            MessageType.Info);

        EditorGUI.BeginChangeCheck();
        EditorGUILayout.BeginHorizontal();
        rpTarget = EditorGUILayout.ObjectField("分析目标", rpTarget, typeof(Object), false);
        if (GUILayout.Button("使用选中对象", GUILayout.Width(110)))
        {
            Object sel = Selection.activeObject;
            if (sel is GameObject || sel is Material)
            {
                rpTarget = sel;
            }
            else
            {
                SetStatus("请先在 Project 窗口选中预制体或材质。");
            }
        }
        EditorGUILayout.EndHorizontal();
        bool targetChanged = EditorGUI.EndChangeCheck();

        if (rpTarget != null && !IsSupportedRefTarget(rpTarget))
        {
            EditorGUILayout.HelpBox("仅支持 Project 里的预制体（.prefab）或材质（.mat）。", MessageType.Warning);
            return;
        }

        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("分析引用", GUILayout.Height(24)))
        {
            AnalyzeReferenceTarget();
        }
        if (GUILayout.Button("清空替换项", GUILayout.Height(24), GUILayout.Width(110)))
        {
            foreach (RefRow r in rpRows)
            {
                r.replacement = null;
            }
            SetStatus("已清空全部待替换项，原引用不变。");
        }
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.BeginHorizontal();
        rpCopyFolder = (DefaultAsset)EditorGUILayout.ObjectField(
            new GUIContent("复制目录", "每行“复制”按钮生成的新文件放在这里"),
            rpCopyFolder, typeof(DefaultAsset), false);
        if (GUILayout.Button("默认（目标同目录）", GUILayout.Width(160)))
        {
            rpCopyFolder = null;
        }
        EditorGUILayout.EndHorizontal();
        EditorGUILayout.BeginHorizontal();
        rpCopyPrefix = EditorGUILayout.TextField(new GUIContent("复制文件名前缀", "复制出的文件命名为：前缀_01、前缀_02 …"), rpCopyPrefix);
        EditorGUILayout.LabelField("示例：" + BuildCopyNamePreview("mat"), EditorStyles.miniLabel, GUILayout.Width(220));
        EditorGUILayout.EndHorizontal();

        if (targetChanged && rpTarget != null)
        {
            AnalyzeReferenceTarget();
        }

        if (rpRows.Count == 0 && rpDependencyPaths.Count == 0)
        {
            EditorGUILayout.HelpBox("点“分析引用”查看结果。", MessageType.None);
            return;
        }

        int replaceCount = CountPendingReplacements();
        EditorGUILayout.LabelField(
            string.Format("可替换引用 {0} 项，待应用替换 {1} 项", rpRows.Count, replaceCount),
            EditorStyles.boldLabel);

        rpRowScroll = EditorGUILayout.BeginScrollView(rpRowScroll, GUILayout.MinHeight(180), GUILayout.MaxHeight(340));
        string lastCategory = null;
        foreach (RefRow row in rpRows)
        {
            if (row.category != lastCategory)
            {
                lastCategory = row.category;
                EditorGUILayout.LabelField(lastCategory, EditorStyles.boldLabel);
            }
            DrawRefRow(row, 0);
        }
        EditorGUILayout.EndScrollView();

        EditorGUI.BeginDisabledGroup(replaceCount == 0);
        if (GUILayout.Button("应用替换（写回原文件）", GUILayout.Height(28)))
        {
            ApplyReplacements();
        }
        EditorGUI.EndDisabledGroup();

        rpDepsFoldout = EditorGUILayout.Foldout(rpDepsFoldout,
            "完整依赖清单（只读，含 Shader / 间接依赖，共 " + rpDependencyPaths.Count + " 项）");
        if (rpDepsFoldout)
        {
            rpDepScroll = EditorGUILayout.BeginScrollView(rpDepScroll, GUILayout.MinHeight(80), GUILayout.MaxHeight(160));
            foreach (string p in rpDependencyPaths)
            {
                Type t = AssetDatabase.GetMainAssetTypeAtPath(p);
                string tag = t != null ? t.Name : "?";
                EditorGUILayout.LabelField("  [" + tag + "] " + p, EditorStyles.miniLabel);
            }
            EditorGUILayout.EndScrollView();
        }
    }

    private static bool IsSupportedRefTarget(Object o)
    {
        string path = AssetDatabase.GetAssetPath(o);
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }
        return path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".mat", StringComparison.OrdinalIgnoreCase);
    }

    private void DrawRefRow(RefRow row, int indent)
    {
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            EditorGUILayout.BeginHorizontal();
            if (indent > 0)
            {
                GUILayout.Space(indent * 18f);
                GUILayout.Label(indent > 0 ? "└ 贴图" : "", EditorStyles.miniBoldLabel, GUILayout.Width(52));
            }
            EditorGUILayout.LabelField(row.displayName, EditorStyles.boldLabel);
            EditorGUILayout.EndHorizontal();

            string loc = string.IsNullOrEmpty(row.subAssetName)
                ? row.assetPath
                : row.assetPath + "  →  " + row.subAssetName;
            EditorGUILayout.BeginHorizontal();
            if (indent > 0)
            {
                GUILayout.Space(indent * 18f + 52f);
            }
            EditorGUILayout.LabelField(loc, EditorStyles.miniLabel);
            EditorGUILayout.EndHorizontal();

            if (!row.canRemap)
            {
                EditorGUILayout.LabelField("（Shader 不提供替换）", EditorStyles.miniLabel);
            }
            else
            {
                EditorGUILayout.BeginHorizontal();
                if (indent > 0)
                {
                    GUILayout.Space(indent * 18f);
                }
                GUILayout.Label("当前", GUILayout.Width(36));
                using (new EditorGUI.DisabledScope(true))
                {
                    EditorGUILayout.ObjectField(row.source, row.source != null ? row.source.GetType() : typeof(Object), false);
                }
                EditorGUILayout.EndHorizontal();

                EditorGUILayout.BeginHorizontal();
                if (indent > 0)
                {
                    GUILayout.Space(indent * 18f);
                }
                GUILayout.Label("替换为", GUILayout.Width(36));
                Type fieldType = row.source != null ? row.source.GetType() : typeof(Object);
                row.replacement = EditorGUILayout.ObjectField(row.replacement, fieldType, false);

                if (GUILayout.Button("复制", GUILayout.Width(60)))
                {
                    CopyRowAssetToLocal(row);
                }
                EditorGUILayout.EndHorizontal();
            }
        }

        if (row.children != null)
        {
            foreach (RefRow child in row.children)
            {
                DrawRefRow(child, indent + 1);
            }
        }
    }

    private int CountPendingReplacements()
    {
        int n = 0;
        foreach (RefRow r in rpRows)
        {
            if (r.replacement != null && r.replacement != r.source)
            {
                n++;
            }
            if (r.children != null)
            {
                foreach (RefRow ch in r.children)
                {
                    if (ch.replacement != null && ch.replacement != ch.source)
                    {
                        n++;
                    }
                }
            }
        }
        return n;
    }

    // ================= 分析 =================

    private void AnalyzeReferenceTarget()
    {
        rpRows.Clear();
        rpDependencyPaths.Clear();
        if (rpTarget == null || !IsSupportedRefTarget(rpTarget))
        {
            SetStatus("请指定预制体或材质。");
            return;
        }
        string path = AssetDatabase.GetAssetPath(rpTarget);

        // 完整依赖清单（只读）
        string[] deps = AssetDatabase.GetDependencies(path, true);
        foreach (string d in deps)
        {
            if (!string.IsNullOrEmpty(d) && d != path && !rpDependencyPaths.Contains(d))
            {
                rpDependencyPaths.Add(d);
            }
        }
        rpDependencyPaths.Sort(StringComparer.OrdinalIgnoreCase);

        if (rpTarget is Material)
        {
            AnalyzeMaterialTarget((Material)rpTarget);
        }
        else
        {
            AnalyzePrefabTarget(path);
        }

        SetStatus(string.Format("分析完成：可替换引用 {0} 项，完整依赖 {1} 项。",
            rpRows.Count, rpDependencyPaths.Count));
    }

    private void AnalyzeMaterialTarget(Material mat)
    {
        Shader shader = mat.shader;
        if (shader != null)
        {
            rpRows.Add(new RefRow
            {
                category = "Shader",
                displayName = "Shader：" + shader.name,
                assetPath = AssetDatabase.GetAssetPath(shader),
                source = shader,
                canRemap = false
            });
        }

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
            Texture tex = mat.GetTexture(propName);
            if (tex == null)
            {
                continue; // 空槽位无需替换
            }
            AddRow(tex, propName);
        }
    }

    private void AnalyzePrefabTarget(string prefabPath)
    {
        GameObject root = PrefabUtility.LoadPrefabContents(prefabPath);
        try
        {
            Component[] components = root.GetComponentsInChildren<Component>(true);
            foreach (Component c in components)
            {
                if (c == null)
                {
                    continue; // 丢失脚本的组件
                }
                SerializedObject so = new SerializedObject(c);
                SerializedProperty prop = so.GetIterator();
                prop.Next(true); // 进入根节点
                while (prop.Next(true)) // 深度优先，含数组元素（如 m_Materials.Array.data[n]）
                {
                    if (prop.propertyType == SerializedPropertyType.ObjectReference && prop.objectReferenceValue != null)
                    {
                        Object referenced = prop.objectReferenceValue;
                        if (EditorUtility.IsPersistent(referenced))
                        {
                            AddRow(referenced, null);
                        }
                    }
                }
            }
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }

        // 给每个材质行挂上它引用的贴图（子项），并收集这些贴图路径
        HashSet<string> materialOwnedTextures = new HashSet<string>();
        foreach (RefRow row in rpRows)
        {
            Material mat = row.source as Material;
            if (mat == null || !string.IsNullOrEmpty(row.subAssetName))
            {
                continue;
            }
            List<RefRow> children = BuildMaterialTextureChildren(mat);
            if (children.Count > 0)
            {
                row.children = children;
                foreach (RefRow ch in children)
                {
                    materialOwnedTextures.Add(ch.assetPath);
                }
            }
        }

        // 仅通过材质间接引用的贴图不再在顶层重复列出
        rpRows.RemoveAll(r =>
            (r.source is Texture) && string.IsNullOrEmpty(r.subAssetName)
            && materialOwnedTextures.Contains(r.assetPath));

        rpRows.Sort((a, b) =>
        {
            int ca = Array.IndexOf(RpCategoryOrder, a.category);
            int cb = Array.IndexOf(RpCategoryOrder, b.category);
            if (ca != cb)
            {
                return ca.CompareTo(cb);
            }
            return string.CompareOrdinal(a.displayName, b.displayName);
        });
    }

    private List<RefRow> BuildMaterialTextureChildren(Material mat)
    {
        List<RefRow> children = new List<RefRow>();
        Shader shader = mat.shader;
        if (shader == null)
        {
            return children;
        }
        int count = ShaderUtil.GetPropertyCount(shader);
        for (int i = 0; i < count; i++)
        {
            if (ShaderUtil.GetPropertyType(shader, i) != ShaderUtil.ShaderPropertyType.TexEnv)
            {
                continue;
            }
            string propName = ShaderUtil.GetPropertyName(shader, i);
            Texture tex = mat.GetTexture(propName);
            if (tex == null)
            {
                continue;
            }
            string assetPath = AssetDatabase.GetAssetPath(tex);
            if (string.IsNullOrEmpty(assetPath))
            {
                continue;
            }
            string subName = AssetDatabase.IsSubAsset(tex) ? tex.name : "";
            string fileName = Path.GetFileNameWithoutExtension(assetPath);
            RefRow child = new RefRow
            {
                category = "贴图/精灵",
                assetPath = assetPath,
                subAssetName = subName,
                propertyName = propName,
                source = tex,
                ownerMaterial = mat,
                canRemap = true,
                displayName = propName + "：" + (string.IsNullOrEmpty(subName) ? fileName : fileName + " / " + subName)
                    + "（" + tex.GetType().Name + "）"
            };
            children.Add(child);
        }
        return children;
    }

    private void AddRow(Object referenced, string propertyName)
    {
        string assetPath = AssetDatabase.GetAssetPath(referenced);
        if (string.IsNullOrEmpty(assetPath))
        {
            return;
        }
        string subName = AssetDatabase.IsSubAsset(referenced) ? referenced.name : "";
        foreach (RefRow existing in rpRows)
        {
            if (existing.assetPath == assetPath && existing.subAssetName == subName
                && (propertyName == null || existing.propertyName == propertyName))
            {
                return;
            }
        }

        RefRow row = new RefRow
        {
            category = CategoryOf(referenced),
            assetPath = assetPath,
            subAssetName = subName,
            propertyName = propertyName,
            source = referenced,
            canRemap = !(referenced is Shader)
        };
        string fileName = Path.GetFileNameWithoutExtension(assetPath);
        row.displayName = string.IsNullOrEmpty(subName)
            ? fileName + "（" + referenced.GetType().Name + "）"
            : fileName + " / " + subName + "（" + referenced.GetType().Name + "）";
        if (!string.IsNullOrEmpty(propertyName))
        {
            row.displayName = propertyName + "：" + row.displayName;
        }
        rpRows.Add(row);
    }

    private static string CategoryOf(Object o)
    {
        if (o is Shader) return "Shader";
        if (o is Material) return "材质";
        if (o is Texture || o is Sprite) return "贴图/精灵";
        if (o is Mesh) return "模型网格";
        if (o is AnimationClip) return "动画";
        if (o is AudioClip) return "音频";
        if (o is RuntimeAnimatorController) return "动画控制器";
        if (o is GameObject) return "预制体";
        if (o is Font) return "字体";
        return "其他";
    }

    // ================= 复制引用文件到本地 =================

    private void CopyRowAssetToLocal(RefRow row)
    {
        string srcPath = row.assetPath;
        if (string.IsNullOrEmpty(srcPath) || !srcPath.StartsWith("Assets", StringComparison.OrdinalIgnoreCase))
        {
            SetStatus("该引用不是工程内资源文件，无法复制。");
            return;
        }

        string folder;
        if (rpCopyFolder != null && AssetDatabase.IsValidFolder(AssetDatabase.GetAssetPath(rpCopyFolder)))
        {
            folder = AssetDatabase.GetAssetPath(rpCopyFolder);
        }
        else
        {
            folder = Path.GetDirectoryName(AssetDatabase.GetAssetPath(rpTarget)).Replace('\\', '/');
        }
        EnsureRpfolder(folder);

        string ext = Path.GetExtension(srcPath);
        string leaf = BuildCopyName(folder, ext);
        string dstPath = ToAssetPath(folder, leaf);
        if (!AssetDatabase.CopyAsset(srcPath, dstPath))
        {
            SetStatus("复制失败：" + srcPath);
            return;
        }
        AssetDatabase.ImportAsset(dstPath, ImportAssetOptions.ForceSynchronousImport);
        // 克隆导入设置（贴图类型 / 法线 / sRGB / 模型缩放动画等）
        CopyImporterSettings(srcPath, dstPath);

        Object newAsset;
        if (string.IsNullOrEmpty(row.subAssetName))
        {
            // 主资源（材质 / 贴图 / 动画片段文件等）
            Type t = row.source != null ? row.source.GetType() : typeof(Object);
            newAsset = AssetDatabase.LoadAssetAtPath(dstPath, t);
        }
        else
        {
            // 子资源（FBX 里的 Mesh 等）：先按名字匹配，再按同类型序号匹配
            newAsset = MatchSubAsset(row.source, srcPath, dstPath);
        }

        if (newAsset == null)
        {
            SetStatus("复制完成但新资源加载失败：" + dstPath);
            return;
        }
        row.replacement = newAsset;
        EditorGUIUtility.PingObject(newAsset);
        SetStatus("已复制 " + Path.GetFileName(srcPath) + " 到 " + dstPath + "，并设为替换项（点“应用替换”后生效）。");
    }

    private static Object MatchSubAsset(Object source, string srcPath, string dstPath)
    {
        Object[] srcAll = AssetDatabase.LoadAllAssetsAtPath(srcPath);
        Object[] dstAll = AssetDatabase.LoadAllAssetsAtPath(dstPath);
        Type t = source.GetType();

        // 1) 同名同类型
        foreach (Object o in dstAll)
        {
            if (o.GetType() == t && o.name == source.name)
            {
                return o;
            }
        }

        // 2) 同类型中的序号
        int ordinal = -1;
        foreach (Object o in srcAll)
        {
            if (o.GetType() != t)
            {
                continue;
            }
            ordinal++;
            if (o == source)
            {
                break;
            }
        }
        int seen = -1;
        foreach (Object o in dstAll)
        {
            if (o.GetType() != t)
            {
                continue;
            }
            seen++;
            if (seen == ordinal)
            {
                return o;
            }
        }
        return null;
    }

    private string BuildCopyNamePreview(string ext)
    {
        string prefix = string.IsNullOrEmpty(rpCopyPrefix) ? "Copy_" : rpCopyPrefix;
        return prefix + "_01." + ext;
    }

    /// <summary>
    /// 复制文件命名：前缀_01、前缀_02……；扫描目标目录里已有同前缀文件续号，
    /// 并做存在性兜底，任何情况下都不会覆盖已有文件。
    /// </summary>
    private string BuildCopyName(string folder, string ext)
    {
        string prefix = string.IsNullOrEmpty(rpCopyPrefix) ? "Copy_" : rpCopyPrefix;
        int next = 1;
        string absFolder = ToAbsolutePath(folder);
        if (!string.IsNullOrEmpty(absFolder) && Directory.Exists(absFolder))
        {
            foreach (string file in Directory.GetFiles(absFolder, prefix + "_*" + ext))
            {
                string name = Path.GetFileNameWithoutExtension(file);
                string numPart = name.Substring(prefix.Length + 1);
                int num;
                if (int.TryParse(numPart, out num) && num >= next)
                {
                    next = num + 1;
                }
            }
        }
        string leaf;
        string assetPath;
        do
        {
            leaf = prefix + "_" + next.ToString("D2") + ext;
            assetPath = ToAssetPath(folder, leaf);
            next++;
        }
        while (File.Exists(assetPath) || AssetDatabase.LoadAssetAtPath<Object>(assetPath) != null);
        return leaf;
    }

    private static void EnsureRpfolder(string assetFolderPath)
    {
        assetFolderPath = assetFolderPath.Replace('\\', '/').TrimEnd('/');
        if (AssetDatabase.IsValidFolder(assetFolderPath))
        {
            return;
        }
        string parent = Path.GetDirectoryName(assetFolderPath).Replace('\\', '/');
        string leaf = Path.GetFileName(assetFolderPath);
        if (!AssetDatabase.IsValidFolder(parent))
        {
            EnsureRpfolder(parent);
        }
        AssetDatabase.CreateFolder(parent, leaf);
    }

    // ================= 应用替换 =================

    private void ApplyReplacements()
    {
        int pending = CountPendingReplacements();
        if (pending == 0)
        {
            return;
        }
        bool ok = EditorUtility.DisplayDialog(
            "确认应用替换",
            string.Format("将把 {0} 处引用替换为新资源，并直接写回原文件：\n{1}\n建议先提交版本管理。是否继续？",
                pending, AssetDatabase.GetAssetPath(rpTarget)),
            "应用", "取消");
        if (!ok)
        {
            return;
        }

        if (rpTarget is Material)
        {
            ApplyMaterialReplacements();
        }
        else
        {
            ApplyPrefabReplacements();
        }
    }

    private void ApplyMaterialReplacements()
    {
        string path = AssetDatabase.GetAssetPath(rpTarget);
        Material mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat == null)
        {
            SetStatus("材质加载失败：" + path);
            return;
        }
        int changed = 0;
        foreach (RefRow row in rpRows)
        {
            if (row.replacement == null || row.replacement == row.source)
            {
                continue;
            }
            Texture newTex = row.replacement as Texture;
            if (newTex == null || string.IsNullOrEmpty(row.propertyName))
            {
                continue;
            }
            mat.SetTexture(row.propertyName, newTex);
            changed++;
        }
        EditorUtility.SetDirty(mat);
        AssetDatabase.SaveAssets();
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
        FinishApply(changed);
    }

    private void ApplyPrefabReplacements()
    {
        string prefabPath = AssetDatabase.GetAssetPath(rpTarget);
        // 资源键（路径 + 子资源名） -> 新资源
        Dictionary<string, Object> remap = new Dictionary<string, Object>();
        foreach (RefRow row in rpRows)
        {
            if (row.replacement != null && row.replacement != row.source)
            {
                remap[ResourceKey(row.assetPath, row.subAssetName)] = row.replacement;
            }
        }

        // 材质下挂的贴图替换：材质已复制就写到新材质，否则写回原材质
        int changed = 0;
        HashSet<Material> dirtyMaterials = new HashSet<Material>();
        foreach (RefRow row in rpRows)
        {
            Material srcMat = row.source as Material;
            if (srcMat == null || row.children == null)
            {
                continue;
            }
            Material targetMat = row.replacement as Material;
            if (targetMat == null)
            {
                targetMat = AssetDatabase.LoadAssetAtPath<Material>(row.assetPath);
            }
            if (targetMat == null)
            {
                continue;
            }
            foreach (RefRow child in row.children)
            {
                Texture newTex = child.replacement as Texture;
                if (newTex != null && newTex != child.source)
                {
                    targetMat.SetTexture(child.propertyName, newTex);
                    dirtyMaterials.Add(targetMat);
                    changed++;
                }
            }
        }
        foreach (Material m in dirtyMaterials)
        {
            EditorUtility.SetDirty(m);
        }
        if (dirtyMaterials.Count > 0)
        {
            AssetDatabase.SaveAssets();
        }

        GameObject root = PrefabUtility.LoadPrefabContents(prefabPath);
        try
        {
            Component[] components = root.GetComponentsInChildren<Component>(true);
            foreach (Component c in components)
            {
                if (c == null)
                {
                    continue;
                }
                SerializedObject so = new SerializedObject(c);
                bool compChanged = false;
                SerializedProperty prop = so.GetIterator();
                prop.Next(true);
                while (prop.Next(true))
                {
                    if (prop.propertyType != SerializedPropertyType.ObjectReference || prop.objectReferenceValue == null)
                    {
                        continue;
                    }
                    Object referenced = prop.objectReferenceValue;
                    string p = AssetDatabase.GetAssetPath(referenced);
                    if (string.IsNullOrEmpty(p))
                    {
                        continue;
                    }
                    string sub = AssetDatabase.IsSubAsset(referenced) ? referenced.name : "";
                    Object newAsset;
                    if (remap.TryGetValue(ResourceKey(p, sub), out newAsset) && newAsset != null)
                    {
                        prop.objectReferenceValue = newAsset;
                        compChanged = true;
                        changed++;
                    }
                }
                if (compChanged)
                {
                    so.ApplyModifiedPropertiesWithoutUndo();
                }
            }

            PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        FinishApply(changed);
    }

    private static string ResourceKey(string path, string subAssetName)
    {
        return path + "|" + (subAssetName ?? "");
    }

    private void FinishApply(int changed)
    {
        SetStatus("替换完成：共写回 " + changed + " 处引用。");
        Debug.Log("[VFX 工具箱] 引用替换完成，写回 " + changed + " 处：" + AssetDatabase.GetAssetPath(rpTarget));
        Object target = rpTarget;
        AnalyzeReferenceTarget();
        rpTarget = target;
        Object refreshed = AssetDatabase.LoadAssetAtPath<Object>(AssetDatabase.GetAssetPath(target));
        if (refreshed != null)
        {
            EditorGUIUtility.PingObject(refreshed);
        }
    }
}
