using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using UnityEngine.SceneManagement;

/// <summary>
/// 场景预览面板（集成于 Tools/预览工具箱 的“场景预览”标签）。
/// 左侧是“场景管理”：两级分类（一级分类 + 子分类），条目可直接挂一级分类，也可挂子分类；
/// 分类与归属持久化到工程内 ProjectSettings/ScenePreviewLayout.json；
/// 把场景或整个文件夹从 Project 窗口直接拖进来即可添加（也保留“＋添加场景(扫描)”），
/// 拖到一级/二级分类标题或分类内条目上会直接归入该分类；列表条目也可拖拽或右键批量移动。
/// 选中场景后在右侧“不进入 Play Mode”直接用该场景自带摄像机离屏渲染并动态播放：
///   粒子 ParticleSystem.Simulate、Timeline PlayableDirector、独立 Animator 的 PlayableGraph、时间 Shader(_Time)。
/// 目标场景 Additive 后台加载、临时隐藏其它场景，切换/关闭时还原，全程不保存、不改磁盘场景。
/// 兼容 Unity 2019.4 LTS ~ Unity 6（不依赖 com.unity.timeline 包，版本差异集中在 PreviewCompat）。
/// 限制：运行时代码（Update/协程/物理）需 Play Mode。
/// </summary>
internal class ScenePreviewPanel : PreviewPanel
{
    public override string Title => "场景预览";

    // ================= 持久化数据模型 =================

    [Serializable]
    private class CategoryData
    {
        public string id;
        public string name;
        public bool collapsed;
        public string parentId; // "" / null = 一级分类；否则指向一级分类 id（只支持两级）
    }

    [Serializable]
    private class ItemData
    {
        public string guid;     // 场景资源 GUID（改名/移动不丢）
        public string category; // 所属分类 id；"" = 未分类
    }

    [Serializable]
    private class LayoutData
    {
        public List<CategoryData> categories = new List<CategoryData>();
        public List<ItemData> items = new List<ItemData>();
        public float listWidth = 240f;
    }

    private sealed class PreviewSession : IDisposable
    {
        public Scene scene;
        public Camera[] cameras;
        public int cameraIndex;
        public ParticleSystem[] particles;
        public PlayableDirector[] directors;
        public readonly List<AnimatorGraph> animGraphs = new List<AnimatorGraph>();
        public RenderTexture rt;
        public readonly List<KeyValuePair<GameObject, bool>> hiddenRoots = new List<KeyValuePair<GameObject, bool>>();

        public float duration = 10f;
        public bool hasTimeline;
        public float time;
        public bool noCamera;

        public Camera ActiveCamera
        {
            get
            {
                if (cameras == null || cameras.Length == 0) return null;
                int i = Mathf.Clamp(cameraIndex, 0, cameras.Length - 1);
                return cameras[i];
            }
        }

        public sealed class AnimatorGraph
        {
            public Animator animator;
            public PlayableGraph graph;
            public AnimationClipPlayable playable;
            public float clipLength;
        }

        public void Dispose()
        {
            foreach (AnimatorGraph g in animGraphs)
                if (g != null && g.graph.IsValid()) g.graph.Destroy();
            animGraphs.Clear();

            if (cameras != null)
                foreach (Camera c in cameras)
                    if (c != null) c.targetTexture = null;

            if (rt != null) { rt.Release(); UnityEngine.Object.DestroyImmediate(rt); rt = null; }

            // 不允许卸载最后一个场景；若只剩这一个，留给后续 OpenScene(Single) 替换
            if (scene.IsValid() && scene.isLoaded && UnityEditor.SceneManagement.EditorSceneManager.sceneCount > 1)
                EditorSceneManager.CloseScene(scene, true);

            foreach (KeyValuePair<GameObject, bool> kv in hiddenRoots)
                if (kv.Key != null) kv.Key.SetActive(kv.Value);
            hiddenRoots.Clear();
        }
    }

    // 扫描面板的一行
    private sealed class ScanRow
    {
        public string guid, path, name, category;
        public bool selected, already;
    }

    // ================= 字段 =================

    private LayoutData layout = new LayoutData();
    private string layoutPath;

    private string sidebarSearch = "";
    private Vector2 listScroll;

    // 分类内联编辑
    private bool creatingCategory;
    private string newCategoryName = "";
    private string creatingParentId = ""; // "" = 新建一级分类；否则为父分类 id（二级分类）
    private string renamingCategoryId = null;
    private string renameBuffer = "";

    // 扫描模式
    private bool scanMode;
    private readonly List<ScanRow> scanRows = new List<ScanRow>();
    private string scanSearch = "";
    private int scanDefaultCategoryIndex;

    private bool draggingSplitter;
    private string dragCandidateGuid;
    private Vector2 dragStartPosition;
    private int dragControlId;
    private int dragClickCount;

    // 拖拽：Project/Hierarchy 拖入添加，列表内拖动则快捷分类
    private readonly PreviewDropController dropController = new PreviewDropController(PreviewAssetKind.Scene);
    private readonly PreviewSelection selection = new PreviewSelection();
    private readonly List<string> visibleGuids = new List<string>();

    [SerializeField] private string selectedPath = "";
    [SerializeField] private bool loop = true;
    [SerializeField] private bool simulateParticles = true;
    [SerializeField] private float customLength = 10f;
    [SerializeField] private int speedIndex = 3;

    private static readonly float[] Speeds = { 0.1f, 0.25f, 0.5f, 1f, 2f, 4f };
    private static readonly string[] SpeedLabels = { "0.1x", "0.25x", "0.5x", "1x", "2x", "4x" };

    private PreviewSession session;
    private bool playing = true;
    private double lastReal;
    private string status = "";

    // ================= 生命周期 =================

    public override void OnEnable()
    {
        layoutPath = Path.Combine(Application.dataPath, "..", "ProjectSettings", "ScenePreviewLayout.json");
        LoadLayout();

        dropController.addAssets = AddDroppedAssets;
        dropController.moveItems = MoveItemsToCategory;
        dropController.categoryName = CategoryName;
        dropController.repaint = Repaint;

        EditorApplication.update += OnEditorTick;
        lastReal = Time.realtimeSinceStartup;
    }

    // 首次切到该标签时才恢复上次打开的场景，避免后台标签预加载、隐藏当前编辑场景
    public override void OnFirstActivate()
    {
        if (!string.IsNullOrEmpty(selectedPath))
            EditorApplication.delayCall += () => { if (host != null && isActive && session == null && !string.IsNullOrEmpty(selectedPath)) OpenScene(selectedPath); };
    }

    public override void OnDisable()
    {
        EditorApplication.update -= OnEditorTick;
        CloseSession();
        SaveLayout();
    }

    public override void OnBecameVisible() { lastReal = Time.realtimeSinceStartup; }

    // ================= 布局持久化 =================

    private void LoadLayout()
    {
        layout = new LayoutData();
        try
        {
            if (File.Exists(layoutPath))
            {
                LayoutData d = JsonUtility.FromJson<LayoutData>(File.ReadAllText(layoutPath));
                if (d != null) layout = d;
            }
        }
        catch (Exception e) { Debug.LogWarning("[场景预览] 读取分类配置失败，将使用空配置：" + e.Message); }
        if (layout.categories == null) layout.categories = new List<CategoryData>();
        if (layout.items == null) layout.items = new List<ItemData>();
        SanitizeCategories();
        if (layout.listWidth < 140f) layout.listWidth = 240f;
    }

