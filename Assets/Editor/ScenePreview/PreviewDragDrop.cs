using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

/// <summary>预览列表接受的资源类别。</summary>
internal enum PreviewAssetKind
{
    Scene,
    Prefab
}

/// <summary>一个分类分组（含“未分类”）在窗口中的投放矩形。</summary>
internal struct PreviewDropTarget
{
    public string categoryId;
    public Rect screenRect;

    public PreviewDropTarget(string categoryId, Rect screenRect)
    {
        this.categoryId = categoryId;
        this.screenRect = screenRect;
    }
}

/// <summary>
/// 列表多选状态：
///   · 普通单击 = 只选这一项（并打开预览）；
///   · Ctrl / Cmd 单击 = 加选 / 减选；
///   · Shift 单击 = 从上次点击处连选（按当前可见顺序）。
/// 选中的 GUID 也会作为列表内拖拽的批量载荷。
/// </summary>
internal sealed class PreviewSelection
{
    private readonly List<string> selected = new List<string>();
    private readonly HashSet<string> lookup = new HashSet<string>();
    private string anchor;

    public int Count
    {
        get { return selected.Count; }
    }

    /// <summary>Ctrl/Cmd 是否按住（同时看 Event 快捷属性和 modifiers 标志）。</summary>
    public static bool IsCtrlPressed(Event e)
    {
        if (e == null) return false;
        if (e.control || e.command) return true;
        return (e.modifiers & EventModifiers.Control) != 0 || (e.modifiers & EventModifiers.Command) != 0;
    }

    public static bool IsShiftPressed(Event e)
    {
        if (e == null) return false;
        return e.shift || (e.modifiers & EventModifiers.Shift) != 0;
    }

    public bool IsSelected(string guid)
    {
        return !string.IsNullOrEmpty(guid) && lookup.Contains(guid);
    }

    public void Clear()
    {
        selected.Clear();
        lookup.Clear();
        anchor = null;
    }

    public void SelectOnly(string guid)
    {
        selected.Clear();
        lookup.Clear();

        if (!string.IsNullOrEmpty(guid))
        {
            selected.Add(guid);
            lookup.Add(guid);
        }

        anchor = guid;
    }

    public void Toggle(string guid)
    {
        if (string.IsNullOrEmpty(guid)) return;

        if (lookup.Contains(guid))
        {
            lookup.Remove(guid);
            selected.Remove(guid);
        }
        else
        {
            lookup.Add(guid);
            selected.Add(guid);
        }

        anchor = guid;
    }

    public void SetSelection(IList<string> guids)
    {
        selected.Clear();
        lookup.Clear();

        if (guids != null)
        {
            for (int i = 0; i < guids.Count; i++)
            {
                string guid = guids[i];
                if (string.IsNullOrEmpty(guid) || !lookup.Add(guid)) continue;
                selected.Add(guid);
            }
        }

        anchor = selected.Count > 0 ? selected[selected.Count - 1] : null;
    }

    public void SelectRange(string guid, IList<string> visibleOrder)
    {
        if (string.IsNullOrEmpty(guid)) return;
        if (string.IsNullOrEmpty(anchor) || visibleOrder == null)
        {
            SelectOnly(guid);
            return;
        }

        int from = IndexOf(visibleOrder, anchor);
        int to = IndexOf(visibleOrder, guid);
        if (from < 0 || to < 0)
        {
            SelectOnly(guid);
            return;
        }

        selected.Clear();
        lookup.Clear();

        int start = Mathf.Min(from, to);
        int end = Mathf.Max(from, to);
        for (int i = start; i <= end; i++)
        {
            string item = visibleOrder[i];
            if (string.IsNullOrEmpty(item) || !lookup.Add(item)) continue;
            selected.Add(item);
        }
    }

    public void Remove(string guid)
    {
        if (string.IsNullOrEmpty(guid) || !lookup.Remove(guid)) return;

        selected.Remove(guid);
        if (anchor == guid) anchor = null;
    }

    public List<string> Snapshot()
    {
        return new List<string>(selected);
    }

    private static int IndexOf(IList<string> list, string guid)
    {
        for (int i = 0; i < list.Count; i++)
            if (list[i] == guid) return i;
        return -1;
    }
}

