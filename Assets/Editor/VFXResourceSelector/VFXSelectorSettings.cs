using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// 分类规则：一级分类。
/// prefix 为空表示匹配所有（一般不使用）；prefix 为文件名前缀（忽略大小写）。
/// </summary>
[Serializable]
public class CategoryRule
{
    public string name = "";
    public string prefix = "";
    public List<SubCategoryRule> subCategories = new List<SubCategoryRule>();
}

/// <summary>
/// 分类规则：二级分类（隶属于某个一级分类）。
/// </summary>
[Serializable]
public class SubCategoryRule
{
    public string name = "";
    public string prefix = "";
    public List<SubSubCategoryRule> subSubCategories = new List<SubSubCategoryRule>();
}

/// <summary>
/// 分类规则：三级分类（隶属于某个二级分类）。
/// </summary>
[Serializable]
public class SubSubCategoryRule
{
    public string name = "";
    public string prefix = "";
    public List<Level4CategoryRule> level4Categories = new List<Level4CategoryRule>();
}

/// <summary>
/// 分类规则：四级分类（隶属于某个三级分类）。项目大、文件名分段多时使用。
/// </summary>
[Serializable]
public class Level4CategoryRule
{
    public string name = "";
    public string prefix = "";
}

/// <summary>
/// VFX 资源选择器的全局设置（作为项目资产保存，可团队共享）。
/// 包含：拦截开关、拦截的资源类型、分类规则（自动前缀 / 自定义规则）。
/// 首次访问时自动在 Assets/Editor/VFXResourceSelector/ 下创建设置资产。
/// </summary>
public class VFXSelectorSettings : ScriptableObject
{
    // ---- 全局拦截 ----
    public bool interceptEnabled = true;
    public bool interceptTextures = true;
    public bool interceptMaterials = true;
    public bool interceptMeshes = true;

    // ---- 分类规则 ----
    /// <summary>false = 自动按文件名前缀分段；true = 使用下方自定义规则。</summary>
    public bool useCustomRules = false;
    public List<CategoryRule> categoryRules = new List<CategoryRule>();

    private static VFXSelectorSettings _instance;

    public static VFXSelectorSettings Instance
    {
        get
        {
            if (_instance == null)
            {
                _instance = LoadOrCreate();
            }
            return _instance;
        }
    }

    private const string SettingsFolder = "Assets/Editor/VFXResourceSelector";
    private const string SettingsPath = SettingsFolder + "/VFXSelectorSettings.asset";

    private static VFXSelectorSettings LoadOrCreate()
    {
        VFXSelectorSettings settings = AssetDatabase.LoadAssetAtPath<VFXSelectorSettings>(SettingsPath);
        if (settings == null)
        {
            settings = CreateInstance<VFXSelectorSettings>();

            if (!AssetDatabase.IsValidFolder(SettingsFolder))
            {
                if (!Directory.Exists(SettingsFolder))
                {
                    Directory.CreateDirectory(SettingsFolder);
                }
                AssetDatabase.Refresh();
            }

            AssetDatabase.CreateAsset(settings, SettingsPath);
            AssetDatabase.SaveAssets();
        }
        return settings;
    }

    public static void Save()
    {
        if (_instance != null)
        {
            EditorUtility.SetDirty(_instance);
            AssetDatabase.SaveAssets();
        }
    }
}

/// <summary>
/// 分类匹配工具：根据文件名计算一级/二级/三级分类，并判断资源归属。
/// </summary>
public static class VFXCategoryMatcher
{
    public const string AllCategory = "全部";
    public const string Uncategorized = "未分类";

    /// <summary>自动模式一级分类：文件名第一个 “_” 之前的内容（无 “_” 归入未分类）。</summary>
    public static string GetAutoLevel1(string fileName)
    {
        int idx = fileName.IndexOf('_');
        return idx > 0 ? fileName.Substring(0, idx) : Uncategorized;
    }

    /// <summary>自动模式二级分类：文件名前两个 “_” 之间的内容（取不到返回空）。</summary>
    public static string GetAutoLevel2(string fileName)
    {
        int idx1 = fileName.IndexOf('_');
        if (idx1 < 0)
        {
            return "";
        }
        int idx2 = fileName.IndexOf('_', idx1 + 1);
        if (idx2 < 0)
        {
            return "";
        }
        return fileName.Substring(idx1 + 1, idx2 - idx1 - 1);
    }

    /// <summary>自动模式三级分类：文件名前三个 “_” 之间的内容（取不到返回空）。</summary>
    public static string GetAutoLevel3(string fileName)
    {
        int idx1 = fileName.IndexOf('_');
        if (idx1 < 0)
        {
            return "";
        }
        int idx2 = fileName.IndexOf('_', idx1 + 1);
        if (idx2 < 0)
        {
            return "";
        }
        int idx3 = fileName.IndexOf('_', idx2 + 1);
        if (idx3 < 0)
        {
            return "";
        }
        return fileName.Substring(idx2 + 1, idx3 - idx2 - 1);
    }

