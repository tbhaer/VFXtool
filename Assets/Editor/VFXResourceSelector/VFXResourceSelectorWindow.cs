using System;
using System.Collections.Generic;
using System.IO;
using Stopwatch = System.Diagnostics.Stopwatch;
using UnityEditor;
using UnityEditor.SearchService;
using UnityEngine;
using Object = UnityEngine.Object;

/// <summary>
/// VFX 通用资源分类选择器窗口。
/// 支持：类型切换（贴图 / 材质 / 网格 / 全部）、一级~四级分类（自动前缀或自定义规则）、搜索、网格选择。
/// 两种打开方式：
///   1. 引擎模式：由 VFXObjectSelectorEngine 接管 Unity 的 select 点击时调用（材质、粒子面板通用）；
///   2. 独立模式：OpenPicker，供材质工具窗口、shader 属性绘制器等主动调用。
/// 关闭时通过回调把选择结果交还给调用方（Unity 会应用该对象到原字段）。
/// </summary>
public class VFXResourceSelectorWindow : EditorWindow
{
    // ---- 上下文与回调 ----
    private ISearchContext searchContext;
    private Action<Object, bool> onClosed;
    private Action<Object> onUpdated;
    private bool confirmed;                 // 用户确认选择或弹窗失焦提交
    private ResourceItem selectedItem;
    private bool selectedNone;
    private bool canCloseOnLostFocus;
    private string initialSearchFilter;

    // ---- 类型 ----
    private string activeType = "All";
    private readonly string[] allTypeTabs = { "All", "Texture", "Material", "Mesh" };
    private List<string> allowedTypes = new List<string>();   // 字段允许的类型（引擎模式来自 context）
    private bool typeLocked;                                  // 字段只允许一种类型时锁定

    // ---- 分类 ----
    private string activeL1 = VFXCategoryMatcher.AllCategory;
    private string activeL2 = VFXCategoryMatcher.AllCategory;
    private string activeL3 = VFXCategoryMatcher.AllCategory;
    private string activeL4 = VFXCategoryMatcher.AllCategory;

    // ---- 搜索 ----
    private string searchText = "";
    private const string SearchControlName = "VFXResourceSelectorSearch";
    private bool focusSearchOnNextGUI;
    private int builtinOrder;
    private readonly HashSet<string> builtinAssetKeys = new HashSet<string>();

    // ---- 资源 ----
    private List<ResourceItem> allItems = new List<ResourceItem>();
    private List<ResourceItem> filteredItems = new List<ResourceItem>();
    private Vector2 scroll;
    private bool autoScrollToSelectedOnOpen;
    private const float MinThumbnailSize = 72f;
    private const float MaxThumbnailSize = MinThumbnailSize * 2f;
    private const string ThumbnailSizePrefsKey = "VFXResourceSelector.ThumbnailSize";
    private float thumbnailSize = MinThumbnailSize;

    // ---- 设置面板 ----
    private bool showSettings;
    private readonly HashSet<int> expandedRules = new HashSet<int>();
    private readonly HashSet<int> expandedSubRules = new HashSet<int>();
    private readonly HashSet<int> expandedSubSubRules = new HashSet<int>();

    private static VFXResourceSelectorWindow s_Instance;
    private int scanGeneration;

    private void OnEnable()
    {
        thumbnailSize = Mathf.Clamp(
            EditorPrefs.GetFloat(ThumbnailSizePrefsKey, MinThumbnailSize),
            MinThumbnailSize,
            MaxThumbnailSize);
    }

    // ================= 打开入口 =================

    /// <summary>引擎模式：由 ObjectSelector 引擎调用。</summary>
    public static void Open(ISearchContext context, Action<Object, bool> onClosed, Action<Object> onUpdated)
    {
        VFXResourceSelectorWindow win = GetOrCreate();
        win.searchContext = context;
        win.onClosed = onClosed;
        win.onUpdated = onUpdated;
        win.confirmed = false;
        win.selectedItem = null;
        win.selectedNone = false;
        win.searchText = "";
        win.initialSearchFilter = "";
        win.scroll = Vector2.zero;
        win.focusSearchOnNextGUI = true;
        win.allowedTypes.Clear();
        win.typeLocked = false;
        win.PrepareCurrent(GetCurrentObject(context));

        // 反射读取 requiredTypeNames（兼容不同 Unity 版本），引擎模式由引擎调用 Open
        IEnumerable<string> requiredTypeNames = VFXObjectSelectorEngine.GetRequiredTypeNames(context);
        if (requiredTypeNames != null)
        {
            win.allowedTypes = ResolveAllowedTypes(requiredTypeNames);
            if (win.allowedTypes.Count == 1)
            {
                win.typeLocked = true;
            }
            // 初始搜索词由引擎的 SetSearchFilter 回调传入（ApplySearchFilter）
        }

        win.InitAndShow();
    }

    /// <summary>独立模式：主动打开选择器（不经过引擎）。typeName 取 “Texture”/“Material”/“Mesh”/“All”。</summary>
    public static void OpenPicker(string typeName, Object current, Action<Object, bool> onClosed, Action<Object> onUpdated)
    {
        VFXResourceSelectorWindow win = GetOrCreate();
        win.searchContext = null;
        win.onClosed = onClosed;
        win.onUpdated = onUpdated;
        win.confirmed = false;
        win.allowedTypes.Clear();
        win.typeLocked = !string.IsNullOrEmpty(typeName) && typeName != "All";
        if (win.typeLocked)
        {
            win.allowedTypes.Add(typeName);
        }
        win.initialSearchFilter = "";
        win.searchText = "";
        win.scroll = Vector2.zero;
        win.focusSearchOnNextGUI = true;
        win.PrepareCurrent(current);
        win.InitAndShow();
    }

    /// <summary>引擎 SetSearchFilter 回调：更新窗口初始搜索词。</summary>
    public static void ApplySearchFilter(string searchFilter)
    {
        if (s_Instance != null && !string.IsNullOrEmpty(searchFilter))
        {
            s_Instance.searchText = searchFilter;
            s_Instance.RefreshFiltered();
            s_Instance.Repaint();
        }
    }

    private static VFXResourceSelectorWindow GetOrCreate()
    {
        if (s_Instance == null)
        {
            s_Instance = CreateInstance<VFXResourceSelectorWindow>();
        }
        return s_Instance;
    }

    private static Object GetCurrentObject(ISearchContext context)
    {
        if (context == null) return null;
        try
        {
            System.Reflection.PropertyInfo property = context.GetType().GetProperty(
                "currentObject",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            return property != null ? property.GetValue(context, null) as Object : null;
        }
        catch
        {
            return null;
        }
    }

    private void InitAndShow()
    {
        autoScrollToSelectedOnOpen = selectedItem != null;

        // 根据字段类型锁定/显示类型标签
        if (typeLocked && allowedTypes.Count == 1)
        {
            activeType = allowedTypes[0];
        }
        else
        {
            activeType = "All";
        }

        activeL1 = VFXCategoryMatcher.AllCategory;
        activeL2 = VFXCategoryMatcher.AllCategory;
        activeL3 = VFXCategoryMatcher.AllCategory;
        activeL4 = VFXCategoryMatcher.AllCategory;

        ScanResources();
        if (!string.IsNullOrEmpty(initialSearchFilter))
        {
            searchText = initialSearchFilter;
        }
        RefreshFiltered();

        titleContent = new GUIContent("VFX 资源分类选择器");
        minSize = new Vector2(460, 400);
        canCloseOnLostFocus = false;
        ShowUtility();
        Focus();
        EditorApplication.delayCall += EnableLostFocusClose;
    }

    private void EnableLostFocusClose()
    {
        if (this != null)
        {
            canCloseOnLostFocus = true;
        }
    }

    private void PrepareCurrent(Object current)
    {
        selectedItem = null;
        selectedNone = false;
        if (current != null)
        {
            string path = AssetDatabase.GetAssetPath(current);
            if (string.IsNullOrEmpty(path))
            {
                // Unity 内置资源没有 AssetDatabase 路径，但仍应作为当前选择保留。
                path = "内置当前/" + current.name;
            }
            if (!string.IsNullOrEmpty(path))
            {
                selectedItem = new ResourceItem
                {
                    path = path,
                    // FBX 内的 Mesh 子资产：路径相同，需用子资产名区分，否则高亮/匹配会串
                    subAssetName = (current is Mesh && AssetDatabase.IsSubAsset(current)) ? current.name : null,
                    asset = current,
                    assetLoaded = true,
                    name = current.name,
                    typeKey = GetTypeKey(current)
                };
            }
        }
    }

    // ================= 资源扫描 =================

    private class ResourceItem
    {
        public string path;
        public string subAssetName; // 子资产名（如 FBX 内的 Mesh 名）；空 = 主资产
        public Object asset;        // 懒加载缓存（大项目避免全量加载资源对象）
        public VFXResourceSelectorWindow owner;
        public string name;
        public string typeKey;      // "Texture" / "Material" / "Mesh"
        public int scanGeneration;
        public bool assetLoaded;
        public bool isBuiltin;
        public int builtinOrder;
        public double lastVisibleTime;
        public Texture2D preview;
        public Texture2D fallbackThumbnail;
        public bool textureCopyFailed;
        public bool previewRequestStarted;
        public bool previewRequestInFlight;
        public int previewRequestAttempts;
        public double previewRequestStartedAt;

        /// <summary>按需加载资源对象并缓存。只在绘制/选择时调用，避免扫描时加载全部资源导致卡顿或崩溃。</summary>
        public Object GetAsset()
        {
            if (assetLoaded) return asset;
            return owner != null ? owner.GetCachedAsset(this) : null;
        }
    }

    private void ScanResources()
    {
        if (meshScanUpdateSubscribed)
        {
            EditorApplication.update -= ProcessMeshScan;
            meshScanUpdateSubscribed = false;
        }
        isScanningMeshes = false;
        pendingMeshPaths.Clear();
        cachePrunePending = false;
        cachePruneCursor = 0;
        scanGeneration++;
        DestroyOwnedPreviews();
        allItems.Clear();
        loadedAssetCache.Clear();
        loadedAssetCacheOrder.Clear();
        meshRepresentationsCache.Clear();
        meshRepresentationsCacheOrder.Clear();
        failedAssetLoadWarnings.Clear();
        attemptedLoadKeys.Clear();
        meshScanErrorCount = 0;
        firstMeshScanError = null;

        // 分类浏览时一次扫描三种资源，切换类型标签只做内存过滤。
        // 字段锁定为单一类型时只扫描该类型，避免无关资源拖慢原生字段选择。
        string[] meshPaths = new string[0];
        if (typeLocked && activeType == "Texture") AddType<Texture>("Texture");
        else if (typeLocked && activeType == "Material") AddType<Material>("Material");
        else if (typeLocked && activeType == "Mesh") meshPaths = FindAssetPaths<Mesh>();
        else
        {
            AddType<Texture>("Texture");
            AddType<Material>("Material");
            meshPaths = FindAssetPaths<Mesh>();
        }
        builtinOrder = 0;
        builtinAssetKeys.Clear();
        AddBuiltinTemplates();
        for (int i = 0; i < allItems.Count; i++)
        {
            allItems[i].owner = this;
            allItems[i].scanGeneration = scanGeneration;
        }

        foreach (string path in meshPaths)
        {
            pendingMeshPaths.Enqueue(path);
        }
        totalMeshPaths = pendingMeshPaths.Count;
        scannedMeshPaths = 0;
        if (pendingMeshPaths.Count > 0)
        {
            isScanningMeshes = true;
            EditorApplication.update += ProcessMeshScan;
            meshScanUpdateSubscribed = true;
            Repaint();
            return;
        }

        FinishResourceScan();
    }

    private void FinishResourceScan()
    {
        allItems.Sort((a, b) =>
        {
            if (a.isBuiltin != b.isBuiltin)
            {
                return a.isBuiltin ? -1 : 1;
            }
            if (a.isBuiltin && a.builtinOrder != b.builtinOrder)
            {
                return a.builtinOrder.CompareTo(b.builtinOrder);
            }
            return string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase);
        });
        for (int i = 0; i < allItems.Count; i++)
        {
            allItems[i].owner = this;
            allItems[i].scanGeneration = scanGeneration;
        }

        // 不调整 AssetPreview 的全局缓存容量：该缓存供整个 Editor 共用，
        // 按项目资源数量扩容会在大型工程中造成持续的显存/内存压力。
        InvalidateCategoryCache();
        isScanningMeshes = false;
        if (meshScanErrorCount > 0)
        {
            Debug.LogWarning("[VFX Resource Selector] 跳过了 " + meshScanErrorCount
                + " 个无法扫描的网格资源。首个错误：" + firstMeshScanError);
        }
        RefreshFiltered();
        Repaint();
    }

