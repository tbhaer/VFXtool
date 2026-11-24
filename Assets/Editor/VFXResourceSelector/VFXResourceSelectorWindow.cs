using System;
using System.Collections.Generic;
using System.IO;
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

    // ---- 设置面板 ----
    private bool showSettings;
    private readonly HashSet<int> expandedRules = new HashSet<int>();
    private readonly HashSet<int> expandedSubRules = new HashSet<int>();
    private readonly HashSet<int> expandedSubSubRules = new HashSet<int>();

    private static VFXResourceSelectorWindow s_Instance;

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
        public string name;
        public string typeKey;      // "Texture" / "Material" / "Mesh"
        public bool assetLoaded;
        public bool isBuiltin;
        public int builtinOrder;

        /// <summary>按需加载资源对象并缓存。只在绘制/选择时调用，避免扫描时加载全部资源导致卡顿或崩溃。</summary>
        public Object GetAsset()
        {
            if (!assetLoaded)
            {
                if (!string.IsNullOrEmpty(subAssetName))
                {
                    // 子资产（如 FBX 内的 Mesh）：从子资产中按名字匹配
                    Object[] reps = AssetDatabase.LoadAllAssetRepresentationsAtPath(path);
                    foreach (Object o in reps)
                    {
                        if (o != null && o is Mesh && o.name == subAssetName)
                        {
                            asset = o;
                            break;
                        }
                    }
                }
                if (asset == null)
                {
                    asset = AssetDatabase.LoadAssetAtPath<Object>(path);
                }
                assetLoaded = true;
            }
            return asset;
        }
    }

    private void ScanResources()
    {
        allItems.Clear();

        // 分类浏览时一次扫描三种资源，切换类型标签只做内存过滤。
        // 字段锁定为单一类型时只扫描该类型，避免无关资源拖慢原生字段选择。
        if (typeLocked && activeType == "Texture") AddType<Texture>("Texture");
        else if (typeLocked && activeType == "Material") AddType<Material>("Material");
        else if (typeLocked && activeType == "Mesh") AddType<Mesh>("Mesh");
        else
        {
            AddType<Texture>("Texture");
            AddType<Material>("Material");
            AddType<Mesh>("Mesh");
        }
        builtinOrder = 0;
        builtinAssetKeys.Clear();
        AddBuiltinTemplates();

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

        // 关键：AssetPreview 默认只缓存 100 张预览图，资源多时预览会互相挤掉，
        // 表现为缩略图反复在占位符与预览图之间闪烁、大量预览刷不出来。
        // 原生 ObjectSelector 同样会按资源数量扩容，这里在每次扫描/切类型时设置一次。
        AssetPreview.SetPreviewTextureCacheSize(Mathf.Clamp(allItems.Count, 256, 2048));
        previewRequested.Clear();
        InvalidateCategoryCache();
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
        string[] guids = AssetDatabase.FindAssets("t:" + typeof(T).Name);
        foreach (string guid in guids)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (!path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (typeKey == "Mesh")
            {
                AddMeshSubAssets(path);
            }
            else
            {
                allItems.Add(new ResourceItem
                {
                    path = path,
                    name = Path.GetFileNameWithoutExtension(path),
                    typeKey = typeKey
                });
            }
        }
    }

    /// <summary>
    /// 网格类型：列出文件内的全部 Mesh 子资产（FBX/模型文件常含多个网格），
    /// 按网格资产名分类与显示，而不是用整个模型文件名。
    /// 文件内无 Mesh 子资产（如 .mesh 资产）时回退为主资产。
    /// </summary>
    private void AddMeshSubAssets(string path)
    {
        bool added = false;
        Object[] reps = AssetDatabase.LoadAllAssetRepresentationsAtPath(path);
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
            added = true;
        }

        if (!added)
        {
            allItems.Add(new ResourceItem
            {
                path = path,
                name = Path.GetFileNameWithoutExtension(path),
                typeKey = "Mesh"
            });
        }
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
        filteredItems.Sort((a, b) =>
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
    }

    /// <summary>本帧是否仍有可见预览在异步渲染（为 true 时在帧末请求一次 Repaint，出图后自动停止）。</summary>
    private bool previewLoadingThisFrame;

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

    /// <summary>已发起预览生成请求的键集合（去重，同一资源/子资产不重复排队）。</summary>
    private readonly HashSet<string> previewRequested = new HashSet<string>();

    /// <summary>
    /// 发起预览预生成请求（去重）。贴图直接画原图不需要预览；
    /// 材质 / 网格（含 FBX 主资产）等通过 AssetPreview 异步生成，Unity 内部队列后台处理。
    /// 去重键必须包含子资产名，否则同一 FBX 内的多个 Mesh 会被当成同一个。
    /// </summary>
    private void EnsurePreviewRequested(ResourceItem item)
    {
        if (item == null || string.IsNullOrEmpty(item.path))
        {
            return;
        }
        string previewKey = string.IsNullOrEmpty(item.subAssetName)
            ? item.path
            : item.path + "|" + item.subAssetName;
        if (previewRequested.Contains(previewKey))
        {
            return;
        }
        previewRequested.Add(previewKey);

        Object asset = item.GetAsset();
        if (asset == null || asset is Texture)
        {
            return;
        }
        AssetPreview.GetAssetPreview(asset); // 发起异步预生成
    }

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

    private void DrawGrid()
    {
        if (filteredItems.Count == 0)
        {
            EditorGUILayout.HelpBox(
                "当前条件下没有项目资源；仍可选择 None 或使用内置通用模板。\n可切换类型/分类，或点击“刷新”。",
                MessageType.Info);
        }

        const float thumbSize = 72f;
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

        scroll = GUI.BeginScrollView(scrollRect, scroll, new Rect(0f, 0f, contentWidth, contentHeight));

        bool closeAfterGrid = HandleGridKeyboard(columns, rowCount, cellH, scrollRect);

        // 可见行范围（基于真实视口高度，与 Unity 原生 Select 一致只绘制可见项）
        int firstRow = Mathf.Max(0, Mathf.FloorToInt(scroll.y / cellH) - 1);
        int lastRow = Mathf.Min(rowCount - 1, Mathf.CeilToInt((scroll.y + scrollRect.height) / cellH) + 1);

        // 预取可见区域上下各 3 行的异步预览（材质、网格都需要）：
        // 快速滚动时新行的预览已在后台开始生成，接近原生 Select 的顺滑体验。
        const int PrefetchRowPadding = 3;
        int prefetchFirstRow = Mathf.Max(0, firstRow - PrefetchRowPadding);
        int prefetchLastRow = Mathf.Min(rowCount - 1, lastRow + PrefetchRowPadding);
        for (int r = prefetchFirstRow; r <= prefetchLastRow; r++)
        {
            for (int c = 0; c < columns; c++)
            {
                int idx = r * columns + c;
                if (idx >= itemCount)
                {
                    break;
                }
                if (idx > 0 && filteredItems[idx - 1].typeKey != "Texture")
                {
                    EnsurePreviewRequested(filteredItems[idx - 1]);
                }
            }
        }

        // 本帧绘制前先清零；DrawThumbnail 发现仍有预览在后台渲染时会置 true，
        // 帧末据此只请求一次 Repaint（出图后自动停止，不会空转闪烁）。
        previewLoadingThisFrame = false;

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

        GUI.EndScrollView();

        if (closeAfterGrid)
        {
            Close();
            return;
        }

        // 仍有可见预览在后台渲染时，请求下一帧继续重绘（原生 ObjectSelector 的标准做法）；
        // 全部出图后 IsLoadingAssetPreview 变为 false，重绘自动停止，不会持续闪烁/空转。
        if (previewLoadingThisFrame)
        {
            Repaint();
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
        return a.path == b.path && a.subAssetName == b.subAssetName;
    }

    private void DrawThumbnail(ResourceItem item, Rect rect)
    {
        Object asset = item.GetAsset();
        if (asset == null)
        {
            GUI.Label(rect, item.typeKey, EditorStyles.centeredGreyMiniLabel);
            return;
        }

        // 贴图（含 Cubemap/Texture3D 等）：直接绘制资源本身。
        Texture tex = asset as Texture;
        if (tex != null)
        {
            GUI.DrawTexture(rect, tex, ScaleMode.ScaleToFit);
            return;
        }

        // 材质 / 网格（含 FBX 模型主资产）：与原生 ObjectSelector 相同的 AssetPreview 异步流程。
        EnsurePreviewRequested(item);
        Texture2D preview = AssetPreview.GetAssetPreview(asset);
        if (preview != null)
        {
            // 预览已生成：一次性绘制最终大图
            GUI.DrawTexture(rect, preview, ScaleMode.ScaleToFit);
            return;
        }

        // 还在后台渲染：标记本帧需要继续重绘，直到出图（出图后自动停止，不会无限刷新）
        if (AssetPreview.IsLoadingAssetPreview(asset.GetInstanceID()))
        {
            previewLoadingThisFrame = true;
        }

        // 加载期间用迷你缩略图做稳定占位（而不是文字），出图时不会出现“文字→图片”的跳变闪烁；
        // 网格同样由此路径显示：预览未就绪时先显示小图标，就绪后显示渲染好的 3D 网格预览。
        Texture2D mini = AssetPreview.GetMiniThumbnail(asset);
        if (mini != null)
        {
            GUI.DrawTexture(rect, mini, ScaleMode.ScaleToFit);
        }
        else
        {
            GUI.Label(rect, item.typeKey, EditorStyles.centeredGreyMiniLabel);
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
        if (GUILayout.Button("取消", GUILayout.Width(70)))
        {
            confirmed = false;
            Close();
        }
        EditorGUILayout.EndHorizontal();
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
