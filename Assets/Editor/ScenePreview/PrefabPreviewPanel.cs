using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

/// <summary>
/// 预制体预览面板（集成于 Tools/预览工具箱 的“预制体预览”标签）。
/// 左侧是两级分类（一级分类 + 子分类），预制体可直接挂一级分类，也可挂子分类；
/// 分类持久化到 ProjectSettings/PrefabPreviewLayout.json；
/// 把预制体或整个文件夹从 Project 窗口直接拖进来即可添加，从 Hierarchy 拖预制体实例也能自动解析到源预制体；
/// 拖到一级/二级分类标题或分类内条目上直接归入该分类；列表条目也可拖拽或右键批量移动。
/// 右侧用 PreviewRenderUtility（Unity Inspector 预览球的同一套底层）在独立隐藏场景中实例化预制体并离屏渲染，
/// 不进入 Play Mode、不影响当前编辑场景。支持：
///   · 粒子 ParticleSystem 手动 Simulate（只驱动根粒子级联子粒子）
///   · Timeline PlayableDirector、独立 Animator 的 PlayableGraph
///   · 鼠标左键拖拽环绕旋转、滚轮缩放、双击重置视角
///   · 播放 / 暂停 / 重播 / 循环 / 倍速
/// 兼容 Unity 2019.4 LTS ~ Unity 6（不依赖 com.unity.timeline 包，版本差异集中在 PreviewCompat）。
/// 限制：运行时代码（Update / 协程 / 物理）需 Play Mode。
/// </summary>
internal class PrefabPreviewPanel : PreviewPanel
{
    public override string Title => "预制体预览";
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
        public string guid;
        public string category; // "" = 未分类
    }

    [Serializable]
    private class LayoutData
    {
        public List<CategoryData> categories = new List<CategoryData>();
        public List<ItemData> items = new List<ItemData>();
        public float listWidth = 240f;
    }

    private sealed class ScanRow
    {
        public string guid, path, name, category;
        public bool selected, already;
    }

    /// <summary>一次预制体预览会话，封装 PreviewRenderUtility 的生命周期。</summary>
    private sealed class PrefabSession : IDisposable
    {
        public PreviewRenderUtility pru;
        public GameObject instance;
        public GameObject asset;
        public ParticleSystem[] particles;       // 全部粒子
        public ParticleSystem[] rootParticles;    // 根粒子（父级链上无其他 ParticleSystem），只对它们 withChildren 模拟
        public PlayableDirector[] directors;      // Timeline
        public readonly List<AnimatorGraph> animGraphs = new List<AnimatorGraph>();
        public Bounds bounds;
        public float baseDistance = 5f;
        public float duration = 10f;
        public bool hasTimeline;

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
            if (directors != null)
            {
                foreach (PlayableDirector d in directors)
                    if (d != null && d.playableGraph.IsValid()) d.playableGraph.Destroy();
            }
            if (pru != null)
            {
                pru.Cleanup();
                pru = null;
            }
            instance = null;
            asset = null;
            particles = null;
            rootParticles = null;
            directors = null;
        }
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
    private string renamingCategoryId;
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

    // 拖拽：Project/Hierarchy 拖入添加，列表内拖动则快捷分类
    private readonly PreviewDropController dropController = new PreviewDropController(PreviewAssetKind.Prefab);
    private readonly PreviewSelection selection = new PreviewSelection();
    private readonly List<string> visibleGuids = new List<string>();

    [SerializeField] private string selectedGuid = "";
    [SerializeField] private bool loop = true;
    [SerializeField] private bool simulateParticles = true;
    [SerializeField] private int speedIndex = 3;

    private static readonly float[] Speeds = { 0.1f, 0.25f, 0.5f, 1f, 2f, 4f };
    private static readonly string[] SpeedLabels = { "0.1x", "0.25x", "0.5x", "1x", "2x", "4x" };

    private PrefabSession session;
    private bool playing = true;
    private double lastReal;
    private float previewTime;
    private string status = "";

    // 轨道相机
    private float camYaw = 30f;
    private float camPitch = -20f;
    private float camDistance = 5f;
    private bool draggingCam;

    // ================= 生命周期 =================

    public override void OnEnable()
    {
        layoutPath = Path.Combine(Application.dataPath, "..", "ProjectSettings", "PrefabPreviewLayout.json");
        LoadLayout();

        dropController.addAssets = AddDroppedAssets;
        dropController.moveItems = MoveItemsToCategory;
        dropController.categoryName = CategoryName;
        dropController.repaint = Repaint;

        EditorApplication.update += OnEditorTick;
        lastReal = Time.realtimeSinceStartup;
    }

    // 首次切到该标签时才恢复上次打开的预制体，避免后台标签预加载
    public override void OnFirstActivate()
    {
        if (!string.IsNullOrEmpty(selectedGuid))
            EditorApplication.delayCall += () => { if (host != null && isActive && session == null && !string.IsNullOrEmpty(selectedGuid)) OpenPrefab(selectedGuid); };
    }

    public override void OnDisable()
    {
        EditorApplication.update -= OnEditorTick;
        CloseSession();
        SaveLayout();
    }

    public override void OnBecameVisible() { lastReal = Time.realtimeSinceStartup; }

    private void OnEditorTick()
    {
        if (session != null && playing) Repaint();
    }

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
        catch (Exception e) { Debug.LogWarning("[预制体预览] 读取分类配置失败：" + e.Message); }
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
        catch (Exception e) { Debug.LogWarning("[预制体预览] 保存分类配置失败：" + e.Message); }
    }

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
        if (guid == selectedGuid)
        {
            selectedGuid = "";
            CloseSession();
        }
        SaveLayout();
        Repaint();
    }

    // ================= 拖拽添加 =================

    /// <summary>把拖入的预制体（可多个）加入列表；只拖入一个时顺手打开预览。</summary>
    private string AddDroppedAssets(List<string> paths, string categoryId)
    {
        int added = 0, moved = 0, duplicated = 0;
        string singleGuid = null;

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
            singleGuid = guid;
        }

        if (added > 0 || moved > 0)
        {
            CategoryData target = FindCategory(categoryId);
            if (target != null) target.collapsed = false;
            SaveLayout();
            status = (added > 0 ? "添加 " + added + " 个预制体" : "") +
                     (added > 0 && moved > 0 ? "，" : "") +
                     (moved > 0 ? "移动 " + moved + " 个到「" + CategoryName(categoryId) + "」" : "") +
                     (duplicated > 0 ? "，" + duplicated + " 个已在该分类" : "");

            // 延长一帧再打开，避免在拖放事件里做重活
            if (added == 1 && !string.IsNullOrEmpty(singleGuid))
            {
                string guidToOpen = singleGuid;
                EditorApplication.delayCall += () =>
                {
                    if (host != null && isActive) OpenPrefab(guidToOpen);
                };
            }
        }
        else if (duplicated > 0)
        {
            status = "拖入的预制体都已在列表中";
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
            status = "选中的预制体都已在「" + CategoryName(categoryId) + "」";
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
        status = "已移动 " + moving.Count + " 个预制体到「" + CategoryName(categoryId) + "」" +
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
            "移除选中的 " + count + " 个预制体？\n\n只从预览列表移除，不会删除磁盘上的预制体文件。",
            "移除", "取消"))
            return;

        bool removedCurrent = guids.Contains(selectedGuid);
        foreach (string guid in guids)
            layout.items.RemoveAll(i => i.guid == guid);

        if (removedCurrent)
        {
            selectedGuid = "";
            CloseSession();
        }

        selection.Clear();
        SaveLayout();
        status = "已从预览列表移除 " + count + " 个预制体";
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
        string[] guids = AssetDatabase.FindAssets("t:Prefab");
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
                selected = existing == null,
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

    private void OpenPrefab(string guid)
    {
        CloseSession();
        selectedGuid = guid;
        string path = AssetDatabase.GUIDToAssetPath(guid);
        GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
        if (prefab == null) { status = "加载失败：不是有效的预制体 " + path; return; }

        status = "预览中：" + Path.GetFileNameWithoutExtension(path);
        try
        {
            var s = new PrefabSession();
            s.pru = new PreviewRenderUtility();
            s.pru.cameraFieldOfView = 30f;
            s.pru.camera.nearClipPlane = 0.01f;
            s.pru.camera.farClipPlane = 5000f;
            s.pru.ambientColor = new Color(0.5f, 0.5f, 0.55f, 1f);
            if (s.pru.lights != null && s.pru.lights.Length > 0 && s.pru.lights[0] != null)
            {
                s.pru.lights[0].intensity = 1.2f;
                s.pru.lights[0].transform.rotation = Quaternion.Euler(50f, -30f, 0f);
            }
            if (s.pru.lights != null && s.pru.lights.Length > 1 && s.pru.lights[1] != null)
            {
                s.pru.lights[1].intensity = 0.5f;
                s.pru.lights[1].transform.rotation = Quaternion.Euler(-30f, 130f, 0f);
            }

            s.asset = prefab;
            s.instance = s.pru.InstantiatePrefabInScene(prefab);
            if (s.instance == null) { status = "实例化预制体失败：" + path; s.Dispose(); return; }

            // ---- 收集粒子 / Timeline / Animator ----
            s.particles = s.instance.GetComponentsInChildren<ParticleSystem>(true);
            PlayableDirector[] allDirectors = s.instance.GetComponentsInChildren<PlayableDirector>(true);
            Animator[] allAnimators = s.instance.GetComponentsInChildren<Animator>(true);

            // 只保留有效 Timeline（有 playableAsset）
            var dirList = new List<PlayableDirector>();
            float timelineMaxDur = 0f;
            foreach (PlayableDirector d in allDirectors)
            {
                if (d == null || d.playableAsset == null) continue;
                dirList.Add(d);
                // 手动驱动，避免编辑器时间轴干扰
                PreviewCompat.PrepareDirector(d, true);
                if (d.duration > timelineMaxDur) timelineMaxDur = (float)d.duration;
            }
            s.directors = dirList.ToArray();
            s.hasTimeline = timelineMaxDur > 0f;

            // 不依赖 Timeline 包：被 Timeline 驱动的 Animator 交给 Timeline，其余独立 Animator 自建 graph
            HashSet<Animator> bound = new HashSet<Animator>();
            foreach (PlayableDirector d in s.directors)
                PreviewCompat.CollectBoundAnimators(d, bound);

            foreach (Animator a in allAnimators)
            {
                if (a == null || bound.Contains(a)) continue;
                AnimationClip clip = PreviewCompat.GetDefaultClip(a);
                if (clip == null) continue;
                var g = new PrefabSession.AnimatorGraph { animator = a, clipLength = Mathf.Max(0.01f, clip.length) };
                g.graph = PlayableGraph.Create("PrefabPreview_" + a.GetInstanceID());
                g.graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                AnimationPlayableOutput output = AnimationPlayableOutput.Create(g.graph, "anim", a);
                g.playable = AnimationClipPlayable.Create(g.graph, clip);
                output.SetSourcePlayable(g.playable);
                g.graph.Play();
                s.animGraphs.Add(g);
            }

            // 根粒子：父级链上找不到另一个 ParticleSystem 的即为根，只对根做 withChildren 驱动
            var rootPsList = new List<ParticleSystem>();
            foreach (ParticleSystem ps in s.particles)
            {
                if (ps == null) continue;
                ParticleSystem ancestor = ps.transform.parent != null
                    ? ps.transform.parent.GetComponentInParent<ParticleSystem>()
                    : null;
                if (ancestor == null) rootPsList.Add(ps);
            }
            s.rootParticles = rootPsList.ToArray();

            // 计算预览时长：Timeline 优先，否则取最长非循环粒子 duration / 动画 clip
            float particleMaxDur = 0f;
            bool hasLoopingPs = false;
            foreach (ParticleSystem ps in s.particles)
            {
                if (ps == null) continue;
                var main = ps.main;
                if (main.loop) hasLoopingPs = true;
                else if (main.duration > particleMaxDur) particleMaxDur = main.duration;
            }
            float animMaxDur = 0f;
            foreach (var ag in s.animGraphs)
                if (ag.clipLength > animMaxDur) animMaxDur = ag.clipLength;
            if (s.hasTimeline)
                s.duration = timelineMaxDur;
            else if (particleMaxDur > 0f || animMaxDur > 0f)
                s.duration = Mathf.Max(particleMaxDur, animMaxDur, 5f);
            else if (hasLoopingPs)
                s.duration = 3600f;   // 全是自身循环粒子：交给粒子系统无限循环，不主动打断
            else
                s.duration = 10f;

            // 粒子清场（启动交给 SeekPreview 的 Simulate(restart=true)，与场景预览一致）
            foreach (ParticleSystem ps in s.rootParticles)
                ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            ComputeBounds(s);

            session = s;
            playing = true;
            previewTime = 0f;
            ResetCamera(s);
            lastReal = Time.realtimeSinceStartup;
            SeekPreview(0f);
        }
        catch (Exception e)
        {
            status = "加载失败：" + e.Message;
            CloseSession();
        }
    }

    private static void ComputeBounds(PrefabSession s)
    {
        Bounds b = new Bounds(Vector3.zero, Vector3.zero);
        bool has = false;

        Renderer[] renderers = s.instance.GetComponentsInChildren<Renderer>(true);
        foreach (Renderer r in renderers)
        {
            // 粒子渲染器在未发射时 bounds 可能为零，后面单独补
            if (r is ParticleSystemRenderer) continue;
            if (!has) { b = r.bounds; has = true; }
            else b.Encapsulate(r.bounds);
        }

        // 粒子系统：用 main.startSize / shape 估算一个包围盒
        foreach (ParticleSystem ps in s.particles)
        {
            var main = ps.main;
            float est = Mathf.Max(0.5f, main.startSize.constantMax);
            ParticleSystem.ShapeModule shape = ps.shape;
            if (shape.enabled)
            {
                switch (shape.shapeType)
                {
                    case ParticleSystemShapeType.Sphere:
                    case ParticleSystemShapeType.Hemisphere:
                        est = Mathf.Max(est, shape.radius * 2f); break;
                    case ParticleSystemShapeType.Box:
                        est = Mathf.Max(est, shape.scale.x, shape.scale.y, shape.scale.z); break;
                    case ParticleSystemShapeType.Cone:
                        est = Mathf.Max(est, shape.radius * 2f, shape.length); break;
                    default:
                        est = Mathf.Max(est, shape.radius * 2f); break;
                }
            }
            Bounds pb = new Bounds(ps.transform.position, Vector3.one * est);
            if (!has) { b = pb; has = true; }
            else b.Encapsulate(pb);
        }

        if (!has) b = new Bounds(Vector3.zero, Vector3.one * 2f);
        if (b.size.sqrMagnitude < 0.001f) b.size = Vector3.one * 2f;
        s.bounds = b;

        // 根据包围盒大小和 FOV 计算合适的观察距离
        float radius = b.extents.magnitude;
        s.baseDistance = radius / Mathf.Tan(s.pru.cameraFieldOfView * 0.5f * Mathf.Deg2Rad) * 1.4f;
        s.baseDistance = Mathf.Clamp(s.baseDistance, 1.5f, 200f);
    }

    private void ResetCamera(PrefabSession s)
    {
        camYaw = 30f;
        camPitch = -20f;
        camDistance = s != null ? s.baseDistance : 5f;
    }

    private void CloseSession()
    {
        if (session != null) { session.Dispose(); session = null; }
    }

    private void Advance(float scaledDt)
    {
        PrefabSession s = session;
        if (s == null) return;
        previewTime += scaledDt;

        bool wrapped = false;
        float dur = Mathf.Max(1f, s.duration);
        if (previewTime >= dur)
        {
            if (loop) { previewTime = previewTime % dur; wrapped = true; }
            else { previewTime = dur; playing = false; }
        }

        // Timeline
        foreach (PlayableDirector d in s.directors)
            if (d != null) { d.time = previewTime; d.Evaluate(); }

        // 独立 Animator
        foreach (PrefabSession.AnimatorGraph g in s.animGraphs)
            if (g.graph.IsValid()) { g.playable.SetTime(previewTime % g.clipLength); g.graph.Evaluate(); }

        // 粒子：只驱动根粒子，withChildren 级联到所有子粒子（解决父层无粒子时子层不播放）
        if (simulateParticles)
        {
            foreach (ParticleSystem ps in s.rootParticles)
            {
                if (ps == null) continue;
                if (wrapped) ps.Simulate(previewTime, true, true, true);
                else ps.Simulate(scaledDt, true, false, false);
            }
        }
    }

    /// <summary>把所有组件定位到绝对时间 t（初始化与拖动进度时使用）。</summary>
    private void SeekPreview(float t)
    {
        PrefabSession s = session;
        if (s == null) return;
        float dur = Mathf.Max(1f, s.duration);
        previewTime = Mathf.Clamp(t, 0f, dur);

        foreach (PlayableDirector d in s.directors)
            if (d != null) { d.time = previewTime; d.Evaluate(); }

        foreach (PrefabSession.AnimatorGraph g in s.animGraphs)
            if (g.graph.IsValid()) { g.playable.SetTime(previewTime % g.clipLength); g.graph.Evaluate(); }

        if (simulateParticles)
        {
            foreach (ParticleSystem ps in s.rootParticles)
                if (ps != null) ps.Simulate(Mathf.Max(0f, previewTime), true, true, true);
        }
    }

    private void RestartPreview()
    {
        PrefabSession s = session;
        if (s == null) return;
        playing = true;
        // 粒子彻底清场后重启
        if (simulateParticles)
        {
            foreach (ParticleSystem ps in s.rootParticles)
                if (ps != null) ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        }
        SeekPreview(0f);
        lastReal = Time.realtimeSinceStartup;
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
        if (GUILayout.Button("＋ 添加预制体", EditorStyles.miniButton, GUILayout.Width(88))) EnterScanMode();
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

        DrawGroup(null);
        foreach (CategoryData cat in RootCategories())
            DrawGroup(cat);

        if (layout.items.Count == 0)
            EditorGUILayout.HelpBox("还没有预制体。\n把预制体（可多选，或整个文件夹）从 Project 窗口直接拖到这里即可添加，\n也可以从 Hierarchy 拖预制体实例，或点上方「＋ 添加预制体」扫描工程。", MessageType.Info);
        else
        {
            int shown = CountVisible();
            if (shown == 0) GUILayout.Label("没有匹配的预制体", EditorStyles.miniLabel);
        }

        EditorGUILayout.EndScrollView();

        GUILayout.Label("共 " + layout.items.Count + " 个预制体 · " + layout.categories.Count + " 个分类" +
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
            e.Use();
        }
        else if (e.type == EventType.MouseUp && GUIUtility.hotControl == dragControlId)
        {
            string clickedGuid = dragCandidateGuid;
            GUIUtility.hotControl = 0;
            dragControlId = 0;
            dragCandidateGuid = null;
            OpenPrefab(clickedGuid);
            e.Use();
        }
        else if (e.type == EventType.DragExited)
        {
            if (GUIUtility.hotControl == dragControlId) GUIUtility.hotControl = 0;
            dragControlId = 0;
            dragCandidateGuid = null;
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

    private int CountVisible()
    {
        int n = 0;
        string q = string.IsNullOrEmpty(sidebarSearch) ? null : sidebarSearch.Trim().ToLowerInvariant();
        foreach (ItemData it in layout.items)
        {
            string p = AssetDatabase.GUIDToAssetPath(it.guid);
            if (string.IsNullOrEmpty(p)) continue;
            if (q != null && !Path.GetFileNameWithoutExtension(p).ToLowerInvariant().Contains(q) && !p.ToLowerInvariant().Contains(q)) continue;
            n++;
        }
        return n;
    }

    private void DrawGroup(CategoryData cat)
    {
        string catId = cat != null ? cat.id : "";
        List<ItemData> members = CollectMembers(catId);

        List<CategoryData> subCategories = new List<CategoryData>();
        if (cat != null)
        {
            foreach (CategoryData sub in ChildCategories(cat.id))
                if (string.IsNullOrEmpty(sidebarSearch) || CollectMembers(sub.id).Count > 0)
                    subCategories.Add(sub);
        }

        if (members.Count == 0 && subCategories.Count == 0 && !string.IsNullOrEmpty(sidebarSearch))
            return;

        int total = members.Count;
        foreach (CategoryData sub in subCategories)
            total += CollectMembers(sub.id).Count;

        DrawCategoryHeader(cat, cat != null ? cat.name : "未分类", total, 0f, cat != null);
        if (cat != null && cat.collapsed) return;

        if (cat != null && creatingCategory && creatingParentId == cat.id)
            DrawCategoryCreator(14f);

        foreach (ItemData it in members)
            DrawPrefabRow(it);

        foreach (CategoryData sub in subCategories)
            DrawSubGroup(sub);
    }

    private void DrawSubGroup(CategoryData sub)
    {
        List<ItemData> members = CollectMembers(sub.id);

        DrawCategoryHeader(sub, sub.name, members.Count, 14f, false);
        if (sub.collapsed) return;

        foreach (ItemData it in members)
            DrawPrefabRow(it, 14f);
    }

    private List<ItemData> CollectMembers(string categoryId)
    {
        var members = new List<ItemData>();
        string filter = string.IsNullOrEmpty(sidebarSearch) ? null : sidebarSearch.Trim().ToLowerInvariant();

        foreach (ItemData it in layout.items)
        {
            if (it.category != categoryId) continue;

            string path = AssetDatabase.GUIDToAssetPath(it.guid);
            if (string.IsNullOrEmpty(path)) continue;

            if (filter != null &&
                !Path.GetFileNameWithoutExtension(path).ToLowerInvariant().Contains(filter) &&
                !path.ToLowerInvariant().Contains(filter)) continue;

            members.Add(it);
        }

        return members;
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

    private void DrawPrefabRow(ItemData it, float indent = 0f)
    {
        string path = AssetDatabase.GUIDToAssetPath(it.guid);
        string nm = Path.GetFileNameWithoutExtension(path);
        Rect row = GUILayoutUtility.GetRect(1f, 19f, GUILayout.ExpandWidth(true));
        row.x += 14f + indent;
        row.width -= 14f + indent;
        int rowControlId = GUIUtility.GetControlID(it.guid.GetHashCode(), FocusType.Passive, row);

        dropController.AddTarget(it.category, row);

        bool active = path == AssetDatabase.GUIDToAssetPath(selectedGuid);
        if (active) EditorGUI.DrawRect(row, new Color(0.24f, 0.45f, 0.72f));
        if (selection.IsSelected(it.guid))
        {
            EditorGUI.DrawRect(row, new Color(0.2f, 0.52f, 0.85f, 0.38f));
            EditorGUI.DrawRect(new Rect(row.x, row.y, 3f, row.height), new Color(0.4f, 0.8f, 1f, 1f));
        }

        // 小缩略图
        GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
        Texture icon = prefab != null ? AssetPreview.GetMiniThumbnail(prefab) : null;
        if (icon != null) GUI.DrawTexture(new Rect(row.x + 2f, row.y + 2f, 15f, 15f), icon, ScaleMode.ScaleToFit);

        GUIStyle rs = new GUIStyle(EditorStyles.label)
        {
            alignment = TextAnchor.MiddleLeft,
            normal = { textColor = active ? Color.white : GUI.skin.label.normal.textColor }
        };
        GUI.Label(new Rect(row.x + 20f, row.y, row.width - 22f, row.height), new GUIContent(nm, path + "\n分类：" + CategoryName(it.category) + "\n可拖到其它分类 · Ctrl/Shift 多选 · 右键批量操作"), rs);

        Event e = Event.current;
        if (e.type != EventType.MouseDown || !row.Contains(e.mousePosition)) return;

        if (e.button == 1)
        {
            if (GUIUtility.hotControl == dragControlId) GUIUtility.hotControl = 0;
            dragControlId = 0;
            dragCandidateGuid = null;
            if (!selection.IsSelected(it.guid)) selection.SelectOnly(it.guid);
            ShowPrefabMenu(it, path);
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
                "删除「" + CategoryName(cat.id) + "」？\n" + where + itemCount + " 个预制体会移到「" + fallback + "」，不会被删除。",
                "删除", "取消"))
            {
                if (renamingCategoryId == cat.id) renamingCategoryId = null;
                DeleteCategory(cat.id);
                Repaint();
            }
        });

        m.ShowAsContext();
    }

    private void ShowPrefabMenu(ItemData it, string path)
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
        m.AddItem(new GUIContent("正式打开预制体"), false, () => OpenPrefabInEditor(path));
        m.AddSeparator("");
        m.AddItem(new GUIContent("从预览列表移除"), false, () =>
        {
            if (EditorUtility.DisplayDialog("移出列表", "把「" + Path.GetFileNameWithoutExtension(path) + "」从预览列表移除？\n（不会删除磁盘文件，可再次扫描添加）", "移除", "取消"))
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
        GUILayout.Label("选择要加入的预制体", EditorStyles.boldLabel);
        GUILayout.FlexibleSpace();
        if (GUILayout.Button("取消", EditorStyles.miniButton, GUILayout.Width(44))) { scanMode = false; Repaint(); }
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.BeginHorizontal();
        scanSearch = GUILayout.TextField(scanSearch, EditorStyles.toolbarSearchField, GUILayout.MinWidth(40));
        if (GUILayout.Button("全选", EditorStyles.miniButton, GUILayout.Width(40))) { foreach (ScanRow r in VisibleScanRows()) r.selected = true; }
        if (GUILayout.Button("全不选", EditorStyles.miniButton, GUILayout.Width(48))) { foreach (ScanRow r in VisibleScanRows()) r.selected = false; }
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.BeginHorizontal();
        GUILayout.Label("新预制体默认分类", GUILayout.Width(96));
        EditorGUI.BeginChangeCheck();
        scanDefaultCategoryIndex = EditorGUILayout.Popup(scanDefaultCategoryIndex, CategoryOptions());
        if (EditorGUI.EndChangeCheck())
        {
            string catId = IndexToCategory(scanDefaultCategoryIndex);
            foreach (ScanRow r in scanRows) if (!r.already) r.category = catId;
        }
        EditorGUILayout.EndHorizontal();

        listScroll = EditorGUILayout.BeginScrollView(listScroll);
        foreach (ScanRow r in scanRows)
        {
            string q = string.IsNullOrEmpty(scanSearch) ? null : scanSearch.Trim().ToLowerInvariant();
            if (q != null && !r.name.ToLowerInvariant().Contains(q) && !r.path.ToLowerInvariant().Contains(q)) continue;
            DrawScanRow(r);
        }
        EditorGUILayout.EndScrollView();

        int sel = 0;
        foreach (ScanRow r in scanRows) if (r.selected) sel++;
        using (new EditorGUI.DisabledScope(sel == 0))
        {
            if (GUILayout.Button("保存：添加选中的 " + sel + " 个预制体", EditorStyles.miniButton))
                ApplyScan();
        }
        GUILayout.Label("勾选=加入并显示在左侧；不勾选=不显示。已添加的预制体默认不勾。", EditorStyles.centeredGreyMiniLabel);
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

        // 标题栏
        EditorGUILayout.BeginHorizontal();
        string title = session == null ? "未选择预制体" : (session.asset != null ? session.asset.name : "");
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

        // 预览视口
        Rect view = GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
        EditorGUI.DrawRect(view, new Color(0.12f, 0.12f, 0.13f));

        if (session == null)
        {
            GUI.Label(view, "\n\n从左侧选择一个预制体开始预览（不进入 Play Mode）\n\n支持粒子特效、模型预制等，左键拖拽旋转 · 滚轮缩放 · 双击重置", CenteredLabel());
        }
        else
        {
            HandleViewportInput(view);
            RenderPrefab(view);
            DrawViewHud(view);
        }

        EditorGUILayout.Space(2);
        DrawTransport();
        EditorGUILayout.EndVertical();
    }

    private void HandleViewportInput(Rect view)
    {
        Event e = Event.current;

        // 左键拖拽旋转
        if (e.type == EventType.MouseDown && e.button == 0 && view.Contains(e.mousePosition))
        {
            if (e.clickCount == 2)
            {
                ResetCamera(session);
                e.Use();
            }
            else
            {
                draggingCam = true;
                e.Use();
            }
        }
        else if (e.type == EventType.MouseDrag && draggingCam)
        {
            camYaw += e.delta.x * 0.4f;
            camPitch = Mathf.Clamp(camPitch - e.delta.y * 0.4f, -89f, 89f);
            Repaint();
            e.Use();
        }
        else if (e.type == EventType.MouseUp && draggingCam)
        {
            draggingCam = false;
            e.Use();
        }
        // 滚轮缩放
        else if (e.type == EventType.ScrollWheel && view.Contains(e.mousePosition))
        {
            float factor = Mathf.Pow(1.12f, Mathf.Clamp(e.delta.y, -3f, 3f));
            camDistance = Mathf.Clamp(camDistance * factor, session.baseDistance * 0.15f, session.baseDistance * 8f);
            Repaint();
            e.Use();
        }
    }

    private void RenderPrefab(Rect view)
    {
        PrefabSession s = session;
        if (s == null || s.pru == null) return;

        // 推进时间
        if (Event.current.type == EventType.Repaint)
        {
            double now = Time.realtimeSinceStartup;
            float dt = Mathf.Clamp((float)(now - lastReal), 0f, 0.1f);
            lastReal = now;
            if (playing)
            {
                float scaledDt = dt * Speeds[Mathf.Clamp(speedIndex, 0, Speeds.Length - 1)];
                Advance(scaledDt);
            }
        }

        // 轨道相机位置
        Bounds b = s.bounds;
        float yawRad = camYaw * Mathf.Deg2Rad;
        float pitchRad = camPitch * Mathf.Deg2Rad;
        Vector3 dir = new Vector3(
            Mathf.Cos(pitchRad) * Mathf.Sin(yawRad),
            Mathf.Sin(pitchRad),
            Mathf.Cos(pitchRad) * Mathf.Cos(yawRad));
        Vector3 camPos = b.center + dir * camDistance;

        // 用 PreviewRenderUtility 渲染
        Rect renderRect = new Rect(0f, 0f, Mathf.Max(64f, view.width), Mathf.Max(64f, view.height));
        Texture tex = null;
        s.pru.BeginPreview(renderRect, GUIStyle.none);
        s.pru.camera.transform.position = camPos;
        s.pru.camera.transform.LookAt(b.center);
        s.pru.camera.backgroundColor = new Color(0.12f, 0.12f, 0.13f, 1f);
        s.pru.camera.clearFlags = CameraClearFlags.SolidColor;
        s.pru.Render(false);
        tex = s.pru.EndPreview();

        if (tex != null)
        {
            float va = view.width / Mathf.Max(1f, view.height);
            float ta = (float)tex.width / Mathf.Max(1, tex.height);
            float w = view.width, h = view.height;
            if (ta > va) h = w / ta; else w = h * ta;
            Rect fit = new Rect(view.x + (view.width - w) * 0.5f, view.y + (view.height - h) * 0.5f, w, h);
            GUI.DrawTexture(fit, tex, ScaleMode.StretchToFill, false);
        }
    }

    private void DrawTransport()
    {
        if (session == null) return;

        EditorGUILayout.BeginHorizontal();
        GUIContent playIcon = PreviewCompat.PlayPauseIcon(playing);
        if (GUILayout.Button(playIcon, EditorStyles.miniButton, GUILayout.Width(34), GUILayout.Height(22)))
        {
            playing = !playing;
            if (playing) lastReal = Time.realtimeSinceStartup;
        }
        if (GUILayout.Button("重播", EditorStyles.miniButton, GUILayout.Width(48), GUILayout.Height(22)))
            RestartPreview();
        loop = GUILayout.Toggle(loop, "循环", EditorStyles.miniButton, GUILayout.Width(44), GUILayout.Height(22));
        speedIndex = EditorGUILayout.Popup(speedIndex, SpeedLabels, GUILayout.Width(58));
        GUILayout.FlexibleSpace();
        simulateParticles = GUILayout.Toggle(simulateParticles, "模拟粒子", EditorStyles.miniButton, GUILayout.Width(70), GUILayout.Height(22));
        if (GUILayout.Button("重置视角", EditorStyles.miniButton, GUILayout.Width(64), GUILayout.Height(22)))
            ResetCamera(session);
        EditorGUILayout.EndHorizontal();

        GUILayout.Label("左键拖拽环绕旋转 · 滚轮缩放 · 双击视口重置视角 · 右键条目可移动分类 · 运行时脚本逻辑与物理需进 Play Mode", EditorStyles.miniLabel);
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
        PrefabSession s = session;
        if (s == null || s.asset == null) return;

        int psCount = s.particles != null ? s.particles.Length : 0;
        int dirCount = s.directors != null ? s.directors.Length : 0;
        int animCount = s.animGraphs != null ? s.animGraphs.Count : 0;
        string tags = "";
        if (dirCount > 0) tags += "Timeline " + dirCount + "   ";
        if (animCount > 0) tags += "动画 " + animCount + "   ";
        if (psCount > 0) tags += "粒子 " + psCount + "   ";
        string hud = s.asset.name + "   " + tags + (playing ? "播放中" : "已暂停");
        GUIContent gc = new GUIContent(hud);
        Vector2 sz = HudLabel().CalcSize(gc);
        Rect bg = new Rect(view.x + 6f, view.y + 5f, sz.x + 12f, sz.y + 4f);
        EditorGUI.DrawRect(bg, new Color(0f, 0f, 0f, 0.45f));
        GUI.Label(new Rect(bg.x + 6f, bg.y + 1f, sz.x, sz.y + 2f), gc, HudLabel());

        if (s.pru != null && s.pru.camera != null)
            PreviewCompat.DrawOrientationGizmo(view, s.pru.camera.transform.rotation);
    }

    // ================= 辅助 =================

    private void PingCurrent()
    {
        if (string.IsNullOrEmpty(selectedGuid)) return;
        string path = AssetDatabase.GUIDToAssetPath(selectedGuid);
        if (string.IsNullOrEmpty(path)) return;
        UnityEngine.Object obj = AssetDatabase.LoadAssetAtPath<GameObject>(path);
        if (obj != null) { EditorGUIUtility.PingObject(obj); Selection.activeObject = obj; }
    }

    private void PingPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        UnityEngine.Object obj = AssetDatabase.LoadAssetAtPath<GameObject>(path);
        if (obj != null) { EditorGUIUtility.PingObject(obj); Selection.activeObject = obj; }
    }

    private void OpenCurrentInEditor()
    {
        if (string.IsNullOrEmpty(selectedGuid)) return;
        string path = AssetDatabase.GUIDToAssetPath(selectedGuid);
        OpenPrefabInEditor(path);
    }

    private void OpenPrefabInEditor(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
        if (prefab != null)
        {
            CloseSession();
            selectedGuid = "";
            AssetDatabase.OpenAsset(prefab);
            status = "已在预制体编辑模式打开：" + path;
        }
    }
}