/// <summary>工具箱列表内部拖拽的轻量载荷；不依赖 Project 资源拖拽数据。</summary>
internal sealed class PreviewInternalDragData
{
    public PreviewAssetKind kind;
    public List<string> guids;
}

/// <summary>
/// 预览工具箱共用的拖放控制器（场景预览 / 预制体预览各持有一个）：
///   · 从 Project 窗口拖入资源（可多选）直接加入预览列表；
///   · 拖入文件夹时递归收编文件夹内的同类资源；
///   · 从 Hierarchy 拖入预制体实例 / 场景物体，自动解析成对应的工程资源；
///   · 拖到某个分类标题或分类内条目上，添加时直接归入该分类；
///   · 列表内条目可拖到其它分类，已多选时整组选中项一起移动。
///
/// 生命周期约定（每个 OnGUI）：
///   ResetFrame() → 绘制左侧栏并在需要的地方 AddTarget() → 设置 area →
///   HandleEvents()（外部资源拖入）→ DrawOverlay()
/// 用到的 DragAndDrop / Event API 在 Unity 2019.4 ~ Unity 6 都是稳定接口。
/// </summary>
internal sealed class PreviewDropController
{
    private const int BulkConfirmThreshold = 100;    // 一次添加超过这个数量先确认
    private const string InternalDragKey = "ScenePreview.InternalDrag";

    private readonly PreviewAssetKind kind;
    private readonly List<PreviewDropTarget> targets = new List<PreviewDropTarget>();

    private string cacheKey;
    private List<string> cachePaths;

    /// <summary>整个窗口的投放区（外部资源拖入用；拖到非分类处 = 加入“未分类”）。</summary>
    public Rect area = Rect.zero;

    public bool hoverValid;
    public bool hoverIsReject;
    public bool hoverIsArea;
    public string hoverCategoryId;
    public Rect hoverRect;
    public string hoverHint = "";

    /// <summary>(资源路径列表, 目标分类 id) => 状态文字。</summary>
    public Func<List<string>, string, string> addAssets;
    /// <summary>(条目 GUID 列表, 目标分类 id) => 是否实际移动。</summary>
    public Func<List<string>, string, bool> moveItems;
    /// <summary>分类 id => 显示名。</summary>
    public Func<string, string> categoryName;
    /// <summary>请求宿主窗口重绘。</summary>
    public Action repaint;

    public PreviewDropController(PreviewAssetKind kind)
    {
        this.kind = kind;
    }

    /// <summary>由列表行在越过拖动阈值时调用，启动 Unity 原生拖放。</summary>
    public void BeginInternalDrag(List<string> guids)
    {
        if (guids == null || guids.Count == 0) return;

        var payload = new PreviewInternalDragData
        {
            kind = kind,
            guids = new List<string>(guids)
        };

        var objects = new List<UnityEngine.Object>();
        for (int i = 0; i < guids.Count; i++)
        {
            string path = AssetDatabase.GUIDToAssetPath(guids[i]);
            UnityEngine.Object asset = AssetDatabase.LoadMainAssetAtPath(path);
            if (asset != null) objects.Add(asset);
        }

        DragAndDrop.PrepareStartDrag();
        DragAndDrop.objectReferences = objects.ToArray();
        DragAndDrop.SetGenericData(InternalDragKey, payload);
        DragAndDrop.StartDrag("移动 " + guids.Count + " 个" + KindLabel);
    }

    private string KindLabel
    {
        get { return kind == PreviewAssetKind.Scene ? "场景" : "预制体"; }
    }

    // ================= 每帧调用 =================

    public void ResetFrame()
    {
        Event e = Event.current;
        // Layout 阶段 GUILayout 只计算尺寸，GetRect 常返回占位矩形；不能在此时覆盖
        // 上一次有效目标。其它事件会重放已完成的布局，此时矩形可靠，逐事件重建，
        // 保证紧接 Layout 的 DragUpdated / DragPerform 也能命中分类。
        if (e == null || e.type != EventType.Layout) targets.Clear();

        // 鼠标按下/松开/按键时清掉上一轮拖拽残留的高亮
        if (e != null && (e.type == EventType.MouseDown || e.type == EventType.MouseUp || e.type == EventType.KeyDown))
            ClearHover();
    }