    private void ProcessMeshScan()
    {
        if (this == null || !isScanningMeshes)
        {
            EditorApplication.update -= ProcessMeshScan;
            meshScanUpdateSubscribed = false;
            return;
        }

        long startedAt = Stopwatch.GetTimestamp();
        int processed = 0;
        while (pendingMeshPaths.Count > 0 && processed < 8
            && (Stopwatch.GetTimestamp() - startedAt) * 1000.0 / Stopwatch.Frequency < 8.0)
        {
            string path = pendingMeshPaths.Dequeue();
            try
            {
                AddMeshSubAssets(path);
            }
            catch (Exception e)
            {
                meshScanErrorCount++;
                if (string.IsNullOrEmpty(firstMeshScanError)) firstMeshScanError = path + "\n" + e.Message;
            }
            processed++;
            scannedMeshPaths++;
        }

        if (pendingMeshPaths.Count == 0 || EditorApplication.timeSinceStartup >= nextScanRepaintTime)
        {
            nextScanRepaintTime = EditorApplication.timeSinceStartup + 0.1;
            Repaint();
        }
        if (pendingMeshPaths.Count == 0)
        {
            EditorApplication.update -= ProcessMeshScan;
            meshScanUpdateSubscribed = false;
            FinishResourceScan();
        }
    }

    /// <summary>Unity 原生 Object Selector 会展示的内置 Mesh 与 Editor Texture。</summary>
    private void AddBuiltinTemplates()
    {
        // 与 Unity 原生 Object Selector 的内置 Mesh 列表保持一致。
        if (activeType == "All" || activeType == "Mesh")
        {
            AddBuiltinMesh("Cube", "Cube.fbx");
            AddBuiltinMesh("Capsule", "Capsule.fbx");
            AddBuiltinMesh("Cylinder", "Cylinder.fbx");
            AddBuiltinMesh("Plane", "Plane.fbx");
            AddBuiltinMesh("Sphere", "Sphere.fbx");
            AddBuiltinMesh("Quad", "Quad.fbx");
        }

        // 原生 Texture 选择器会显示 Unity Editor 内置贴图。
        if (activeType == "All" || activeType == "Texture")
        {
            AddBuiltinTexturesFromUnityResources();
            AddBuiltinTexture("Default-Checker", "Default-Checker.png");
            AddBuiltinTexture("Default-Particle", "Default-Particle.png");
            AddBuiltinTexture("Default-ParticleSystem", "Default-ParticleSystem.png");
            AddBuiltinTexture("Background", "Background.png");
            AddBuiltinTexture("Checkmark", "Checkmark.png");
            AddBuiltinTexture("DropdownArrow", "DropdownArrow.png");
            AddBuiltinTexture("InputFieldBackground", "InputFieldBackground.png");
            AddBuiltinTexture("Knob", "Knob.png");
            AddBuiltinTexture("UIMask", "UIMask.png");
            AddBuiltinTexture("UISprite", "UISprite.png");
        }
    }

    private void AddBuiltin(string displayName, string typeKey, Object asset)
    {
        if (asset == null) return;
        if (!builtinAssetKeys.Add(typeKey + ":" + displayName)) return;
        int order = builtinOrder++;
        allItems.Add(new ResourceItem
        {
            path = "内置资源/" + typeKey + "/" + displayName,
            name = displayName,
            typeKey = typeKey,
            asset = asset,
            assetLoaded = true,
            isBuiltin = true,
            builtinOrder = order
        });
    }

    private void AddBuiltinMesh(string displayName, string resourceName)
    {
        AddBuiltin(displayName, "Mesh", Resources.GetBuiltinResource<Mesh>(resourceName));
    }

    private void AddBuiltinTexturesFromUnityResources()
    {
        string[] builtinPaths = { "Library/unity default resources", "Resources/unity_builtin_extra" };
        string[] wantedNames =
        {
            "Default-Checker", "Default-Particle", "Default-ParticleSystem",
            "Background", "Checkmark", "DropdownArrow", "InputFieldBackground",
            "Knob", "UIMask", "UISprite"
        };
        HashSet<string> wanted = new HashSet<string>(wantedNames, StringComparer.OrdinalIgnoreCase);

        foreach (string path in builtinPaths)
        {
            Object[] assets = AssetDatabase.LoadAllAssetsAtPath(path);
            foreach (Object asset in assets)
            {
                Texture2D texture = asset as Texture2D;
                if (texture != null && wanted.Contains(texture.name))
                {
                    AddBuiltin(texture.name, "Texture", texture);
                }
            }
        }
    }

    private void AddBuiltinTexture(string displayName, string resourcePath)
    {
        Texture2D texture = LoadBuiltinTexture(resourcePath);
        AddBuiltin(displayName, "Texture", texture);
    }

    private static Texture2D LoadBuiltinTexture(string resourcePath)
    {
        string name = Path.GetFileNameWithoutExtension(resourcePath);
        // 只按名称查询，绝不把猜测的文件路径传给 Unity 的资源加载 API。
        return FindBuiltinTextureByName(name);
    }

