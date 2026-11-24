using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// 模块三：批量重命名（精简版）。
/// 规则：
///   · 单选一个文件（非文件夹）：直接用“命名格式”原名，起始序号 / 位数不生效；
///   · 多选文件，或选中文件夹：新名字 = 命名格式 + “_” + 序号；
///     起始序号决定末尾数字从几开始，位数决定补零位数。
/// 例：命名格式 Effect_VoiceNationalWar_Material，起始序号 1，位数 2
///   → Effect_VoiceNationalWar_Material_01
///   → Effect_VoiceNationalWar_Material_02
///   → Effect_VoiceNationalWar_Material_03 ...
/// 文件扩展名一律保持不变。
///
/// 全部改名走 AssetDatabase.RenameAsset：只改文件名，.meta 与 GUID 原样保留，
/// 材质 / shader / 预制体 / 场景等对该资源的引用不会丢失。
/// 文件夹本身永远不改名；选中文件夹时，处理其内部（含子文件夹）的全部资源。
/// </summary>
public partial class VFXToolboxWindow
{
    [Serializable]
    private class RenameEntry
    {
        public string oldPath;       // Assets/... 相对路径（含扩展名）
        public string oldBaseName;   // 不含扩展名
        public string extension;     // 含点，如 ".png"
        public string newBaseName;   // 预览的新名字（不含扩展名）
        public string issue;         // 非空表示有问题，禁止执行
    }

    [SerializeField] private string rnFormat = "NewAsset";
    [SerializeField] private int rnStartIndex = 1;
    [SerializeField] private int rnPadding = 2;

    private readonly List<RenameEntry> rnEntries = new List<RenameEntry>();
    private Vector2 rnScroll;
    private bool rnDirty = true;
    private bool rnSingleMode; // true = 单选一个文件，直接用命名格式原名

    private void InitRenameTool()
    {
    }

    private void OnSelectionChange()
    {
        rnDirty = true;
        mcDirty = true;
        exSelectionDirty = true;
        emDirty = true;
        Repaint();
    }

    private void DrawRenameTool()
    {
        EditorGUILayout.LabelField("批量重命名（保留 GUID / 引用）", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "在 Project 窗口选中要改名的资源（可多选，也可直接选文件夹）。\n" +
            "· 单选一个文件：直接用“命名格式”命名，起始序号 / 位数不生效；\n" +
            "· 多选或选中文件夹：命名格式 + _序号，起始 1、位数 2 时得到 _01、_02、_03 …\n" +
            "改名通过 AssetDatabase.RenameAsset 完成，.meta 与 GUID 不变，引用不会丢失；文件夹不会被改名。",
            MessageType.None);

        EditorGUI.BeginChangeCheck();
        rnFormat = EditorGUILayout.TextField(new GUIContent("命名格式", "单选文件时直接用此名字；多选时序号自动以 _01、_02 追加在最后"), rnFormat);

        // 单选文件时序号规则不生效，灰掉两个字段
        using (new EditorGUI.DisabledScope(rnSingleMode))
        {
            rnStartIndex = EditorGUILayout.IntField(new GUIContent("起始序号", "多选时命名格式最后的数字从多少开始"), rnStartIndex);
            rnPadding = Mathf.Clamp(EditorGUILayout.IntField(new GUIContent("位数", "多选时序号补零位数，如 2 → 01、02；0 = 不补零"), rnPadding), 0, 10);
        }
        if (rnSingleMode)
        {
            EditorGUILayout.HelpBox("当前为单选文件：将直接命名为 “" + (rnFormat ?? string.Empty).Trim() + "”，不追加序号。", MessageType.None);
        }
        if (EditorGUI.EndChangeCheck())
        {
            rnDirty = true;
        }

        EditorGUILayout.Space(4);
        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("刷新预览", GUILayout.Height(24)))
        {
            rnDirty = true;
        }
        int selectedAssetCount = Selection.GetFiltered(typeof(UnityEngine.Object), SelectionMode.Assets).Length;
        GUILayout.Label("当前 Project 选中：" + selectedAssetCount + " 项", EditorStyles.miniLabel);
        EditorGUILayout.EndHorizontal();

        if (rnDirty)
        {
            RebuildRenamePreview();
            rnDirty = false;
        }