    public void AddTarget(string categoryId, Rect rect)
    {
        Event e = Event.current;
        if (e != null && e.type == EventType.Layout) return;
        if (rect.width <= 1f || rect.height <= 0f) return;

        // 分类列表位于 ScrollView 中，rect 是当前 GUIClip 下的局部坐标；
        // 保存为屏幕坐标，避免 EndScrollView 后鼠标坐标系变化导致命中错误。
        Vector2 screenPosition = GUIUtility.GUIToScreenPoint(rect.position);
        targets.Add(new PreviewDropTarget(categoryId ?? "", new Rect(screenPosition, rect.size)));
    }

    public void ClearHover()
    {
        hoverValid = false;
        hoverIsReject = false;
        hoverIsArea = false;
        hoverCategoryId = null;
        hoverRect = Rect.zero;
        hoverHint = "";
    }

    // ================= 外部资源拖入 =================

    public void HandleEvents()
    {
        Event e = Event.current;
        if (e == null) return;

        if (e.type == EventType.DragExited)
        {
            ClearHover();
            return;
        }

        if (e.type != EventType.DragUpdated && e.type != EventType.DragPerform) return;

        string categoryId;
        Rect targetScreenRect;
        Vector2 screenMouse = GUIUtility.GUIToScreenPoint(e.mousePosition);
        bool onTarget = TryGetTarget(screenMouse, out categoryId, out targetScreenRect);
        Rect targetRect = onTarget
            ? new Rect(GUIUtility.ScreenToGUIPoint(targetScreenRect.position), targetScreenRect.size)
            : Rect.zero;
        bool inside = onTarget || (area.width > 1f && area.Contains(e.mousePosition));

        PreviewInternalDragData internalDrag = DragAndDrop.GetGenericData(InternalDragKey) as PreviewInternalDragData;
        if (internalDrag != null)
        {
            HandleInternalDrag(e, internalDrag, onTarget, categoryId, targetRect);
            return;
        }

        if (!inside)
        {
            ClearHover();
            SetVisualMode(DragAndDropVisualMode.None);
            return;
        }

        if (!onTarget)
        {
            categoryId = "";
            targetRect = area;
        }

        List<string> paths;
        string reason;
        if (!CollectDroppedAssets(out paths, out reason))
        {
            hoverValid = true;
            hoverIsReject = true;
            hoverIsArea = !onTarget;
            hoverCategoryId = categoryId;
            hoverRect = targetRect;
            hoverHint = reason;
            SetVisualMode(DragAndDropVisualMode.Rejected);
            RequestRepaint();
            return;
        }

        hoverValid = true;
        hoverIsReject = false;
        hoverIsArea = !onTarget;
        hoverCategoryId = categoryId;
        hoverRect = targetRect;
        hoverHint = "添加 " + paths.Count + " 个" + KindLabel + "到「" + DisplayCategory(categoryId) + "」";
        SetVisualMode(DragAndDropVisualMode.Copy);
        RequestRepaint();

        if (e.type != EventType.DragPerform) return;

        if (paths.Count > BulkConfirmThreshold &&
            !EditorUtility.DisplayDialog(
                "批量添加",
                "这一次将添加 " + paths.Count + " 个" + KindLabel + "到预览列表，确定吗？\n\n（已存在的会自动跳过）",
                "添加", "取消"))
        {
            SetVisualMode(DragAndDropVisualMode.Rejected);
            ClearHover();
            return;
        }

        DragAndDrop.AcceptDrag();
        e.Use();
        ClearHover();
        if (addAssets != null) addAssets(paths, categoryId);
    }

    private void HandleInternalDrag(Event e, PreviewInternalDragData payload, bool onTarget, string categoryId, Rect targetRect)
    {
        bool valid = payload.kind == kind && payload.guids != null && payload.guids.Count > 0 && onTarget;

        hoverValid = onTarget;
        hoverIsReject = onTarget && !valid;
        hoverIsArea = false;
        hoverCategoryId = categoryId;
        hoverRect = targetRect;
        hoverHint = valid
            ? "移动 " + payload.guids.Count + " 个" + KindLabel + "到「" + DisplayCategory(categoryId) + "」"
            : (onTarget ? "不能移动到这里" : "请拖到左侧分类标题或分类内条目上");

        SetVisualMode(valid ? DragAndDropVisualMode.Move : DragAndDropVisualMode.Rejected);
        RequestRepaint();

        if (e.type != EventType.DragPerform || !valid) return;

        DragAndDrop.AcceptDrag();
        e.Use();
        ClearHover();
        DragAndDrop.SetGenericData(InternalDragKey, null);
        if (moveItems != null) moveItems(new List<string>(payload.guids), categoryId ?? "");
    }

