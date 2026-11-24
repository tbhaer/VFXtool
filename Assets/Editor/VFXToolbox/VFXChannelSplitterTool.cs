using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

/// <summary>
/// 模块六：贴图通道拆分（通道合并的逆操作）。
/// 选一张贴图，把 R/G/B/A 任意通道拆成独立的灰度 PNG（R=G=B=通道值，A=1）。
/// GPU 采样输出，不要求源贴图开启 Read/Write。
/// </summary>
public partial class VFXToolboxWindow
{
    [SerializeField] private Texture2D csSource;
    [SerializeField] private bool[] csChannelEnabled = { true, true, true, true };
    [SerializeField] private bool csDataTexture = true;
    [SerializeField] private DefaultAsset csFolderAsset;
    [SerializeField] private string csBaseName = "";

    private readonly Texture2D[] csPreviews = new Texture2D[4];
    private static Shader csShader;

    private const string CsShaderSource = @"
Shader ""Hidden/VFXToolboxChannelSplit""
{
    Properties { _Tex(""Tex"", 2D) = ""black"" {} }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include ""UnityCG.cginc""
            sampler2D _Tex;
            float4 _Sel;
            fixed4 frag(v2f_img i) : SV_Target
            {
                float v = dot(tex2D(_Tex, i.uv).rgba, _Sel);
                return fixed4(v, v, v, 1.0);
            }
            ENDCG
        }
    }
}";

    private void InitChannelSplitter()
    {
    }

    private static Shader GetSplitShader()
    {
        if (csShader == null)
        {
            csShader = ShaderUtil.CreateShaderAsset(CsShaderSource);
        }
        return csShader;
    }

    private void DrawChannelSplitterTool()
    {
        EditorGUILayout.LabelField("贴图通道拆分", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "通道合并的逆操作：选择一张贴图，勾选要拆出的通道，每个通道导出一张灰度 PNG（如 Name_R.png）。\n" +
            "遮罩 / 数据贴图请勾选“输出为数据贴图”（关闭 sRGB），拆出的值与原通道一致。",
            MessageType.None);

        EditorGUILayout.BeginHorizontal();
        csSource = (Texture2D)EditorGUILayout.ObjectField("源贴图", csSource, typeof(Texture2D), false);
        if (GUILayout.Button("使用选中贴图", GUILayout.Width(110)))
        {
            Texture2D t = Selection.activeObject as Texture2D;
            if (t != null)
            {
                csSource = t;
            }
        }
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.LabelField("导出通道", EditorStyles.boldLabel);
        EditorGUILayout.BeginHorizontal();
        for (int i = 0; i < 4; i++)
        {
            Color old = GUI.color;
            GUI.color = ChannelColors[i];
            csChannelEnabled[i] = GUILayout.Toggle(csChannelEnabled[i], " " + ChannelNames[i], "Button", GUILayout.Height(24));
            GUI.color = old;
        }
        EditorGUILayout.EndHorizontal();

        csDataTexture = EditorGUILayout.ToggleLeft("输出为数据贴图（关闭 sRGB，推荐遮罩类）", csDataTexture);

        EditorGUILayout.Space(4);
        EditorGUILayout.LabelField("导出设置", EditorStyles.boldLabel);
        DrawVFXExportMode();
        using (new EditorGUI.DisabledScope(vfxExportMode != VFXExportMode.ChooseLocation))
        {
            csFolderAsset = (DefaultAsset)EditorGUILayout.ObjectField("输出目录", csFolderAsset, typeof(DefaultAsset), false);
        }
        csBaseName = EditorGUILayout.TextField("文件名前缀", string.IsNullOrEmpty(csBaseName) && csSource != null ? csSource.name : csBaseName);
        string folder = GetCsFolder();
        EditorGUILayout.LabelField(vfxExportMode == VFXExportMode.ChooseLocation
            ? "将输出到：" + folder + "/<前缀>_R.png ..."
            : "原址模式输出到源贴图目录，文件前缀：" + csBaseName, EditorStyles.miniLabel);

        EditorGUILayout.Space(4);
        EditorGUILayout.BeginHorizontal();
        EditorGUI.BeginDisabledGroup(csSource == null || !AnyChannelEnabled());
        if (GUILayout.Button("生成预览", GUILayout.Height(26)))
        {
            BakeSplitPreviews();
        }
        if (GUILayout.Button("导出 PNG", GUILayout.Height(26)))
        {
            ExportSplitChannels(folder);
        }
        EditorGUI.EndDisabledGroup();
        EditorGUILayout.EndHorizontal();

        DrawSplitPreviews();
    }

    private bool AnyChannelEnabled()
    {
        foreach (bool b in csChannelEnabled)
        {
            if (b)
            {
                return true;
            }
        }
        return false;
    }

    private string GetCsFolder()
    {
        if (csFolderAsset != null)
        {
            string p = AssetDatabase.GetAssetPath(csFolderAsset);
            if (AssetDatabase.IsValidFolder(p))
            {
                return p;
            }
        }
        if (csSource != null)
        {
            string p = AssetDatabase.GetAssetPath(csSource);
            if (!string.IsNullOrEmpty(p))
            {
                return Path.GetDirectoryName(p).Replace('\\', '/');
            }
        }
        return "Assets";
    }

    private Texture2D RenderSplitChannel(int channel, int w, int h)
    {
        Shader sh = GetSplitShader();
        if (sh == null || !sh.isSupported)
        {
            SetStatus("内部拆分 Shader 创建/编译失败，请检查控制台。");
            return null;
        }
        Material mat = new Material(sh);
        mat.hideFlags = HideFlags.HideAndDontSave;
        mat.SetTexture("_Tex", csSource);
        mat.SetVector("_Sel", ChannelSelector(channel));

        RenderTexture rt = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
        rt.hideFlags = HideFlags.HideAndDontSave;
        rt.Create();

        RenderTexture prev = RenderTexture.active;
        Graphics.Blit(null, rt, mat);
        RenderTexture.active = rt;
        Texture2D output = new Texture2D(w, h, TextureFormat.RGBA32, false);
        output.ReadPixels(new Rect(0, 0, w, h), 0, 0);
        output.Apply(false);
        RenderTexture.active = prev;

        rt.Release();
        Object.DestroyImmediate(mat);
        Object.DestroyImmediate(rt);
        return output;
    }

    private void BakeSplitPreviews()
    {
        if (csSource == null)
        {
            return;
        }
        int scale = Mathf.Max(1, Mathf.CeilToInt(Mathf.Max(csSource.width, csSource.height) / 512f));
        int w = Mathf.Max(1, csSource.width / scale);
        int h = Mathf.Max(1, csSource.height / scale);
        for (int i = 0; i < 4; i++)
        {
            if (csPreviews[i] != null)
            {
                DestroyImmediate(csPreviews[i]);
                csPreviews[i] = null;
            }
            if (csChannelEnabled[i])
            {
                csPreviews[i] = RenderSplitChannel(i, w, h);
            }
        }
        SetStatus("已生成通道预览。");
    }

    private void DrawSplitPreviews()
    {
        bool any = false;
        foreach (Texture2D t in csPreviews)
        {
            if (t != null)
            {
                any = true;
            }
        }
        if (!any)
        {
            return;
        }
        EditorGUILayout.Space(6);
        EditorGUILayout.BeginHorizontal();
        for (int i = 0; i < 4; i++)
        {
            if (csPreviews[i] == null)
            {
                continue;
            }
            EditorGUILayout.BeginVertical();
            Color old = GUI.color;
            GUI.color = ChannelColors[i];
            GUILayout.Label(ChannelNames[i] + " 通道", EditorStyles.boldLabel, GUILayout.Width(80));
            GUI.color = old;
            Rect r = GUILayoutUtility.GetRect(96, 96, GUILayout.Width(96), GUILayout.Height(96));
            EditorGUI.DrawRect(r, new Color(0f, 0f, 0f, 0.35f));
            GUI.DrawTexture(r, csPreviews[i], ScaleMode.ScaleToFit, false);
            EditorGUILayout.EndVertical();
        }
        EditorGUILayout.EndHorizontal();
    }

    private void ExportSplitChannels(string folder)
    {
        if (csSource == null || !AnyChannelEnabled())
        {
            SetStatus("请指定源贴图并至少勾选一个通道。");
            return;
        }
        string baseName = string.IsNullOrEmpty(csBaseName) ? csSource.name : csBaseName;
        List<string> outputNames = new List<string>();
        for (int channel = 0; channel < 4; channel++)
        {
            if (csChannelEnabled[channel]) outputNames.Add("{name}_" + ChannelNames[channel] + ".png");
        }
        folder = ResolveVFXBatchFolder(folder, AssetDatabase.GetAssetPath(csSource), ref baseName, outputNames.ToArray());
        string absFolder = ToAbsolutePath(folder);
        if (string.IsNullOrEmpty(absFolder))
        {
            SetStatus("输出目录无效。");
            return;
        }
        Directory.CreateDirectory(absFolder);

        int w = csSource.width;
        int h = csSource.height;
        int exported = 0;

        bool overwrite = false;
        bool asked = false;
        for (int i = 0; i < 4; i++)
        {
            if (!csChannelEnabled[i])
            {
                continue;
            }
            string path = Path.Combine(absFolder, baseName + "_" + ChannelNames[i] + ".png");
            if (File.Exists(path))
            {
                if (!asked)
                {
                    overwrite = EditorUtility.DisplayDialog("文件已存在", "输出目录中已有同名 PNG，是否覆盖？", "覆盖", "取消");
                    asked = true;
                }
                if (!overwrite)
                {
                    SetStatus("已取消导出。");
                    return;
                }
            }
        }

        try
        {
            for (int i = 0; i < 4; i++)
            {
                if (!csChannelEnabled[i])
                {
                    continue;
                }
                EditorUtility.DisplayProgressBar("拆分通道", ChannelNames[i] + " 通道", i / 4f);
                Texture2D tex = RenderSplitChannel(i, w, h);
                if (tex == null)
                {
                    continue;
                }
                byte[] png = tex.EncodeToPNG();
                DestroyImmediate(tex);
                string absPath = Path.Combine(absFolder, baseName + "_" + ChannelNames[i] + ".png");
                File.WriteAllBytes(absPath, png);
                exported++;
            }
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }

        AssetDatabase.Refresh();

        if (folder.StartsWith("Assets", StringComparison.OrdinalIgnoreCase))
        {
            for (int i = 0; i < 4; i++)
            {
                if (!csChannelEnabled[i])
                {
                    continue;
                }
                string rel = (folder.TrimEnd('/') + "/" + baseName + "_" + ChannelNames[i] + ".png").Replace('\\', '/');
                TextureImporter importer = AssetImporter.GetAtPath(rel) as TextureImporter;
                if (importer != null)
                {
                    importer.textureType = TextureImporterType.Default;
                    importer.sRGBTexture = !csDataTexture;
                    importer.mipmapEnabled = false;
                    importer.wrapMode = TextureWrapMode.Clamp;
                    importer.SaveAndReimport();
                }
            }
            string first = (folder.TrimEnd('/') + "/" + baseName + "_" + ChannelNames[FirstEnabledChannel()] + ".png").Replace('\\', '/');
            Texture2D loaded = AssetDatabase.LoadAssetAtPath<Texture2D>(first);
            if (loaded != null)
            {
                EditorGUIUtility.PingObject(loaded);
            }
        }

        SetStatus("通道拆分完成：导出 " + exported + " 张到 " + folder + "/");
    }

    private int FirstEnabledChannel()
    {
        for (int i = 0; i < 4; i++)
        {
            if (csChannelEnabled[i])
            {
                return i;
            }
        }
        return 0;
    }

    private static string ToAbsolutePath(string assetsRelative)
    {
        if (string.IsNullOrEmpty(assetsRelative))
        {
            return null;
        }
        assetsRelative = assetsRelative.Replace('\\', '/').TrimEnd('/');
        if (!assetsRelative.StartsWith("Assets", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        string tail = assetsRelative.Substring("Assets".Length).TrimStart('/');
        return Path.Combine(Application.dataPath, tail).Replace('\\', '/');
    }
}
