using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

/// <summary>
/// 模块十四：内嵌网格改名（FBX / 模型导入资源）。
/// 把模型文件（.fbx 等）内部由 ModelImporter 生成的 Mesh 子资产名，统一成模型文件名：
///   · 模型内只有 1 个 Mesh：Mesh 名 = 文件名（不含扩展名）；
///   · 模型内有多个 Mesh：Mesh 名 = 文件名_01、文件名_02 …（按层级深度优先顺序）。
///
/// 为什么不能直接 RenameAsset：
///   内嵌 Mesh 是导入器生成的子资产，Project 窗口里名字只读，直接改 mesh.name 也会在下次
///   重新导入时被还原。持久改名的唯一途径是 AssetPostprocessor.OnPostprocessModel，在导入管线
///   里设置 mesh.sharedMesh.name，再重新导入模型。本工具因此内置一个常驻的后处理器，并把
///   “哪些模型启用了跟随文件名”的规则持久化到 ProjectSettings/EmbeddedMeshNames.json，
///   以后美术重新导入 / 更新这些模型时，Mesh 名依旧稳定，FileID 也随之稳定。
///
/// 引用安全（核心）：
///   Mesh 子资产在模型文件内的 FileID 由其名字决定，改名并重新导入后 FileID 会变化一次，
///   工程里对该 Mesh 的引用（MeshFilter / SkinnedMeshRenderer / 粒子 ParticleSystemRenderer
///   的 m_Mesh，以及任意 ScriptableObject 里的 Mesh 引用）会暂时悬空。
///   本工具在改名前后分别读取每个 Mesh 的真实 FileID（AssetDatabase.TryGetGUIDAndLocalFileIdentifier），
///   仅当 FileID 确实变化时，扫描工程内全部文本序列化资源（.prefab/.unity/.asset/.mat 等），
///   把同一模型 GUID 下的旧 FileID 精确替换为新 FileID（正则只命中 {fileID, guid} 同时匹配的引用）。
///   模型文件本身不移动、不改名，其 .meta GUID 始终不变；GameObject / Transform / 材质等其它
///   子资产不改名、FileID 不变。改动前会把所有被改写的文件备份到 Temp 目录。
/// </summary>
public partial class VFXToolboxWindow
{
    [Serializable]
    private class EmbeddedMeshItem
    {
        public string oldName;
        public string newName;
        public string oldFileId;
        public string newFileId;
        public bool WillChange { get { return oldName != newName; } }
    }

    [Serializable]
    private class EmbeddedMeshFbxEntry
    {
        public string path;
        public string guid;
        public string fileBase;
        public List<EmbeddedMeshItem> items = new List<EmbeddedMeshItem>();
        public bool registered;   // 已在持久化规则集中
        public string issue;      // 非空表示该模型有问题，跳过
    }

    private readonly List<EmbeddedMeshFbxEntry> emEntries = new List<EmbeddedMeshFbxEntry>();
    private Vector2 emScroll;
    private bool emDirty = true;

    // 扫描范围：emScanFolder 指定文件夹；emScannedPaths 非空表示当前预览来自文件夹/全工程扫描，null 表示跟随 Project 选中
    private DefaultAsset emScanFolder;
    private List<string> emScannedPaths;
    private string emScanScopeLabel = "Project 选中";

    private void InitEmbeddedMeshTool()
    {
    }

    // ================= UI =================

