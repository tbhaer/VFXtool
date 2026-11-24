using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

/// <summary>
/// 模块九：重复贴图扫描与合并。
/// 扫描指定文件夹（递归）或整个 Assets，按“尺寸 + 像素内容哈希”精确找出相同贴图；
/// 每组指定一张保留贴图，应用时把工程内所有对冗余贴图的 GUID 引用改写为保留贴图
/// （材质 / 预制体 / 动画 / 控制器 / 场景 / 模型 .meta 重映射等），随后删除冗余文件。
/// 像素哈希走 GPU（Blit + ReadPixels），不要求贴图开启 Read/Write。
/// </summary>
public partial class VFXToolboxWindow
{
    private DefaultAsset dfFolder;
    private readonly List<DupGroup> dfGroups = new List<DupGroup>();
    private bool dfScanning;
    private float dfProgress;
    private string dfProgressText = "";
    private Vector2 dfResultScroll;
    private string dfScanSummary = "";

    [Serializable]
    private class DupItem
    {
        public string path;
        public string guid;
        public long bytes;
        public Texture2D texture;
    }

    [Serializable]
    private class DupGroup
    {
        public int width;
        public int height;
        public string hash;
        public readonly List<DupItem> items = new List<DupItem>();
        public int keepIndex;
        public bool expanded = true;
    }

    private void InitDuplicateFinder()
    {
    }

    // ================= UI =================

    private void DrawDuplicateTextureTool()
    {
        EditorGUILayout.LabelField("重复贴图扫描与合并", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "按像素内容精确找出相同贴图。每组选一张保留，应用后工程内所有引用会指向保留贴图，\n" +
            "其余重复文件被删除（GUID 改写，引用不丢）。目录留空 = 扫描整个 Assets。建议先提交版本管理。",
            MessageType.Info);

        dfFolder = (DefaultAsset)EditorGUILayout.ObjectField(
            new GUIContent("扫描文件夹", "留空则扫描全工程；文件夹会递归包含所有子目录"),
            dfFolder, typeof(DefaultAsset), false);

        EditorGUILayout.BeginHorizontal();
        EditorGUI.BeginDisabledGroup(dfScanning);
        if (GUILayout.Button("扫描选中文件夹（含子目录）", GUILayout.Height(24)))
        {
            string folder = dfFolder != null ? AssetDatabase.GetAssetPath(dfFolder) : null;
            if (string.IsNullOrEmpty(folder) || !AssetDatabase.IsValidFolder(folder))
            {
                SetStatus("请指定有效的文件夹，或点“扫描全工程”。");
            }
            else
            {
                ScanDuplicates(new[] { folder });
            }
        }
        if (GUILayout.Button("扫描全工程（Assets）", GUILayout.Height(24), GUILayout.Width(200)))
        {
            ScanDuplicates(new[] { "Assets" });
        }
        EditorGUI.EndDisabledGroup();
        EditorGUILayout.EndHorizontal();

        if (dfScanning)
        {
            Rect r = EditorGUILayout.GetControlRect(false, 22);
            EditorGUI.ProgressBar(r, dfProgress, dfProgressText);
            return;
        }

        if (!string.IsNullOrEmpty(dfScanSummary))
        {
            EditorGUILayout.HelpBox(dfScanSummary, MessageType.None);
        }

        if (dfGroups.Count == 0)
        {
            return;
        }

        long redundantBytes = ComputeRedundantBytes();
        EditorGUILayout.LabelField(
            string.Format("重复组 {0}，冗余贴图 {1} 张，预计节省 {2}",
                dfGroups.Count, CountRedundant(), FormatBytes(redundantBytes)),
            EditorStyles.boldLabel);

        dfResultScroll = EditorGUILayout.BeginScrollView(dfResultScroll, GUILayout.MinHeight(200), GUILayout.MaxHeight(380));
        for (int g = 0; g < dfGroups.Count; g++)
        {
            DrawDupGroup(g, dfGroups[g]);
        }
        EditorGUILayout.EndScrollView();

        EditorGUILayout.Space(2);
        if (GUILayout.Button("应用：重定向引用并删除冗余贴图", GUILayout.Height(28)))
        {
            ApplyDeduplication();
        }
    }