    /// <summary>兼容旧配置并保证只有两级：清理找不到父级、自引用、三级以上的 parentId。</summary>
    private void SanitizeCategories()
    {
        layout.categories.RemoveAll(c => c == null || string.IsNullOrEmpty(c.id));

        foreach (CategoryData c in layout.categories)
        {
            if (string.IsNullOrEmpty(c.parentId)) continue;
            if (c.parentId == c.id || FindCategory(c.parentId) == null) c.parentId = "";
        }

        // 父级本身是二级分类 → 提升为一级，保证最多两级
        foreach (CategoryData c in layout.categories)
        {
            if (string.IsNullOrEmpty(c.parentId)) continue;
            CategoryData parent = FindCategory(c.parentId);
            if (parent != null && !string.IsNullOrEmpty(parent.parentId)) c.parentId = "";
        }

        foreach (ItemData it in layout.items)
            if (!string.IsNullOrEmpty(it.category) && FindCategory(it.category) == null) it.category = "";
    }

    private void SaveLayout()
    {
        try
        {
            if (layout != null)
            {
                if (layout.listWidth <= 0f) layout.listWidth = 240f;
                File.WriteAllText(layoutPath, JsonUtility.ToJson(layout, true));
            }
        }
        catch (Exception e) { Debug.LogWarning("[场景预览] 保存分类配置失败：" + e.Message); }
    }

    // 分类/条目 helper
    private CategoryData FindCategory(string id) { return string.IsNullOrEmpty(id) ? null : layout.categories.Find(c => c.id == id); }
    private ItemData FindItem(string guid) { return layout.items.Find(i => i.guid == guid); }

    private bool IsRootCategory(CategoryData cat)
    {
        return cat != null && string.IsNullOrEmpty(cat.parentId);
    }

    private List<CategoryData> RootCategories()
    {
        return layout.categories.FindAll(c => IsRootCategory(c));
    }

    private List<CategoryData> ChildCategories(string parentId)
    {
        if (string.IsNullOrEmpty(parentId)) return new List<CategoryData>();
        return layout.categories.FindAll(c => c.parentId == parentId);
    }

    /// <summary>分类的完整显示名：一级分类 = 名字，二级分类 = "父/子"。</summary>
    private string CategoryName(string id)
    {
        CategoryData c = FindCategory(id);
        if (c == null) return "未分类";

        CategoryData parent = FindCategory(c.parentId);
        return parent != null ? parent.name + "/" + c.name : c.name;
    }

    /// <summary>所有分类的显示顺序：一级分类后紧跟它的二级分类。</summary>
    private List<CategoryData> OrderedCategories()
    {
        var ordered = new List<CategoryData>();

        foreach (CategoryData root in layout.categories)
        {
            if (!IsRootCategory(root)) continue;

            ordered.Add(root);
            foreach (CategoryData sub in layout.categories)
                if (sub.parentId == root.id) ordered.Add(sub);
        }

        // 保险：异常数据里没有被父级带上的分类也列出来
        foreach (CategoryData c in layout.categories)
            if (!ordered.Contains(c)) ordered.Add(c);

        return ordered;
    }

    private string[] CategoryOptions()
    {
        var options = new List<string> { "未分类" };
        foreach (CategoryData c in OrderedCategories()) options.Add(CategoryName(c.id));
        return options.ToArray();
    }

    private int CategoryToIndex(string id)
    {
        if (string.IsNullOrEmpty(id)) return 0;
        int index = OrderedCategories().FindIndex(c => c.id == id);
        return index < 0 ? 0 : index + 1;
    }

    private string IndexToCategory(int index)
    {
        List<CategoryData> ordered = OrderedCategories();
        return index <= 0 || index > ordered.Count ? "" : ordered[index - 1].id;
    }

