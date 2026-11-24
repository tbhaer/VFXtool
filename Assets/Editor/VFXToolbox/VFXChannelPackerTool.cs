using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// 模块四：贴图通道合并。
/// 为输出的 R/G/B/A 四个通道分别指定源贴图与源通道（4 张槽位，同一张贴图可重复使用）；
/// 没有给贴图的通道按常量填充（RGB 默认 0、A 默认 1）。
/// 通过 GPU（Graphics.Blit + 内存中编译的 channel-swizzle shader）合成，
/// 不需要修改源贴图的 Read/Write 导入设置；最后编码为一张四通道 PNG 导出。
/// 常用于：Metallic(R) / Occlusion(G) / Detail(B) / Smoothness(A) 等遮罩打包。
/// </summary>
public partial class VFXToolboxWindow
{
    private static readonly string[] ChannelNames = { "R", "G", "B", "A" };
    private static readonly Color[] ChannelColors =
    {
        new Color(1f, 0.45f, 0.45f),
        new Color(0.5f, 1f, 0.5f),
        new Color(0.5f, 0.7f, 1f),
        new Color(0.85f, 0.85f, 0.85f)
    };

    [SerializeField] private Texture2D[] packSources;   // 4 个槽位（可空）
    [SerializeField] private int[] packSrcChannels;    // 每个槽位取哪个源通道
    [SerializeField] private float[] packConstants;    // 槽位为空时填充值
    [SerializeField] private int packW;                // 0 = 自动（取最大）
    [SerializeField] private int packH;
    [SerializeField] private bool packMipmaps;
    [SerializeField] private string packName = "ChannelPacked";

    private Texture2D packPreview;
    private int packLastW;
    private int packLastH;
    private static Shader packShader;