    private void RequestRepaint()
    {
        if (repaint != null) repaint();
    }

    private void SetVisualMode(DragAndDropVisualMode mode)
    {
        try { DragAndDrop.visualMode = mode; }
        catch (Exception) { }
    }

    private string DisplayCategory(string categoryId)
    {
        if (string.IsNullOrEmpty(categoryId)) return "未分类";
        string name = categoryName != null ? categoryName(categoryId) : null;
        return string.IsNullOrEmpty(name) ? "未分类" : name;
    }

    private bool TryGetTarget(Vector2 mouse, out string categoryId, out Rect rect)
    {
        // 后注册的（行）优先于先注册的（分组标题）
        for (int i = targets.Count - 1; i >= 0; i--)
        {
            if (targets[i].screenRect.Contains(mouse))
            {
                categoryId = targets[i].categoryId;
                rect = targets[i].screenRect;
                return true;
            }
        }

        categoryId = null;
        rect = Rect.zero;
        return false;
    }

    // ================= 拖入内容解析 =================

    private bool CollectDroppedAssets(out List<string> paths, out string reason)
    {
        paths = new List<string>();
        reason = null;

        string key = BuildDragKey();
        if (cachePaths != null && cacheKey == key)
        {
            paths.AddRange(cachePaths);
            if (paths.Count == 0) reason = "拖入的内容不是" + KindLabel;
            return paths.Count > 0;
        }

        List<string> ordered = new List<string>();
        HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        HashSet<string> folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int rejected = 0;

        // 1) 资源路径（Project 里拖资源/文件夹时最可靠）
        string[] dragPaths = null;
        try { dragPaths = DragAndDrop.paths; } catch (Exception) { dragPaths = null; }
        if (dragPaths != null)
        {
            for (int i = 0; i < dragPaths.Length; i++)
            {
                string path = PreviewCompat.NormalizePath(dragPaths[i]);
                if (string.IsNullOrEmpty(path)) continue;

                if (IsFolder(path))
                {
                    AddFolder(path, ordered, seen, folders);
                    continue;
                }

                if (Accept(path)) AddUnique(path, ordered, seen);
                else rejected++;
            }
        }

        // 2) 对象引用（Hierarchy 的预制体实例、场景物体；以及部分拖拽源只给 objectReferences）
        UnityEngine.Object[] objects = null;
        try { objects = DragAndDrop.objectReferences; } catch (Exception) { objects = null; }
        if (objects != null)
        {
            for (int i = 0; i < objects.Length; i++)
            {
                UnityEngine.Object obj = objects[i];
                if (obj == null) continue;

                string assetPath = PreviewCompat.NormalizePath(AssetDatabase.GetAssetPath(obj));
                if (!string.IsNullOrEmpty(assetPath))
                {
                    if (IsFolder(assetPath))
                    {
                        AddFolder(assetPath, ordered, seen, folders);
                        continue;
                    }

                    if (Accept(assetPath))
                    {
                        AddUnique(assetPath, ordered, seen);
                        continue;
                    }

                    rejected++;
                    continue;
                }

                // Hierarchy 物体：预制体实例 → 预制体资源；场景物体 → 所属场景资源
                string resolved = kind == PreviewAssetKind.Prefab
                    ? PreviewCompat.PrefabPathOf(obj)
                    : PreviewCompat.ScenePathOf(obj);
                if (!string.IsNullOrEmpty(resolved) && Accept(resolved))
                {
                    AddUnique(resolved, ordered, seen);
                    continue;
                }

                rejected++;
            }
        }

        cacheKey = key;
        cachePaths = new List<string>(ordered);
        paths.AddRange(ordered);

        if (paths.Count == 0)
        {
            reason = rejected > 0
                ? "拖入的内容不是" + KindLabel + "（当前标签只接受" + KindLabel + "）"
                : "没有可添加的" + KindLabel;
            return false;
        }

        return true;
    }