    /// <summary>自动模式四级分类：文件名前四个 “_” 之间的内容（取不到返回空）。</summary>
    public static string GetAutoLevel4(string fileName)
    {
        int idx1 = fileName.IndexOf('_');
        if (idx1 < 0)
        {
            return "";
        }
        int idx2 = fileName.IndexOf('_', idx1 + 1);
        if (idx2 < 0)
        {
            return "";
        }
        int idx3 = fileName.IndexOf('_', idx2 + 1);
        if (idx3 < 0)
        {
            return "";
        }
        int idx4 = fileName.IndexOf('_', idx3 + 1);
        if (idx4 < 0)
        {
            return "";
        }
        return fileName.Substring(idx3 + 1, idx4 - idx3 - 1);
    }

    /// <summary>自定义规则模式下，资源匹配到的一级分类名（未匹配任何规则返回未分类）。</summary>
    public static string MatchRuleLevel1(string fileName, List<CategoryRule> rules)
    {
        foreach (CategoryRule rule in rules)
        {
            if (!string.IsNullOrEmpty(rule.prefix) && StartsWithIgnoreCase(fileName, rule.prefix))
            {
                return rule.name;
            }
        }
        return Uncategorized;
    }

    /// <summary>
    /// 自定义规则模式下，资源在某个一级分类下匹配到的二级分类名（未匹配返回空）。
    /// 二级前缀支持两种写法：完整前缀（如 “EF_Fire”）或只填本段名（如 “Fire”，自动在一级前缀之后匹配）。
    /// </summary>
    public static string MatchRuleLevel2(string fileName, CategoryRule rule)
    {
        if (rule == null)
        {
            return "";
        }
        // 方式一：完整前缀匹配（兼容 “EF_Fire” 这类完整前缀配置）
        foreach (SubCategoryRule sub in rule.subCategories)
        {
            if (!string.IsNullOrEmpty(sub.prefix) && StartsWithIgnoreCase(fileName, sub.prefix))
            {
                return sub.name;
            }
        }
        // 方式二：去掉一级前缀后按段名匹配（支持只填本段名，如 “Fire”）
        string rest = StripPrefix(fileName, rule.prefix);
        foreach (SubCategoryRule sub in rule.subCategories)
        {
            if (!string.IsNullOrEmpty(sub.prefix) && StartsWithIgnoreCase(rest, sub.prefix))
            {
                return sub.name;
            }
        }
        return "";
    }

    /// <summary>
    /// 自定义规则模式下，资源在某个二级分类下匹配到的三级分类名（未匹配返回空）。
    /// 三级前缀支持两种写法：完整前缀，或只填本段名（如 “Mask”，自动在一、二级前缀之后匹配）。
    /// </summary>
    public static string MatchRuleLevel3(string fileName, CategoryRule rule, SubCategoryRule sub)
    {
        if (sub == null)
        {
            return "";
        }
        // 方式一：完整前缀匹配
        foreach (SubSubCategoryRule subsub in sub.subSubCategories)
        {
            if (!string.IsNullOrEmpty(subsub.prefix) && StartsWithIgnoreCase(fileName, subsub.prefix))
            {
                return subsub.name;
            }
        }
        // 方式二：依次去掉一级、二级前缀后按段名匹配
        string rest = StripPrefix(fileName, rule != null ? rule.prefix : "");
        rest = StripPrefix(rest, sub.prefix);
        foreach (SubSubCategoryRule subsub in sub.subSubCategories)
        {
            if (!string.IsNullOrEmpty(subsub.prefix) && StartsWithIgnoreCase(rest, subsub.prefix))
            {
                return subsub.name;
            }
        }
        return "";
    }

    /// <summary>
    /// 自定义规则模式下，资源在某个三级分类下匹配到的四级分类名（未匹配返回空）。
    /// 四级前缀支持完整前缀，或只填本段名（如 “Core”，自动在一、二、三级前缀之后匹配）。
    /// </summary>
    public static string MatchRuleLevel4(string fileName, CategoryRule rule, SubCategoryRule sub, SubSubCategoryRule subsub)
    {
        if (subsub == null)
        {
            return "";
        }
        // 方式一：完整前缀匹配
        foreach (Level4CategoryRule l4 in subsub.level4Categories)
        {
            if (!string.IsNullOrEmpty(l4.prefix) && StartsWithIgnoreCase(fileName, l4.prefix))
            {
                return l4.name;
            }
        }
        // 方式二：依次去掉一级、二级、三级前缀后按段名匹配
        string rest = StripPrefix(fileName, rule != null ? rule.prefix : "");
        rest = StripPrefix(rest, sub != null ? sub.prefix : "");
        rest = StripPrefix(rest, subsub.prefix);
        foreach (Level4CategoryRule l4 in subsub.level4Categories)
        {
            if (!string.IsNullOrEmpty(l4.prefix) && StartsWithIgnoreCase(rest, l4.prefix))
            {
                return l4.name;
            }
        }
        return "";
    }

    /// <summary>去掉文件名开头的指定前缀，并清理紧随的分隔符（_、-、空格、.）。</summary>
    private static string StripPrefix(string fileName, string prefix)
    {
        if (string.IsNullOrEmpty(fileName) || string.IsNullOrEmpty(prefix))
        {
            return fileName;
        }
        if (StartsWithIgnoreCase(fileName, prefix))
        {
            return fileName.Substring(prefix.Length).TrimStart('_', '-', ' ', '.');
        }
        return fileName;
    }

    private static bool StartsWithIgnoreCase(string value, string prefix)
    {
        return value.IndexOf(prefix, StringComparison.OrdinalIgnoreCase) == 0;
    }
}