    private const string PackShaderSource = @"
Shader ""Hidden/VFXToolboxChannelPacker""
{
    Properties
    {
        _Tex0(""Tex0"", 2D) = ""black"" {}
        _Tex1(""Tex1"", 2D) = ""black"" {}
        _Tex2(""Tex2"", 2D) = ""black"" {}
        _Tex3(""Tex3"", 2D) = ""black"" {}
    }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include ""UnityCG.cginc""
            sampler2D _Tex0; sampler2D _Tex1; sampler2D _Tex2; sampler2D _Tex3;
            float4 _Sel0; float4 _Sel1; float4 _Sel2; float4 _Sel3;
            float _Const0; float _Const1; float _Const2; float _Const3;
            float _Has0; float _Has1; float _Has2; float _Has3;
            fixed4 frag(v2f_img i) : SV_Target
            {
                float v0 = _Has0 > 0.5 ? dot(tex2D(_Tex0, i.uv).rgba, _Sel0) : _Const0;
                float v1 = _Has1 > 0.5 ? dot(tex2D(_Tex1, i.uv).rgba, _Sel1) : _Const1;
                float v2 = _Has2 > 0.5 ? dot(tex2D(_Tex2, i.uv).rgba, _Sel2) : _Const2;
                float v3 = _Has3 > 0.5 ? dot(tex2D(_Tex3, i.uv).rgba, _Sel3) : _Const3;
                return fixed4(v0, v1, v2, v3);
            }
            ENDCG
        }
    }
}";

    private void InitPacker()
    {
        if (packSources == null)
        {
            packSources = new Texture2D[4];
            packSrcChannels = new[] { 0, 1, 2, 3 };
            packConstants = new[] { 0f, 0f, 0f, 1f };
            packW = 0;
            packH = 0;
        }
    }

    private void DrawPackerTool()
    {
        EditorGUILayout.LabelField("贴图通道合并", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "为 R/G/B/A 每个输出通道选择源贴图和源通道；槽位留空时用常量填充。\n" +
            "例：Metallic→R、AO→G、DetailMask→B、Smoothness→A。遮罩/数据贴图建议在导入设置里关闭 sRGB。",
            MessageType.None);

        // 四个输出通道行
        for (int i = 0; i < 4; i++)
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
            {
                Color old = GUI.color;
                GUI.color = ChannelColors[i];
                GUILayout.Label(ChannelNames[i], EditorStyles.boldLabel, GUILayout.Width(20));
                GUI.color = old;

                packSources[i] = (Texture2D)EditorGUILayout.ObjectField(packSources[i], typeof(Texture2D), false, GUILayout.MinWidth(120));
                GUILayout.Label("取通道", GUILayout.Width(50));
                packSrcChannels[i] = EditorGUILayout.Popup(packSrcChannels[i], ChannelNames, GUILayout.Width(50));

                if (packSources[i] == null)
                {
                    GUILayout.Label("空时填", GUILayout.Width(50));
                    packConstants[i] = EditorGUILayout.Slider(packConstants[i], 0f, 1f);
                }
                else
                {
                    GUILayout.Label("", GUILayout.Width(120));
                }
            }
        }

        EditorGUILayout.Space(4);
        EditorGUILayout.LabelField("输出设置", EditorStyles.boldLabel);
        EditorGUILayout.BeginHorizontal();
        packW = EditorGUILayout.IntField(new GUIContent("宽度", "0 = 自动取源贴图最大值"), packW);
        packH = EditorGUILayout.IntField(new GUIContent("高度", "0 = 自动取源贴图最大值"), packH);
        EditorGUILayout.EndHorizontal();
        packMipmaps = EditorGUILayout.Toggle("生成 Mipmap", packMipmaps);
        packName = EditorGUILayout.TextField("导出文件名", packName);
        DrawVFXExportMode();

        int autoW, autoH;
        ComputePackSize(out autoW, out autoH);
        EditorGUILayout.LabelField("实际输出尺寸：" + autoW + " × " + autoH, EditorStyles.miniLabel);

        EditorGUILayout.Space(4);
        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("生成预览", GUILayout.Height(26)))
        {
            BakePackPreview();
        }
        GUI.enabled = packPreview != null;
        if (GUILayout.Button("导出 PNG…", GUILayout.Height(26)))
        {
            ExportPackedPng();
        }
        GUI.enabled = true;
        EditorGUILayout.EndHorizontal();

        if (packPreview != null)
        {
            EditorGUILayout.Space(6);
            float size = Mathf.Min(256f, position.width - 60f);
            Rect r = GUILayoutUtility.GetRect(size, size, GUILayout.Width(size), GUILayout.Height(size));
            EditorGUI.DrawRect(r, new Color(0f, 0f, 0f, 0.35f));
            GUI.DrawTexture(r, packPreview, ScaleMode.ScaleToFit, false);
        }
    }

    private void ComputePackSize(out int w, out int h)
    {
        w = 0;
        h = 0;
        foreach (Texture2D t in packSources)
        {
            if (t != null)
            {
                w = Mathf.Max(w, t.width);
                h = Mathf.Max(h, t.height);
            }
        }
        if (packW > 0) w = packW;
        if (packH > 0) h = packH;
        w = Mathf.Clamp(w, 1, 8192);
        h = Mathf.Clamp(h, 1, 8192);
    }

    private static Shader GetPackShader()
    {
        if (packShader == null)
        {
            packShader = ShaderUtil.CreateShaderAsset(PackShaderSource);
        }
        return packShader;
    }

    private void BakePackPreview()
    {
        int w, h;
        ComputePackSize(out w, out h);
        Texture2D result = PackChannels(w, h);
        if (result == null)
        {
            return;
        }
        if (packPreview != null)
        {
            DestroyImmediate(packPreview);
        }
        packPreview = result;
        packLastW = w;
        packLastH = h;
    }

    /// <summary>
    /// GPU 合成四通道贴图。Linear RT + 直接采样：源贴图若按数据贴图导入（sRGB 关），
    /// 输出即逐通道原值；不需要源贴图开启 Read/Write。
    /// </summary>
    private Texture2D PackChannels(int w, int h)
    {
        bool any = false;
        foreach (Texture2D t in packSources)
        {
            if (t != null)
            {
                any = true;
                break;
            }
        }
        if (!any)
        {
            SetStatus("请至少为一个通道指定源贴图。");
            return null;
        }

        Shader sh = GetPackShader();
        if (sh == null || sh.isSupported == false)
        {
            SetStatus("内部合成 Shader 创建/编译失败，请检查控制台。");
            return null;
        }

        Material mat = new Material(sh);
        mat.hideFlags = HideFlags.HideAndDontSave;
        for (int i = 0; i < 4; i++)
        {
            if (packSources[i] != null)
            {
                mat.SetTexture("_Tex" + i, packSources[i]);
                mat.SetVector("_Sel" + i, ChannelSelector(packSrcChannels[i]));
                mat.SetFloat("_Has" + i, 1f);
            }
            else
            {
                mat.SetFloat("_Has" + i, 0f);
            }
            mat.SetFloat("_Const" + i, Mathf.Clamp01(packConstants[i]));
        }

        RenderTexture rt = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
        rt.hideFlags = HideFlags.HideAndDontSave;
        rt.Create();
        if (!rt.IsCreated())
        {
            SetStatus("RenderTexture 创建失败。");
            DestroyImmediate(mat);
            DestroyImmediate(rt);
            return null;
        }

        RenderTexture prev = RenderTexture.active;
        Graphics.Blit(null, rt, mat);
        RenderTexture.active = rt;

        Texture2D output = new Texture2D(w, h, TextureFormat.RGBA32, false);
        output.ReadPixels(new Rect(0, 0, w, h), 0, 0);
        output.Apply(false);

        RenderTexture.active = prev;
        rt.Release();
        DestroyImmediate(mat);
        DestroyImmediate(rt);
        return output;
    }

    private static Vector4 ChannelSelector(int channelIndex)
    {
        switch (channelIndex)
        {
            case 0: return new Vector4(1f, 0f, 0f, 0f);
            case 1: return new Vector4(0f, 1f, 0f, 0f);
            case 2: return new Vector4(0f, 0f, 1f, 0f);
            default: return new Vector4(0f, 0f, 0f, 1f);
        }
    }

    private void ExportPackedPng()
    {
        int w, h;
        ComputePackSize(out w, out h);
        string fileName = string.IsNullOrEmpty(packName) ? "ChannelPacked" : packName;
        string sourcePath = null;
        if (packSources != null)
        {
            foreach (Texture2D source in packSources)
            {
                if (source != null)
                {
                    sourcePath = AssetDatabase.GetAssetPath(source);
                    break;
                }
            }
        }
        string absPath = ResolveVFXExportPath("导出合并贴图 PNG", fileName, "png", sourcePath, false);
        if (string.IsNullOrEmpty(absPath))
        {
            return;
        }

        Texture2D tex = PackChannels(w, h);
        if (tex == null)
        {
            return;
        }
        byte[] png = tex.EncodeToPNG();
        DestroyImmediate(tex);
        if (png == null || png.Length == 0)
        {
            SetStatus("导出失败：PNG 编码为空。");
            return;
        }

        File.WriteAllBytes(absPath, png);
        AssetDatabase.Refresh();

        string rel = ToAssetsRelative(absPath);
        if (rel != null)
        {
            TextureImporter importer = AssetImporter.GetAtPath(rel) as TextureImporter;
            if (importer != null)
            {
                importer.textureType = TextureImporterType.Default;
                importer.sRGBTexture = false; // 通道打包是数据贴图
                importer.mipmapEnabled = packMipmaps;
                importer.wrapMode = TextureWrapMode.Repeat;
                importer.SaveAndReimport();
            }
            Texture2D loaded = AssetDatabase.LoadAssetAtPath<Texture2D>(rel);
            if (loaded != null)
            {
                EditorGUIUtility.PingObject(loaded);
            }
            SetStatus("已导出合并贴图：" + rel);
        }
        else
        {
            SetStatus("已导出到项目外（未导入 Unity）：" + absPath);
        }
    }
}