    private static Texture2D FindBuiltinTextureByName(string name)
    {
        try
        {
            System.Reflection.MethodInfo method = typeof(EditorGUIUtility).GetMethod(
                "FindTextureByName",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            if (method != null)
            {
                return method.Invoke(null, new object[] { name }) as Texture2D;
            }
        }
        catch
        {
            // 找不到内置资源时静默返回，由其他安全路径继续尝试。
        }
        return null;
    }

    /// <summary>扫描指定类型资源：只记录路径与文件名元数据，不加载资源对象（懒加载见 ResourceItem.GetAsset）。</summary>
    private void AddType<T>(string typeKey) where T : Object
    {
        foreach (string path in FindAssetPaths<T>())
        {
            allItems.Add(new ResourceItem
            {
                path = path,
                name = Path.GetFileNameWithoutExtension(path),
                typeKey = typeKey
            });
        }
    }

    private static string[] FindAssetPaths<T>() where T : Object
    {
        // 本选择器只展示 Assets 下的项目资源，避免额外扫描 Packages。
        string[] guids = AssetDatabase.FindAssets("t:" + typeof(T).Name, new[] { "Assets" });
        List<string> paths = new List<string>(guids.Length);
        foreach (string guid in guids)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (!path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)) continue;
            // 按主资产类型过滤：TerrainData 等会被 t:Mesh/t:Texture 误命中，加载会触发原生崩溃，从 GUID 层就挡掉。
            if (!IsCompatibleMainType(path, typeof(T))) continue;
            paths.Add(path);
        }
        return paths.ToArray();
    }

    /// <summary>校验路径的主资产类型是否属于预期类型；Mesh 额外允许 FBX 模型（其主资产为 GameObject）。</summary>
    private static bool IsCompatibleMainType(string path, Type expectedType)
    {
        try
        {
            Type mainType = AssetDatabase.GetMainAssetTypeAtPath(path);
            if (mainType == null)
            {
                return false; // 类型未知时不加载，避免把导入中/失效路径传给原生资源加载器
            }
            if (expectedType.IsAssignableFrom(mainType))
            {
                return true;
            }
            return expectedType == typeof(Mesh)
                && typeof(GameObject).IsAssignableFrom(mainType)
                && AssetImporter.GetAtPath(path) is ModelImporter;
        }
        catch
        {
            return false; // 刷新期间失效的 GUID/path 安全跳过
        }
    }

    /// <summary>
    /// 网格类型：列出文件内的全部 Mesh 子资产（FBX/模型文件常含多个网格），
    /// 按网格资产名分类与显示，而不是用整个模型文件名。
    /// 单独的 Mesh 主资产直接作为一项；模型文件只列出明确的 Mesh 子资产。
    /// </summary>
    private void AddMeshSubAssets(string path)
    {
        Type mainType = AssetDatabase.GetMainAssetTypeAtPath(path);
        if (mainType == null)
        {
            return;
        }

        if (typeof(Mesh).IsAssignableFrom(mainType))
        {
            allItems.Add(new ResourceItem
            {
                path = path,
                name = Path.GetFileNameWithoutExtension(path),
                typeKey = "Mesh"
            });
            return;
        }

        // 只枚举明确由 ModelImporter 导入的模型子资产；Prefab/GameObject 和未知类型不进入原生子资产加载 API。
        if (!typeof(GameObject).IsAssignableFrom(mainType) || !(AssetImporter.GetAtPath(path) is ModelImporter))
        {
            return;
        }

        Object[] reps = AssetDatabase.LoadAllAssetRepresentationsAtPath(path);
        if (reps == null)
        {
            return;
        }
        foreach (Object o in reps)
        {
            Mesh m = o as Mesh;
            if (m == null)
            {
                continue;
            }
            allItems.Add(new ResourceItem
            {
                path = path,
                subAssetName = m.name,
                name = m.name,
                typeKey = "Mesh"
            });
        }
    }

    private Object GetCachedAsset(ResourceItem item)
    {
        string key = item.path + "|" + item.subAssetName;
        Object cached;
        if (loadedAssetCache.TryGetValue(key, out cached))
        {
            if (cached != null)
            {
                return cached;
            }
            loadedAssetCache.Remove(key); // 清除已被 Unity 卸载的伪空对象，允许安全重载
        }

        // 加载前先检查主资产类型：TerrainData 等资源加载会触发 Unity 原生崩溃（DetailDatabase），
        // 且不属于本选择器展示范围（贴图 / 材质 / 网格 / FBX 模型），一律跳过，返回 null 走占位。
        Type mainType;
        bool expectedTypeMatches;
        try
        {
            mainType = AssetDatabase.GetMainAssetTypeAtPath(item.path);
            if (mainType == null)
            {
                return null;
            }
            expectedTypeMatches = item.typeKey == "Texture" && typeof(Texture).IsAssignableFrom(mainType)
                || item.typeKey == "Material" && typeof(Material).IsAssignableFrom(mainType)
                || item.typeKey == "Mesh" && (typeof(Mesh).IsAssignableFrom(mainType)
                    || typeof(GameObject).IsAssignableFrom(mainType)
                    && AssetImporter.GetAtPath(item.path) is ModelImporter);
        }
        catch (Exception e)
        {
            if (failedAssetLoadWarnings.Add(item.path))
            {
                Debug.LogWarning("[VFX Resource Selector] 校验资源类型失败，已跳过：" + item.path + "\n" + e.Message);
            }
            return null;
        }
        if (!expectedTypeMatches)
        {
            return null;
        }

        Object loaded = null;
        try
        {
            if (!string.IsNullOrEmpty(item.subAssetName))
            {
                Object[] representations;
                if (!meshRepresentationsCache.TryGetValue(item.path, out representations))
                {
                    representations = AssetDatabase.LoadAllAssetRepresentationsAtPath(item.path);
                    if (representations == null) representations = new Object[0];
                    while (meshRepresentationsCache.Count >= MaxMeshFileCacheCount && meshRepresentationsCacheOrder.Count > 0)
                    {
                        meshRepresentationsCache.Remove(meshRepresentationsCacheOrder.Dequeue());
                    }
                    meshRepresentationsCache[item.path] = representations;
                    meshRepresentationsCacheOrder.Enqueue(item.path);
                }
                foreach (Object representation in representations)
                {
                    Mesh mesh = representation as Mesh;
                    if (mesh != null && mesh.name == item.subAssetName)
                    {
                        loaded = mesh;
                        break;
                    }
                }
                if (loaded == null)
                {
                    return null; // 子网格已不存在时不回退加载 FBX 主 GameObject
                }
            }
            if (loaded == null)
            {
                loaded = AssetDatabase.LoadAssetAtPath<Object>(item.path);
            }
        }
        catch (Exception e)
        {
            if (failedAssetLoadWarnings.Add(item.path))
            {
                Debug.LogWarning("[VFX Resource Selector] 读取资源失败：" + item.path + "\n" + e.Message);
            }
        }

        if (loaded != null && !IsLoadedAssetCompatible(item, loaded))
        {
            return null;
        }

        if (loaded != null)
        {
            // 贴图缩略图单独生成并持有小尺寸副本，不长期缓存全尺寸源图。
            if (loaded is Texture)
            {
                return loaded;
            }
            while (loadedAssetCache.Count >= MaxLoadedAssetCacheCount && loadedAssetCacheOrder.Count > 0)
            {
                loadedAssetCache.Remove(loadedAssetCacheOrder.Dequeue());
            }
            loadedAssetCache[key] = loaded;
            loadedAssetCacheOrder.Enqueue(key);
        }
        return loaded;
    }

    private static bool IsLoadedAssetCompatible(ResourceItem item, Object loaded)
    {
        if (item.typeKey == "Texture") return loaded is Texture;
        if (item.typeKey == "Material") return loaded is Material;
        if (item.typeKey == "Mesh") return loaded is Mesh;
        return false;
    }

    /// <summary>按帧预算获取资源：命中缓存不计数；新加载受 MaxThumbLoadsPerFrame 限制，超预算返回 null（先占位）。</summary>
    private Object GetAssetBudgeted(ResourceItem item)
    {
        if (item.assetLoaded)
        {
            return item.GetAsset();
        }
        string key = item.path + "|" + item.subAssetName;
        if (loadedAssetCache.ContainsKey(key))
        {
            Object cached = item.GetAsset();
            if (cached != null)
            {
                item.asset = cached;
                item.assetLoaded = true;
            }
            return cached;   // 已加载：命中缓存，不计预算
        }
        if (attemptedLoadKeys.Contains(key))
        {
            return null;              // 已尝试过且未成功（损坏/读取失败），本会话不再重试
        }
        if (thumbLoadBudget >= MaxThumbLoadsPerFrame)
        {
            thumbLoadPending = true;  // 本帧预算耗尽：先占位，并请求下一帧继续
            return null;
        }
        thumbLoadBudget++;
        Object result = item.GetAsset();   // 触发懒加载
        if (result == null)
        {
            attemptedLoadKeys.Add(key);    // 加载失败，标记避免下一帧反复重试
        }
        else
        {
            // 保留当前可见项自己的对象引用。共享 LRU 缓存容量较小，若可见项远多于
            // 缓存容量，每帧从列表头加载会不断淘汰头部，导致后面的资源永久饥饿。
            item.asset = result;
            item.assetLoaded = true;
        }
        return result;
    }

    // ================= 过滤 =================

    private void RefreshFiltered()
    {
        filteredItems.Clear();
        VFXSelectorSettings s = VFXSelectorSettings.Instance;
        string keyword = searchText.Trim();

        foreach (ResourceItem item in allItems)
        {
            if (activeType != "All" && item.typeKey != activeType)
            {
                continue;
            }

            string fileName = Path.GetFileNameWithoutExtension(item.path);

            // 通用模板不属于项目命名分类，筛选具体分类时也持续显示。
            if (item.isBuiltin)
            {
                if (keyword.Length == 0
                    || item.name.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0
                    || item.path.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    filteredItems.Add(item);
                }
                continue;
            }

            // 一级分类过滤
            if (activeL1 == VFXCategoryMatcher.AllCategory)
            {
                // 不限制
            }
            else if (activeL1 == VFXCategoryMatcher.Uncategorized)
            {
                string l1 = s.useCustomRules
                    ? VFXCategoryMatcher.MatchRuleLevel1(fileName, s.categoryRules)
                    : VFXCategoryMatcher.GetAutoLevel1(fileName);
                if (l1 != VFXCategoryMatcher.Uncategorized)
                {
                    continue;
                }
            }
            else
            {
                if (s.useCustomRules)
                {
                    string l1 = VFXCategoryMatcher.MatchRuleLevel1(fileName, s.categoryRules);
                    if (l1 != activeL1)
                    {
                        continue;
                    }
                    CategoryRule rule = s.categoryRules.Find(r => r.name == activeL1);
                    if (activeL2 != VFXCategoryMatcher.AllCategory)
                    {
                        if (VFXCategoryMatcher.MatchRuleLevel2(fileName, rule) != activeL2)
                        {
                            continue;
                        }
                    }
                    if (activeL3 != VFXCategoryMatcher.AllCategory)
                    {
                        SubCategoryRule sub = rule != null ? rule.subCategories.Find(x => x.name == activeL2) : null;
                        if (VFXCategoryMatcher.MatchRuleLevel3(fileName, rule, sub) != activeL3)
                        {
                            continue;
                        }
                        if (activeL4 != VFXCategoryMatcher.AllCategory)
                        {
                            SubSubCategoryRule subsub = sub != null
                                ? sub.subSubCategories.Find(y => y.name == activeL3)
                                : null;
                            if (VFXCategoryMatcher.MatchRuleLevel4(fileName, rule, sub, subsub) != activeL4)
                            {
                                continue;
                            }
                        }
                    }
                }
                else
                {
                    if (VFXCategoryMatcher.GetAutoLevel1(fileName) != activeL1)
                    {
                        continue;
                    }
                    if (activeL2 != VFXCategoryMatcher.AllCategory && VFXCategoryMatcher.GetAutoLevel2(fileName) != activeL2)
                    {
                        continue;
                    }
                    if (activeL3 != VFXCategoryMatcher.AllCategory && VFXCategoryMatcher.GetAutoLevel3(fileName) != activeL3)
                    {
                        continue;
                    }
                    if (activeL4 != VFXCategoryMatcher.AllCategory && VFXCategoryMatcher.GetAutoLevel4(fileName) != activeL4)
                    {
                        continue;
                    }
                }
            }

            // 搜索
            if (keyword.Length > 0
                && fileName.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) < 0
                && item.name.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) < 0
                && item.path.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            filteredItems.Add(item);
        }

        // 内置资源固定置顶，其余资源按名称排序，接近 Unity 原生 Select 的布局。
        // allItems 已在扫描后排序；按原顺序筛选即可，避免每次按键都再对大列表排序。
    }

    // ---- 自动分类列表缓存 ----
    // 资源量大时 GetLevelXCategories 每帧都是 O(N) 字符串解析，预览加载期的持续重绘会被它拖慢，
    // 自动模式下缓存结果，仅在重新扫描时失效（自定义规则数量少，不缓存以便实时编辑）。
    private List<string> autoL1Cache;
    private string autoL2Parent;
    private List<string> autoL2Cache;
    private string autoL3L1;
    private string autoL3L2;
    private List<string> autoL3Cache;
    private string autoL4L1;
    private string autoL4L2;
    private string autoL4L3;
    private List<string> autoL4Cache;

    /// <summary>网格标签样式（只创建一次，避免每帧 new GUIStyle 产生 GC）。</summary>
    private GUIStyle gridLabelStyle;

    /// <summary>并发中的 AssetPreview 请求，限制原生预览队列规模。</summary>
    private readonly List<ResourceItem> activePreviewRequests = new List<ResourceItem>();
    private readonly Dictionary<string, Object> loadedAssetCache = new Dictionary<string, Object>();
    private readonly Queue<string> loadedAssetCacheOrder = new Queue<string>();
    private readonly Dictionary<string, Object[]> meshRepresentationsCache = new Dictionary<string, Object[]>();
    private readonly Queue<string> meshRepresentationsCacheOrder = new Queue<string>();
    private readonly Queue<string> pendingMeshPaths = new Queue<string>();
    private double nextPreviewRepaintTime;
    private bool previewUpdateSubscribed;
    private double nextCachePruneTime;
    private int cachePruneCursor;
    private bool cachePrunePending;
    private bool meshScanUpdateSubscribed;
    private bool isScanningMeshes;
    private int totalMeshPaths;
    private int scannedMeshPaths;
    private double nextScanRepaintTime;
    private int meshScanErrorCount;
    private string firstMeshScanError;
    private readonly HashSet<string> failedAssetLoadWarnings = new HashSet<string>();

    private const int MaxLoadedAssetCacheCount = 12;
    private const int MaxMeshFileCacheCount = 4;
    private const int MaxConcurrentPreviewRequests = 24;
    private const double MaxPreviewWaitSeconds = 30.0;
    private const int MaxThumbLoadsPerFrame = 12;  // 提高可见项加载吞吐，同时限制单帧资源加载突发量
    private int thumbLoadBudget;                    // 本帧已新加载的资源计数（每帧在 DrawGrid 重置）
    private bool thumbLoadPending;                  // 本帧预算耗尽、还有可见项没加载到缩略图，请求下一帧继续
    private readonly HashSet<string> attemptedLoadKeys = new HashSet<string>();  // 加载失败过的项，本会话不再重试

    // ================= 分类列表 =================

    private void InvalidateCategoryCache()
    {
        autoL1Cache = null;
        autoL2Cache = null;
        autoL3Cache = null;
        autoL4Cache = null;
    }

    private List<string> GetLevel1Categories()
    {
        List<string> result = new List<string>();
        VFXSelectorSettings s = VFXSelectorSettings.Instance;

        if (s.useCustomRules)
        {
            foreach (CategoryRule rule in s.categoryRules)
            {
                if (!string.IsNullOrEmpty(rule.name) && !result.Contains(rule.name))
                {
                    result.Add(rule.name);
                }
            }
        }
        else
        {
            if (autoL1Cache == null)
            {
                HashSet<string> set = new HashSet<string>();
                foreach (ResourceItem item in allItems)
                {
                    if (activeType != "All" && item.typeKey != activeType)
                    {
                        continue;
                    }
                    string l1 = VFXCategoryMatcher.GetAutoLevel1(Path.GetFileNameWithoutExtension(item.path));
                    if (l1 != VFXCategoryMatcher.Uncategorized)
                    {
                        set.Add(l1);
                    }
                }
                autoL1Cache = new List<string>(set);
                autoL1Cache.Sort(StringComparer.OrdinalIgnoreCase);
            }
            result.AddRange(autoL1Cache);
        }

        result.Sort(StringComparer.OrdinalIgnoreCase);
        return result;
    }

    private List<string> GetLevel2Categories(string level1)
    {
        List<string> result = new List<string>();
        VFXSelectorSettings s = VFXSelectorSettings.Instance;

        if (s.useCustomRules)
        {
            CategoryRule rule = s.categoryRules.Find(r => r.name == level1);
            if (rule != null)
            {
                foreach (SubCategoryRule sub in rule.subCategories)
                {
                    if (!string.IsNullOrEmpty(sub.name) && !result.Contains(sub.name))
                    {
                        result.Add(sub.name);
                    }
                }
            }
        }
        else
        {
            if (autoL2Cache == null || autoL2Parent != level1)
            {
                HashSet<string> set = new HashSet<string>();
                foreach (ResourceItem item in allItems)
                {
                    if (activeType != "All" && item.typeKey != activeType)
                    {
                        continue;
                    }
                    string fileName = Path.GetFileNameWithoutExtension(item.path);
                    if (VFXCategoryMatcher.GetAutoLevel1(fileName) != level1)
                    {
                        continue;
                    }
                    string l2 = VFXCategoryMatcher.GetAutoLevel2(fileName);
                    if (l2.Length > 0)
                    {
                        set.Add(l2);
                    }
                }
                autoL2Parent = level1;
                autoL2Cache = new List<string>(set);
                autoL2Cache.Sort(StringComparer.OrdinalIgnoreCase);
            }
            result.AddRange(autoL2Cache);
        }

        result.Sort(StringComparer.OrdinalIgnoreCase);
        return result;
    }

    private List<string> GetLevel3Categories(string level1, string level2)
    {
        List<string> result = new List<string>();
        VFXSelectorSettings s = VFXSelectorSettings.Instance;

        if (s.useCustomRules)
        {
            CategoryRule rule = s.categoryRules.Find(r => r.name == level1);
            SubCategoryRule sub = rule != null ? rule.subCategories.Find(x => x.name == level2) : null;
            if (sub != null)
            {
                foreach (SubSubCategoryRule subsub in sub.subSubCategories)
                {
                    if (!string.IsNullOrEmpty(subsub.name) && !result.Contains(subsub.name))
                    {
                        result.Add(subsub.name);
                    }
                }
            }
        }
        else
        {
            if (autoL3Cache == null || autoL3L1 != level1 || autoL3L2 != level2)
            {
                HashSet<string> set = new HashSet<string>();
                foreach (ResourceItem item in allItems)
                {
                    if (activeType != "All" && item.typeKey != activeType)
                    {
                        continue;
                    }
                    string fileName = Path.GetFileNameWithoutExtension(item.path);
                    if (VFXCategoryMatcher.GetAutoLevel1(fileName) != level1)
                    {
                        continue;
                    }
                    if (VFXCategoryMatcher.GetAutoLevel2(fileName) != level2)
                    {
                        continue;
                    }
                    string l3 = VFXCategoryMatcher.GetAutoLevel3(fileName);
                    if (l3.Length > 0)
                    {
                        set.Add(l3);
                    }
                }
                autoL3L1 = level1;
                autoL3L2 = level2;
                autoL3Cache = new List<string>(set);
                autoL3Cache.Sort(StringComparer.OrdinalIgnoreCase);
            }
            result.AddRange(autoL3Cache);
        }

        result.Sort(StringComparer.OrdinalIgnoreCase);
        return result;
    }

    private List<string> GetLevel4Categories(string level1, string level2, string level3)
    {
        List<string> result = new List<string>();
        VFXSelectorSettings s = VFXSelectorSettings.Instance;

        if (s.useCustomRules)
        {
            CategoryRule rule = s.categoryRules.Find(r => r.name == level1);
            SubCategoryRule sub = rule != null ? rule.subCategories.Find(x => x.name == level2) : null;
            SubSubCategoryRule subsub = sub != null ? sub.subSubCategories.Find(y => y.name == level3) : null;
            if (subsub != null)
            {
                foreach (Level4CategoryRule l4 in subsub.level4Categories)
                {
                    if (!string.IsNullOrEmpty(l4.name) && !result.Contains(l4.name))
                    {
                        result.Add(l4.name);
                    }
                }
            }
        }
        else
        {
            if (autoL4Cache == null || autoL4L1 != level1 || autoL4L2 != level2 || autoL4L3 != level3)
            {
                HashSet<string> set = new HashSet<string>();
                foreach (ResourceItem item in allItems)
                {
                    if (activeType != "All" && item.typeKey != activeType)
                    {
                        continue;
                    }
                    string fileName = Path.GetFileNameWithoutExtension(item.path);
                    if (VFXCategoryMatcher.GetAutoLevel1(fileName) != level1)
                    {
                        continue;
                    }
                    if (VFXCategoryMatcher.GetAutoLevel2(fileName) != level2)
                    {
                        continue;
                    }
                    if (VFXCategoryMatcher.GetAutoLevel3(fileName) != level3)
                    {
                        continue;
                    }
                    string l4 = VFXCategoryMatcher.GetAutoLevel4(fileName);
                    if (l4.Length > 0)
                    {
                        set.Add(l4);
                    }
                }
                autoL4L1 = level1;
                autoL4L2 = level2;
                autoL4L3 = level3;
                autoL4Cache = new List<string>(set);
                autoL4Cache.Sort(StringComparer.OrdinalIgnoreCase);
            }
            result.AddRange(autoL4Cache);
        }

        result.Sort(StringComparer.OrdinalIgnoreCase);
        return result;
    }

    // ================= UI =================

    private void OnGUI()
    {
        if (Event.current.type == EventType.KeyDown && (Event.current.control || Event.current.command)
            && Event.current.keyCode == KeyCode.F)
        {
            GUI.FocusControl(SearchControlName);
            Event.current.Use();
        }
        if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Escape)
        {
            confirmed = false;
            Close();
            Event.current.Use();
            return;
        }

        DrawToolbar();
        DrawTypeBar();
        DrawCategoryBars();
        DrawSearchBar();
        DrawThumbnailSizeSlider();
        DrawGrid();
        DrawSettingsPanel();
        DrawFooter();
    }

    private void DrawToolbar()
    {
        EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
        if (GUILayout.Button(new GUIContent("刷新", "重新扫描项目资源"), EditorStyles.toolbarButton, GUILayout.Width(48)))
        {
            ScanResources();
            RefreshFiltered();
        }
        GUILayout.Label("共 " + filteredItems.Count + " / " + allItems.Count + " 项", EditorStyles.miniLabel);
        GUILayout.FlexibleSpace();
        if (GUILayout.Button(showSettings ? "收起设置" : "设置与分类", EditorStyles.toolbarButton, GUILayout.Width(80)))
        {
            showSettings = !showSettings;
        }
        EditorGUILayout.EndHorizontal();
    }

    private void DrawTypeBar()
    {
        EditorGUILayout.BeginHorizontal();
        if (typeLocked)
        {
            GUILayout.Label("类型: " + activeType, EditorStyles.boldLabel);
        }
        else
        {
            EditorGUILayout.LabelField("类型", GUILayout.Width(40));
            foreach (string t in allTypeTabs)
            {
                bool isActive = activeType == t;
                bool clicked = GUILayout.Toggle(isActive, t, "toolbarButton", GUILayout.MinWidth(56));
                if (clicked && !isActive)
                {
                    activeType = t;
                    activeL1 = VFXCategoryMatcher.AllCategory;
                    activeL2 = VFXCategoryMatcher.AllCategory;
                    activeL3 = VFXCategoryMatcher.AllCategory;
                    activeL4 = VFXCategoryMatcher.AllCategory;
                    InvalidateCategoryCache();
                    RefreshFiltered();
                }
            }
        }
        EditorGUILayout.EndHorizontal();
    }

    private void DrawCategoryBars()
    {
        // 一级分类栏
        List<string> l1s = GetLevel1Categories();
        List<string> l1Items = new List<string> { VFXCategoryMatcher.AllCategory };
        l1Items.AddRange(l1s);
        l1Items.Add(VFXCategoryMatcher.Uncategorized);
        DrawWrappedRow("一级", l1Items, activeL1, ref activeL1, 1);

        // 二级分类栏（选中具体一级分类时显示）
        if (activeL1 != VFXCategoryMatcher.AllCategory && activeL1 != VFXCategoryMatcher.Uncategorized)
        {
            List<string> l2s = GetLevel2Categories(activeL1);
            List<string> l2Items = new List<string> { VFXCategoryMatcher.AllCategory };
            l2Items.AddRange(l2s);
            DrawWrappedRow("二级", l2Items, activeL2, ref activeL2, 2);
        }

        // 三级分类栏（选中具体二级分类时显示）
        if (activeL1 != VFXCategoryMatcher.AllCategory && activeL1 != VFXCategoryMatcher.Uncategorized
            && activeL2 != VFXCategoryMatcher.AllCategory)
        {
            List<string> l3s = GetLevel3Categories(activeL1, activeL2);
            List<string> l3Items = new List<string> { VFXCategoryMatcher.AllCategory };
            l3Items.AddRange(l3s);
            DrawWrappedRow("三级", l3Items, activeL3, ref activeL3, 3);
        }

        // 四级分类栏（选中具体三级分类时显示）
        if (activeL1 != VFXCategoryMatcher.AllCategory && activeL1 != VFXCategoryMatcher.Uncategorized
            && activeL2 != VFXCategoryMatcher.AllCategory
            && activeL3 != VFXCategoryMatcher.AllCategory)
        {
            List<string> l4s = GetLevel4Categories(activeL1, activeL2, activeL3);
            List<string> l4Items = new List<string> { VFXCategoryMatcher.AllCategory };
            l4Items.AddRange(l4s);
            DrawWrappedRow("四级", l4Items, activeL4, ref activeL4, 4);
        }
    }

    /// <summary>
    /// 自动换行的分类按钮行：按钮排满一行后自动换到下一行，
    /// 分类数量多时不会溢出窗口右侧看不到。
    /// </summary>
    private void DrawWrappedRow(string label, List<string> items, string current, ref string activeTarget, int level)
    {
        GUIStyle style = "toolbarButton";
        const float labelW = 36f;
        const float gap = 4f;
        const float rowHeight = 22f;
        float maxWidth = position.width - 8f;

        // 测量每个按钮的宽度（按内容自适应，最小 48）
        GUIContent tmp = new GUIContent();
        float[] btnW = new float[items.Count];
        for (int i = 0; i < items.Count; i++)
        {
            tmp.text = items[i];
            btnW[i] = Mathf.Max(48f, style.CalcSize(tmp).x + 8f);
        }

        // 第一行：左侧画 label
        Rect rowRect = GUILayoutUtility.GetRect(maxWidth, rowHeight);
        GUI.Label(new Rect(rowRect.x, rowRect.y, labelW, rowHeight), label, EditorStyles.miniLabel);
        float lineX = labelW + gap;

        for (int i = 0; i < items.Count; i++)
        {
            // 超出可用宽度 → 换行
            if (lineX + btnW[i] > maxWidth)
            {
                rowRect = GUILayoutUtility.GetRect(maxWidth, rowHeight);
                lineX = 0f;
            }

            Rect btnRect = new Rect(rowRect.x + lineX, rowRect.y, btnW[i], rowHeight);
            bool isActive = current == items[i];
            bool clicked = GUI.Toggle(btnRect, isActive, items[i], style);
            if (clicked && !isActive)
            {
                activeTarget = items[i];
                if (level == 1)
                {
                    activeL2 = VFXCategoryMatcher.AllCategory;
                    activeL3 = VFXCategoryMatcher.AllCategory;
                    activeL4 = VFXCategoryMatcher.AllCategory;
                }
                else if (level == 2)
                {
                    activeL3 = VFXCategoryMatcher.AllCategory;
                    activeL4 = VFXCategoryMatcher.AllCategory;
                }
                else if (level == 3)
                {
                    activeL4 = VFXCategoryMatcher.AllCategory;
                }
                RefreshFiltered();
            }
            lineX += btnW[i] + gap;
        }
    }

    private void DrawSearchBar()
    {
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField("搜索", GUILayout.Width(40));
        GUI.SetNextControlName(SearchControlName);
        string newSearch = EditorGUILayout.TextField(searchText);
        if (newSearch != searchText)
        {
            searchText = newSearch;
            scroll = Vector2.zero;
            RefreshFiltered();
        }
        if (focusSearchOnNextGUI)
        {
            GUI.FocusControl(SearchControlName);
            focusSearchOnNextGUI = false;
        }
        if (searchText.Length > 0 && GUILayout.Button("×", GUILayout.Width(24)))
        {
            searchText = "";
            GUI.FocusControl(SearchControlName);
            scroll = Vector2.zero;
            RefreshFiltered();
        }
        EditorGUILayout.EndHorizontal();
    }

    private void DrawThumbnailSizeSlider()
    {
        EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
        GUILayout.Label("缩略图", EditorStyles.miniLabel, GUILayout.Width(46));
        GUILayout.Label(Mathf.RoundToInt(MinThumbnailSize).ToString(), EditorStyles.miniLabel, GUILayout.Width(22));
        float newSize = GUILayout.HorizontalSlider(
            thumbnailSize,
            MinThumbnailSize,
            MaxThumbnailSize,
            GUILayout.Width(150));
        GUILayout.Label(Mathf.RoundToInt(MaxThumbnailSize).ToString(), EditorStyles.miniLabel, GUILayout.Width(28));
        newSize = Mathf.Clamp(Mathf.Round(newSize), MinThumbnailSize, MaxThumbnailSize);
        if (!Mathf.Approximately(newSize, thumbnailSize))
        {
            thumbnailSize = newSize;
            EditorPrefs.SetFloat(ThumbnailSizePrefsKey, thumbnailSize);
            Repaint();
        }
        GUILayout.Label(Mathf.RoundToInt(thumbnailSize) + " px", EditorStyles.miniLabel, GUILayout.Width(42));
        GUILayout.FlexibleSpace();
        EditorGUILayout.EndHorizontal();
    }

    private void DrawGrid()
    {
        thumbLoadBudget = 0;   // 每帧重置缩略图加载预算
        thumbLoadPending = false;
        if (isScanningMeshes)
        {
            float progress = totalMeshPaths > 0 ? scannedMeshPaths / (float)totalMeshPaths : 1f;
            Rect progressRect = EditorGUILayout.GetControlRect(false, 18f);
            EditorGUI.ProgressBar(progressRect, progress,
                "正在扫描网格资源 " + scannedMeshPaths + " / " + totalMeshPaths);
        }
        else if (filteredItems.Count == 0)
        {
            EditorGUILayout.HelpBox(
                "当前条件下没有项目资源；仍可选择 None 或使用内置通用模板。\n可切换类型/分类，或点击“刷新”。",
                MessageType.Info);
        }

        float thumbSize = thumbnailSize;
        const float cellGap = 6f;
        const float labelHeight = 22f;
        const float padding = 8f;
        float cellW = thumbSize + cellGap;
        float cellH = thumbSize + labelHeight;
        int columns = Mathf.Max(1, Mathf.FloorToInt((position.width - padding * 2f) / cellW));
        int itemCount = filteredItems.Count + 1; // 第 0 项为 None
        int rowCount = Mathf.CeilToInt((float)itemCount / columns);

        // 布局得到滚动视口的真实矩形（高度准确，可见行计算不抖动）
        Rect scrollRect = GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none, GUILayout.ExpandHeight(true));
        float contentHeight = rowCount * cellH + padding * 2f;
        float contentWidth = scrollRect.width;

        if (autoScrollToSelectedOnOpen)
        {
            int selectedIndex = IndexOfSelectedItem();
            if (selectedIndex >= 0)
            {
                float selectedY = padding + (selectedIndex / columns) * cellH;
                scroll.y = Mathf.Max(0f, selectedY - (scrollRect.height - cellH) * 0.5f);
                autoScrollToSelectedOnOpen = false;
            }
            else if (!isScanningMeshes)
            {
                // 当前资源不在本选择器支持的资源列表中。
                autoScrollToSelectedOnOpen = false;
            }
        }

        scroll = GUI.BeginScrollView(scrollRect, scroll, new Rect(0f, 0f, contentWidth, contentHeight));

        bool closeAfterGrid = HandleGridKeyboard(columns, rowCount, cellH, scrollRect);

        // 可见行范围（基于真实视口高度，与 Unity 原生 Select 一致只绘制可见项）
        int firstRow = Mathf.Max(0, Mathf.FloorToInt(scroll.y / cellH));
        int lastRow = Mathf.Min(rowCount - 1, Mathf.FloorToInt((scroll.y + scrollRect.height) / cellH));

        // 标签样式：居中、超出截断（像原生 Select）；只创建一次并缓存
        if (gridLabelStyle == null)
        {
            gridLabelStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                alignment = TextAnchor.UpperCenter,
                clipping = TextClipping.Clip,
                wordWrap = false
            };
        }

        for (int row = firstRow; row <= lastRow && !closeAfterGrid; row++)
        {
            for (int col = 0; col < columns; col++)
            {
                int i = row * columns + col;
                if (i >= itemCount)
                {
                    break;
                }
                ResourceItem item = i == 0 ? null : filteredItems[i - 1];

                float x = padding + col * cellW;
                float y = padding + row * cellH;
                Rect cell = new Rect(x, y, thumbSize, thumbSize);
                Rect cellRect = new Rect(cell.x - 2f, cell.y - 2f, cell.width + 4f, cell.height + 4f);
                Rect itemRect = new Rect(cell.x - 2f, cell.y - 2f, cell.width + 4f, cell.height + labelHeight + 6f);

                bool isSelected = i == 0 ? selectedNone : IsSameItem(selectedItem, item);
                if (isSelected)
                {
                    EditorGUI.DrawRect(cellRect, new Color(0.25f, 0.55f, 1f, 0.9f));
                }

                // Unity 2021 的 GUI.Button 在 MouseUp 阶段可能把双击 clickCount 重置为 1，
                // 因此在 MouseDown 阶段直接判断双击，确保第二次点击必定关闭窗口。
                if (Event.current.type == EventType.MouseDown
                    && Event.current.button == 0
                    && Event.current.clickCount >= 2
                    && itemRect.Contains(Event.current.mousePosition))
                {
                    SelectGridItem(i);
                    confirmed = true;
                    GUI.FocusControl(null);
                    Event.current.Use();
                    closeAfterGrid = true;
                    break;
                }

                if (GUI.Button(itemRect, GUIContent.none, GUIStyle.none))
                {
                    SelectGridItem(i);
                    GUI.FocusControl(null);
                }

                Rect thumbRect = new Rect(cell.x + 2f, cell.y + 2f, cell.width - 4f, cell.height - 4f);
                if (item == null)
                {
                    GUI.Label(thumbRect, "—", EditorStyles.centeredGreyMiniLabel);
                }
                else
                {
                    DrawThumbnail(item, thumbRect);
                }

                GUI.Label(
                    new Rect(cell.x, cell.y + cell.height + 2f, cell.width, labelHeight),
                    item == null ? "None" : item.name,
                    gridLabelStyle);
            }
            if (closeAfterGrid)
            {
                break;
            }
        }

        ReleaseOffscreenItemCaches();

        GUI.EndScrollView();

        if (closeAfterGrid)
        {
            Close();
            return;
        }

        // 仅在可见预览加载期间低频轮询；不在 OnGUI 中连续 Repaint，避免空转占用主线程。
        if (activePreviewRequests.Count > 0 && !previewUpdateSubscribed)
        {
            EditorApplication.update += PollPreviewLoading;
            previewUpdateSubscribed = true;
        }

        // 本帧缩略图预算耗尽、同屏还有可见项没加载完：请求下一帧继续，直到整屏加载完成（加载完自动停止，不空转）。
        if (thumbLoadPending)
        {
            Repaint();
        }
    }

    private void PollPreviewLoading()
    {
        if (this == null || activePreviewRequests.Count == 0)
        {
            EditorApplication.update -= PollPreviewLoading;
            previewUpdateSubscribed = false;
            return;
        }

        if (EditorApplication.timeSinceStartup >= nextPreviewRepaintTime)
        {
            nextPreviewRepaintTime = EditorApplication.timeSinceStartup + 0.1;
            bool completedAny = false;
            for (int i = activePreviewRequests.Count - 1; i >= 0; i--)
            {
                ResourceItem item = activePreviewRequests[i];
                Object asset = item != null ? item.asset : null;
                if (asset == null)
                {
                    CompletePreviewRequest(item, null);
                    completedAny = true;
                    continue;
                }
                if (IsPreviewLoading(item, asset))
                {
                    if (EditorApplication.timeSinceStartup - item.previewRequestStartedAt < MaxPreviewWaitSeconds)
                    {
                        continue;
                    }
                    CompletePreviewRequest(item, null);
                    completedAny = true;
                    continue;
                }

                Texture2D preview = GetAssetPreviewSafe(item, asset);
                if (preview != null)
                {
                    CompletePreviewRequest(item, preview);
                    completedAny = true;
                }
                else if (item.previewRequestAttempts < 2)
                {
                    // 个别 Unity 版本首次返回 null 且未报告 loading；只允许一次重试。
                    item.previewRequestAttempts++;
                    Texture2D retryPreview = GetAssetPreviewSafe(item, asset);
                    if (retryPreview != null)
                    {
                        CompletePreviewRequest(item, retryPreview);
                        completedAny = true;
                    }
                }
                else
                {
                    CompletePreviewRequest(item, null);
                    completedAny = true;
                }
            }

            if (completedAny)
            {
                Repaint();
            }
            if (activePreviewRequests.Count == 0)
            {
                EditorApplication.update -= PollPreviewLoading;
                previewUpdateSubscribed = false;
            }
        }
    }

    /// <summary>提供与 Unity 资源选择器一致的方向键浏览和回车确认。</summary>
    private bool HandleGridKeyboard(int columns, int rowCount, float cellH, Rect scrollRect)
    {
        Event evt = Event.current;
        if (evt.type != EventType.KeyDown)
        {
            return false;
        }
        int itemCount = filteredItems.Count + 1;

        bool searchFocused = GUI.GetNameOfFocusedControl() == SearchControlName;
        if (searchFocused && evt.keyCode == KeyCode.DownArrow)
        {
            GUI.FocusControl(null);
            if (IndexOfSelectedItem() < 0)
            {
                SelectGridItem(0);
            }
            EnsureSelectionVisible(columns, cellH, scrollRect.height);
            evt.Use();
            Repaint();
            return false;
        }
        if (searchFocused && (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter)
            && IndexOfSelectedItem() >= 0)
        {
            confirmed = true;
            evt.Use();
            return true;
        }
        if (searchFocused)
        {
            return false;
        }

        int index = IndexOfSelectedItem();
        if (index < 0 && (evt.keyCode == KeyCode.DownArrow || evt.keyCode == KeyCode.UpArrow
            || evt.keyCode == KeyCode.LeftArrow || evt.keyCode == KeyCode.RightArrow))
        {
            SelectGridItem(0);
            EnsureSelectionVisible(columns, cellH, scrollRect.height);
            evt.Use();
            Repaint();
            return false;
        }
        int next = index;
        switch (evt.keyCode)
        {
            case KeyCode.DownArrow: next = Mathf.Min(itemCount - 1, index + columns); break;
            case KeyCode.UpArrow: next = Mathf.Max(0, index - columns); break;
            case KeyCode.RightArrow: next = Mathf.Min(itemCount - 1, index + 1); break;
            case KeyCode.LeftArrow: next = Mathf.Max(0, index - 1); break;
            case KeyCode.Return:
            case KeyCode.KeypadEnter:
                if (index >= 0)
                {
                    confirmed = true;
                    evt.Use();
                    return true;
                }
                return false;
            default: return false;
        }

        SelectGridItem(next);
        EnsureSelectionVisible(columns, cellH, scrollRect.height);
        evt.Use();
        Repaint();
        return false;
    }

    private int IndexOfSelectedItem()
    {
        if (selectedNone) return 0;
        if (selectedItem == null) return -1;
        for (int i = 0; i < filteredItems.Count; i++)
        {
            if (IsSameItem(selectedItem, filteredItems[i])) return i + 1;
        }
        return -1;
    }

    private void SelectGridItem(int index)
    {
        if (index == 0)
        {
            selectedItem = null;
            selectedNone = true;
            if (onUpdated != null) onUpdated(null);
            return;
        }

        selectedItem = filteredItems[index - 1];
        selectedNone = false;
        if (onUpdated != null) onUpdated(selectedItem.GetAsset());
    }

    private void EnsureSelectionVisible(int columns, float cellH, float viewportHeight)
    {
        int index = IndexOfSelectedItem();
        if (index < 0) return;
        float y = 8f + (index / columns) * cellH;
        if (y < scroll.y) scroll.y = y;
        else if (y + cellH > scroll.y + viewportHeight) scroll.y = y + cellH - viewportHeight;
    }

    /// <summary>判断两个资源项是否同一个（FBX 内 Mesh 子资产路径相同，必须额外比较子资产名）。</summary>
    private static bool IsSameItem(ResourceItem a, ResourceItem b)
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }
        if (a == null || b == null)
        {
            return false;
        }
        if (a.assetLoaded && b.assetLoaded && a.asset != null && a.asset == b.asset)
        {
            return true; // 内置资源没有 AssetDatabase 路径，用对象实例匹配
        }
        return a.path == b.path && a.subAssetName == b.subAssetName;
    }

    private void DrawThumbnail(ResourceItem item, Rect rect)
    {
        item.lastVisibleTime = EditorApplication.timeSinceStartup;

        // 内置模板：资源已加载，直接绘制（内置贴图直接画，内置网格走预览）。
        if (item.isBuiltin)
        {
            Object builtin = item.GetAsset();
            Texture builtinTex = builtin as Texture;
            if (builtinTex != null)
            {
                GUI.DrawTexture(rect, builtinTex, ScaleMode.ScaleToFit);
                return;
            }
            DrawPreview(item, builtin, rect);
            return;
        }

        if (item.preview != null)
        {
            GUI.DrawTexture(rect, item.preview, ScaleMode.ScaleToFit);
            return;
        }
        if (item.previewRequestStarted && !item.previewRequestInFlight)
        {
            DrawPreviewFallback(item, rect);
            return;
        }
        if (activePreviewRequests.Count >= MaxConcurrentPreviewRequests && !item.previewRequestInFlight)
        {
            DrawPreviewFallback(item, rect);
            return;
        }

        // 贴图 / 材质 / 网格：按帧预算加载，超预算先占位、下一帧继续。
        Object asset = GetAssetBudgeted(item);
        if (asset == null)
        {
            GUI.Label(rect, item.typeKey, EditorStyles.centeredGreyMiniLabel);
            return;
        }
        DrawPreview(item, asset, rect);
    }

    /// <summary>贴图 / 材质 / 网格预览：限制并发预览数，并为结果保留稳定的小尺寸副本。</summary>
    private void DrawPreview(ResourceItem item, Object asset, Rect rect)
    {
        // 优先用当前资源项持有的副本，避免 Unity 回收全局预览纹理后退回图标。
        if (item.preview != null)
        {
            GUI.DrawTexture(rect, item.preview, ScaleMode.ScaleToFit);
            return;
        }

        // 贴图直接生成 128px 以内的独立缩略图，显示真实贴图内容，同时避免长时间持有原始大图。
        Texture2D sourceTexture = asset as Texture2D;
        if (sourceTexture != null && !item.textureCopyFailed)
        {
            item.preview = CopyPreviewTexture(sourceTexture);
            if (item.preview != null)
            {
                ReleaseItemAsset(item);
                GUI.DrawTexture(rect, item.preview, ScaleMode.ScaleToFit);
                return;
            }
            item.textureCopyFailed = true;
        }

        if (item.fallbackThumbnail == null)
        {
            item.fallbackThumbnail = GetMiniThumbnailSafe(item, asset);
        }

        if (!item.previewRequestStarted && activePreviewRequests.Count < MaxConcurrentPreviewRequests)
        {
            item.previewRequestStarted = true;
            item.previewRequestInFlight = true;
            item.previewRequestAttempts = 1;
            item.previewRequestStartedAt = EditorApplication.timeSinceStartup;
            activePreviewRequests.Add(item);
            Texture2D preview = GetAssetPreviewSafe(item, asset);
            if (preview != null)
            {
                CompletePreviewRequest(item, preview);
                if (item.preview != null)
                {
                    GUI.DrawTexture(rect, item.preview, ScaleMode.ScaleToFit);
                    return;
                }
            }
        }

        if (activePreviewRequests.Count > 0 && !previewUpdateSubscribed)
        {
            EditorApplication.update += PollPreviewLoading;
            previewUpdateSubscribed = true;
        }

        DrawPreviewFallback(item, rect);
    }

    private void CompletePreviewRequest(ResourceItem item, Texture2D sourcePreview)
    {
        if (item == null)
        {
            return;
        }
        if (item.previewRequestInFlight)
        {
            item.previewRequestInFlight = false;
            activePreviewRequests.Remove(item);
        }
        if (sourcePreview != null)
        {
            Texture2D stablePreview = CopyPreviewTexture(sourcePreview);
            if (item.scanGeneration == scanGeneration)
            {
                item.preview = stablePreview;
            }
            else if (stablePreview != null)
            {
                DestroyImmediate(stablePreview);
            }
        }
        ReleaseItemAsset(item);
    }

    private Texture2D GetAssetPreviewSafe(ResourceItem item, Object asset)
    {
        try
        {
            return asset != null ? AssetPreview.GetAssetPreview(asset) : null;
        }
        catch (Exception e)
        {
            LogPreviewErrorOnce(item, e);
            return null;
        }
    }

    private Texture2D GetMiniThumbnailSafe(ResourceItem item, Object asset)
    {
        try
        {
            return asset != null ? AssetPreview.GetMiniThumbnail(asset) : null;
        }
        catch (Exception e)
        {
            LogPreviewErrorOnce(item, e);
            return null;
        }
    }

    private bool IsPreviewLoading(ResourceItem item, Object asset)
    {
        try
        {
            return asset != null && AssetPreview.IsLoadingAssetPreview(asset.GetInstanceID());
        }
        catch (Exception e)
        {
            LogPreviewErrorOnce(item, e);
            return false;
        }
    }

    private void LogPreviewErrorOnce(ResourceItem item, Exception exception)
    {
        string path = item != null ? item.path : "未知资源";
        if (failedAssetLoadWarnings.Add("preview:" + path))
        {
            Debug.LogWarning("[VFX Resource Selector] 生成预览失败：" + path + "\n" + exception.Message);
        }
    }

    private static void ReleaseItemAsset(ResourceItem item)
    {
        if (item == null || item.isBuiltin)
        {
            return;
        }
        item.asset = null;
        item.assetLoaded = false;
    }

    private static void DrawPreviewFallback(ResourceItem item, Rect rect)
    {
        if (item.fallbackThumbnail != null)
        {
            GUI.DrawTexture(rect, item.fallbackThumbnail, ScaleMode.ScaleToFit);
        }
        else
        {
            GUI.Label(rect, item.typeKey, EditorStyles.centeredGreyMiniLabel);
        }
    }

    /// <summary>定期释放离开视口的资源和预览引用；当前可见项不受共享 LRU 容量影响。</summary>
    private void ReleaseOffscreenItemCaches()
    {
        double now = EditorApplication.timeSinceStartup;
        if (!cachePrunePending)
        {
            if (now < nextCachePruneTime)
            {
                return;
            }
            cachePrunePending = true;
            cachePruneCursor = 0;
        }

        int released = 0;
        while (cachePruneCursor < allItems.Count && released < 8)
        {
            ResourceItem item = allItems[cachePruneCursor++];
            if (!item.isBuiltin && !item.previewRequestInFlight && now - item.lastVisibleTime > 1.0)
            {
                item.asset = null;
                item.assetLoaded = false;
                if (item.preview != null)
                {
                    DestroyImmediate(item.preview);
                }
                item.preview = null;
                item.textureCopyFailed = false;
                item.previewRequestStarted = false;
                item.previewRequestAttempts = 0;
                item.previewRequestStartedAt = 0.0;
                item.fallbackThumbnail = null;
                released++;
            }
        }

        if (cachePruneCursor >= allItems.Count)
        {
            cachePrunePending = false;
            cachePruneCursor = 0;
            nextCachePruneTime = now + 1.0;
        }
        else
        {
            // 每帧最多销毁 8 张自有缩略图，避免滚动离屏时一次性释放大量原生纹理。
            Repaint();
        }
    }

    private static Texture2D CopyPreviewTexture(Texture2D source)
    {
        if (source == null)
        {
            return null;
        }

        const int maxThumbnailSize = 128;
        float scale = Mathf.Min(1f, maxThumbnailSize / (float)Mathf.Max(source.width, source.height));
        int width = Mathf.Max(1, Mathf.RoundToInt(source.width * scale));
        int height = Mathf.Max(1, Mathf.RoundToInt(source.height * scale));
        RenderTexture temporary = null;
        RenderTexture previous = RenderTexture.active;
        Texture2D copy = null;
        try
        {
            temporary = RenderTexture.GetTemporary(width, height, 0,
                RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            Graphics.Blit(source, temporary);
            RenderTexture.active = temporary;
            copy = new Texture2D(width, height, TextureFormat.RGBA32, false, true)
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            copy.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
            copy.Apply(false, true);
            return copy;
        }
        catch
        {
            if (copy != null)
            {
                DestroyImmediate(copy);
            }
            return null;
        }
        finally
        {
            RenderTexture.active = previous;
            if (temporary != null)
            {
                RenderTexture.ReleaseTemporary(temporary);
            }
        }
    }

    private void DestroyOwnedPreviews()
    {
        foreach (ResourceItem item in allItems)
        {
            if (item.preview != null)
            {
                DestroyImmediate(item.preview);
                item.preview = null;
            }
        }
    }

    private void DrawFooter()
    {
        EditorGUILayout.Space(4);
        EditorGUILayout.BeginHorizontal();
        if (selectedNone)
        {
            GUILayout.Label("选中: None（清空字段）", EditorStyles.boldLabel);
        }
        else if (selectedItem != null)
        {
            GUILayout.Label("选中: " + selectedItem.name, EditorStyles.boldLabel, GUILayout.Width(220));
            GUILayout.Label(selectedItem.path, EditorStyles.miniLabel);
        }
        else
        {
            GUILayout.Label("未选中资源", EditorStyles.miniLabel);
        }
        GUILayout.FlexibleSpace();
        using (new EditorGUI.DisabledScope(selectedItem == null || selectedNone || selectedItem.isBuiltin))
        {
            if (GUILayout.Button(new GUIContent("定位", "在 Project 窗口中选中并高亮此资源"), GUILayout.Width(70)))
            {
                LocateSelectedAsset();
            }
        }
        EditorGUILayout.EndHorizontal();
    }

    private void LocateSelectedAsset()
    {
        if (selectedItem == null || selectedItem.isBuiltin)
        {
            return;
        }

        Object asset = selectedItem.GetAsset();
        if (asset == null || string.IsNullOrEmpty(AssetDatabase.GetAssetPath(asset)))
        {
            return;
        }

        Selection.activeObject = asset;
        EditorGUIUtility.PingObject(asset);
    }

    // ================= 设置与分类规则 =================

    private void DrawSettingsPanel()
    {
        if (!showSettings)
        {
            return;
        }

        VFXSelectorSettings s = VFXSelectorSettings.Instance;

        EditorGUILayout.Space(4);
        EditorGUILayout.LabelField("全局设置", EditorStyles.boldLabel);
        EditorGUI.BeginChangeCheck();

        s.interceptEnabled = EditorGUILayout.Toggle("启用自定义选择器（全局接管 Select）", s.interceptEnabled);
        EditorGUILayout.LabelField("接管类型（其他类型放行给 Unity 默认）", EditorStyles.miniLabel);
        s.interceptTextures = EditorGUILayout.Toggle("贴图 Texture", s.interceptTextures);
        s.interceptMaterials = EditorGUILayout.Toggle("材质 Material", s.interceptMaterials);
        s.interceptMeshes = EditorGUILayout.Toggle("网格 Mesh", s.interceptMeshes);

        EditorGUILayout.Space(4);
        s.useCustomRules = EditorGUILayout.Toggle("使用自定义分类规则", s.useCustomRules);

        if (EditorGUI.EndChangeCheck())
        {
            VFXSelectorSettings.Save();
            InvalidateCategoryCache();
            RefreshFiltered();
        }

        if (s.useCustomRules)
        {
            DrawRulesEditor(s);
        }
        else
        {
            EditorGUILayout.HelpBox(
                "自动分类：一级 = 文件名第一个 “_” 前的内容；二级 = 前两个 “_” 之间；三级 = 前三个 “_” 之间；四级 = 前四个 “_” 之间。\n" +
                "例如：EF_Fire_Hit_Core.png → 一级 EF、二级 Fire、三级 Hit、四级 Core；无 “_” 归入未分类。",
                MessageType.None);
        }
    }

    private void DrawRulesEditor(VFXSelectorSettings s)
    {
        EditorGUILayout.Space(4);
        EditorGUILayout.LabelField("自定义分类规则（文件名前缀匹配）", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "一级前缀匹配文件名开头；二级/三级/四级前缀支持只填本段名（如 Fire），自动在上级前缀之后匹配。\n" +
            "例如：一级 EF、二级 VoiceNationalWar、三级 Mask、四级 Core → 匹配 EF_VoiceNationalWar_Mask_Core_xxx。\n" +
            "不匹配任何规则则归入“未分类”。",
            MessageType.None);

        for (int i = 0; i < s.categoryRules.Count; i++)
        {
            CategoryRule rule = s.categoryRules[i];

            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(8);
            rule.name = EditorGUILayout.TextField(rule.name, GUILayout.Width(120));
            EditorGUILayout.LabelField("前缀", GUILayout.Width(36));
            rule.prefix = EditorGUILayout.TextField(rule.prefix);
            bool expanded = expandedRules.Contains(i);
            bool newExpanded = EditorGUILayout.Foldout(expanded, "二级", true);
            if (newExpanded != expanded)
            {
                if (newExpanded) expandedRules.Add(i);
                else expandedRules.Remove(i);
            }
            if (GUILayout.Button("删除", GUILayout.Width(50)))
            {
                s.categoryRules.RemoveAt(i);
                expandedRules.Remove(i);
                i--;
                VFXSelectorSettings.Save();
                RefreshFiltered();
                continue;
            }
            EditorGUILayout.EndHorizontal();

            if (expanded)
            {
                DrawSubRulesEditor(rule, s, i);
            }
        }

        if (GUILayout.Button("+ 添加一级分类", GUILayout.Width(120)))
        {
            s.categoryRules.Add(new CategoryRule());
            VFXSelectorSettings.Save();
        }

        EditorGUILayout.Space(4);
    }

    private void DrawSubRulesEditor(CategoryRule rule, VFXSelectorSettings s, int ruleIndex)
    {
        for (int j = 0; j < rule.subCategories.Count; j++)
        {
            SubCategoryRule sub = rule.subCategories[j];
            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(24);
            sub.name = EditorGUILayout.TextField(sub.name, GUILayout.Width(100));
            EditorGUILayout.LabelField("前缀", GUILayout.Width(36));
            sub.prefix = EditorGUILayout.TextField(sub.prefix);
            int subKey = ruleIndex * 1000 + j;
            bool expanded = expandedSubRules.Contains(subKey);
            bool newExpanded = EditorGUILayout.Foldout(expanded, "三级", true);
            if (newExpanded != expanded)
            {
                if (newExpanded) expandedSubRules.Add(subKey);
                else expandedSubRules.Remove(subKey);
            }
            if (GUILayout.Button("删除", GUILayout.Width(50)))
            {
                rule.subCategories.RemoveAt(j);
                j--;
                VFXSelectorSettings.Save();
                RefreshFiltered();
                continue;
            }
            EditorGUILayout.EndHorizontal();

            if (expanded)
            {
                DrawSubSubRulesEditor(sub, s, ruleIndex, j);
            }
        }

        EditorGUILayout.BeginHorizontal();
        GUILayout.Space(24);
        if (GUILayout.Button("+ 添加二级分类", GUILayout.Width(110)))
        {
            rule.subCategories.Add(new SubCategoryRule());
            VFXSelectorSettings.Save();
        }
        EditorGUILayout.EndHorizontal();
    }

    private void DrawSubSubRulesEditor(SubCategoryRule sub, VFXSelectorSettings s, int ruleIndex, int subIndex)
    {
        for (int k = 0; k < sub.subSubCategories.Count; k++)
        {
            SubSubCategoryRule subsub = sub.subSubCategories[k];
            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(40);
            subsub.name = EditorGUILayout.TextField(subsub.name, GUILayout.Width(90));
            EditorGUILayout.LabelField("前缀", GUILayout.Width(36));
            subsub.prefix = EditorGUILayout.TextField(subsub.prefix);
            int subsubKey = ruleIndex * 1000000 + subIndex * 1000 + k;
            bool expandedL4 = expandedSubSubRules.Contains(subsubKey);
            bool newExpandedL4 = EditorGUILayout.Foldout(expandedL4, "四级", true);
            if (newExpandedL4 != expandedL4)
            {
                if (newExpandedL4) expandedSubSubRules.Add(subsubKey);
                else expandedSubSubRules.Remove(subsubKey);
            }
            if (GUILayout.Button("删除", GUILayout.Width(50)))
            {
                sub.subSubCategories.RemoveAt(k);
                expandedSubSubRules.Remove(subsubKey);
                k--;
                VFXSelectorSettings.Save();
                RefreshFiltered();
            }
            EditorGUILayout.EndHorizontal();

            if (expandedL4)
            {
                DrawLevel4RulesEditor(subsub);
            }
        }

        EditorGUILayout.BeginHorizontal();
        GUILayout.Space(40);
        if (GUILayout.Button("+ 添加三级分类", GUILayout.Width(110)))
        {
            sub.subSubCategories.Add(new SubSubCategoryRule());
            VFXSelectorSettings.Save();
        }
        EditorGUILayout.EndHorizontal();
    }

    private void DrawLevel4RulesEditor(SubSubCategoryRule subsub)
    {
        for (int m = 0; m < subsub.level4Categories.Count; m++)
        {
            Level4CategoryRule l4 = subsub.level4Categories[m];
            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(56);
            l4.name = EditorGUILayout.TextField(l4.name, GUILayout.Width(80));
            EditorGUILayout.LabelField("前缀", GUILayout.Width(36));
            l4.prefix = EditorGUILayout.TextField(l4.prefix);
            if (GUILayout.Button("删除", GUILayout.Width(50)))
            {
                subsub.level4Categories.RemoveAt(m);
                m--;
                VFXSelectorSettings.Save();
                RefreshFiltered();
            }
            EditorGUILayout.EndHorizontal();
        }

        EditorGUILayout.BeginHorizontal();
        GUILayout.Space(56);
        if (GUILayout.Button("+ 添加四级分类", GUILayout.Width(110)))
        {
            subsub.level4Categories.Add(new Level4CategoryRule());
            VFXSelectorSettings.Save();
        }
        EditorGUILayout.EndHorizontal();
    }

    // ================= 关闭与回调 =================

    private void OnLostFocus()
    {
        // 原生对象选择器点击弹窗外会提交当前高亮项并关闭。
        if (!canCloseOnLostFocus)
        {
            return;
        }
        if (selectedItem != null || selectedNone)
        {
            confirmed = true;
        }
        Close();
    }

    private void OnDisable()
    {
        if (previewUpdateSubscribed)
        {
            EditorApplication.update -= PollPreviewLoading;
            previewUpdateSubscribed = false;
        }
        if (meshScanUpdateSubscribed)
        {
            EditorApplication.update -= ProcessMeshScan;
            meshScanUpdateSubscribed = false;
        }
        DestroyOwnedPreviews();
        if (onClosed != null)
        {
            Object result = null;
            bool accepted = confirmed;

            Object selectedAsset = selectedItem != null ? selectedItem.GetAsset() : null;
            if (accepted && selectedNone)
            {
                result = null;
            }
            else if (accepted && selectedAsset != null && TypeMatches(selectedAsset))
            {
                result = selectedAsset;
            }
            else
            {
                accepted = false;
            }

            Action<Object, bool> callback = onClosed;
            onClosed = null;
            // 注意：Unity 的回调签名为 (Object obj, bool canceled)，第二个参数是“是否取消”，需取反
            callback(result, !accepted);
        }

        if (s_Instance == this)
        {
            s_Instance = null;
        }
        loadedAssetCache.Clear();
        loadedAssetCacheOrder.Clear();
        meshRepresentationsCache.Clear();
        meshRepresentationsCacheOrder.Clear();
        pendingMeshPaths.Clear();
        activePreviewRequests.Clear();
        failedAssetLoadWarnings.Clear();
        attemptedLoadKeys.Clear();
    }

    private bool TypeMatches(Object obj)
    {
        if (allowedTypes == null || allowedTypes.Count == 0)
        {
            return true;
        }
        foreach (string t in allowedTypes)
        {
            if (t == "Texture" && obj is Texture) return true;
            if (t == "Material" && obj is Material) return true;
            if (t == "Mesh" && obj is Mesh) return true;
        }
        return false;
    }

    private static List<string> ResolveAllowedTypes(IEnumerable<string> requiredTypeNames)
    {
        List<string> result = new List<string>();
        if (requiredTypeNames == null)
        {
            return result;
        }
        foreach (string n in requiredTypeNames)
        {
            if (n.IndexOf("Texture", StringComparison.OrdinalIgnoreCase) >= 0 && !result.Contains("Texture"))
            {
                result.Add("Texture");
            }
            else if (n.IndexOf("Material", StringComparison.OrdinalIgnoreCase) >= 0 && !result.Contains("Material"))
            {
                result.Add("Material");
            }
            else if (n.IndexOf("Mesh", StringComparison.OrdinalIgnoreCase) >= 0 && !result.Contains("Mesh"))
            {
                result.Add("Mesh");
            }
        }
        return result;
    }

    private static string GetTypeKey(Object obj)
    {
        if (obj is Texture) return "Texture";
        if (obj is Material) return "Material";
        if (obj is Mesh) return "Mesh";
        return "All";
    }
}