    private void DrawEmbeddedMeshTool()
    {
        EditorGUILayout.LabelField("内嵌网格改名（Mesh 名跟随文件名 / 自动修复引用）", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "选择扫描范围：指定“扫描文件夹”后点“扫描选中文件夹（含子目录）”，或直接“扫描全工程（Assets）”；\n" +
            "也可在 Project 窗口选中模型文件（.fbx，可多选或选文件夹）后点“按 Project 选中刷新”。\n" +
            "· 模型内只有 1 个 Mesh：改名为“模型文件名”；多个 Mesh：文件名_01、文件名_02 …\n" +
            "· 只改模型内部 Mesh 子资产的名字，模型文件本身不移动、不改名，GUID 不变；\n" +
            "· 改名会让 Mesh 的内部 FileID 变化一次，工具会自动扫描并修复工程内所有引用（含粒子渲染的 Mesh）；\n" +
            "· 规则会持久化，之后重新导入这些模型 Mesh 名依旧稳定。执行前会备份被改动文件，建议先提交版本管理。",
            MessageType.None);

        // —— 扫描范围：指定文件夹 / 全工程，点按钮才扫描（与“未使用资源”工具一致） ——
        emScanFolder = (DefaultAsset)EditorGUILayout.ObjectField(
            new GUIContent("扫描文件夹", "指定一个文件夹后点左侧按钮，递归扫描其中所有模型；也可直接点“扫描全工程”"),
            emScanFolder, typeof(DefaultAsset), false);

        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("扫描选中文件夹（含子目录）", GUILayout.Height(24)))
        {
            string folder = emScanFolder != null ? AssetDatabase.GetAssetPath(emScanFolder) : null;
            if (string.IsNullOrEmpty(folder) || !AssetDatabase.IsValidFolder(folder))
            {
                SetStatus("请先在“扫描文件夹”里指定一个有效的文件夹。");
            }
            else
            {
                emScannedPaths = EmbeddedMeshUtil.CollectModelPathsInFolders(new[] { folder });
                emScanScopeLabel = "文件夹：" + folder;
                emDirty = true;
                if (emScannedPaths.Count == 0)
                {
                    SetStatus("该文件夹（含子目录）下没有找到模型文件。");
                }
            }
        }
        if (GUILayout.Button("扫描全工程（Assets）", GUILayout.Height(24), GUILayout.Width(200)))
        {
            emScannedPaths = EmbeddedMeshUtil.CollectAllModelPaths();
            emScanScopeLabel = "全工程（Assets）";
            emDirty = true;
            if (emScannedPaths.Count == 0)
            {
                SetStatus("整个 Assets 下没有找到模型文件。");
            }
        }
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("按 Project 选中刷新", GUILayout.Height(22)))
        {
            emScannedPaths = null;
            emScanScopeLabel = "Project 选中";
            emDirty = true;
        }
        int selectedAssetCount = Selection.GetFiltered(typeof(Object), SelectionMode.Assets).Length;
        GUILayout.Label("当前 Project 选中：" + selectedAssetCount + " 项", EditorStyles.miniLabel);
        int ruleCount = EmbeddedMeshNameMap.EnabledCount;
        GUILayout.Label("已启用规则模型：" + ruleCount + " 个", EditorStyles.miniLabel);
        GUILayout.FlexibleSpace();
        GUILayout.Label("预览范围：" + emScanScopeLabel, EditorStyles.miniLabel);
        EditorGUILayout.EndHorizontal();

        if (emDirty)
        {
            RebuildEmbeddedMeshPreview();
            emDirty = false;
        }

        DrawEmbeddedMeshPreviewAndExecute();
    }

    private void DrawEmbeddedMeshPreviewAndExecute()
    {
        if (emEntries.Count == 0)
        {
            EditorGUILayout.HelpBox(
                "没有可处理的模型。可任选一种方式：\n" +
                "· 在上方“扫描文件夹”指定目录后点“扫描选中文件夹（含子目录）”；\n" +
                "· 直接点“扫描全工程（Assets）”；\n" +
                "· 或在 Project 窗口选中一个/多个 .fbx（或含模型的文件夹）后点“按 Project 选中刷新”。",
                MessageType.Info);
            return;
        }

        int issueCount = 0;
        int changeFbx = 0;
        int changeMesh = 0;
        foreach (EmbeddedMeshFbxEntry e in emEntries)
        {
            if (!string.IsNullOrEmpty(e.issue))
            {
                issueCount++;
                continue;
            }
            bool any = false;
            foreach (EmbeddedMeshItem it in e.items)
            {
                if (it.WillChange)
                {
                    any = true;
                    changeMesh++;
                }
            }
            if (any)
            {
                changeFbx++;
            }
        }

        EditorGUILayout.Space(4);
        string summary = "共 " + emEntries.Count + " 个模型；" + changeMesh + " 个 Mesh 将改名（涉及 " + changeFbx + " 个模型）";
        if (issueCount > 0)
        {
            summary += "；" + issueCount + " 个有问题将跳过";
        }
        EditorGUILayout.LabelField(summary, EditorStyles.boldLabel);

        emScroll = EditorGUILayout.BeginScrollView(emScroll, EditorStyles.helpBox, GUILayout.MinHeight(220));
        foreach (EmbeddedMeshFbxEntry e in emEntries)
        {
            bool hasIssue = !string.IsNullOrEmpty(e.issue);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.BeginHorizontal();
            Color oldColor = GUI.color;
            if (hasIssue)
            {
                GUI.color = new Color(1f, 0.55f, 0.55f);
            }
            GUILayout.Label("FBX", GUILayout.Width(32));
            GUILayout.Label(e.fileBase, EditorStyles.boldLabel, GUILayout.MinWidth(160));
            GUILayout.Label(hasIssue ? e.issue : (e.items.Count + " 个 Mesh"), EditorStyles.miniLabel, GUILayout.Width(90));
            GUILayout.Label(e.registered ? "已启用规则" : "未启用规则",
                e.registered ? EditorStyles.miniLabel : EditorStyles.miniBoldLabel, GUILayout.Width(90));
            GUI.color = oldColor;
            EditorGUILayout.EndHorizontal();

            if (!hasIssue)
            {
                foreach (EmbeddedMeshItem it in e.items)
                {
                    EditorGUILayout.BeginHorizontal();
                    GUILayout.Space(34f);
                    Color c = GUI.color;
                    GUI.color = it.WillChange ? new Color(0.65f, 1f, 0.65f) : new Color(0.75f, 0.75f, 0.75f);
                    GUILayout.Label("Mesh", GUILayout.Width(40));
                    GUILayout.Label(it.oldName, GUILayout.MinWidth(150));
                    GUILayout.Label("→", GUILayout.Width(18));
                    GUILayout.Label(it.newName, EditorStyles.boldLabel, GUILayout.MinWidth(150));
                    GUI.color = c;
                    EditorGUILayout.EndHorizontal();
                }
            }
            EditorGUILayout.EndVertical();
            GUILayout.Space(2f);
        }
        EditorGUILayout.EndScrollView();

        EditorGUILayout.Space(4);
        using (new EditorGUI.DisabledScope(changeMesh == 0 || issueCount == emEntries.Count))
        {
            if (GUILayout.Button("执行：统一 Mesh 名并修复引用", GUILayout.Height(30)))
            {
                ExecuteEmbeddedMeshRename(changeFbx, changeMesh);
            }
        }
        if (issueCount > 0)
        {
            EditorGUILayout.HelpBox("存在标红的问题项，执行时会自动跳过这些模型。", MessageType.Warning);
        }
    }

    // ================= 扫描预览 =================

    private void RebuildEmbeddedMeshPreview()
    {
        emEntries.Clear();
        EmbeddedMeshNameMap.Reload();
        HashSet<string> enabled = EmbeddedMeshNameMap.Enabled;

        List<string> modelPaths = emScannedPaths != null
            ? new List<string>(emScannedPaths)
            : EmbeddedMeshUtil.CollectSelectedModelPaths();

        bool showProgress = modelPaths.Count > 15;
        try
        {
            for (int pi = 0; pi < modelPaths.Count; pi++)
            {
                string path = modelPaths[pi];
                if (showProgress)
                {
                    EditorUtility.DisplayProgressBar("扫描模型内嵌 Mesh",
                        "读取：" + Path.GetFileName(path) + "（" + (pi + 1) + "/" + modelPaths.Count + "）",
                        modelPaths.Count == 0 ? 0f : (float)pi / modelPaths.Count);
                }

                EmbeddedMeshFbxEntry entry = new EmbeddedMeshFbxEntry
                {
                    path = path,
                    fileBase = Path.GetFileNameWithoutExtension(path),
                    guid = AssetDatabase.AssetPathToGUID(path),
                    registered = enabled.Contains(AssetDatabase.AssetPathToGUID(path))
                };

                GameObject root = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (root == null)
                {
                    entry.issue = "无法加载模型";
                    emEntries.Add(entry);
                    continue;
                }

                List<Mesh> meshes = EmbeddedMeshUtil.CollectMeshes(root);
                if (meshes.Count == 0)
                {
                    entry.issue = "未找到 Mesh";
                    emEntries.Add(entry);
                    continue;
                }

                for (int i = 0; i < meshes.Count; i++)
                {
                    Mesh mesh = meshes[i];
                    string fileId;
                    string guid;
                    EmbeddedMeshUtil.TryGetFileId(mesh, out guid, out fileId);
                    entry.items.Add(new EmbeddedMeshItem
                    {
                        oldName = mesh.name,
                        newName = EmbeddedMeshUtil.TargetName(entry.fileBase, i, meshes.Count),
                        oldFileId = fileId,
                        newFileId = fileId
                    });
                }
                emEntries.Add(entry);
            }
        }
        finally
        {
            if (showProgress)
            {
                EditorUtility.ClearProgressBar();
            }
        }
    }

    // ================= 执行 =================

    private void ExecuteEmbeddedMeshRename(int changeFbx, int changeMesh)
    {
        // 仅处理“有 Mesh 需要改名且无问题”的模型
        List<EmbeddedMeshFbxEntry> targets = new List<EmbeddedMeshFbxEntry>();
        foreach (EmbeddedMeshFbxEntry e in emEntries)
        {
            if (!string.IsNullOrEmpty(e.issue))
            {
                continue;
            }
            bool any = false;
            foreach (EmbeddedMeshItem it in e.items)
            {
                if (it.WillChange)
                {
                    any = true;
                    break;
                }
            }
            if (any)
            {
                targets.Add(e);
            }
        }
        if (targets.Count == 0)
        {
            return;
        }

        string backupDir = Path.GetFullPath(Path.Combine(Application.dataPath,
            "../Temp/VFXEmbeddedMeshBackup_" + DateTime.Now.ToString("yyyyMMdd_HHmmss")));

        bool ok = EditorUtility.DisplayDialog(
            "确认统一内嵌 Mesh 名",
            string.Format(
                "将对 {0} 个模型、{1} 个 Mesh 执行改名并重新导入，随后自动扫描修复工程内引用。\n\n" +
                "· 模型文件不移动、不改名，GUID 不变；\n" +
                "· 被改写的引用文件会先备份到：\n{2}\n\n" +
                "建议先提交版本管理。是否继续？",
                changeFbx, changeMesh, backupDir),
            "执行", "取消");
        if (!ok)
        {
            return;
        }

        try
        {
            // 1) 记录旧 FileID（预览阶段已读取 oldFileId），把全部目标模型 GUID 写入持久规则
            List<string> allGuids = new List<string>();
            foreach (EmbeddedMeshFbxEntry e in emEntries)
            {
                if (string.IsNullOrEmpty(e.issue) && !string.IsNullOrEmpty(e.guid) && !allGuids.Contains(e.guid))
                {
                    allGuids.Add(e.guid);
                }
            }
            EmbeddedMeshNameMap.AddGuids(allGuids);
            EmbeddedMeshNameMap.Reload();

            // guid -> (旧 FileID -> 新 FileID)，仅保留真正变化的
            Dictionary<string, Dictionary<string, string>> remap =
                new Dictionary<string, Dictionary<string, string>>();

            // 2) 逐个重新导入，触发后处理器改名，再读取新 FileID
            for (int i = 0; i < targets.Count; i++)
            {
                EmbeddedMeshFbxEntry e = targets[i];
                EditorUtility.DisplayProgressBar("统一内嵌 Mesh 名",
                    "重新导入：" + e.fileBase + "（" + (i + 1) + "/" + targets.Count + "）",
                    targets.Count == 1 ? 0.2f : 0.2f + 0.4f * i / targets.Count);

                AssetDatabase.ImportAsset(e.path, ImportAssetOptions.ForceUpdate);

                GameObject newRoot = AssetDatabase.LoadAssetAtPath<GameObject>(e.path);
                if (newRoot == null)
                {
                    e.issue = "重导入后加载失败";
                    continue;
                }
                List<Mesh> newMeshes = EmbeddedMeshUtil.CollectMeshes(newRoot);
                if (newMeshes.Count != e.items.Count)
                {
                    e.issue = "重导入后 Mesh 数量变化，已跳过其引用修复";
                    continue;
                }

                Dictionary<string, string> perFile = null;
                for (int k = 0; k < e.items.Count; k++)
                {
                    EmbeddedMeshItem it = e.items[k];
                    string newGuid;
                    string newFileId;
                    EmbeddedMeshUtil.TryGetFileId(newMeshes[k], out newGuid, out newFileId);
                    it.newFileId = newFileId;
                    it.oldName = newMeshes[k].name; // 后处理器已改名，刷新为最终名
                    if (!string.IsNullOrEmpty(newFileId) && newFileId != it.oldFileId)
                    {
                        if (perFile == null)
                        {
                            perFile = new Dictionary<string, string>();
                        }
                        perFile[it.oldFileId] = newFileId;
                    }
                }
                if (perFile != null && !string.IsNullOrEmpty(e.guid))
                {
                    remap[e.guid] = perFile;
                }
            }

            // 3) 扫描并修复工程内文本序列化资源的 FileID 引用
            int changedFiles;
            int changedRefs;
            List<string> changedPaths = new List<string>();
            EditorUtility.DisplayProgressBar("统一内嵌 Mesh 名", "扫描并修复引用…", 0.75f);
            EmbeddedMeshReferenceFixer.FixAll(remap, backupDir, out changedFiles, out changedRefs, changedPaths);

            EditorUtility.DisplayProgressBar("统一内嵌 Mesh 名", "重新导入被修改的资源…", 0.95f);
            AssetDatabase.SaveAssets();
            int changedScenes = ReimportChangedAssets(changedPaths);

            emDirty = true;
            string sceneNote = changedScenes > 0
                ? "；另修改了 " + changedScenes + " 个场景文件，若正打开请在 Unity 提示外部修改时选择重载"
                : string.Empty;
            SetStatus(string.Format(
                "内嵌 Mesh 改名完成：{0} 个模型重新导入，{1} 个 Mesh 改名；FileID 变化 {2} 处，修复 {3} 个文件中的 {4} 处引用{5}。备份：{6}",
                targets.Count, changeMesh, CountChangedFileIds(remap), changedFiles, changedRefs, sceneNote, backupDir));
            Debug.Log("[VFX 工具箱] 内嵌 Mesh 改名完成。模型 " + targets.Count +
                      "，修复文件 " + changedFiles + "，引用 " + changedRefs + "，场景 " + changedScenes + "，备份目录 " + backupDir);
        }
        catch (Exception ex)
        {
            Debug.LogException(ex);
            EditorUtility.ClearProgressBar();
            EditorUtility.DisplayDialog("执行中断",
                "执行过程中出现异常：" + ex.Message + "\n\n被改写文件已备份到：\n" + backupDir +
                "\n可从备份恢复，或用版本管理回滚。", "知道了");
            SetStatus("内嵌 Mesh 改名执行异常：" + ex.Message);
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }
    }

    private static int CountChangedFileIds(Dictionary<string, Dictionary<string, string>> remap)
    {
        int n = 0;
        foreach (KeyValuePair<string, Dictionary<string, string>> kv in remap)
        {
            n += kv.Value.Count;
        }
        return n;
    }

    /// <summary>
    /// 对文本改写过的非场景资源强制重新导入，让 AssetDatabase 按新 FileID 解析；
    /// 场景文件（.unity）若正打开不能强制重导入，跳过并计数，交由 Unity 的外部修改提示重载。
    /// </summary>
    private static int ReimportChangedAssets(List<string> absolutePaths)
    {
        string dataPath = Application.dataPath.Replace('\\', '/');
        int scenes = 0;
        foreach (string abs in absolutePaths)
        {
            string norm = abs.Replace('\\', '/');
            if (!norm.StartsWith(dataPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            string rel = "Assets" + norm.Substring(dataPath.Length);
            if (rel.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
            {
                scenes++;
                continue;
            }
            AssetDatabase.ImportAsset(rel, ImportAssetOptions.ForceUpdate);
        }
        return scenes;
    }
}

// ================= 公共辅助 =================

internal static class EmbeddedMeshUtil
{
    /// <summary>
    /// 按固定、可复现的顺序收集模型层级里的全部 Mesh：先 MeshFilter 再 SkinnedMeshRenderer，
    /// 均按 GetComponentsInChildren 的深度优先顺序，并对共享同一 Mesh 的情况去重。
    /// 后处理器改名与工具侧读取 FileID 必须使用同一顺序，才能一一对应新旧 Mesh。
    /// </summary>
    public static List<Mesh> CollectMeshes(GameObject root)
    {
        List<Mesh> result = new List<Mesh>();
        HashSet<Mesh> seen = new HashSet<Mesh>();

        MeshFilter[] filters = root.GetComponentsInChildren<MeshFilter>(true);
        foreach (MeshFilter mf in filters)
        {
            if (mf.sharedMesh != null && seen.Add(mf.sharedMesh))
            {
                result.Add(mf.sharedMesh);
            }
        }

        SkinnedMeshRenderer[] skinned = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        foreach (SkinnedMeshRenderer smr in skinned)
        {
            if (smr.sharedMesh != null && seen.Add(smr.sharedMesh))
            {
                result.Add(smr.sharedMesh);
            }
        }
        return result;
    }

    /// <summary>目标 Mesh 名：单 Mesh 用文件名；多 Mesh 追加 _01、_02 …</summary>
    public static string TargetName(string fileBase, int index, int count)
    {
        return count == 1 ? fileBase : fileBase + "_" + (index + 1).ToString("D2");
    }

    /// <summary>取对象所属资源 GUID 与其在文件内的 FileID（无符号十进制，与 YAML 中写法一致）。</summary>
    public static bool TryGetFileId(Object obj, out string guid, out string fileId)
    {
        long local;
        if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(obj, out guid, out local))
        {
            fileId = ((ulong)local).ToString();
            return true;
        }
        guid = null;
        fileId = null;
        return false;
    }

    /// <summary>从当前 Project 选中项（多选 / 文件夹）收集全部模型文件路径（.fbx 等），去重并排序。</summary>
    public static List<string> CollectSelectedModelPaths()
    {
        List<string> paths = new List<string>();
        HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        Object[] selected = Selection.GetFiltered(typeof(Object), SelectionMode.Assets);
        foreach (Object o in selected)
        {
            if (o == null)
            {
                continue;
            }
            string p = AssetDatabase.GetAssetPath(o);
            if (string.IsNullOrEmpty(p) || !p.StartsWith("Assets", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (AssetDatabase.IsValidFolder(p))
            {
                string[] guids = AssetDatabase.FindAssets(string.Empty, new[] { p });
                foreach (string g in guids)
                {
                    string child = AssetDatabase.GUIDToAssetPath(g);
                    if (!AssetDatabase.IsValidFolder(child) && IsModelFile(child) && seen.Add(child))
                    {
                        paths.Add(child);
                    }
                }
            }
            else
            {
                // 选中内嵌 Mesh 子资产时 GetAssetPath 同样返回模型主文件路径
                if (IsModelFile(p) && seen.Add(p))
                {
                    paths.Add(p);
                }
            }
        }

        paths.Sort(StringComparer.OrdinalIgnoreCase);
        return paths;
    }

    /// <summary>递归收集指定若干文件夹下的全部模型文件路径（去重、排序）。</summary>
    public static List<string> CollectModelPathsInFolders(IEnumerable<string> folders)
    {
        List<string> paths = new List<string>();
        HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (folders != null)
        {
            foreach (string folder in folders)
            {
                if (string.IsNullOrEmpty(folder) || !AssetDatabase.IsValidFolder(folder))
                {
                    continue;
                }
                string[] guids = AssetDatabase.FindAssets("t:Model", new[] { folder });
                foreach (string g in guids)
                {
                    string child = AssetDatabase.GUIDToAssetPath(g);
                    if (!AssetDatabase.IsValidFolder(child) && IsModelFile(child) && seen.Add(child))
                    {
                        paths.Add(child);
                    }
                }
            }
        }
        paths.Sort(StringComparer.OrdinalIgnoreCase);
        return paths;
    }

    /// <summary>收集整个 Assets 下的全部模型文件路径。</summary>
    public static List<string> CollectAllModelPaths()
    {
        return CollectModelPathsInFolders(new[] { "Assets" });
    }

    private static bool IsModelFile(string path)
    {
        return path.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".dae", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".3ds", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".dxf", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".obj", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".blend", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".max", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".mb", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".ma", StringComparison.OrdinalIgnoreCase);
    }
}

// ================= 持久化规则 =================

[Serializable]
public class EmbeddedMeshNameMap
{
    public List<string> guids = new List<string>();

    private static readonly string FilePath =
        Path.GetFullPath(Path.Combine(Application.dataPath, "../ProjectSettings/EmbeddedMeshNames.json"));

    private static EmbeddedMeshNameMap instance;
    private static HashSet<string> enabledCache;

    public static HashSet<string> Enabled
    {
        get
        {
            if (enabledCache == null)
            {
                Reload();
            }
            return enabledCache;
        }
    }

    public static int EnabledCount
    {
        get { return Enabled.Count; }
    }

    public static void Reload()
    {
        instance = Load();
        enabledCache = new HashSet<string>(instance.guids ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
    }

    private static EmbeddedMeshNameMap Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                string json = File.ReadAllText(FilePath, Encoding.UTF8);
                if (!string.IsNullOrWhiteSpace(json))
                {
                    EmbeddedMeshNameMap map = JsonUtility.FromJson<EmbeddedMeshNameMap>(json);
                    if (map != null)
                    {
                        return map;
                    }
                }
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning("[VFX 工具箱] 读取 EmbeddedMeshNames.json 失败：" + e.Message);
        }
        return new EmbeddedMeshNameMap();
    }

    private void Save()
    {
        try
        {
            string dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            string json = JsonUtility.ToJson(this, true);
            File.WriteAllText(FilePath, json, new UTF8Encoding(false));
        }
        catch (Exception e)
        {
            Debug.LogWarning("[VFX 工具箱] 写入 EmbeddedMeshNames.json 失败：" + e.Message);
        }
    }

    /// <summary>把一批 GUID 加入启用集合并落盘（已存在的不重复）。</summary>
    public static void AddGuids(IEnumerable<string> newGuids)
    {
        if (instance == null)
        {
            Reload();
        }
        bool changed = false;
        HashSet<string> set = new HashSet<string>(instance.guids ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
        foreach (string g in newGuids)
        {
            if (!string.IsNullOrEmpty(g) && set.Add(g))
            {
                changed = true;
            }
        }
        if (changed)
        {
            instance.guids = new List<string>(set);
            instance.guids.Sort(StringComparer.OrdinalIgnoreCase);
            instance.Save();
        }
    }
}

// ================= 导入后处理器：持久维持 Mesh 名 =================

/// <summary>
/// 常驻模型后处理器：对启用了“Mesh 名跟随文件名”的模型，在导入管线里统一设置 Mesh 名。
/// 这样无论首次改名还是日后美术重新导入 / 更新模型，Mesh 名与其 FileID 都保持稳定。
/// </summary>
public class EmbeddedMeshNamePostprocessor : AssetPostprocessor
{
    private void OnPostprocessModel(GameObject root)
    {
        try
        {
            string guid = AssetDatabase.AssetPathToGUID(assetPath);
            if (string.IsNullOrEmpty(guid) || !EmbeddedMeshNameMap.Enabled.Contains(guid))
            {
                return;
            }

            List<Mesh> meshes = EmbeddedMeshUtil.CollectMeshes(root);
            if (meshes.Count == 0)
            {
                return;
            }

            string fileBase = Path.GetFileNameWithoutExtension(assetPath);
            for (int i = 0; i < meshes.Count; i++)
            {
                string target = EmbeddedMeshUtil.TargetName(fileBase, i, meshes.Count);
                if (meshes[i].name != target)
                {
                    meshes[i].name = target;
                }
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning("[VFX 工具箱] OnPostprocessModel 处理 " + assetPath + " 失败：" + e.Message);
        }
    }
}

// ================= 引用修复（文本级 FileID 替换） =================

internal static class EmbeddedMeshReferenceFixer
{
    // 工程内可能以 YAML 文本序列化、且能持有 Mesh 引用的资源扩展名
    private static readonly HashSet<string> TextExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".prefab", ".unity", ".asset", ".mat", ".anim", ".controller", ".overridecontroller",
        ".playable", ".preset", ".signal", ".mask", ".flare", ".terrainlayer", ".spriteatlas",
        ".lighting", ".rendertexture", ".cubemap", ".brushtemplate", ".shadervariants"
    };

    private static readonly Regex BracesRegex = new Regex(@"\{[^{}]*\}", RegexOptions.Compiled);
    private static readonly Regex GuidRegex = new Regex(@"guid:\s*([0-9a-fA-F]{32})", RegexOptions.Compiled);
    private static readonly Regex FileIdRegex = new Regex(@"fileID:\s*(-?\d+)", RegexOptions.Compiled);
    // 捕获 fileID 前缀（含冒号与空白），只替换其后的数字，保证块内其余内容不变
    private static readonly Regex FileIdPrefixRegex = new Regex(@"(fileID:\s*)-?\d+", RegexOptions.Compiled);

    /// <summary>
    /// 扫描 Assets 下全部文本序列化资源，把同一模型 GUID 下的旧 FileID 替换为新 FileID。
    /// 只命中同时匹配 guid 与旧 fileID 的内联引用块，绝不误伤；改写前整文件备份。
    /// </summary>
    public static void FixAll(
        Dictionary<string, Dictionary<string, string>> remap,
        string backupDir,
        out int changedFiles,
        out int changedRefs,
        List<string> changedPaths)
    {
        changedFiles = 0;
        changedRefs = 0;

        if (remap == null || remap.Count == 0)
        {
            return;
        }

        string assetsRoot = Application.dataPath;
        string[] allFiles = Directory.GetFiles(assetsRoot, "*", SearchOption.AllDirectories);
        List<string> candidates = new List<string>();
        foreach (string f in allFiles)
        {
            if (f.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            string ext = Path.GetExtension(f);
            if (TextExtensions.Contains(ext))
            {
                candidates.Add(f);
            }
        }

        for (int i = 0; i < candidates.Count; i++)
        {
            string file = candidates[i];
            if (candidates.Count > 20)
            {
                EditorUtility.DisplayProgressBar("扫描并修复引用",
                    Path.GetFileName(file), 0.75f + 0.2f * i / candidates.Count);
            }

            string original;
            try
            {
                original = File.ReadAllText(file);
            }
            catch
            {
                continue; // 占用 / 编码异常的文件跳过
            }

            // 粗筛：不含任何目标 GUID 的文件直接跳过
            bool containsGuid = false;
            foreach (string g in remap.Keys)
            {
                if (original.IndexOf(g, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    containsGuid = true;
                    break;
                }
            }
            if (!containsGuid)
            {
                continue;
            }

            int fileRefs = 0;
            string updated = BracesRegex.Replace(original, match =>
            {
                string body = match.Value;
                Match gm = GuidRegex.Match(body);
                Match fm = FileIdRegex.Match(body);
                if (!gm.Success || !fm.Success)
                {
                    return body;
                }
                string guid = gm.Groups[1].Value;
                string oldFileId = fm.Groups[1].Value;

                Dictionary<string, string> perFile;
                string newFileId;
                if (remap.TryGetValue(guid, out perFile) && perFile.TryGetValue(oldFileId, out newFileId)
                    && newFileId != oldFileId)
                {
                    fileRefs++;
                    return FileIdPrefixRegex.Replace(body, m => m.Groups[1].Value + newFileId, 1);
                }
                return body;
            });

            if (fileRefs > 0 && updated != original)
            {
                BackupFile(file, assetsRoot, backupDir);
                File.WriteAllText(file, updated, new UTF8Encoding(false));
                changedFiles++;
                changedRefs += fileRefs;
                if (changedPaths != null)
                {
                    changedPaths.Add(file);
                }
            }
        }
    }

    private static void BackupFile(string file, string assetsRoot, string backupDir)
    {
        string relative = file.Substring(assetsRoot.Length).TrimStart('\\', '/');
        string dest = Path.Combine(backupDir, relative);
        string dir = Path.GetDirectoryName(dest);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }
        File.Copy(file, dest, true);
    }
}