    private void DrawDupGroup(int index, DupGroup group)
    {
        long waste = 0;
        for (int i = 0; i < group.items.Count; i++)
        {
            if (i != group.keepIndex)
            {
                waste += group.items[i].bytes;
            }
        }
        string title = string.Format("组 {0}：{1} × {2}，{3} 张重复，冗余 {4}",
            index + 1, group.width, group.height, group.items.Count, FormatBytes(waste));
        group.expanded = EditorGUILayout.Foldout(group.expanded, title, true);
        if (!group.expanded)
        {
            return;
        }

        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            for (int i = 0; i < group.items.Count; i++)
            {
                DupItem item = group.items[i];
                EditorGUILayout.BeginHorizontal();
                bool keep = GUILayout.Toggle(group.keepIndex == i, "保留", EditorStyles.radioButton, GUILayout.Width(50));
                if (keep && group.keepIndex != i)
                {
                    group.keepIndex = i;
                }

                Texture2D thumb = AssetPreview.GetMiniThumbnail(item.texture);
                Rect tr = GUILayoutUtility.GetRect(24, 24, GUILayout.Width(24), GUILayout.Height(24));
                if (thumb != null)
                {
                    GUI.DrawTexture(tr, thumb, ScaleMode.ScaleToFit, true);
                }

                using (new EditorGUI.DisabledScope(true))
                {
                    EditorGUILayout.ObjectField(item.texture, typeof(Texture2D), false, GUILayout.MinWidth(120));
                }
                EditorGUILayout.EndHorizontal();

                EditorGUILayout.BeginHorizontal();
                GUILayout.Space(56);
                EditorGUILayout.LabelField(item.path + "  (" + FormatBytes(item.bytes) + ")", EditorStyles.miniLabel);
                EditorGUILayout.EndHorizontal();
            }
        }
    }

    private int CountRedundant()
    {
        int n = 0;
        foreach (DupGroup g in dfGroups)
        {
            n += g.items.Count - 1;
        }
        return n;
    }

    private long ComputeRedundantBytes()
    {
        long b = 0;
        foreach (DupGroup g in dfGroups)
        {
            for (int i = 0; i < g.items.Count; i++)
            {
                if (i != g.keepIndex)
                {
                    b += g.items[i].bytes;
                }
            }
        }
        return b;
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0)
        {
            return "0 B";
        }
        string[] units = { "B", "KB", "MB", "GB" };
        double v = bytes;
        int u = 0;
        while (v >= 1024 && u < units.Length - 1)
        {
            v /= 1024;
            u++;
        }
        return v.ToString(u == 0 ? "0" : "0.0") + " " + units[u];
    }

    // ================= 扫描 =================

    private void ScanDuplicates(string[] folders)
    {
        dfScanning = true;
        dfGroups.Clear();
        dfScanSummary = "";

        try
        {
            EditorUtility.DisplayProgressBar("扫描重复贴图", "收集贴图…", 0f);
            string[] guids = AssetDatabase.FindAssets("t:Texture", folders);
            List<Texture2D> textures = new List<Texture2D>();
            for (int i = 0; i < guids.Length; i++)
            {
                string p = AssetDatabase.GUIDToAssetPath(guids[i]);
                if (string.IsNullOrEmpty(p) || !p.StartsWith("Assets", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                Texture2D tex = AssetDatabase.LoadAssetAtPath<Texture2D>(p);
                if (tex == null)
                {
                    continue; // RenderTexture / 非 Texture2D 主资源
                }
                textures.Add(tex);
                dfProgress = (float)i / Mathf.Max(1, guids.Length) * 0.4f;
                dfProgressText = "收集贴图 " + i + " / " + guids.Length;
                if ((i & 31) == 0)
                {
                    EditorUtility.DisplayProgressBar("扫描重复贴图", dfProgressText, dfProgress);
                }
            }

            // 第一轮：按尺寸 + 64px 缩略哈希分桶
            Dictionary<string, List<Texture2D>> buckets = new Dictionary<string, List<Texture2D>>();
            for (int i = 0; i < textures.Count; i++)
            {
                Texture2D t = textures[i];
                string thumbHash = HashTexturePixels(t, 64);
                string key = t.width + "x" + t.height + "|" + thumbHash;
                List<Texture2D> bucket;
                if (!buckets.TryGetValue(key, out bucket))
                {
                    bucket = new List<Texture2D>();
                    buckets[key] = bucket;
                }
                bucket.Add(t);

                dfProgress = 0.4f + 0.55f * ((float)i / Mathf.Max(1, textures.Count));
                dfProgressText = "计算缩略哈希 " + (i + 1) + " / " + textures.Count;
                if ((i & 31) == 0)
                {
                    EditorUtility.DisplayProgressBar("扫描重复贴图", dfProgressText, dfProgress);
                }
            }

            // 第二轮：桶内做全分辨率像素哈希，得到精确分组
            Dictionary<string, DupGroup> exact = new Dictionary<string, DupGroup>();
            int processed = 0;
            int candidates = 0;
            foreach (List<Texture2D> bucket in buckets.Values)
            {
                if (bucket.Count < 2)
                {
                    continue;
                }
                candidates += bucket.Count;
                foreach (Texture2D t in bucket)
                {
                    string fullHash = HashTexturePixels(t, 0); // 0 = 原尺寸
                    string key = t.width + "x" + t.height + "|" + fullHash;
                    DupGroup group;
                    if (!exact.TryGetValue(key, out group))
                    {
                        group = new DupGroup { width = t.width, height = t.height, hash = fullHash };
                        exact[key] = group;
                    }
                    string path = AssetDatabase.GetAssetPath(t);
                    group.items.Add(new DupItem
                    {
                        path = path,
                        guid = AssetDatabase.AssetPathToGUID(path),
                        bytes = GetFileBytes(path),
                        texture = t
                    });
                    processed++;
                    dfProgress = 0.95f + 0.05f * ((float)processed / Mathf.Max(1, candidates));
                    dfProgressText = "全分辨率比对 " + processed + " / " + candidates;
                    if ((processed & 15) == 0)
                    {
                        EditorUtility.DisplayProgressBar("扫描重复贴图", dfProgressText, dfProgress);
                    }
                }
            }

            foreach (DupGroup g in exact.Values)
            {
                if (g.items.Count >= 2)
                {
                    g.items.Sort((a, b) => string.CompareOrdinal(a.path, b.path));
                    dfGroups.Add(g);
                }
            }
            dfGroups.Sort((a, b) => b.items.Count.CompareTo(a.items.Count));

            long waste = ComputeRedundantBytes();
            dfScanSummary = string.Format(
                "扫描 {0} 张贴图，发现 {1} 组重复，冗余 {2} 张，可节省约 {3}。",
                textures.Count, dfGroups.Count, CountRedundant(), FormatBytes(waste));
            SetStatus(dfScanSummary);
        }
        finally
        {
            EditorUtility.ClearProgressBar();
            dfScanning = false;
            Repaint();
        }
    }

    private static long GetFileBytes(string assetPath)
    {
        string abs = ToAbsolutePath(assetPath);
        if (string.IsNullOrEmpty(abs) || !File.Exists(abs))
        {
            return 0;
        }
        try
        {
            return new FileInfo(abs).Length;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// GPU 读回贴图像素并做 MD5。size 大于 0 时缩到 size×size 做预筛；为 0 时按原尺寸精确哈希。
    /// </summary>
    private static string HashTexturePixels(Texture2D tex, int size)
    {
        int w = tex.width;
        int h = tex.height;
        if (size > 0 && (w > size || h > size))
        {
            float scale = Mathf.Min((float)size / w, (float)size / h);
            w = Mathf.Max(1, Mathf.RoundToInt(w * scale));
            h = Mathf.Max(1, Mathf.RoundToInt(h * scale));
        }

        bool srgb = IsSourceSRGB(tex);
        RenderTexture rt = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32,
            srgb ? RenderTextureReadWrite.sRGB : RenderTextureReadWrite.Linear);
        rt.hideFlags = HideFlags.HideAndDontSave;
        rt.Create();

        RenderTexture prev = RenderTexture.active;
        Graphics.Blit(tex, rt);
        RenderTexture.active = rt;
        Texture2D read = new Texture2D(w, h, TextureFormat.RGBA32, false);
        read.ReadPixels(new Rect(0, 0, w, h), 0, 0);
        read.Apply(false);
        RenderTexture.active = prev;
        rt.Release();
        Object.DestroyImmediate(rt);

        byte[] raw = read.GetRawTextureData();
        Object.DestroyImmediate(read);
        using (MD5 md5 = MD5.Create())
        {
            byte[] hash = md5.ComputeHash(raw);
            StringBuilder sb = new StringBuilder(hash.Length * 2);
            foreach (byte b in hash)
            {
                sb.Append(b.ToString("x2"));
            }
            return sb.ToString();
        }
    }

    // ================= 应用：重定向 + 删除 =================

    private void ApplyDeduplication()
    {
        if (dfGroups.Count == 0)
        {
            return;
        }
        int redundantCount = CountRedundant();
        bool ok = EditorUtility.DisplayDialog(
            "确认合并重复贴图",
            string.Format("将把 {0} 张冗余贴图的全部引用改写为各组保留贴图，并删除这些文件。\n" +
                "操作不可撤销（可用版本管理回退）。是否继续？", redundantCount),
            "应用", "取消");
        if (!ok)
        {
            return;
        }

        // 冗余 GUID -> 保留 GUID
        Dictionary<string, string> guidMap = new Dictionary<string, string>();
        HashSet<string> texturePaths = new HashSet<string>();
        foreach (DupGroup g in dfGroups)
        {
            if (g.keepIndex < 0 || g.keepIndex >= g.items.Count)
            {
                g.keepIndex = 0;
            }
            string keepGuid = g.items[g.keepIndex].guid;
            texturePaths.Add(g.items[g.keepIndex].path);
            for (int i = 0; i < g.items.Count; i++)
            {
                texturePaths.Add(g.items[i].path);
                if (i != g.keepIndex)
                {
                    guidMap[g.items[i].guid] = keepGuid;
                }
            }
        }

        List<string> rewritten = new List<string>();
        List<string> skippedScenes = new List<string>();
        HashSet<string> openScenes = new HashSet<string>();
        for (int s = 0; s < EditorSceneManager.sceneCount; s++)
        {
            UnityEngine.SceneManagement.Scene sc = EditorSceneManager.GetSceneAt(s);
            if (sc.isLoaded && !string.IsNullOrEmpty(sc.path))
            {
                openScenes.Add(sc.path);
            }
        }

        try
        {
            // 反向依赖：找出直接引用了冗余贴图的工程文件
            string[] allAssets = AssetDatabase.GetAllAssetPaths();
            for (int i = 0; i < allAssets.Length; i++)
            {
                string assetPath = allAssets[i];
                if (!assetPath.StartsWith("Assets", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (texturePaths.Contains(assetPath))
                {
                    continue; // 贴图自身不改写
                }
                EditorUtility.DisplayProgressBar("查找引用", assetPath, (float)i / allAssets.Length);

                HashSet<string> targets = new HashSet<string> { assetPath };
                if (File.Exists(ToAbsolutePath(assetPath) + ".meta"))
                {
                    targets.Add(assetPath + ".meta"); // 模型导入器的外部贴图重映射
                }

                foreach (string target in targets)
                {
                    string result = RewriteGuidsInFile(target, guidMap, openScenes, skippedScenes);
                    if (result != null && !rewritten.Contains(result))
                    {
                        rewritten.Add(result);
                    }
                }
            }
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }

        AssetDatabase.Refresh();

        // 删除冗余贴图
        int deleted = 0;
        foreach (DupGroup g in dfGroups)
        {
            for (int i = 0; i < g.items.Count; i++)
            {
                if (i == g.keepIndex)
                {
                    continue;
                }
                EditorUtility.DisplayProgressBar("删除冗余贴图", g.items[i].path, 0.9f);
                if (AssetDatabase.DeleteAsset(g.items[i].path))
                {
                    deleted++;
                }
            }
        }
        EditorUtility.ClearProgressBar();
        AssetDatabase.Refresh();

        string msg = string.Format("合并完成：改写 {0} 个文件的引用，删除冗余贴图 {1} 张。", rewritten.Count, deleted);
        if (skippedScenes.Count > 0)
        {
            msg += "\n以下已打开场景未改写（请保存关闭后重跑）：\n" + string.Join("\n", skippedScenes.ToArray());
        }
        SetStatus(msg);
        Debug.Log("[VFX 工具箱] " + msg + "\n改写文件：\n" + string.Join("\n", rewritten.ToArray()));

        // 重新扫描刷新结果
        string folder = dfFolder != null ? AssetDatabase.GetAssetPath(dfFolder) : null;
        if (!string.IsNullOrEmpty(folder) && AssetDatabase.IsValidFolder(folder))
        {
            ScanDuplicates(new[] { folder });
        }
        else
        {
            ScanDuplicates(new[] { "Assets" });
        }
    }

    /// <summary>把文件中出现的冗余 GUID 文本替换为保留 GUID；返回被改写的路径，无需改写返回 null。</summary>
    private static string RewriteGuidsInFile(string assetPath, Dictionary<string, string> guidMap,
        HashSet<string> openScenes, List<string> skippedScenes)
    {
        string abs = ToAbsolutePath(assetPath);
        if (string.IsNullOrEmpty(abs) || !File.Exists(abs))
        {
            return null;
        }
        if (assetPath.EndsWith(".unity", StringComparison.OrdinalIgnoreCase) && openScenes.Contains(assetPath))
        {
            if (!skippedScenes.Contains(assetPath))
            {
                skippedScenes.Add(assetPath);
            }
            return null;
        }

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(abs);
        }
        catch
        {
            return null;
        }
        if (IsBinary(bytes))
        {
            return null;
        }

        string text = Encoding.UTF8.GetString(bytes);
        string original = text;
        foreach (KeyValuePair<string, string> kv in guidMap)
        {
            text = text.Replace(kv.Key, kv.Value);
        }
        if (text == original)
        {
            return null;
        }

        try
        {
            File.WriteAllText(abs, text, new UTF8Encoding(false));
            return assetPath;
        }
        catch (Exception e)
        {
            Debug.LogWarning("[VFX 工具箱] 改写失败 " + assetPath + "：" + e.Message);
            return null;
        }
    }

    private static bool IsBinary(byte[] bytes)
    {
        int check = Mathf.Min(bytes.Length, 4096);
        for (int i = 0; i < check; i++)
        {
            if (bytes[i] == 0)
            {
                return true;
            }
        }
        return false;
    }
}