        DrawRenamePreviewAndExecute();
    }

    private void DrawRenamePreviewAndExecute()
    {
        if (rnEntries.Count == 0)
        {
            EditorGUILayout.HelpBox(
                "没有可改名的对象。请在 Project 窗口选中资源或文件夹（不要在场景 Hierarchy 里选）。",
                MessageType.Info);
            return;
        }

        int errorCount = 0;
        int changeCount = 0;
        foreach (RenameEntry e in rnEntries)
        {
            if (!string.IsNullOrEmpty(e.issue))
            {
                errorCount++;
            }
            else if (e.newBaseName != e.oldBaseName)
            {
                changeCount++;
            }
        }

        EditorGUILayout.Space(4);
        string summary;
        if (rnSingleMode)
        {
            summary = "单选 1 个文件：直接命名为 “" + (rnFormat ?? string.Empty).Trim() + "”（不追加序号）";
        }
        else
        {
            summary = "共 " + rnEntries.Count + " 个文件；将改名 " + changeCount + " 个";
            if (errorCount > 0)
            {
                summary += "；" + errorCount + " 个有冲突/非法，将跳过";
            }
        }
        EditorGUILayout.LabelField(summary, EditorStyles.boldLabel);

        rnScroll = EditorGUILayout.BeginScrollView(rnScroll, EditorStyles.helpBox, GUILayout.MinHeight(200));
        foreach (RenameEntry e in rnEntries)
        {
            bool hasIssue = !string.IsNullOrEmpty(e.issue);
            EditorGUILayout.BeginHorizontal(EditorStyles.miniLabel);
            Color oldColor = GUI.color;
            if (hasIssue)
            {
                GUI.color = new Color(1f, 0.55f, 0.55f);
            }
            else if (e.newBaseName == e.oldBaseName)
            {
                GUI.color = new Color(0.75f, 0.75f, 0.75f);
            }
            else
            {
                GUI.color = new Color(0.65f, 1f, 0.65f);
            }

            GUILayout.Label("FILE", GUILayout.Width(32));
            GUILayout.Label(e.oldBaseName + e.extension, GUILayout.MinWidth(150));
            GUILayout.Label("→", GUILayout.Width(20));
            GUILayout.Label(e.newBaseName + e.extension, GUILayout.MinWidth(150));
            if (hasIssue)
            {
                GUILayout.Label(e.issue, EditorStyles.boldLabel);
            }
            GUI.color = oldColor;
            EditorGUILayout.EndHorizontal();
        }
        EditorGUILayout.EndScrollView();

        EditorGUILayout.Space(4);
        using (new EditorGUI.DisabledScope(changeCount == 0 || errorCount > 0))
        {
            if (GUILayout.Button("执行重命名（保留 GUID）", GUILayout.Height(30)))
            {
                ExecuteRename();
            }
        }
        if (errorCount > 0)
        {
            EditorGUILayout.HelpBox("存在标红的冲突项，请先修改命名格式 / 起始序号 / 位数后再执行。", MessageType.Warning);
        }
    }

    private void RebuildRenamePreview()
    {
        rnEntries.Clear();

        UnityEngine.Object[] selected = Selection.GetFiltered(typeof(UnityEngine.Object), SelectionMode.Assets);
        List<string> filePaths = new List<string>();
        HashSet<string> seen = new HashSet<string>();

        foreach (UnityEngine.Object o in selected)
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
                // 选中文件夹：收集其内部全部资源（含子文件夹），但文件夹本身不改名
                string[] guids = AssetDatabase.FindAssets(string.Empty, new[] { p });
                foreach (string guid in guids)
                {
                    string child = AssetDatabase.GUIDToAssetPath(guid);
                    if (!AssetDatabase.IsValidFolder(child) && seen.Add(child))
                    {
                        filePaths.Add(child);
                    }
                }
            }
            else
            {
                if (AssetDatabase.IsSubAsset(o))
                {
                    continue; // FBX 内嵌网格等子资产不能按文件改名
                }
                if (seen.Add(p))
                {
                    filePaths.Add(p);
                }
            }
        }

        filePaths.Sort(StringComparer.OrdinalIgnoreCase);

        // 单选一个文件（非文件夹、非子资产）：直接用命名格式原名，序号不生效
        rnSingleMode = false;
        if (filePaths.Count == 1 && selected.Length == 1 && selected[0] != null)
        {
            string onlyPath = AssetDatabase.GetAssetPath(selected[0]);
            if (!string.IsNullOrEmpty(onlyPath)
                && !AssetDatabase.IsValidFolder(onlyPath)
                && !AssetDatabase.IsSubAsset(selected[0]))
            {
                rnSingleMode = true;
            }
        }

        if (rnSingleMode)
        {
            AddRenameEntry(filePaths[0], 0, false);
        }
        else
        {
            for (int i = 0; i < filePaths.Count; i++)
            {
                AddRenameEntry(filePaths[i], rnStartIndex + i, true);
            }
        }

        ValidateRenameEntries();
    }

    private void AddRenameEntry(string path, int index, bool appendIndex)
    {
        string fileName = Path.GetFileName(path);
        string ext = Path.GetExtension(fileName);
        string baseName = Path.GetFileNameWithoutExtension(fileName);

        string format = (rnFormat ?? string.Empty).Trim();

        string finalName;
        if (!appendIndex)
        {
            // 单选文件：直接使用命名格式，不追加任何序号
            finalName = format;
        }
        else
        {
            // 序号补零：位数 2、序号 1 → "01"；位数 0 → 不补零
            string number = rnPadding > 0 ? index.ToString("D" + rnPadding) : index.ToString();

            // 格式末尾已带下划线就不再重复添加
            string separator = format.EndsWith("_", StringComparison.Ordinal) ? string.Empty : "_";
            finalName = format.Length > 0 ? format + separator + number : number;
        }

        rnEntries.Add(new RenameEntry
        {
            oldPath = path,
            oldBaseName = baseName,
            extension = ext,
            newBaseName = finalName,
            issue = ""
        });
    }

    private void ValidateRenameEntries()
    {
        // 同目录下目标名重复检查
        Dictionary<string, int> targetCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        char[] invalid = Path.GetInvalidFileNameChars();

        foreach (RenameEntry e in rnEntries)
        {
            if (string.IsNullOrEmpty(e.newBaseName))
            {
                e.issue = "新名字为空（请填写命名格式）";
                continue;
            }
            if (e.newBaseName.IndexOfAny(invalid) >= 0 ||
                e.newBaseName.IndexOf('/') >= 0 || e.newBaseName.IndexOf('\\') >= 0)
            {
                e.issue = "命名格式含非法字符";
                continue;
            }

            string dir = Path.GetDirectoryName(e.oldPath)?.Replace('\\', '/');
            string targetFull = (dir + "/" + e.newBaseName + e.extension).Replace('\\', '/');
            if (targetCount.ContainsKey(targetFull))
            {
                targetCount[targetFull]++;
            }
            else
            {
                targetCount[targetFull] = 1;
            }
        }

        foreach (RenameEntry e in rnEntries)
        {
            if (!string.IsNullOrEmpty(e.issue))
            {
                continue;
            }
            string dir = Path.GetDirectoryName(e.oldPath)?.Replace('\\', '/');
            string targetFull = (dir + "/" + e.newBaseName + e.extension).Replace('\\', '/');

            if (targetCount[targetFull] > 1)
            {
                e.issue = "目标名字重复";
                continue;
            }

            if (e.newBaseName == e.oldBaseName)
            {
                continue;
            }

            // 交给 Unity 预检：是否与目录内其他资源冲突等
            string validate = AssetDatabase.ValidateMoveAsset(e.oldPath, targetFull);
            if (!string.IsNullOrEmpty(validate))
            {
                e.issue = "冲突：" + validate;
            }
        }
    }

    private void ExecuteRename()
    {
        int ok = 0;
        int fail = 0;
        StringBuilder errors = new StringBuilder();

        foreach (RenameEntry e in rnEntries)
        {
            if (!string.IsNullOrEmpty(e.issue) || e.newBaseName == e.oldBaseName)
            {
                continue;
            }
            // RenameAsset 的新名字【不能带扩展名】，Unity 会自动保留原扩展名与 .meta（GUID 不变）
            string err = AssetDatabase.RenameAsset(e.oldPath, e.newBaseName);
            if (!string.IsNullOrEmpty(err))
            {
                fail++;
                errors.AppendLine("• " + e.oldBaseName + " → " + e.newBaseName + "：" + err);
            }
            else
            {
                ok++;
            }
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        rnDirty = true;

        if (fail == 0)
        {
            SetStatus("重命名完成：" + ok + " 个文件，GUID 与引用均保持不变。");
        }
        else
        {
            SetStatus("完成 " + ok + " 个，失败 " + fail + " 个。\n" + errors);
            EditorUtility.DisplayDialog("批量重命名", "完成 " + ok + " 个，失败 " + fail + " 个：\n" + errors, "OK");
        }
    }
}
