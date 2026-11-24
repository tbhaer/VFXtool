using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

/// <summary>
/// 模块十：未使用资源（零引用）扫描。
/// 扫描指定文件夹（递归）或整个 Assets：建立工程内所有文本序列化文件（预制体 / 场景 / 材质 /
/// 动画 / 控制器 / ScriptableObject / .meta 等）的 GUID 引用图，从“入口资源”（场景、Resources
/// 目录、Addressables 配置、打了 AssetBundle 标签的资源、SpriteAtlas）出发做可达性遍历，
/// 不可达的资源即疑似未使用。零引用 = 没有任何资源引用；仅被未使用资源引用 = 引用方自身也不可达。
/// 结果支持勾选删除与导出报告。按路径 / 名字动态加载的资源无法被识别，删除前需人工确认。
/// </summary>
public partial class VFXToolboxWindow
{
    private DefaultAsset uaFolder;
    private readonly List<UnusedItem> uaItems = new List<UnusedItem>();
    private bool uaScanning;
    private float uaProgress;
    private string uaProgressText = "";
    private Vector2 uaResultScroll;
    private string uaScanSummary = "";
    private string[] uaLastFolders;

    private static readonly string[] UaCategoryNames =
    {
        "贴图", "材质", "模型", "预制体", "动画/控制器", "Shader", "音频", "其他", "空文件夹"
    };
    private readonly bool[] uaCategoryEnabled =
    {
        true, true, true, true, true, true, true, true, true
    };