    private bool Accept(string path)
    {
        path = PreviewCompat.NormalizePath(path);
        if (!PreviewCompat.IsUnderAssets(path)) return false;

        return kind == PreviewAssetKind.Scene
            ? PreviewCompat.IsScenePath(path)
            : PreviewCompat.IsPrefabPath(path);
    }

    private static bool IsFolder(string path)
    {
        try { return AssetDatabase.IsValidFolder(path); }
        catch (Exception) { return false; }
    }

    private void AddFolder(string folder, List<string> ordered, HashSet<string> seen, HashSet<string> folders)
    {
        if (string.IsNullOrEmpty(folder) || !folders.Add(folder)) return;

        string filter = kind == PreviewAssetKind.Scene ? "t:Scene" : "t:Prefab";
        string[] guids;
        try { guids = AssetDatabase.FindAssets(filter, new[] { folder }); }
        catch (Exception) { return; }

        if (guids == null) return;
        for (int i = 0; i < guids.Length; i++)
        {
            string path = PreviewCompat.NormalizePath(AssetDatabase.GUIDToAssetPath(guids[i]));
            if (Accept(path)) AddUnique(path, ordered, seen);
        }
    }

    private static void AddUnique(string path, List<string> ordered, HashSet<string> seen)
    {
        path = PreviewCompat.NormalizePath(path);
        if (string.IsNullOrEmpty(path)) return;
        if (!seen.Add(path)) return;
        ordered.Add(path);
    }

    private string BuildDragKey()
    {
        string key = kind == PreviewAssetKind.Scene ? "S:" : "P:";

        string[] paths = null;
        try { paths = DragAndDrop.paths; } catch (Exception) { paths = null; }
        if (paths != null)
            for (int i = 0; i < paths.Length; i++) key += paths[i] + "|";

        UnityEngine.Object[] objects = null;
        try { objects = DragAndDrop.objectReferences; } catch (Exception) { objects = null; }
        if (objects != null)
            for (int i = 0; i < objects.Length; i++)
                key += (objects[i] != null ? objects[i].GetInstanceID() : 0) + "|";

        return key;
    }

    // ================= 悬停高亮 =================

    private static GUIStyle hintStyle;

    private static GUIStyle HintStyle()
    {
        if (hintStyle == null)
        {
            hintStyle = new GUIStyle(EditorStyles.miniLabel);
            hintStyle.normal.textColor = Color.white;
            hintStyle.alignment = TextAnchor.MiddleLeft;
        }
        return hintStyle;
    }

    public void DrawOverlay()
    {
        Event e = Event.current;
        if (!hoverValid || e == null || e.type != EventType.Repaint) return;
        if (hoverRect.width <= 1f || hoverRect.height <= 1f) return;

        // 拖在分类/条目上时高亮更强；只是悬停在窗口空白处时用很淡的一层，避免糊住预览画面
        float alpha = hoverIsArea ? 0.08f : 0.22f;
        Color color = hoverIsReject
            ? new Color(1f, 0.35f, 0.35f, alpha)
            : new Color(0.35f, 0.62f, 1f, alpha);
        EditorGUI.DrawRect(hoverRect, color);

        DrawHint(new Rect(hoverRect.x + 4f, hoverRect.y + 2f, Mathf.Max(1f, hoverRect.width - 8f), 18f), hoverHint);
    }

    private static void DrawHint(Rect area, string text)
    {
        if (string.IsNullOrEmpty(text) || area.width < 32f) return;

        GUIContent content = new GUIContent(text);
        Vector2 size = HintStyle().CalcSize(content);
        float width = Mathf.Min(area.width, size.x + 10f);
        if (width < 32f) return;

        Rect bg = new Rect(area.x, area.y, width, 18f);
        EditorGUI.DrawRect(bg, new Color(0f, 0f, 0f, 0.66f));
        GUI.Label(new Rect(bg.x + 5f, bg.y + 1f, bg.width - 10f, bg.height - 2f), content, HintStyle());
    }
}
