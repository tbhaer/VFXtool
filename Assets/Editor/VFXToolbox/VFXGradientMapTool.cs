using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

/// <summary>
/// 模块十一：渐变映射（对应 Photoshop 的 Gradient Map）。
/// 取源贴图每个像素的明暗（亮度或指定通道），到渐变（渐变栏或一张渐变贴图）上取色，
/// 输出重新上色后的贴图，原贴图的形状 Alpha 保留；支持反向、仿色（Bayer 抖动减少色带）。
/// GPU 处理，不要求源贴图开启 Read/Write。
/// </summary>
public partial class VFXToolboxWindow
{
    private enum GradientMapSource
    {
        GradientField = 0, // 渐变栏自己调色
        GradientTexture = 1 // 一张渐变贴图
    }

    [SerializeField] private Texture2D gmSource;
    [SerializeField] private Gradient gmGradient;
    [SerializeField] private int gmGradientSource;
    [SerializeField] private Texture2D gmGradientTexture;
    [SerializeField] private bool gmReverse;
    [SerializeField] private bool gmDither = true;
    [SerializeField] private string gmOutputName = "GradientMap";
    private Texture2D gmPreview;

    private static Shader gmShader;

    private const string GmShaderSource = @"
Shader ""Hidden/VFXToolboxGradientMap""
{
    Properties
    {
        _MainTex (""Tex"", 2D) = ""white"" {}
        _GradLUT (""Gradient LUT"", 2D) = ""white"" {}
    }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        Blend Off
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include ""UnityCG.cginc""
            sampler2D _MainTex;
            float4 _MainTex_TexelSize;
            sampler2D _GradLUT;
            float _Reverse;
            float _Dither;

            static const float bayer[16] = {
                 0.0,  8.0,  2.0, 10.0,
                12.0,  4.0, 14.0,  6.0,
                 3.0, 11.0,  1.0,  9.0,
                15.0,  7.0, 13.0,  5.0
            };

            fixed4 frag(v2f_img i) : SV_Target
            {
                fixed4 src = tex2D(_MainTex, i.uv);
                float l = dot(src.rgb, float3(0.299, 0.587, 0.114));
                l = saturate(l);
                if (_Reverse > 0.5) l = 1.0 - l;

                if (_Dither > 0.5)
                {
                    int2 pi = int2(i.uv * _MainTex_TexelSize.zw);
                    int2 mi = pi & 3;
                    float b = bayer[mi.y * 4 + mi.x] / 16.0;
                    l = saturate(l + (b - 0.5) / 255.0);
                }

                fixed4 g = tex2D(_GradLUT, float2(l, 0.5));
                // 形状 Alpha 始终来自源贴图；渐变自身的透明度作为额外遮罩相乘
                return fixed4(g.rgb, src.a * g.a);
            }
            ENDCG
        }
    }
}";

    private void InitGradientMap()
    {
        if (gmGradient == null)
        {
            gmGradient = new Gradient();
            gmGradient.SetKeys(
                new[]
                {
                    new GradientColorKey(new Color(0.16f, 0.05f, 0.35f), 0f),
                    new GradientColorKey(new Color(0.95f, 0.10f, 0.45f), 0.55f),
                    new GradientColorKey(new Color(1.00f, 0.62f, 0.25f), 1f)
                },
                new[]
                {
                    new GradientAlphaKey(1f, 0f),
                    new GradientAlphaKey(1f, 1f)
                });
        }
    }

    private static Shader GetGradientMapShader()
    {
        if (gmShader == null)
        {
            gmShader = ShaderUtil.CreateShaderAsset(GmShaderSource);
        }
        return gmShader;
    }

    private void DrawGradientMapTool()
    {
        EditorGUILayout.LabelField("渐变映射（Gradient Map）", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "类似 PS 的渐变映射：按源贴图每个像素的明暗，到渐变上取色重新上色。\n" +
            "渐变可以用下方渐变栏自己调，也可以指定一张渐变贴图；源贴图的形状 Alpha 会保留。",
            MessageType.None);

        EditorGUILayout.BeginHorizontal();
        gmSource = (Texture2D)EditorGUILayout.ObjectField("源贴图", gmSource, typeof(Texture2D), false);
        if (GUILayout.Button("使用选中", GUILayout.Width(70)))
        {
            Texture2D picked = Selection.activeObject as Texture2D;
            if (picked != null)
            {
                gmSource = picked;
            }
        }
        EditorGUILayout.EndHorizontal();

        gmGradientSource = EditorGUILayout.Popup("渐变来源", gmGradientSource,
            new[] { "渐变栏（自己调色）", "渐变贴图（取一张渐变图）" });
        if (gmGradientSource == (int)GradientMapSource.GradientField)
        {
            EditorGUI.indentLevel++;
            gmGradient = EditorGUILayout.GradientField("映射渐变", gmGradient);
            EditorGUI.indentLevel--;
        }
        else
        {
            gmGradientTexture = (Texture2D)EditorGUILayout.ObjectField(
                new GUIContent("渐变贴图", "按横向渐变读取（取中间区域平均），左端对应暗部、右端对应亮部"),
                gmGradientTexture, typeof(Texture2D), false);
        }

        EditorGUILayout.Space(2);
        EditorGUILayout.BeginHorizontal();
        gmReverse = EditorGUILayout.Toggle(new GUIContent("反向", "暗部取渐变右端颜色，亮部取左端（对应 PS 的反向）"), gmReverse);
        gmDither = EditorGUILayout.Toggle(new GUIContent("仿色", "Bayer 抖动，减少大面积渐变的色带（对应 PS 的仿色）"), gmDither);
        EditorGUILayout.EndHorizontal();

        gmOutputName = EditorGUILayout.TextField("导出文件名",
            string.IsNullOrEmpty(gmOutputName) && gmSource != null ? gmSource.name + "_GradientMap" : gmOutputName);
        DrawVFXExportMode();

        EditorGUILayout.Space(4);
        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("刷新预览", GUILayout.Height(26)))
        {
            BakeGradientMapPreview();
        }
        GUI.enabled = gmSource != null && GradientReady();
        if (GUILayout.Button("导出 PNG…", GUILayout.Height(26)))
        {
            ExportGradientMapPng();
        }
        GUI.enabled = true;
        EditorGUILayout.EndHorizontal();

        if (gmPreview != null)
        {
            EditorGUILayout.Space(6);
            float aspect = (float)gmPreview.height / Mathf.Max(1, gmPreview.width);
            float w = Mathf.Min(360f, position.width - 60f);
            float h = w * aspect;
            const float maxH = 300f;
            if (h > maxH)
            {
                h = maxH;
                w = h / Mathf.Max(0.0001f, aspect);
            }
            Rect r = GUILayoutUtility.GetRect(w, h, GUILayout.Width(w), GUILayout.Height(h));
            EditorGUI.DrawRect(r, new Color(0f, 0f, 0f, 0.35f));
            GUI.DrawTexture(r, gmPreview, ScaleMode.ScaleToFit, true);
            EditorGUILayout.LabelField(gmPreview.width + " × " + gmPreview.height + " RGBA32（预览缩放）", EditorStyles.miniLabel);
        }
    }

    private bool GradientReady()
    {
        return gmGradientSource == (int)GradientMapSource.GradientField
            ? gmGradient != null
            : gmGradientTexture != null;
    }

    /// <summary>把渐变栏或渐变贴图烘焙成 256×1 的线性数据 LUT（Clamp/Bilinear）。</summary>
    private Texture2D BuildGradientLut()
    {
        Color[] lut = new Color[256];

        if (gmGradientSource == (int)GradientMapSource.GradientField)
        {
            if (gmGradient == null)
            {
                return null;
            }
            for (int i = 0; i < 256; i++)
            {
                lut[i] = gmGradient.Evaluate(i / 255f);
            }
        }
        else
        {
            if (gmGradientTexture == null)
            {
                return null;
            }
            // 横向重采样到 256×32 后纵向平均，弱化渐变图上的噪点/划痕
            bool srgb = IsSourceSRGB(gmGradientTexture);
            RenderTexture rt = new RenderTexture(256, 32, 0, RenderTextureFormat.ARGB32,
                srgb ? RenderTextureReadWrite.sRGB : RenderTextureReadWrite.Linear);
            rt.hideFlags = HideFlags.HideAndDontSave;
            rt.Create();
            Texture2D read = new Texture2D(256, 32, TextureFormat.RGBA32, false, true);
            RenderTexture prev = RenderTexture.active;
            try
            {
                Graphics.Blit(gmGradientTexture, rt);
                RenderTexture.active = rt;
                read.ReadPixels(new Rect(0, 0, 256, 32), 0, 0);
                read.Apply(false);
                Color[] px = read.GetPixels();
                for (int x = 0; x < 256; x++)
                {
                    Color acc = Color.clear;
                    for (int y = 0; y < 32; y++)
                    {
                        acc += px[y * 256 + x];
                    }
                    lut[x] = acc / 32f;
                }
            }
            finally
            {
                RenderTexture.active = prev;
                rt.Release();
                Object.DestroyImmediate(rt);
                Object.DestroyImmediate(read);
            }
        }

        Texture2D lutTex = new Texture2D(256, 1, TextureFormat.RGBA32, false, true);
        lutTex.wrapMode = TextureWrapMode.Clamp;
        lutTex.filterMode = FilterMode.Bilinear;
        lutTex.SetPixels(lut);
        lutTex.Apply(false);
        lutTex.hideFlags = HideFlags.HideAndDontSave;
        return lutTex;
    }

    private void BakeGradientMapPreview()
    {
        if (gmSource == null)
        {
            SetStatus("请先指定要上色的源贴图。");
            return;
        }
        if (!GradientReady())
        {
            SetStatus("请先指定渐变（渐变栏或渐变贴图）。");
            return;
        }

        int w = gmSource.width;
        int h = gmSource.height;
        int pw = w, ph = h;
        int longSide = Mathf.Max(w, h);
        if (longSide > 512)
        {
            float scale = 512f / longSide;
            pw = Mathf.Max(1, Mathf.RoundToInt(w * scale));
            ph = Mathf.Max(1, Mathf.RoundToInt(h * scale));
        }

        if (gmPreview != null)
        {
            DestroyImmediate(gmPreview);
            gmPreview = null;
        }
        gmPreview = RenderGradientMap(pw, ph);
        if (gmPreview != null)
        {
            SetStatus("已刷新渐变映射预览。");
        }
    }

    private Texture2D RenderGradientMap(int targetW, int targetH)
    {
        Shader sh = GetGradientMapShader();
        if (sh == null || !sh.isSupported)
        {
            SetStatus("内部渐变映射 Shader 创建/编译失败，请检查控制台。");
            return null;
        }

        Texture2D lut = BuildGradientLut();
        if (lut == null)
        {
            SetStatus("渐变 LUT 生成失败。");
            return null;
        }

        bool srgb = IsSourceSRGB(gmSource);
        RenderTexture rt = new RenderTexture(targetW, targetH, 0, RenderTextureFormat.ARGB32,
            srgb ? RenderTextureReadWrite.sRGB : RenderTextureReadWrite.Linear);
        rt.hideFlags = HideFlags.HideAndDontSave;
        rt.Create();

        Material mat = new Material(sh);
        mat.hideFlags = HideFlags.HideAndDontSave;
        Texture2D output;
        RenderTexture prev = RenderTexture.active;
        try
        {
            mat.SetTexture("_GradLUT", lut);
            mat.SetFloat("_Reverse", gmReverse ? 1f : 0f);
            mat.SetFloat("_Dither", gmDither ? 1f : 0f);
            Graphics.Blit(gmSource, rt, mat);

            RenderTexture.active = rt;
            output = new Texture2D(targetW, targetH, TextureFormat.RGBA32, false);
            output.ReadPixels(new Rect(0, 0, targetW, targetH), 0, 0);
            output.Apply(false);
        }
        finally
        {
            RenderTexture.active = prev;
            rt.Release();
            Object.DestroyImmediate(rt);
            Object.DestroyImmediate(mat);
            Object.DestroyImmediate(lut);
        }
        return output;
    }

    private void ExportGradientMapPng()
    {
        if (gmSource == null || !GradientReady())
        {
            SetStatus("源贴图或渐变未指定。");
            return;
        }

        string fileName = string.IsNullOrEmpty(gmOutputName)
            ? (gmSource.name + "_GradientMap")
            : gmOutputName;
        string absPath = ResolveVFXExportPath("导出渐变映射 PNG", fileName, "png",
            AssetDatabase.GetAssetPath(gmSource), true);
        if (string.IsNullOrEmpty(absPath))
        {
            return;
        }

        Texture2D tex = RenderGradientMap(gmSource.width, gmSource.height);
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
            TextureImporter srcImporter = AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(gmSource)) as TextureImporter;
            TextureImporter importer = AssetImporter.GetAtPath(rel) as TextureImporter;
            if (importer != null)
            {
                importer.textureType = srcImporter != null ? srcImporter.textureType : TextureImporterType.Default;
                importer.sRGBTexture = IsSourceSRGB(gmSource);
                importer.mipmapEnabled = srcImporter != null && srcImporter.mipmapEnabled;
                importer.wrapMode = srcImporter != null ? srcImporter.wrapMode : TextureWrapMode.Clamp;
                if (srcImporter != null)
                {
                    importer.alphaSource = srcImporter.alphaSource;
                    importer.alphaIsTransparency = srcImporter.alphaIsTransparency;
                }
                else
                {
                    importer.alphaSource = TextureImporterAlphaSource.FromInput;
                }
                importer.SaveAndReimport();
            }
            Texture2D loaded = AssetDatabase.LoadAssetAtPath<Texture2D>(rel);
            if (loaded != null)
            {
                EditorGUIUtility.PingObject(loaded);
            }
            SetStatus("渐变映射已导出：" + rel);
        }
        else
        {
            SetStatus("已导出到项目外（未导入 Unity）：" + absPath);
        }
    }
}