    // 参与 GUID 解析的文本序列化文件后缀（.meta 始终参与，模型导入器的外部材质引用写在 .meta 里）
    private static readonly HashSet<string> UaTextExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".prefab", ".unity", ".mat", ".anim", ".controller", ".overridecontroller", ".playable",
        ".asset", ".preset", ".spriteatlas", ".shadergraph", ".shadersubgraph", ".shader",
        ".cginc", ".hlsl", ".uxml", ".uss", ".json", ".xml", ".yaml", ".yml", ".txt",
        ".guiskin", ".flare", ".rendertexture", ".lighting", ".lightingsettings",
        ".terrainlayer", ".brush", ".cubemap", ".physicmaterial", ".physicmaterial2d",
        ".mask", ".signal", ".receiver", ".spritecustom"
    };

    // 不作为“未使用”候选的后缀：入口 / 代码 / 配置类
    private static readonly HashSet<string> UaExcludedExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".dll", ".meta", ".asmdef", ".asmref", ".rsp", ".unity", ".shadervariants",
        ".json", ".xml", ".txt", ".bytes", ".asmdef"
    };

    private static readonly Regex UaGuidRegex = new Regex("[a-f0-9]{32}", RegexOptions.Compiled);

    [Serializable]
    private class UnusedItem
    {
        public string path;
        public string guid;
        public string category;
        public long bytes;
        public int referencerCount;
        public bool chainOnly;   // true=仅被未使用资源引用；false=零引用
        public bool selected = true;
        public Object cachedAsset;
    }

    private void InitUnusedScanner()
    {
    }

    // ================= UI =================

    private void DrawUnusedAssetTool()
    {
        EditorGUILayout.LabelField("未使用资源扫描（零引用）", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "扫描没有被任何资源引用、或仅被其他未使用资源引用的资源（贴图/材质/模型/预制体等）。\n" +
            "场景、Resources 目录、Addressables 与 AssetBundle 标记的资源视为入口，不会列出。\n" +
            "注意：代码里按路径/名字动态加载（Resources.Load、Shader.Find 等）的资源无法识别，删除前请人工确认。",
            MessageType.Info);

        uaFolder = (DefaultAsset)EditorGUILayout.ObjectField(
            new GUIContent("扫描文件夹", "留空则扫描全工程；文件夹会递归包含所有子目录"),
            uaFolder, typeof(DefaultAsset), false);

        EditorGUILayout.BeginHorizontal();
        EditorGUI.BeginDisabledGroup(uaScanning);
        if (GUILayout.Button("扫描选中文件夹（含子目录）", GUILayout.Height(24)))
        {
            string folder = uaFolder != null ? AssetDatabase.GetAssetPath(uaFolder) : null;
            if (string.IsNullOrEmpty(folder) || !AssetDatabase.IsValidFolder(folder))
            {
                SetStatus("请指定有效的文件夹，或点“扫描全工程”。");
            }
            else
            {
                ScanUnusedAssets(new[] { folder });
            }
        }
        if (GUILayout.Button("扫描全工程（Assets）", GUILayout.Height(24), GUILayout.Width(200)))
        {
            ScanUnusedAssets(new[] { "Assets" });
        }
        EditorGUI.EndDisabledGroup();
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.Space(2);
        EditorGUILayout.LabelField("资源类型筛选", EditorStyles.miniBoldLabel);
        int perRow = 4;
        for (int row = 0; row < Mathf.CeilToInt(UaCategoryNames.Length / (float)perRow); row++)
        {
            EditorGUILayout.BeginHorizontal();
            for (int i = row * perRow; i < Mathf.Min((row + 1) * perRow, UaCategoryNames.Length); i++)
            {
                uaCategoryEnabled[i] = GUILayout.Toggle(uaCategoryEnabled[i], UaCategoryNames[i],
                    EditorStyles.toggle, GUILayout.MinWidth(90));
            }
            EditorGUILayout.EndHorizontal();
        }

        if (uaScanning)
        {
            Rect r = EditorGUILayout.GetControlRect(false, 22);
            EditorGUI.ProgressBar(r, uaProgress, uaProgressText);
            return;
        }

        if (!string.IsNullOrEmpty(uaScanSummary))
        {
            EditorGUILayout.HelpBox(uaScanSummary, MessageType.None);
        }

        if (uaItems.Count == 0)
        {
            return;
        }

        int selectedCount = CountSelectedUnused();
        long selectedBytes = CountSelectedUnusedBytes();
        EditorGUILayout.LabelField(
            string.Format("疑似未使用 {0} 个（勾选 {1} 个，可回收 {2}）",
                uaItems.Count, selectedCount, FormatBytes(selectedBytes)),
            EditorStyles.boldLabel);

        uaResultScroll = EditorGUILayout.BeginScrollView(uaResultScroll, GUILayout.MinHeight(200), GUILayout.MaxHeight(380));
        foreach (UnusedItem item in uaItems)
        {
            DrawUnusedRow(item);
        }
        EditorGUILayout.EndScrollView();

        EditorGUILayout.Space(2);
        DrawVFXExportMode();
        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("全选", GUILayout.Width(80)))
        {
            foreach (UnusedItem item in uaItems)
            {
                item.selected = true;
            }
        }
        if (GUILayout.Button("全不选", GUILayout.Width(80)))
        {
            foreach (UnusedItem item in uaItems)
            {
                item.selected = false;
            }
        }
        if (GUILayout.Button("导出报告（txt）", GUILayout.Width(120)))
        {
            ExportUnusedReport();
        }
        GUILayout.FlexibleSpace();
        if (GUILayout.Button(string.Format("删除勾选项（{0}）", selectedCount), GUILayout.Width(180), GUILayout.Height(26)))
        {
            DeleteSelectedUnused();
        }
        EditorGUILayout.EndHorizontal();
    }

    private void DrawUnusedRow(UnusedItem item)
    {
        EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
        item.selected = GUILayout.Toggle(item.selected, GUIContent.none, GUILayout.Width(18));

        if (item.cachedAsset == null)
        {
            item.cachedAsset = AssetDatabase.LoadMainAssetAtPath(item.path);
        }
        Texture icon = AssetPreview.GetMiniThumbnail(item.cachedAsset);
        Rect ir = GUILayoutUtility.GetRect(18, 18, GUILayout.Width(20), GUILayout.Height(20));
        if (icon != null)
        {
            GUI.DrawTexture(ir, icon, ScaleMode.ScaleToFit, true);
        }

        using (new EditorGUI.DisabledScope(true))
        {
            EditorGUILayout.ObjectField(item.cachedAsset, typeof(Object), false, GUILayout.MinWidth(140));
        }

        string tag = item.chainOnly ? "仅被未使用资源引用" : "零引用";
        EditorGUILayout.LabelField(item.category, EditorStyles.miniLabel, GUILayout.Width(78));
        EditorGUILayout.LabelField(FormatBytes(item.bytes), EditorStyles.miniLabel, GUILayout.Width(64));
        EditorGUILayout.LabelField(tag, EditorStyles.miniLabel, GUILayout.Width(130));

        if (GUILayout.Button("定位", EditorStyles.miniButton, GUILayout.Width(44)))
        {
            Selection.activeObject = item.cachedAsset;
            EditorGUIUtility.PingObject(item.cachedAsset);
        }
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.BeginHorizontal();
        GUILayout.Space(42);
        EditorGUILayout.LabelField(item.path, EditorStyles.miniLabel);
        EditorGUILayout.EndHorizontal();
    }

    private int CountSelectedUnused()
    {
        int n = 0;
        foreach (UnusedItem item in uaItems)
        {
            if (item.selected)
            {
                n++;
            }
        }
        return n;
    }

    private long CountSelectedUnusedBytes()
    {
        long b = 0;
        foreach (UnusedItem item in uaItems)
        {
            if (item.selected)
            {
                b += item.bytes;
            }
        }
        return b;
    }

    // ================= 扫描 =================

    private void ScanUnusedAssets(string[] folders)
    {
        uaScanning = true;
        uaItems.Clear();
        uaScanSummary = "";
        uaLastFolders = folders;

        try
        {
            EditorUtility.DisplayProgressBar("扫描未使用资源", "收集资源清单…", 0f);

            string assetsRoot = ToAbsolutePath("Assets");
            if (string.IsNullOrEmpty(assetsRoot))
            {
                SetStatus("无法定位 Assets 目录。");
                return;
            }

            // 1. 全部资源的 GUID -> 路径
            string[] allPaths = AssetDatabase.GetAllAssetPaths();
            Dictionary<string, string> guidToPath = new Dictionary<string, string>(allPaths.Length);
            Dictionary<string, string> pathToGuid = new Dictionary<string, string>(allPaths.Length);
            int inScope = 0;
            foreach (string p in allPaths)
            {
                if (!p.StartsWith("Assets", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (AssetDatabase.IsValidFolder(p))
                {
                    continue;
                }
                string g = AssetDatabase.AssetPathToGUID(p);
                if (string.IsNullOrEmpty(g))
                {
                    continue;
                }
                guidToPath[g] = p;
                pathToGuid[p] = g;
                if (IsInFolders(p, folders))
                {
                    inScope++;
                }
            }

            // 2. 遍历磁盘上的文本文件，建立 引用方GUID -> 被引用GUID集合 与反向计数
            Dictionary<string, HashSet<string>> edgesOut = new Dictionary<string, HashSet<string>>();
            Dictionary<string, HashSet<string>> edgesIn = new Dictionary<string, HashSet<string>>();
            HashSet<string> bundleRoots = new HashSet<string>();

            string[] diskFiles = Directory.GetFiles(assetsRoot, "*.*", SearchOption.AllDirectories);
            for (int i = 0; i < diskFiles.Length; i++)
            {
                string abs = diskFiles[i].Replace('\\', '/');
                string ext = Path.GetExtension(abs);
                bool isMeta = ext.Equals(".meta", StringComparison.OrdinalIgnoreCase);
                if (!isMeta && !UaTextExts.Contains(ext))
                {
                    continue;
                }

                string assetPath = ToAssetsRelative(abs);
                if (string.IsNullOrEmpty(assetPath))
                {
                    continue;
                }

                // 打了 AssetBundle 标签的资源作为入口
                if (isMeta)
                {
                    string bundleName = PeekAssetBundleName(abs);
                    if (!string.IsNullOrEmpty(bundleName))
                    {
                        string ownerPath = assetPath.Substring(0, assetPath.Length - 5);
                        string ownerGuid;
                        if (pathToGuid.TryGetValue(ownerPath, out ownerGuid))
                        {
                            bundleRoots.Add(ownerGuid);
                        }
                    }
                }

                string ownerPath2 = isMeta ? assetPath.Substring(0, assetPath.Length - 5) : assetPath;
                string ownerGuid2;
                if (!pathToGuid.TryGetValue(ownerPath2, out ownerGuid2))
                {
                    continue;
                }

                byte[] bytes;
                try
                {
                    bytes = File.ReadAllBytes(abs);
                }
                catch
                {
                    continue;
                }
                if (IsBinary(bytes))
                {
                    continue;
                }
                string text = Encoding.UTF8.GetString(bytes);

                MatchCollection matches = UaGuidRegex.Matches(text);
                HashSet<string> targets = null;
                foreach (Match m in matches)
                {
                    string targetGuid = m.Value;
                    if (targetGuid == ownerGuid2 || !guidToPath.ContainsKey(targetGuid))
                    {
                        continue;
                    }
                    if (targets == null)
                    {
                        targets = new HashSet<string>();
                    }
                    if (targets.Add(targetGuid))
                    {
                        HashSet<string> outSet;
                        if (!edgesOut.TryGetValue(ownerGuid2, out outSet))
                        {
                            outSet = new HashSet<string>();
                            edgesOut[ownerGuid2] = outSet;
                        }
                        outSet.Add(targetGuid);

                        HashSet<string> inSet;
                        if (!edgesIn.TryGetValue(targetGuid, out inSet))
                        {
                            inSet = new HashSet<string>();
                            edgesIn[targetGuid] = inSet;
                        }
                        inSet.Add(ownerGuid2);
                    }
                }

                if ((i & 31) == 0)
                {
                    uaProgress = (float)i / Mathf.Max(1, diskFiles.Length);
                    uaProgressText = "解析引用 " + (i + 1) + " / " + diskFiles.Length;
                    EditorUtility.DisplayProgressBar("扫描未使用资源", uaProgressText, uaProgress);
                }
            }

            // 3. 入口资源（根）：全部场景、Resources 目录、Addressables 配置、AB 标签、SpriteAtlas
            HashSet<string> roots = new HashSet<string>(bundleRoots);
            foreach (KeyValuePair<string, string> kv in guidToPath)
            {
                string p = kv.Value;
                if (p.EndsWith(".unity", StringComparison.OrdinalIgnoreCase)
                    || p.IndexOf("/Resources/", StringComparison.OrdinalIgnoreCase) >= 0
                    || p.StartsWith("Assets/Resources/", StringComparison.OrdinalIgnoreCase)
                    || p.IndexOf("/AddressableAssetsData/", StringComparison.OrdinalIgnoreCase) >= 0
                    || p.EndsWith(".spriteatlas", StringComparison.OrdinalIgnoreCase))
                {
                    roots.Add(kv.Key);
                }
            }

            // 4. 从入口做可达性遍历
            HashSet<string> reachable = new HashSet<string>(roots);
            Stack<string> stack = new Stack<string>(roots);
            while (stack.Count > 0)
            {
                string g = stack.Pop();
                HashSet<string> targets;
                if (!edgesOut.TryGetValue(g, out targets))
                {
                    continue;
                }
                foreach (string t in targets)
                {
                    if (reachable.Add(t))
                    {
                        stack.Push(t);
                    }
                }
            }

            // 5. 收集范围内、类型勾选的不可达资源
            HashSet<string> enabledCategories = new HashSet<string>();
            for (int i = 0; i < UaCategoryNames.Length; i++)
            {
                if (uaCategoryEnabled[i])
                {
                    enabledCategories.Add(UaCategoryNames[i]);
                }
            }

            foreach (string p in allPaths)
            {
                if (!IsInFolders(p, folders))
                {
                    continue;
                }

                // 文件夹：只列出递归后不含任何文件的空文件夹，含文件的文件夹不显示
                if (AssetDatabase.IsValidFolder(p))
                {
                    if (!enabledCategories.Contains("空文件夹"))
                    {
                        continue;
                    }
                    if (FolderContainsFiles(p))
                    {
                        continue;
                    }
                    uaItems.Add(new UnusedItem
                    {
                        path = p,
                        guid = AssetDatabase.AssetPathToGUID(p),
                        category = "空文件夹",
                        bytes = 0,
                        referencerCount = 0,
                        chainOnly = false
                    });
                    continue;
                }

                string ext = Path.GetExtension(p);
                if (UaExcludedExts.Contains(ext))
                {
                    continue;
                }
                string category = ClassifyAsset(p, ext);
                if (category == "排除" || !enabledCategories.Contains(category))
                {
                    continue;
                }
                string g = AssetDatabase.AssetPathToGUID(p);
                if (string.IsNullOrEmpty(g) || reachable.Contains(g))
                {
                    continue;
                }

                HashSet<string> referencers;
                int refCount = edgesIn.TryGetValue(g, out referencers) ? referencers.Count : 0;
                uaItems.Add(new UnusedItem
                {
                    path = p,
                    guid = g,
                    category = category,
                    bytes = GetFileBytes(p),
                    referencerCount = refCount,
                    chainOnly = refCount > 0
                });
            }

            uaItems.Sort((a, b) =>
            {
                int c = string.CompareOrdinal(a.category, b.category);
                return c != 0 ? c : b.bytes.CompareTo(a.bytes);
            });

            int zeroCount = 0;
            long totalBytes = 0;
            foreach (UnusedItem item in uaItems)
            {
                if (!item.chainOnly)
                {
                    zeroCount++;
                }
                totalBytes += item.bytes;
            }
            uaScanSummary = string.Format(
                "范围内资源 {0} 个，疑似未使用 {1} 个（零引用 {2} 个，仅被未使用资源引用 {3} 个），可回收约 {4}。",
                inScope, uaItems.Count, zeroCount, uaItems.Count - zeroCount, FormatBytes(totalBytes));
            SetStatus(uaScanSummary);
            Debug.Log("[VFX 工具箱] " + uaScanSummary);
        }
        finally
        {
            EditorUtility.ClearProgressBar();
            uaScanning = false;
            Repaint();
        }
    }

    private static bool IsInFolders(string assetPath, string[] folders)
    {
        foreach (string f in folders)
        {
            string prefix = f.EndsWith("/") ? f : f + "/";
            if (assetPath.Equals(f, StringComparison.OrdinalIgnoreCase)
                || assetPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>文件夹内（递归）是否含有真实资源文件；只含空子文件夹或 .meta 视为空文件夹。</summary>
    private static bool FolderContainsFiles(string assetFolderPath)
    {
        string abs = ToAbsolutePath(assetFolderPath);
        if (string.IsNullOrEmpty(abs) || !Directory.Exists(abs))
        {
            return false;
        }
        string[] files = Directory.GetFiles(abs, "*", SearchOption.AllDirectories);
        foreach (string f in files)
        {
            if (!f.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static string PeekAssetBundleName(string metaAbsolutePath)
    {
        try
        {
            string[] lines = File.ReadAllLines(metaAbsolutePath);
            foreach (string line in lines)
            {
                const string key = "assetBundleName:";
                int idx = line.IndexOf(key, StringComparison.Ordinal);
                if (idx >= 0)
                {
                    string val = line.Substring(idx + key.Length).Trim();
                    if (!string.IsNullOrEmpty(val))
                    {
                        return val;
                    }
                }
            }
        }
        catch
        {
            return null;
        }
        return null;
    }

    private static string ClassifyAsset(string path, string ext)
    {
        Type t = AssetDatabase.GetMainAssetTypeAtPath(path);
        if (t != null)
        {
            if (typeof(Texture).IsAssignableFrom(t))
            {
                return "贴图";
            }
            if (t == typeof(Material))
            {
                return "材质";
            }
            if (t == typeof(Mesh))
            {
                return "模型";
            }
            if (t == typeof(GameObject))
            {
                return "预制体";
            }
            if (t == typeof(AnimationClip) || t.Name.IndexOf("AnimatorController", StringComparison.Ordinal) >= 0)
            {
                return "动画/控制器";
            }
            if (t == typeof(Shader) || t == typeof(ComputeShader))
            {
                return "Shader";
            }
            if (t == typeof(AudioClip))
            {
                return "音频";
            }
            if (t == typeof(MonoScript) || t == typeof(SceneAsset))
            {
                return "排除";
            }
        }

        switch (ext.ToLowerInvariant())
        {
            case ".png": case ".jpg": case ".jpeg": case ".tga": case ".psd":
            case ".tif": case ".tiff": case ".exr": case ".hdr": case ".bmp":
            case ".gif": case ".cubemap":
                return "贴图";
            case ".mat":
                return "材质";
            case ".fbx": case ".obj": case ".blend": case ".mb": case ".ma":
            case ".3ds": case ".dae": case ".max":
                return "模型";
            case ".prefab":
                return "预制体";
            case ".anim": case ".controller": case ".overridecontroller":
                return "动画/控制器";
            case ".shader": case ".shadergraph": case ".shadersubgraph": case ".compute":
                return "Shader";
            case ".wav": case ".mp3": case ".ogg": case ".aif": case ".aiff":
                return "音频";
            default:
                return "其他";
        }
    }

    // ================= 删除 / 报告 =================

    private void DeleteSelectedUnused()
    {
        List<UnusedItem> toDelete = uaItems.FindAll(i => i.selected);
        if (toDelete.Count == 0)
        {
            SetStatus("没有勾选任何资源。");
            return;
        }

        long bytes = 0;
        foreach (UnusedItem item in toDelete)
        {
            bytes += item.bytes;
        }
        bool ok = EditorUtility.DisplayDialog(
            "确认删除未使用资源",
            string.Format("将删除 {0} 个资源（约 {1}）。\n" +
                "代码动态加载（Resources.Load / Shader.Find / 按名字加载）的资源无法被扫描识别，请确认没有误勾。\n" +
                "删除不可撤销（可用版本管理回退）。是否继续？", toDelete.Count, FormatBytes(bytes)),
            "删除", "取消");
        if (!ok)
        {
            return;
        }

        int deleted = 0;
        try
        {
            for (int i = 0; i < toDelete.Count; i++)
            {
                EditorUtility.DisplayProgressBar("删除未使用资源", toDelete[i].path,
                    (float)i / Mathf.Max(1, toDelete.Count));
                if (AssetDatabase.DeleteAsset(toDelete[i].path))
                {
                    deleted++;
                }
            }
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        string msg = string.Format("已删除 {0} 个未使用资源（约 {1}）。", deleted, FormatBytes(bytes));
        SetStatus(msg);
        Debug.Log("[VFX 工具箱] " + msg + "\n" +
            string.Join("\n", toDelete.ConvertAll(i => i.path).ToArray()));

        // 重新扫描，刷新“仅被未使用资源引用”的连锁结果
        if (uaLastFolders != null)
        {
            ScanUnusedAssets(uaLastFolders);
        }
    }

    private void ExportUnusedReport()
    {
        if (uaItems.Count == 0)
        {
            return;
        }
        string path = ResolveVFXExportPath("导出未使用资源报告", "UnusedAssetsReport", "txt", null, false);
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        StringBuilder sb = new StringBuilder();
        sb.AppendLine(uaScanSummary);
        sb.AppendLine("生成时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        sb.AppendLine();
        string lastCategory = null;
        foreach (UnusedItem item in uaItems)
        {
            if (item.category != lastCategory)
            {
                sb.AppendLine();
                sb.AppendLine("==== " + item.category + " ====");
                lastCategory = item.category;
            }
            sb.AppendLine(string.Format("[{0}] {1}  ({2})  引用方 {3}",
                item.chainOnly ? "连锁" : "零引用",
                item.path, FormatBytes(item.bytes), item.referencerCount));
        }

        try
        {
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
            SetStatus("报告已导出：" + path);
            EditorUtility.RevealInFinder(path);
        }
        catch (Exception e)
        {
            SetStatus("导出失败：" + e.Message);
        }
    }
}