    private void CreateCategory(string name, string parentId)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0) return;

        // 只允许两级：父级必须存在且本身是一级分类
        CategoryData parent = FindCategory(parentId);
        if (parent == null || !IsRootCategory(parent))
        {
            parent = null;
            parentId = "";
        }

        layout.categories.Add(new CategoryData
        {
            id = Guid.NewGuid().ToString("N"),
            name = name,
            collapsed = false,
            parentId = parentId ?? ""
        });

        if (parent != null && parent.collapsed) parent.collapsed = false;
        SaveLayout();
    }

    private void DeleteCategory(string id)
    {
        CategoryData cat = FindCategory(id);
        if (cat == null) return;

        // 删一级分类时连它的二级分类一起删；条目落到未分类。
        // 删二级分类时条目落到它的一级分类里。
        string fallback = IsRootCategory(cat) ? "" : cat.parentId;

        var removing = new HashSet<string>();
        removing.Add(id);
        bool grew = true;
        while (grew)
        {
            grew = false;
            foreach (CategoryData c in layout.categories)
            {
                if (string.IsNullOrEmpty(c.parentId) || removing.Contains(c.id)) continue;
                if (!removing.Contains(c.parentId)) continue;
                removing.Add(c.id);
                grew = true;
            }
        }

        foreach (ItemData it in layout.items)
            if (it.category != null && removing.Contains(it.category)) it.category = fallback;

        layout.categories.RemoveAll(c => c == null || removing.Contains(c.id));

        if (renamingCategoryId != null && removing.Contains(renamingCategoryId)) renamingCategoryId = null;
        if (creatingCategory && removing.Contains(creatingParentId))
        {
            creatingCategory = false;
            creatingParentId = "";
            newCategoryName = "";
        }

        SaveLayout();
    }

    private void SetItemCategory(string guid, string categoryId)
    {
        ItemData it = FindItem(guid);
        if (it == null) return;

        it.category = categoryId;

        CategoryData target = FindCategory(categoryId);
        if (target != null && target.collapsed) target.collapsed = false;

        SaveLayout();
    }

    private void RemoveItem(string guid)
    {
        layout.items.RemoveAll(i => i.guid == guid);
        selection.Remove(guid);
        if (AssetDatabase.GUIDToAssetPath(guid) == selectedPath)
        {
            selectedPath = "";
            CloseSession();
        }
        SaveLayout();
        Repaint();
    }

    // ================= 拖拽添加 =================

    /// <summary>把拖入的场景（可多个）加入列表；只拖入一个时顺手打开预览。</summary>
    private string AddDroppedAssets(List<string> paths, string categoryId)
    {
        int added = 0, moved = 0, duplicated = 0;
        string singlePath = null;

        foreach (string path in paths)
        {
            string guid = AssetDatabase.AssetPathToGUID(path);
            if (string.IsNullOrEmpty(guid)) continue;

            ItemData existing = FindItem(guid);
            if (existing != null)
            {
                if (existing.category != categoryId)
                {
                    existing.category = categoryId;
                    moved++;
                }
                else duplicated++;
                continue;
            }

            layout.items.Add(new ItemData { guid = guid, category = categoryId });
            added++;
            singlePath = path;
        }

        if (added > 0 || moved > 0)
        {
            CategoryData target = FindCategory(categoryId);
            if (target != null) target.collapsed = false;
            SaveLayout();
            status = (added > 0 ? "添加 " + added + " 个场景" : "") +
                     (added > 0 && moved > 0 ? "，" : "") +
                     (moved > 0 ? "移动 " + moved + " 个到「" + CategoryName(categoryId) + "」" : "") +
                     (duplicated > 0 ? "，" + duplicated + " 个已在该分类" : "");

            // 延长一帧再打开，避免在拖放事件/场景加载里做重活
            if (added == 1 && !string.IsNullOrEmpty(singlePath))
            {
                string pathToOpen = singlePath;
                EditorApplication.delayCall += () =>
                {
                    if (host != null && isActive) OpenScene(pathToOpen);
                };
            }
        }
        else if (duplicated > 0)
        {
            status = "拖入的场景都已在列表中";
        }

        Repaint();
        return status;
    }

    // ================= 多选：批量移动 / 删除 =================

    private bool MoveItemsToCategory(List<string> guids, string categoryId)
    {
        if (guids == null || guids.Count == 0) return false;

        var moving = new List<ItemData>();
        int already = 0;

        foreach (string guid in guids)
        {
            ItemData item = FindItem(guid);
            if (item == null) continue;
            if (item.category == categoryId) { already++; continue; }
            moving.Add(item);
        }

        if (moving.Count == 0)
        {
            status = "选中的场景都已在「" + CategoryName(categoryId) + "」";
            Repaint();
            return false;
        }

        foreach (ItemData item in moving)
        {
            layout.items.Remove(item);
            item.category = categoryId;
        }

        int insertAt = layout.items.FindIndex(x => x.category == categoryId);
        if (insertAt < 0) layout.items.AddRange(moving);
        else layout.items.InsertRange(insertAt, moving);

        CategoryData target = FindCategory(categoryId);
        if (target != null && target.collapsed) target.collapsed = false;

        selection.Clear();
        SaveLayout();
        status = "已移动 " + moving.Count + " 个场景到「" + CategoryName(categoryId) + "」" +
                 (already > 0 ? "（" + already + " 个已在该分类）" : "");

        if (host != null) host.ShowNotification(new GUIContent(status));
        Repaint();
        return true;
    }

    private void DeleteSelectedItems(List<string> guids)
    {
        if (guids == null || guids.Count == 0) return;

        int count = 0;
        foreach (string guid in guids)
            if (FindItem(guid) != null) count++;
        if (count == 0) return;

        if (!EditorUtility.DisplayDialog("从预览列表移除",
            "移除选中的 " + count + " 个场景？\n\n只从预览列表移除，不会删除磁盘上的场景文件。",
            "移除", "取消"))
            return;

        bool removedCurrent = false;
        foreach (string guid in guids)
        {
            if (AssetDatabase.GUIDToAssetPath(guid) == selectedPath) removedCurrent = true;
            layout.items.RemoveAll(i => i.guid == guid);
        }

        if (removedCurrent)
        {
            selectedPath = "";
            CloseSession();
        }

        selection.Clear();
        SaveLayout();
        status = "已从预览列表移除 " + count + " 个场景";
        if (host != null) host.ShowNotification(new GUIContent(status));
        Repaint();
    }

    // ================= 扫描 =================

    private void EnterScanMode()
    {
        scanMode = true;
        scanRows.Clear();
        scanSearch = "";
        scanDefaultCategoryIndex = 0;
        string[] guids = AssetDatabase.FindAssets("t:Scene");
        foreach (string g in guids)
        {
            string p = AssetDatabase.GUIDToAssetPath(g);
            if (string.IsNullOrEmpty(p) || !p.StartsWith("Assets", StringComparison.OrdinalIgnoreCase)) continue;
            ItemData existing = FindItem(g);
            scanRows.Add(new ScanRow
            {
                guid = g,
                path = p,
                name = Path.GetFileNameWithoutExtension(p),
                already = existing != null,
                selected = existing == null,            // 默认勾选新场景，已添加的不勾
                category = existing != null ? existing.category : ""
            });
        }
        scanRows.Sort((a, b) =>
        {
            if (a.already != b.already) return a.already.CompareTo(b.already);
            return string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase);
        });
        Repaint();
    }

    private void ApplyScan()
    {
        int added = 0, moved = 0;
        foreach (ScanRow r in scanRows)
        {
            if (!r.selected) continue;
            ItemData it = FindItem(r.guid);
            if (it == null) { layout.items.Add(new ItemData { guid = r.guid, category = r.category }); added++; }
            else if (it.category != r.category) { it.category = r.category; moved++; }
        }
        SaveLayout();
        scanMode = false;
        status = "扫描添加完成：新增 " + added + " 个，调整分类 " + moved + " 个";
        Repaint();
    }

    // ================= 预览会话 =================

    private void OpenScene(string path)
    {
        CloseSession();
        selectedPath = path;
        status = "加载场景：" + path;
        try
        {
            Scene preview = EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
            var s = new PreviewSession { scene = preview };

            for (int i = EditorSceneManager.sceneCount - 1; i >= 0; i--)
            {
                Scene sc = EditorSceneManager.GetSceneAt(i);
                if (sc == preview) continue;
                foreach (GameObject root in sc.GetRootGameObjects())
                {
                    s.hiddenRoots.Add(new KeyValuePair<GameObject, bool>(root, root.activeSelf));
                    if (root.activeSelf) root.SetActive(false);
                }
            }

            var camList = new List<Camera>();
            var psList = new List<ParticleSystem>();
            var dirList = new List<PlayableDirector>();
            var animList = new List<Animator>();
            foreach (GameObject root in preview.GetRootGameObjects())
            {
                camList.AddRange(root.GetComponentsInChildren<Camera>(true));
                psList.AddRange(root.GetComponentsInChildren<ParticleSystem>(true));
                dirList.AddRange(root.GetComponentsInChildren<PlayableDirector>(true));
                animList.AddRange(root.GetComponentsInChildren<Animator>(true));
            }
            s.cameras = camList.ToArray();
            s.particles = psList.ToArray();
            s.directors = dirList.ToArray();
            s.noCamera = s.cameras.Length == 0;

            float maxDur = 0f;
            foreach (PlayableDirector d in s.directors)
                if (d != null && d.playableAsset != null && d.duration > maxDur) maxDur = (float)d.duration;
            s.hasTimeline = maxDur > 0f;
            s.duration = s.hasTimeline ? maxDur : Mathf.Max(1f, customLength);

            // 不依赖 Timeline 包：用 PlayableAsset.outputs + GetGenericBinding 找出被 Timeline 绑定的 Animator，
            // 其余独立 Animator 才自建手动驱动的 PlayableGraph；补建播放图只为让 Evaluate 真正驱动画面。
            HashSet<Animator> bound = new HashSet<Animator>();
            foreach (PlayableDirector d in s.directors)
            {
                if (d == null) continue;
                PreviewCompat.EnsureDirectorGraph(d);
                PreviewCompat.CollectBoundAnimators(d, bound);
            }
            foreach (Animator a in animList)
            {
                if (a == null || bound.Contains(a)) continue;
                AnimationClip clip = PreviewCompat.GetDefaultClip(a);
                if (clip == null) continue;
                var g = new PreviewSession.AnimatorGraph { animator = a, clipLength = Mathf.Max(0.01f, clip.length) };
                g.graph = PlayableGraph.Create("ScenePreview_" + a.GetInstanceID());
                g.graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                AnimationPlayableOutput output = AnimationPlayableOutput.Create(g.graph, "anim", a);
                g.playable = AnimationClipPlayable.Create(g.graph, clip);
                output.SetSourcePlayable(g.playable);
                g.graph.Play();
                s.animGraphs.Add(g);
            }

            foreach (ParticleSystem ps in s.particles)
                ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            s.cameraIndex = 0;
            for (int i = 0; i < s.cameras.Length; i++)
                if (s.cameras[i] != null && s.cameras[i].gameObject.activeInHierarchy) { s.cameraIndex = i; break; }

            session = s;
            playing = true;
            s.time = 0f;
            EnsureRT(512, 512);
            lastReal = Time.realtimeSinceStartup;
            Seek(0f);
            status = "预览中：" + Path.GetFileNameWithoutExtension(path) + (s.noCamera ? "（该场景没有摄像机）" : "");
        }
        catch (Exception e)
        {
            status = "加载失败：" + e.Message;
            CloseSession();
        }
    }

    private void CloseSession()
    {
        if (session != null) { session.Dispose(); session = null; }
    }

    private void EnsureRT(int w, int h)
    {
        if (session == null || session.noCamera) return;
        w = Mathf.Clamp(w, 16, 1920);
        h = Mathf.Clamp(h, 16, 1920);
        if (session.rt != null && Mathf.Abs(session.rt.width - w) <= 2 && Mathf.Abs(session.rt.height - h) <= 2) return;
        if (session.rt != null)
        {
            foreach (Camera c in session.cameras)
                if (c != null && c.targetTexture == session.rt) c.targetTexture = null;
            session.rt.Release();
            UnityEngine.Object.DestroyImmediate(session.rt);
        }
        session.rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32);
        session.rt.antiAliasing = 1;
        session.rt.Create();
        ApplyCameraTarget();
    }

    private void ApplyCameraTarget()
    {
        foreach (Camera c in session.cameras)
            if (c != null) c.targetTexture = null;
        Camera cam = session.ActiveCamera;
        if (cam != null && session.rt != null)
        {
            cam.targetTexture = session.rt;
            cam.aspect = (float)session.rt.width / Mathf.Max(1, session.rt.height);
        }
    }

    // ================= 时间驱动 =================

    private void OnEditorTick()
    {
        if (session != null && !session.noCamera && playing)
            Repaint();
    }

    private void Advance(float scaledDt)
    {
        PreviewSession s = session;
        s.time += scaledDt;
        bool wrapped = false;
        float dur = s.hasTimeline ? s.duration : Mathf.Max(1f, customLength);
        s.duration = dur;
        if (s.time >= dur)
        {
            if (loop) { s.time = s.time % dur; wrapped = true; }
            else { s.time = dur; playing = false; }
        }

        foreach (PlayableDirector d in s.directors)
            if (d != null) { d.time = s.time; d.Evaluate(); }

        foreach (PreviewSession.AnimatorGraph g in s.animGraphs)
            if (g.graph.IsValid()) { g.playable.SetTime(s.time % g.clipLength); g.graph.Evaluate(); }

        if (simulateParticles)
            foreach (ParticleSystem ps in s.particles)
                if (ps != null)
                {
                    if (wrapped) ps.Simulate(s.time, true, true, true);
                    else ps.Simulate(scaledDt, true, false, false);
                }
    }

    private void Seek(float t)
    {
        if (session == null) return;
        PreviewSession s = session;
        float dur = s.hasTimeline ? s.duration : Mathf.Max(1f, customLength);
        s.duration = dur;
        s.time = Mathf.Clamp(t, 0f, dur);

        foreach (PlayableDirector d in s.directors)
            if (d != null) { d.time = s.time; d.Evaluate(); }
        foreach (PreviewSession.AnimatorGraph g in s.animGraphs)
            if (g.graph.IsValid()) { g.playable.SetTime(s.time % g.clipLength); g.graph.Evaluate(); }
        if (simulateParticles)
            foreach (ParticleSystem ps in s.particles)
                if (ps != null) ps.Simulate(Mathf.Max(0f, s.time), true, true, true);

        RenderFrame();
        Repaint();
    }

    private void RenderFrame()
    {
        Camera cam = session != null ? session.ActiveCamera : null;
        if (cam != null && session.rt != null) cam.Render();
    }

    // ================= 主布局 =================

    public override void OnGUI()
    {
        EditorGUILayout.BeginHorizontal();
        DrawSidebar();
        DrawSplitter();
        DrawPreview();
        EditorGUILayout.EndHorizontal();
    }

    private void DrawSplitter()
    {
        Rect sp = GUILayoutUtility.GetRect(6f, 18f, GUILayout.Width(6f), GUILayout.ExpandHeight(true));
        EditorGUIUtility.AddCursorRect(sp, MouseCursor.ResizeHorizontal);
        if (Event.current.type == EventType.Repaint)
            EditorGUI.DrawRect(sp, new Color(0f, 0f, 0f, 0.25f));

        Event e = Event.current;
        if (e.type == EventType.MouseDown && e.button == 0 && sp.Contains(e.mousePosition))
        {
            draggingSplitter = true;
            e.Use();
        }
        else if (e.type == EventType.MouseDrag && draggingSplitter)
        {
            layout.listWidth = Mathf.Clamp(e.mousePosition.x, 150f, 480f);
            Repaint();
            e.Use();
        }
        else if (e.type == EventType.MouseUp && draggingSplitter)
        {
            draggingSplitter = false;
            SaveLayout();
            e.Use();
        }
    }

    // ================= 左侧栏 =================

    private void DrawSidebar()
    {
        dropController.ResetFrame();
        HandleInternalDragStart();
        HandleSelectionKeys();

        EditorGUILayout.BeginVertical(GUI.skin.box, GUILayout.Width(layout.listWidth), GUILayout.ExpandHeight(true));
        if (scanMode) DrawScanSidebar();
        else DrawNormalSidebar();
        EditorGUILayout.EndVertical();

        // 整个窗口都是投放区：从 Project/Hierarchy 拖到分类标题/条目上归入该分类，
        // 拖到其它地方归入“未分类”，单个资源还会自动打开预览。
        float windowWidth = Mathf.Max(EditorGUIUtility.currentViewWidth, layout.listWidth);
        float windowHeight = host != null ? host.position.height : 600f;
        dropController.area = new Rect(0f, 0f, windowWidth, windowHeight);
        dropController.HandleEvents();
        dropController.DrawOverlay();
    }

    private void DrawNormalSidebar()
    {
        EditorGUILayout.BeginHorizontal();
        sidebarSearch = GUILayout.TextField(sidebarSearch, EditorStyles.toolbarSearchField, GUILayout.MinWidth(40));
        if (GUILayout.Button("＋ 添加场景", EditorStyles.miniButton, GUILayout.Width(80))) EnterScanMode();
        if (GUILayout.Button("分类＋", EditorStyles.miniButton, GUILayout.Width(48)))
        {
            creatingCategory = true;
            creatingParentId = "";
            newCategoryName = "";
            sidebarSearch = "";
            GUI.FocusControl("newCatField");
        }
        EditorGUILayout.EndHorizontal();

        if (creatingCategory && string.IsNullOrEmpty(creatingParentId))
            DrawCategoryCreator(0f);

        RebuildVisibleGuids();
        HandleSelectAllKey();
        DrawSelectionBar();

        listScroll = EditorGUILayout.BeginScrollView(listScroll);

        DrawGroup(null, sidebarSearch); // 未分类
        foreach (CategoryData cat in RootCategories())
            DrawGroup(cat, sidebarSearch);

        if (layout.items.Count == 0)
            EditorGUILayout.HelpBox("还没有场景。\n把场景（可多选，或整个文件夹）从 Project 窗口直接拖到这里即可添加，\n也可以点上方「＋ 添加场景」扫描工程。", MessageType.Info);
        else if (CountMatchingItems(sidebarSearch) == 0)
            GUILayout.Label("没有匹配的场景", EditorStyles.miniLabel);

        EditorGUILayout.EndScrollView();

        GUILayout.Label("共 " + layout.items.Count + " 个场景 · " + layout.categories.Count + " 个分类" +
                        (selection.Count > 0 ? " · 已选 " + selection.Count + " 个" : ""),
                        EditorStyles.centeredGreyMiniLabel);
        GUILayout.Label(selection.Count > 0
                ? "拖动任一选中项或右键，可批量移动分类 · Esc 取消"
                : "从 Project 拖入添加 · 条目可拖动分类 · Ctrl/Shift 多选",
                EditorStyles.centeredGreyMiniLabel);
    }

    private void HandleInternalDragStart()
    {
        Event e = Event.current;
        if (e == null || string.IsNullOrEmpty(dragCandidateGuid)) return;

        if (e.type == EventType.MouseDrag && GUIUtility.hotControl == dragControlId &&
            Vector2.Distance(e.mousePosition, dragStartPosition) >= 4f)
        {
            List<string> guids = selection.IsSelected(dragCandidateGuid)
                ? selection.Snapshot()
                : new List<string> { dragCandidateGuid };
            GUIUtility.hotControl = 0;
            dragControlId = 0;
            dropController.BeginInternalDrag(guids);
            dragCandidateGuid = null;
            dragClickCount = 0;
            e.Use();
        }
        else if (e.type == EventType.MouseUp && GUIUtility.hotControl == dragControlId)
        {
            string clickedGuid = dragCandidateGuid;
            int clickCount = dragClickCount;
            GUIUtility.hotControl = 0;
            dragControlId = 0;
            dragCandidateGuid = null;
            dragClickCount = 0;

            string clickedPath = AssetDatabase.GUIDToAssetPath(clickedGuid);
            if (clickCount >= 2) OpenCurrentInEditor(clickedPath);
            else OpenScene(clickedPath);
            e.Use();
        }
        else if (e.type == EventType.DragExited)
        {
            if (GUIUtility.hotControl == dragControlId) GUIUtility.hotControl = 0;
            dragControlId = 0;
            dragCandidateGuid = null;
            dragClickCount = 0;
        }
    }

    /// <summary>按当前显示顺序预计算可见条目，供 Shift 连选/全选使用。</summary>
    private void RebuildVisibleGuids()
    {
        visibleGuids.Clear();
        AppendVisibleGuids("");

        foreach (CategoryData root in RootCategories())
        {
            if (root.collapsed) continue;

            AppendVisibleGuids(root.id);

            foreach (CategoryData sub in ChildCategories(root.id))
            {
                if (sub.collapsed) continue;
                AppendVisibleGuids(sub.id);
            }
        }
    }

    private void AppendVisibleGuids(string categoryId)
    {
        string filter = string.IsNullOrEmpty(sidebarSearch) ? null : sidebarSearch.Trim().ToLowerInvariant();

        foreach (ItemData it in layout.items)
        {
            if (it.category != categoryId) continue;

            string path = AssetDatabase.GUIDToAssetPath(it.guid);
            if (string.IsNullOrEmpty(path)) continue;

            if (filter != null &&
                !Path.GetFileNameWithoutExtension(path).ToLowerInvariant().Contains(filter) &&
                !path.ToLowerInvariant().Contains(filter)) continue;

            visibleGuids.Add(it.guid);
        }
    }

    /// <summary>空闲时按 Esc 清空多选。</summary>
    private void HandleSelectionKeys()
    {
        Event e = Event.current;
        if (e == null || e.type != EventType.KeyDown || e.keyCode != KeyCode.Escape) return;
        if (renamingCategoryId != null || creatingCategory) return;
        if (selection.Count == 0) return;

        selection.Clear();
        Repaint();
    }

    /// <summary>Ctrl/Cmd + A 全选当前可见条目（文本编辑时不拦截）。</summary>
    private void HandleSelectAllKey()
    {
        Event e = Event.current;
        if (e == null || e.type != EventType.KeyDown) return;
        if (!PreviewSelection.IsCtrlPressed(e)) return;
        if (e.keyCode != KeyCode.A) return;
        if (EditorGUIUtility.editingTextField) return;

        selection.SetSelection(visibleGuids);
        e.Use();
        Repaint();
    }

    private void DrawSelectionBar()
    {
        // 始终占位，避免选中/取消时列表上下位移
        EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
        GUILayout.Label(selection.Count > 0 ? "已选 " + selection.Count + " 个" : "未选择条目", EditorStyles.miniLabel);
        GUILayout.FlexibleSpace();

        using (new EditorGUI.DisabledScope(visibleGuids.Count == 0))
        {
            if (GUILayout.Button("全选", EditorStyles.miniButton, GUILayout.Width(36)))
            {
                selection.SetSelection(visibleGuids);
                Repaint();
            }
        }

        using (new EditorGUI.DisabledScope(selection.Count == 0))
        {
            if (GUILayout.Button("清空", EditorStyles.miniButton, GUILayout.Width(36)))
            {
                selection.Clear();
                Repaint();
            }

            if (GUILayout.Button("删除", EditorStyles.miniButton, GUILayout.Width(36)))
            {
                DeleteSelectedItems(selection.Snapshot());
            }
        }

        EditorGUILayout.EndHorizontal();
    }

    // 绘制一个一级分类（cat==null 表示未分类）；返回显示的行数
    private int DrawGroup(CategoryData cat, string filter)
    {
        string catId = cat != null ? cat.id : "";
        List<ItemData> members = CollectMembers(catId, filter);

        List<CategoryData> subCategories = new List<CategoryData>();
        if (cat != null)
        {
            foreach (CategoryData sub in ChildCategories(cat.id))
                if (string.IsNullOrEmpty(filter) || CollectMembers(sub.id, filter).Count > 0)
                    subCategories.Add(sub);
        }

        if (members.Count == 0 && subCategories.Count == 0 && !string.IsNullOrEmpty(filter))
            return 0;

        int total = members.Count;
        foreach (CategoryData sub in subCategories)
            total += CollectMembers(sub.id, filter).Count;

        DrawCategoryHeader(cat, cat != null ? cat.name : "未分类", total, 0f, cat != null);
        if (cat != null && cat.collapsed) return 0;

        if (cat != null && creatingCategory && creatingParentId == cat.id)
            DrawCategoryCreator(14f);

        int count = 0;
        foreach (ItemData it in members)
        {
            DrawSceneRow(it);
            count++;
        }

        foreach (CategoryData sub in subCategories)
            count += DrawSubGroup(sub, filter);

        return count;
    }

    // 绘制一个二级分类
    private int DrawSubGroup(CategoryData sub, string filter)
    {
        List<ItemData> members = CollectMembers(sub.id, filter);

        DrawCategoryHeader(sub, sub.name, members.Count, 14f, false);
        if (sub.collapsed) return 0;

        int count = 0;
        foreach (ItemData it in members)
        {
            DrawSceneRow(it, 14f);
            count++;
        }
        return count;
    }

    private List<ItemData> CollectMembers(string categoryId, string filter)
    {
        var members = new List<ItemData>();

        foreach (ItemData it in layout.items)
        {
            if (it.category != categoryId) continue;
            if (!MatchesFilter(it, filter)) continue;
            members.Add(it);
        }

        return members;
    }

    /// <summary>搜索匹配判定：按资源名或完整路径；空搜索 = 全部匹配。</summary>
    private bool MatchesFilter(ItemData it, string filter)
    {
        string path = AssetDatabase.GUIDToAssetPath(it.guid);
        if (string.IsNullOrEmpty(path)) return false; // 资源已删除/找不到则不显示
        if (string.IsNullOrEmpty(filter)) return true;

        string q = filter.Trim().ToLowerInvariant();
        return Path.GetFileNameWithoutExtension(path).ToLowerInvariant().Contains(q) ||
               path.ToLowerInvariant().Contains(q);
    }

    private int CountMatchingItems(string filter)
    {
        int count = 0;
        foreach (ItemData it in layout.items)
            if (MatchesFilter(it, filter)) count++;
        return count;
    }

    /// <summary>绘制分类标题（含折叠、重命名、右键菜单、拖放目标）。cat==null 表示未分类。</summary>
    private void DrawCategoryHeader(CategoryData cat, string title, int count, float indent, bool allowSubCategory)
    {
        string catId = cat != null ? cat.id : "";
        bool collapsed = cat != null && cat.collapsed;

        Rect header = GUILayoutUtility.GetRect(1f, 20f, GUILayout.ExpandWidth(true));
        dropController.AddTarget(catId, header);

        bool showAddSub = allowSubCategory && cat != null;
        Rect addRect = new Rect(header.xMax - 20f, header.y + 2f, 17f, 16f);

        Event e = Event.current;
        if (e.type == EventType.MouseDown && header.Contains(e.mousePosition) &&
            (!showAddSub || !addRect.Contains(e.mousePosition)))
        {
            if (e.button == 0)
            {
                if (cat != null && renamingCategoryId != cat.id) { cat.collapsed = !cat.collapsed; SaveLayout(); }
                e.Use();
            }
            else if (e.button == 1 && cat != null)
            {
                ShowCategoryMenu(cat, allowSubCategory);
                e.Use();
            }
        }

        string label = (collapsed ? "▶ " : "▼ ") + title + "  (" + count + ")";
        GUIStyle style = new GUIStyle(EditorStyles.boldLabel) { alignment = TextAnchor.MiddleLeft };
        float contentWidth = header.width - indent - (showAddSub ? 22f : 0f);
        Rect content = new Rect(header.x + indent, header.y, Mathf.Max(1f, contentWidth), header.height);

        if (cat != null && renamingCategoryId == cat.id)
        {
            GUI.SetNextControlName("renameCatField");
            renameBuffer = GUI.TextField(
                new Rect(content.x + 20f, content.y, Mathf.Max(20f, content.width - 24f), content.height),
                renameBuffer);

            if (EnterPressed() && GUI.GetNameOfFocusedControl() == "renameCatField")
            {
                cat.name = string.IsNullOrEmpty(renameBuffer.Trim()) ? cat.name : renameBuffer.Trim();
                renamingCategoryId = null;
                SaveLayout();
                GUI.FocusControl(null);
            }
            else if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape)
            {
                renamingCategoryId = null;
                GUI.FocusControl(null);
            }
        }
        else
        {
            EditorGUI.DrawRect(header, new Color(1f, 1f, 1f, 0.05f));
            GUI.Label(content, label, style);

            // 一级分类标题右侧的“+”：直接新建子分类（比右键更直观）
            if (showAddSub)
            {
                if (GUI.Button(addRect, new GUIContent("+", "新建子分类"), EditorStyles.miniButton))
                    StartCreatingSubCategory(cat);
            }
        }
    }

    private void StartCreatingSubCategory(CategoryData cat)
    {
        if (cat == null || !IsRootCategory(cat)) return;

        creatingCategory = true;
        creatingParentId = cat.id;
        newCategoryName = "";
        sidebarSearch = "";
        if (cat.collapsed) cat.collapsed = false;

        GUI.FocusControl("newCatField");
        Repaint();
    }

    /// <summary>分类内联新建输入框；indent 用于二级分类缩进。</summary>
    private void DrawCategoryCreator(float indent)
    {
        EditorGUILayout.BeginHorizontal(GUI.skin.box);
        if (indent > 0f) GUILayout.Space(indent);

        GUI.SetNextControlName("newCatField");
        newCategoryName = EditorGUILayout.TextField(newCategoryName);

        if (GUILayout.Button("OK", EditorStyles.miniButton, GUILayout.Width(28)) ||
            (EnterPressed() && GUI.GetNameOfFocusedControl() == "newCatField"))
        {
            CreateCategory(newCategoryName, creatingParentId);
            creatingCategory = false;
            creatingParentId = "";
            newCategoryName = "";
            GUI.FocusControl(null);
        }

        if (GUILayout.Button("×", EditorStyles.miniButton, GUILayout.Width(20)))
        {
            creatingCategory = false;
            creatingParentId = "";
            newCategoryName = "";
            GUI.FocusControl(null);
        }
        else if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Escape)
        {
            creatingCategory = false;
            creatingParentId = "";
            newCategoryName = "";
            GUI.FocusControl(null);
        }

        EditorGUILayout.EndHorizontal();
    }

    private void DrawSceneRow(ItemData it, float indent = 0f)
    {
        string path = AssetDatabase.GUIDToAssetPath(it.guid);
        string nm = Path.GetFileNameWithoutExtension(path);
        Rect row = GUILayoutUtility.GetRect(1f, 19f, GUILayout.ExpandWidth(true));
        row.x += 14f + indent;
        row.width -= 14f + indent;
        int rowControlId = GUIUtility.GetControlID(it.guid.GetHashCode(), FocusType.Passive, row);

        dropController.AddTarget(it.category, row);

        bool active = path == selectedPath;
        if (active) EditorGUI.DrawRect(row, new Color(0.24f, 0.45f, 0.72f));
        if (selection.IsSelected(it.guid))
        {
            EditorGUI.DrawRect(row, new Color(0.2f, 0.52f, 0.85f, 0.38f));
            EditorGUI.DrawRect(new Rect(row.x, row.y, 3f, row.height), new Color(0.4f, 0.8f, 1f, 1f));
        }
        GUIStyle rs = new GUIStyle(EditorStyles.label)
        {
            alignment = TextAnchor.MiddleLeft,
            normal = { textColor = active ? Color.white : GUI.skin.label.normal.textColor }
        };
        GUI.Label(row, new GUIContent(nm, path + "\n分类：" + CategoryName(it.category) + "\n可拖到其它分类 · Ctrl/Shift 多选 · 右键批量操作"), rs);

        Event e = Event.current;
        if (e.type != EventType.MouseDown || !row.Contains(e.mousePosition)) return;

        if (e.button == 1)
        {
            if (GUIUtility.hotControl == dragControlId) GUIUtility.hotControl = 0;
            dragControlId = 0;
            dragCandidateGuid = null;
            if (!selection.IsSelected(it.guid)) selection.SelectOnly(it.guid);
            ShowSceneMenu(it, path);
            e.Use();
            return;
        }

        if (e.button != 0) return;

        if (PreviewSelection.IsCtrlPressed(e))
        {
            dragControlId = 0;
            selection.Toggle(it.guid);
            e.Use();
            return;
        }

        if (PreviewSelection.IsShiftPressed(e))
        {
            dragControlId = 0;
            selection.SelectRange(it.guid, visibleGuids);
            e.Use();
            return;
        }

        if (!selection.IsSelected(it.guid) || selection.Count <= 1)
            selection.SelectOnly(it.guid);
        dragControlId = rowControlId;
        GUIUtility.hotControl = dragControlId;
        dragCandidateGuid = it.guid;
        dragStartPosition = e.mousePosition;
        dragClickCount = e.clickCount;
        Repaint();
        e.Use();
    }

    private void ShowCategoryMenu(CategoryData cat, bool allowSubCategory)
    {
        var m = new GenericMenu();
        m.AddItem(new GUIContent("重命名分类"), false, () =>
        {
            renamingCategoryId = cat.id;
            renameBuffer = cat.name;
            GUI.FocusControl("renameCatField");
            Repaint();
        });

        if (allowSubCategory)
        {
            m.AddItem(new GUIContent("新建子分类"), false, () => StartCreatingSubCategory(cat));
        }

        m.AddItem(new GUIContent("删除分类"), false, () =>
        {
            int childCount = ChildCategories(cat.id).Count;
            int itemCount = 0;
            var ids = new HashSet<string> { cat.id };
            foreach (CategoryData c in ChildCategories(cat.id)) ids.Add(c.id);
            foreach (ItemData it in layout.items)
                if (it.category != null && ids.Contains(it.category)) itemCount++;

            string where = IsRootCategory(cat)
                ? "该分类及其 " + childCount + " 个子分类会被删除，"
                : "该子分类会被删除，";
            string fallback = IsRootCategory(cat) ? "未分类" : CategoryName(cat.parentId);

            if (EditorUtility.DisplayDialog("删除分类",
                "删除「" + CategoryName(cat.id) + "」？\n" + where + itemCount + " 个场景会移到「" + fallback + "」，不会被删除。",
                "删除", "取消"))
            {
                if (renamingCategoryId == cat.id) renamingCategoryId = null;
                DeleteCategory(cat.id);
                Repaint();
            }
        });

        m.ShowAsContext();
    }

    private void ShowSceneMenu(ItemData it, string path)
    {
        var m = new GenericMenu();
        string g = it.guid;

        List<string> targets = selection.IsSelected(g)
            ? selection.Snapshot()
            : new List<string> { g };
        int count = targets.Count;
        string movePrefix = count > 1 ? "移动选中的 " + count + " 个到/" : "移动到分类/";

        m.AddItem(new GUIContent(movePrefix + "未分类"), count == 1 && it.category == "", () => MoveItemsToCategory(targets, ""));
        foreach (CategoryData c in OrderedCategories())
        {
            CategoryData cc = c;
            m.AddItem(new GUIContent(movePrefix + CategoryName(cc.id)), count == 1 && it.category == cc.id, () => MoveItemsToCategory(targets, cc.id));
        }
        m.AddSeparator("");
        if (count > 1)
        {
            m.AddItem(new GUIContent("删除选中的 " + count + " 个"), false, () => DeleteSelectedItems(targets));
            m.AddSeparator("");
        }
        m.AddItem(new GUIContent("在项目中定位"), false, () => PingPath(path));
        m.AddItem(new GUIContent("正式打开场景"), false, () => OpenCurrentInEditor(path));
        m.AddSeparator("");
        m.AddItem(new GUIContent("从预览列表移除"), false, () =>
        {
            if (EditorUtility.DisplayDialog("移出列表", "把「" + Path.GetFileNameWithoutExtension(path) + "」从预览列表移除？\n（不会删除磁盘上的场景文件，可再次扫描添加）", "移除", "取消"))
                RemoveItem(g);
        });
        m.ShowAsContext();
    }

    private static bool EnterPressed()
    {
        Event e = Event.current;
        return e != null && e.type == EventType.KeyDown && (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter);
    }

    // ================= 扫描面板 =================

    private void DrawScanSidebar()
    {
        EditorGUILayout.BeginHorizontal();
        GUILayout.Label("选择要加入的场景", EditorStyles.boldLabel);
        GUILayout.FlexibleSpace();
        if (GUILayout.Button("取消", EditorStyles.miniButton, GUILayout.Width(44))) { scanMode = false; Repaint(); }
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.BeginHorizontal();
        scanSearch = GUILayout.TextField(scanSearch, EditorStyles.toolbarSearchField, GUILayout.MinWidth(40));
        if (GUILayout.Button("全选", EditorStyles.miniButton, GUILayout.Width(40))) { foreach (ScanRow r in VisibleScanRows()) r.selected = true; }
        if (GUILayout.Button("全不选", EditorStyles.miniButton, GUILayout.Width(48))) { foreach (ScanRow r in VisibleScanRows()) r.selected = false; }
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.BeginHorizontal();
        GUILayout.Label("新场景默认分类", GUILayout.Width(92));
        EditorGUI.BeginChangeCheck();
        scanDefaultCategoryIndex = EditorGUILayout.Popup(scanDefaultCategoryIndex, CategoryOptions());
        if (EditorGUI.EndChangeCheck())
        {
            string catId = IndexToCategory(scanDefaultCategoryIndex);
            foreach (ScanRow r in scanRows) if (!r.already) r.category = catId;
        }
        EditorGUILayout.EndHorizontal();

        listScroll = EditorGUILayout.BeginScrollView(listScroll);
        string q = string.IsNullOrEmpty(scanSearch) ? null : scanSearch.Trim().ToLowerInvariant();
        foreach (ScanRow r in scanRows)
        {
            if (q != null && !r.name.ToLowerInvariant().Contains(q) && !r.path.ToLowerInvariant().Contains(q)) continue;
            DrawScanRow(r);
        }
        EditorGUILayout.EndScrollView();

        int sel = 0;
        foreach (ScanRow r in scanRows) if (r.selected) sel++;
        using (new EditorGUI.DisabledScope(sel == 0))
        {
            if (GUILayout.Button("保存：添加选中的 " + sel + " 个场景", EditorStyles.miniButton))
                ApplyScan();
        }
        GUILayout.Label("勾选=加入并显示在左侧；不勾选=不显示。已添加的场景默认不勾。", EditorStyles.centeredGreyMiniLabel);
    }

    private List<ScanRow> VisibleScanRows()
    {
        var l = new List<ScanRow>();
        string q = string.IsNullOrEmpty(scanSearch) ? null : scanSearch.Trim().ToLowerInvariant();
        foreach (ScanRow r in scanRows)
        {
            if (q != null && !r.name.ToLowerInvariant().Contains(q) && !r.path.ToLowerInvariant().Contains(q)) continue;
            l.Add(r);
        }
        return l;
    }

    private void DrawScanRow(ScanRow r)
    {
        EditorGUILayout.BeginHorizontal();
        using (new EditorGUI.DisabledScope(r.already))
        {
            r.selected = EditorGUILayout.Toggle(r.selected, GUILayout.Width(16));
        }
        EditorGUILayout.LabelField(new GUIContent(r.name, r.path), GUILayout.MinWidth(30));
        int idx = EditorGUILayout.Popup(CategoryToIndex(r.category), CategoryOptions(), GUILayout.Width(70));
        r.category = IndexToCategory(idx);
        if (r.already)
        {
            GUIStyle g = new GUIStyle(EditorStyles.miniLabel) { normal = { textColor = new Color(0.6f, 0.85f, 0.6f) } };
            GUILayout.Label("已添加", g, GUILayout.Width(40));
        }
        EditorGUILayout.EndHorizontal();
    }

    // ================= 右侧预览 =================

    private void DrawPreview()
    {
        EditorGUILayout.BeginVertical(GUI.skin.box, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));

        EditorGUILayout.BeginHorizontal();
        string title = session == null ? "未选择场景" : Path.GetFileNameWithoutExtension(selectedPath);
        GUILayout.Label(title, EditorStyles.boldLabel);
        if (!string.IsNullOrEmpty(status))
        {
            GUIStyle ss = new GUIStyle(EditorStyles.miniLabel) { normal = { textColor = new Color(0.8f, 0.8f, 0.6f) } };
            GUILayout.Label(status, ss);
        }
        GUILayout.FlexibleSpace();
        using (new EditorGUI.DisabledScope(session == null))
        {
            if (GUILayout.Button("定位", EditorStyles.miniButton, GUILayout.Width(48))) PingCurrent();
            if (GUILayout.Button("正式打开", EditorStyles.miniButton, GUILayout.Width(64))) OpenCurrentInEditor();
        }
        EditorGUILayout.EndHorizontal();

        Rect view = GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
        EditorGUI.DrawRect(view, new Color(0.08f, 0.08f, 0.08f));

        if (session == null)
        {
            GUI.Label(view, "\n\n从左侧选择一个 Scene 开始预览（不进入 Play Mode）", CenteredLabel());
        }
        else if (session.noCamera)
        {
            GUI.Label(view, "\n\n该场景中没有找到摄像机。\n请在场景里放置摄像机后再选择。", CenteredLabel());
        }
        else
        {
            float ppp = EditorGUIUtility.pixelsPerPoint;
            if (view.width > 4f && view.height > 4f)
                EnsureRT(Mathf.RoundToInt(view.width * ppp), Mathf.RoundToInt(view.height * ppp));

            if (Event.current.type == EventType.Repaint && session.rt != null)
            {
                double now = Time.realtimeSinceStartup;
                float dt = Mathf.Clamp((float)(now - lastReal), 0f, 0.1f);
                lastReal = now;
                if (playing)
                    Advance(dt * Speeds[Mathf.Clamp(speedIndex, 0, Speeds.Length - 1)]);
                RenderFrame();
            }

            if (session.rt != null)
            {
                Rect tex = FitRect(view, session.rt.width, session.rt.height);
                GUI.DrawTexture(tex, session.rt, ScaleMode.ScaleToFit, false);
                DrawViewHud(view);
            }
        }

        EditorGUILayout.Space(2);
        DrawTransport();
        EditorGUILayout.EndVertical();
    }

    private void DrawTransport()
    {
        if (session == null || session.noCamera) return;

        if (session.cameras.Length > 1)
        {
            string[] names = new string[session.cameras.Length];
            for (int i = 0; i < names.Length; i++)
                names[i] = session.cameras[i] != null ? session.cameras[i].name : "(missing)";
            EditorGUI.BeginChangeCheck();
            int idx = EditorGUILayout.Popup("摄像机", session.cameraIndex, names);
            if (EditorGUI.EndChangeCheck() && idx != session.cameraIndex)
            {
                session.cameraIndex = idx;
                ApplyCameraTarget();
                RenderFrame();
            }
        }

        EditorGUILayout.BeginHorizontal();
        GUIContent playIcon = PreviewCompat.PlayPauseIcon(playing);
        if (GUILayout.Button(playIcon, EditorStyles.miniButton, GUILayout.Width(34), GUILayout.Height(22)))
        {
            playing = !playing;
            if (playing) lastReal = Time.realtimeSinceStartup;
        }
        if (GUILayout.Button("重播", EditorStyles.miniButton, GUILayout.Width(48), GUILayout.Height(22)))
        {
            playing = true;
            foreach (ParticleSystem ps in session.particles)
                if (ps != null) ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            Seek(0f);
        }
        loop = GUILayout.Toggle(loop, "循环", EditorStyles.miniButton, GUILayout.Width(44), GUILayout.Height(22));
        speedIndex = EditorGUILayout.Popup(speedIndex, SpeedLabels, GUILayout.Width(58));
        GUILayout.FlexibleSpace();
        GUILayout.Label(FormatTime(session.time) + " / " + FormatTime(session.duration), GUILayout.Width(92));
        EditorGUILayout.EndHorizontal();

        // 进度滑杆：显式指定高度，避免 GUILayout.HorizontalSlider 默认高度不足导致与下方设置行重叠
        Rect sliderRect = GUILayoutUtility.GetRect(GUIContent.none, GUI.skin.horizontalSlider, GUILayout.Height(22), GUILayout.ExpandWidth(true));
        EditorGUI.BeginChangeCheck();
        float t = GUI.HorizontalSlider(sliderRect, session.time, 0f, session.duration);
        if (EditorGUI.EndChangeCheck()) { playing = false; Seek(t); }

        EditorGUILayout.Space(2);

        EditorGUILayout.BeginHorizontal();
        using (new EditorGUI.DisabledScope(session.hasTimeline))
        {
            GUILayout.Label("预览时长", GUILayout.Width(56));
            customLength = EditorGUILayout.FloatField(Mathf.Max(1f, customLength), GUILayout.Width(56));
        }
        GUILayout.FlexibleSpace();
        simulateParticles = GUILayout.Toggle(simulateParticles, "手动模拟粒子", EditorStyles.miniButton);
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.Space(2);
        GUILayout.Label("单击场景切换预览 · 右键条目可移动分类/移除 · 运行时脚本逻辑与物理需进 Play Mode", EditorStyles.miniLabel);
    }

    private static string FormatTime(float t)
    {
        int sec = Mathf.FloorToInt(t);
        return string.Format("{0:00}:{1:00}", sec / 60, sec % 60);
    }

    private static GUIStyle centeredLabel;
    private static GUIStyle CenteredLabel()
    {
        if (centeredLabel == null)
            centeredLabel = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, wordWrap = true };
        return centeredLabel;
    }

    private static GUIStyle hudLabel;
    private static GUIStyle HudLabel()
    {
        if (hudLabel == null)
        {
            hudLabel = new GUIStyle(GUI.skin.label) { fontSize = 11 };
            hudLabel.normal.textColor = new Color(1f, 1f, 1f, 0.9f);
        }
        return hudLabel;
    }

    private void DrawViewHud(Rect view)
    {
        Camera cam = session != null ? session.ActiveCamera : null;
        string hud;
        if (cam == null) hud = "无有效相机（相机物体未激活或组件缺失）";
        else
        {
            string state = cam.gameObject.activeInHierarchy
                ? (cam.enabled ? "enabled" : "相机组件 disabled")
                : "相机物体未激活";
            hud = "相机: " + cam.name + "  [" + state + "]   RT " + session.rt.width + "x" + session.rt.height
                + "   " + FormatTime(session.time) + (playing ? "   播放中" : "   已暂停");
        }
        GUIContent gc = new GUIContent(hud);
        Vector2 sz = HudLabel().CalcSize(gc);
        Rect bg = new Rect(view.x + 6f, view.y + 5f, sz.x + 12f, sz.y + 4f);
        EditorGUI.DrawRect(bg, new Color(0f, 0f, 0f, 0.45f));
        GUI.Label(new Rect(bg.x + 6f, bg.y + 1f, sz.x, sz.y + 2f), gc, HudLabel());

        if (cam != null)
            PreviewCompat.DrawOrientationGizmo(view, cam.transform.rotation);
    }

    private static Rect FitRect(Rect view, float texW, float texH)
    {
        float va = view.width / Mathf.Max(1f, view.height);
        float ta = texW / Mathf.Max(1f, texH);
        float w = view.width, h = view.height;
        if (ta > va) { h = w / ta; } else { w = h * ta; }
        return new Rect(view.x + (view.width - w) * 0.5f, view.y + (view.height - h) * 0.5f, w, h);
    }

    private void PingCurrent()
    {
        if (!string.IsNullOrEmpty(selectedPath)) PingPath(selectedPath);
    }

    private void PingPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        UnityEngine.Object obj = AssetDatabase.LoadAssetAtPath<SceneAsset>(path);
        if (obj != null) { EditorGUIUtility.PingObject(obj); Selection.activeObject = obj; }
    }

    private void OpenCurrentInEditor() { OpenCurrentInEditor(selectedPath); }

    private void OpenCurrentInEditor(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        CloseSession();
        selectedPath = "";
        if (EditorSceneManager.GetActiveScene().isDirty)
        {
            bool ok = EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo();
            if (!ok) return;
        }
        EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
        status = "已在编辑器打开：" + path;
    }
}
