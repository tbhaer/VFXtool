using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEditor.SearchService;
using UnityEngine;
using Object = UnityEngine.Object;

#pragma warning disable 0618 // SearchService.ObjectSelector 在 2021.2+ 标记为过时，但功能正常

/// <summary>
/// 全局对象选择器引擎：通过 Unity 官方 SearchService 扩展点接管 Unity 自带的 Select 弹窗。
/// 注册后，任意 ObjectField（材质贴图槽、粒子系统的材质/网格/贴图槽等）点击 select 时，
/// 都会调用本引擎并打开自定义的 VFX 资源分类选择器。
///
/// 只拦截配置中允许的类型（贴图 / 材质 / 网格），其他类型放行给 Unity 默认选择器。
/// </summary>
[ObjectSelectorEngine]
public class VFXObjectSelectorEngine : IObjectSelectorEngine
{
    public string name => "VFX Resource Selector";

    public void BeginSession(ISearchContext context) { }
    public void EndSession(ISearchContext context) { }
    public void BeginSearch(ISearchContext context, string query) { }
    public void EndSearch(ISearchContext context) { }

    public bool SelectObject(ISearchContext context, Action<Object, bool> onObjectSelectorClosed, Action<Object> onObjectSelectedUpdated)
    {
        VFXSelectorSettings settings;
        bool shouldIntercept;
        try
        {
            settings = VFXSelectorSettings.Instance;
            if (!settings.interceptEnabled)
            {
                return false; // 未开启拦截：交给 Unity 默认选择器
            }
            shouldIntercept = IsInterceptType(GetRequiredTypeNames(context), settings);
        }
        catch (Exception e)
        {
            Debug.LogWarning("[VFX Resource Selector] 无法判断选择器字段类型，改用 Unity 原生选择器：" + e.Message);
            return false;
        }

        if (!shouldIntercept)
        {
            return false; // 未知或不支持的类型一律交给 Unity 原生选择器
        }

        // 打开自定义选择器（引擎接管）
        VFXResourceSelectorWindow.Open(context, onObjectSelectorClosed, onObjectSelectedUpdated);
        return true;
    }

    public void SetSearchFilter(ISearchContext context, string searchFilter)
    {
        VFXResourceSelectorWindow.ApplySearchFilter(searchFilter);
    }

    /// <summary>
    /// 反射读取 search context 的 requiredTypeNames 属性。
    /// 不同 Unity 版本中 context 的运行时类型（ObjectSelectorSearchContext）可能不同，
    /// 用反射避免编译期依赖，保证 2020.x ~ Unity 6 全版本可编译。
    /// </summary>
    public static IEnumerable<string> GetRequiredTypeNames(ISearchContext ctx)
    {
        if (ctx == null)
        {
            return null;
        }
        try
        {
            PropertyInfo prop = ctx.GetType().GetProperty("requiredTypeNames", BindingFlags.Public | BindingFlags.Instance);
            return prop != null ? prop.GetValue(ctx, null) as IEnumerable<string> : null;
        }
        catch
        {
            // Unity 不同版本的 SearchContext 实现可能不存在该属性或 getter 不可用；
            // 未能确认字段类型时让原生选择器接管，避免把未知类型错误送入自定义窗口。
            return null;
        }
    }

    /// <summary>根据字段要求的类型判断是否属于本工具接管范围。</summary>
    private static bool IsInterceptType(IEnumerable<string> typeNames, VFXSelectorSettings settings)
    {
        if (typeNames == null)
        {
            return false;
        }

        bool intercepted = false;
        foreach (string typeName in typeNames)
        {
            if (string.IsNullOrEmpty(typeName))
            {
                return false;
            }
            if (typeName.IndexOf("Texture", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                if (settings.interceptTextures) intercepted = true;
            }
            else if (typeName.IndexOf("Material", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                if (settings.interceptMaterials) intercepted = true;
            }
            else if (typeName.IndexOf("Mesh", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                if (settings.interceptMeshes) intercepted = true;
            }
            else
            {
                // 字段要求的是其他类型（如 GameObject 等）：不拦截
                return false;
            }
        }
        return intercepted;
    }
}

/// <summary>
/// 引擎激活辅助：编辑器启动后确保自定义引擎处于激活状态。
/// 使用反射调用，兼容 Unity 2021.3（ObjectSelector）与 2022.x（ObjectSelectorSearch）的差异。
/// </summary>
[InitializeOnLoad]
public static class VFXObjectSelectorActivator
{
    private const string EngineName = "VFX Resource Selector";

    static VFXObjectSelectorActivator()
    {
        EditorApplication.delayCall += Activate;
    }

    private static void Activate()
    {
        try
        {
            System.Reflection.Assembly core = typeof(UnityEditor.EditorWindow).Assembly;

            // 2022.x: UnityEditor.SearchService.ObjectSelectorSearch.SetActiveSearchEngine(name)
            System.Type searchType = core.GetType("UnityEditor.SearchService.ObjectSelectorSearch");
            if (TrySetActiveEngine(searchType))
            {
                return;
            }

            // 2021.3: UnityEditor.SearchService.ObjectSelector.SetActiveSearchEngine(name)（若存在）
            System.Type selectorType = core.GetType("UnityEditor.SearchService.ObjectSelector");
            TrySetActiveEngine(selectorType);
        }
        catch (Exception e)
        {
            Debug.LogWarning("[VFX Resource Selector] 激活自定义选择器失败：\n" + e.Message);
        }
    }

    private static bool TrySetActiveEngine(System.Type type)
    {
        if (type == null)
        {
            return false;
        }
        System.Reflection.MethodInfo method = type.GetMethod(
            "SetActiveSearchEngine",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
        if (method == null)
        {
            return false;
        }
        method.Invoke(null, new object[] { EngineName });
        return true;
    }
}
