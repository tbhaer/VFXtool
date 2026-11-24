using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

/// <summary>
/// 模块七：序列贴图拆分 / 合并。
/// 拆分：选一张 N×M 序列帧大图，按行列数拆成 N*M 张单帧 PNG（从左到右、从上到下，00 起编号）。
/// 合并：载入 N*M 张单帧（同顺序），按行列合成一张序列大图，可选每帧输出尺寸。
/// GPU 裁剪 / 缩放，不要求源贴图开启 Read/Write。
/// </summary>
public partial class VFXToolboxWindow
{
    private static readonly string[] SeqModes = { "拆分序列（大图→单帧）", "合并序列（单帧→大图）" };
    private int seqMode;

    // 拆分
    [SerializeField] private Texture2D sqSource;
    [SerializeField] private int sqCols = 3;
    [SerializeField] private int sqRows = 3;
    [SerializeField] private DefaultAsset sqSplitFolder;
    [SerializeField] private string sqSplitPrefix = "";
    // 拆分模式：0 = 原图均分（不能整除时居中裁掉边缘零头像素，不缩放）；1 = 统一新尺寸（每帧缩放）
    [SerializeField] private int sqSplitMode;
    private static readonly string[] SqSplitModes = { "原图均分（取整，边缘零头居中裁掉）", "统一新尺寸（拆分后缩放每帧）" };
    [SerializeField] private int sqSplitSizeOption;
    [SerializeField] private int sqSplitCellW = 128;
    [SerializeField] private int sqSplitCellH = 128;

    // 合并
    [SerializeField] private List<Texture2D> sqFrames = new List<Texture2D>();
    [SerializeField] private int sqMergeCols = 3;
    [SerializeField] private int sqMergeRows = 3;
    private static readonly string[] SqSizeOptions = { "自动（首张尺寸）", "64 × 64", "128 × 128", "256 × 256", "512 × 512", "1024 × 1024", "自定义" };
    [SerializeField] private int sqSizeOption;
    [SerializeField] private int sqCellW = 128;
    [SerializeField] private int sqCellH = 128;
    [SerializeField] private string sqMergeName = "SequenceMerged";

    private Texture2D sqMergePreview;
    private Vector2 sqFrameScroll;
    private static Shader sqShader;

    private const string SqShaderSource = @"
Shader ""Hidden/VFXToolboxSequence""
{
    Properties { _Tex(""Tex"", 2D) = ""black"" {} }
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
            sampler2D _Tex;
            float4 _Crop;      // xy = 缩放, zw = 偏移
            float4 _HalfTexel; // xy = 源贴图半纹素 1/(2w),1/(2h)
            fixed4 frag(v2f_img i) : SV_Target
            {
                float2 mapped = i.uv * _Crop.xy + _Crop.zw;
                // 只写当前格子：格子之外的像素直接丢弃，避免覆盖其他帧
                if (mapped.x < 0.0 || mapped.x > 1.0 || mapped.y < 0.0 || mapped.y > 1.0) discard;
                // 边缘半纹素钳制，防止 Repeat 包裹采样到对侧内容
                float2 uv = clamp(mapped, _HalfTexel.xy, 1.0 - _HalfTexel.xy);
                return tex2D(_Tex, uv);
            }
            ENDCG
        }
    }
}";

    private void InitSequenceTool()
    {
    }

    private static Shader GetSequenceShader()
    {
        if (sqShader == null)
        {
            sqShader = ShaderUtil.CreateShaderAsset(SqShaderSource);
        }
        return sqShader;
    }

    private void DrawSequenceTool()
    {
        EditorGUILayout.LabelField("序列贴图拆分 / 合并", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "帧顺序统一为：从左到右、从上到下（与粒子 Texture Sheet 动画一致），编号从 00 开始。\n" +
            "原图均分：不能整除时按 floor 取整，居中裁掉边缘零头像素（如 512 拆 3 列 → 用 510，每帧 170）。\n" +
            "统一新尺寸：按理想比例取每帧后缩放到指定尺寸，不要求 2 的幂。",
            MessageType.None);

        seqMode = GUILayout.Toolbar(seqMode, SeqModes, EditorStyles.miniButton, GUILayout.Height(22));
        EditorGUILayout.Space(4);

        if (seqMode == 0)
        {
            DrawSequenceSplit();
        }
        else
        {
            DrawSequenceMerge();
        }
    }

    // ================= 拆分 =================

    private void DrawSequenceSplit()
    {
        EditorGUILayout.BeginHorizontal();
        sqSource = (Texture2D)EditorGUILayout.ObjectField("序列大图", sqSource, typeof(Texture2D), false);
        if (GUILayout.Button("使用选中贴图", GUILayout.Width(110)))
        {
            Texture2D t = Selection.activeObject as Texture2D;
            if (t != null)
            {
                sqSource = t;
            }
        }
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.BeginHorizontal();
        sqCols = EditorGUILayout.IntField("列数（横向）", Mathf.Max(1, sqCols));
        sqRows = EditorGUILayout.IntField("行数（纵向）", Mathf.Max(1, sqRows));
        EditorGUILayout.EndHorizontal();

        if (sqSource != null)
        {
            int cols = Mathf.Max(1, sqCols);
            int rows = Mathf.Max(1, sqRows);
            int naturalW = Mathf.FloorToInt(sqSource.width / (float)cols);
            int naturalH = Mathf.FloorToInt(sqSource.height / (float)rows);

            sqSplitMode = EditorGUILayout.Popup("拆分模式", sqSplitMode, SqSplitModes);
            int frameW, frameH;
            if (sqSplitMode == 1)
            {
                sqSplitSizeOption = EditorGUILayout.Popup("单帧尺寸", sqSplitSizeOption, SqSizeOptions);
                if (sqSplitSizeOption == 6)
                {
                    EditorGUILayout.BeginHorizontal();
                    sqSplitCellW = EditorGUILayout.IntField("单帧宽", Mathf.Clamp(sqSplitCellW, 1, 8192));
                    sqSplitCellH = EditorGUILayout.IntField("单帧高", Mathf.Clamp(sqSplitCellH, 1, 8192));
                    EditorGUILayout.EndHorizontal();
                }
                frameW = ResolveSplitSize(sqSplitSizeOption, sqSplitCellW, naturalW);
                frameH = ResolveSplitSize(sqSplitSizeOption, sqSplitCellH, naturalH);
            }
            else
            {
                frameW = naturalW;
                frameH = naturalH;
            }

            int usedW = naturalW * cols;
            int usedH = naturalH * rows;
            EditorGUILayout.LabelField(
                string.Format("大图 {0} × {1}，单帧 {2} × {3}，共 {4} 帧",
                    sqSource.width, sqSource.height, frameW, frameH, cols * rows),
                EditorStyles.miniLabel);

            if (sqSplitMode == 0)
            {
                if (usedW != sqSource.width || usedH != sqSource.height)
                {
                    EditorGUILayout.HelpBox(
                        string.Format("无法整除：将居中使用原图 {0} × {1} 区域（左右各裁 {2}px、上下各裁 {3}px），每帧 {4} × {5}，不缩放。",
                            usedW, usedH,
                            (sqSource.width - usedW) / 2f, (sqSource.height - usedH) / 2f,
                            frameW, frameH),
                        MessageType.None);
                }
            }
            else
            {
                EditorGUILayout.HelpBox(
                    string.Format("每帧按理想比例取样后缩放到 {0} × {1}（不需要 2 的幂）。", frameW, frameH),
                    MessageType.None);
            }

            if (frameW <= 0 || frameH <= 0 || naturalW <= 0 || naturalH <= 0)
            {
                EditorGUILayout.HelpBox("单帧尺寸为 0，请减小行列数。", MessageType.Warning);
            }
        }

        DrawVFXExportMode();
        using (new EditorGUI.DisabledScope(vfxExportMode != VFXExportMode.ChooseLocation))
        {
            sqSplitFolder = (DefaultAsset)EditorGUILayout.ObjectField("输出目录", sqSplitFolder, typeof(DefaultAsset), false);
        }
        sqSplitPrefix = EditorGUILayout.TextField("文件名前缀",
            string.IsNullOrEmpty(sqSplitPrefix) && sqSource != null ? sqSource.name : sqSplitPrefix);
        string folder = GetSqSplitFolder();
        EditorGUILayout.LabelField(vfxExportMode == VFXExportMode.ChooseLocation
            ? "将输出到：" + folder + "/<前缀>_00.png ..."
            : "原址模式输出到序列图目录，文件前缀：" + sqSplitPrefix, EditorStyles.miniLabel);

        EditorGUILayout.Space(4);
        EditorGUI.BeginDisabledGroup(sqSource == null);
        if (GUILayout.Button("拆分为单帧 PNG", GUILayout.Height(26)))
        {
            ExportSequenceFrames(folder);
        }
        EditorGUI.EndDisabledGroup();

        DrawSheetPreview();
    }

    private string GetSqSplitFolder()
    {
        if (sqSplitFolder != null)
        {
            string p = AssetDatabase.GetAssetPath(sqSplitFolder);
            if (AssetDatabase.IsValidFolder(p))
            {
                return p;
            }
        }
        if (sqSource != null)
        {
            string p = AssetDatabase.GetAssetPath(sqSource);
            if (!string.IsNullOrEmpty(p))
            {
                return Path.GetDirectoryName(p).Replace('\\', '/');
            }
        }
        return "Assets";
    }

    private void DrawSheetPreview()
    {
        if (sqSource == null)
        {
            return;
        }
        EditorGUILayout.Space(6);
        float size = Mathf.Min(300f, position.width - 80f);
        float aspect = (float)sqSource.height / sqSource.width;
        float h = size * aspect;
        Rect r = GUILayoutUtility.GetRect(size, h, GUILayout.Width(size), GUILayout.Height(h));
        EditorGUI.DrawRect(r, new Color(0f, 0f, 0f, 0.35f));
        GUI.DrawTexture(r, sqSource, ScaleMode.ScaleToFit, false);

        int cols = Mathf.Max(1, sqCols);
        int rows = Mathf.Max(1, sqRows);

        // 格子在预览图中的横向/纵向起止比例（原图均分模式按居中裁剪后的区域）
        float x0 = 0f, x1 = 1f, yTop0 = 0f, yTop1 = 1f;
        if (sqSplitMode == 0)
        {
            int naturalW = Mathf.FloorToInt(sqSource.width / (float)cols);
            int naturalH = Mathf.FloorToInt(sqSource.height / (float)rows);
            int usedW = naturalW * cols;
            int usedH = naturalH * rows;
            x0 = Mathf.FloorToInt((sqSource.width - usedW) / 2f) / (float)sqSource.width;
            x1 = x0 + usedW / (float)sqSource.width;
            // GUI 坐标从上往下，顶部裁边
            yTop0 = Mathf.FloorToInt((sqSource.height - usedH) / 2f) / (float)sqSource.height;
            yTop1 = yTop0 + usedH / (float)sqSource.height;
        }

        Handles.BeginGUI();
        Color old = Handles.color;
        Handles.color = new Color(1f, 0.85f, 0.2f, 0.9f);
        for (int c = 1; c < cols; c++)
        {
            float t = x0 + (x1 - x0) * c / cols;
            float x = r.x + r.width * t;
            Handles.DrawLine(new Vector3(x, r.y + r.height * yTop0), new Vector3(x, r.y + r.height * yTop1));
        }
        for (int row = 1; row < rows; row++)
        {
            float t = yTop0 + (yTop1 - yTop0) * row / rows;
            float y = r.y + r.height * t;
            Handles.DrawLine(new Vector3(r.x + r.width * x0, y), new Vector3(r.x + r.width * x1, y));
        }
        // 原图均分模式下，用绿色框标出实际使用区域（框外是被裁掉的零头）
        if (sqSplitMode == 0 && (x0 > 0f || yTop0 > 0f || x1 < 1f || yTop1 < 1f))
        {
            Handles.color = new Color(0.4f, 1f, 0.5f, 0.9f);
            Rect usedRect = new Rect(
                r.x + r.width * x0, r.y + r.height * yTop0,
                r.width * (x1 - x0), r.height * (yTop1 - yTop0));
            Handles.DrawSolidRectangleWithOutline(usedRect, new Color(0f, 0f, 0f, 0f), new Color(0.4f, 1f, 0.5f, 0.9f));
        }
        Handles.color = old;
        Handles.EndGUI();

        for (int row = 0; row < rows; row++)
        {
            for (int c = 0; c < cols; c++)
            {
                int idx = row * cols + c;
                float cx = x0 + (x1 - x0) * (c + 0.5f) / cols;
                float cy = yTop0 + (yTop1 - yTop0) * (row + 0.5f) / rows;
                Rect cell = new Rect(r.x + r.width * cx - 10f, r.y + r.height * cy - 8f, 30f, 16f);
                GUI.Label(cell, idx.ToString("00"), EditorStyles.whiteBoldLabel);
            }
        }
    }

    private void ExportSequenceFrames(string folder)
    {
        int cols = Mathf.Max(1, sqCols);
        int rows = Mathf.Max(1, sqRows);

        // 理想格子的整数尺寸（floor 取整）
        int naturalW = Mathf.FloorToInt(sqSource.width / (float)cols);
        int naturalH = Mathf.FloorToInt(sqSource.height / (float)rows);

        int cellW;
        int cellH;
        if (sqSplitMode == 1)
        {
            cellW = ResolveSplitSize(sqSplitSizeOption, sqSplitCellW, naturalW);
            cellH = ResolveSplitSize(sqSplitSizeOption, sqSplitCellH, naturalH);
        }
        else
        {
            cellW = naturalW;
            cellH = naturalH;
        }

        if (cellW <= 0 || cellH <= 0 || naturalW <= 0 || naturalH <= 0)
        {
            SetStatus("单帧尺寸为 0，请检查行列数。");
            return;
        }

        // 原图均分模式：居中使用 cell*N 的区域，边缘零头被裁掉
        int usedW = naturalW * cols;
        int usedH = naturalH * rows;
        int startXPx = Mathf.FloorToInt((sqSource.width - usedW) / 2f);
        int startTopPx = Mathf.FloorToInt((sqSource.height - usedH) / 2f);

        string prefix = string.IsNullOrEmpty(sqSplitPrefix) ? sqSource.name : sqSplitPrefix;
        string[] outputNames = new string[cols * rows];
        for (int i = 0; i < outputNames.Length; i++)
        {
            outputNames[i] = "{name}_" + i.ToString("00") + ".png";
        }
        folder = ResolveVFXBatchFolder(folder, AssetDatabase.GetAssetPath(sqSource), ref prefix, outputNames);
        string absFolder = ToAbsolutePath(folder);
        if (string.IsNullOrEmpty(absFolder))
        {
            SetStatus("输出目录无效。");
            return;
        }
        Directory.CreateDirectory(absFolder);
        if (!ConfirmOverwrite(absFolder, prefix, cols * rows))
        {
            return;
        }

        bool srgb = IsSourceSRGB(sqSource);
        TextureImporter srcImporter = AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(sqSource)) as TextureImporter;
        Material mat = new Material(GetSequenceShader());
        mat.hideFlags = HideFlags.HideAndDontSave;
        mat.SetTexture("_Tex", sqSource);
        mat.SetVector("_HalfTexel", new Vector4(0.5f / sqSource.width, 0.5f / sqSource.height, 0f, 0f));

        RenderTexture rt = new RenderTexture(cellW, cellH, 0, RenderTextureFormat.ARGB32,
            srgb ? RenderTextureReadWrite.sRGB : RenderTextureReadWrite.Linear);
        rt.hideFlags = HideFlags.HideAndDontSave;
        rt.Create();
        RenderTexture prev = RenderTexture.active;

        int done = 0;
        try
        {
            for (int row = 0; row < rows; row++)
            {
                for (int c = 0; c < cols; c++)
                {
                    int idx = row * cols + c;
                    EditorUtility.DisplayProgressBar("拆分序列", prefix + "_" + idx.ToString("00"), (float)done / (cols * rows));

                    Vector4 crop;
                    if (sqSplitMode == 1)
                    {
                        // 统一新尺寸：按理想分数比例取样，GPU 缩放到目标单帧尺寸
                        crop = new Vector4(
                            1f / cols, 1f / rows,
                            (float)c / cols,
                            1f - (float)(row + 1) / rows);
                    }
                    else
                    {
                        // 原图均分：每帧精确对应原图上 naturalW×naturalH 的像素块，不缩放
                        float scaleX = (float)naturalW / sqSource.width;
                        float scaleY = (float)naturalH / sqSource.height;
                        float offsetX = (float)(startXPx + c * naturalW) / sqSource.width;
                        int cellBottomPx = sqSource.height - (startTopPx + (row + 1) * naturalH);
                        float offsetY = (float)cellBottomPx / sqSource.height;
                        crop = new Vector4(scaleX, scaleY, offsetX, offsetY);
                    }
                    mat.SetVector("_Crop", crop);

                    RenderTexture.active = null;
                    Graphics.Blit(null, rt, mat);
                    RenderTexture.active = rt;
                    Texture2D frame = new Texture2D(cellW, cellH, TextureFormat.RGBA32, false);
                    frame.ReadPixels(new Rect(0, 0, cellW, cellH), 0, 0);
                    frame.Apply(false);
                    byte[] png = frame.EncodeToPNG();
                    Object.DestroyImmediate(frame);
                    File.WriteAllBytes(Path.Combine(absFolder, prefix + "_" + idx.ToString("00") + ".png"), png);
                    done++;
                }
            }
        }
        finally
        {
            RenderTexture.active = prev;
            rt.Release();
            Object.DestroyImmediate(rt);
            Object.DestroyImmediate(mat);
            EditorUtility.ClearProgressBar();
        }

        AssetDatabase.Refresh();
        ConfigureFrameImporters(folder, prefix, cols * rows, srgb, srcImporter);

        string first = (folder.TrimEnd('/') + "/" + prefix + "_00.png").Replace('\\', '/');
        Texture2D loaded = AssetDatabase.LoadAssetAtPath<Texture2D>(first);
        if (loaded != null)
        {
            EditorGUIUtility.PingObject(loaded);
        }

        if (sqSplitMode == 1)
        {
            SetStatus(string.Format("拆分完成：{0} 帧（{1}×{2}），每帧缩放为 {3}×{4}，输出到 {5}/",
                done, cols, rows, cellW, cellH, folder));
        }
        else if (usedW != sqSource.width || usedH != sqSource.height)
        {
            SetStatus(string.Format("拆分完成：{0} 帧，原图居中裁剪到 {1}×{2}，单帧 {3}×{4}，输出到 {5}/",
                done, usedW, usedH, cellW, cellH, folder));
        }
        else
        {
            SetStatus(string.Format("拆分完成：{0} 帧（{1}×{2}，单帧 {3}×{4}）输出到 {5}/",
                done, cols, rows, cellW, cellH, folder));
        }
    }

    private bool ConfirmOverwrite(string absFolder, string prefix, int count)
    {
        bool exists = false;
        for (int i = 0; i < count; i++)
        {
            if (File.Exists(Path.Combine(absFolder, prefix + "_" + i.ToString("00") + ".png")))
            {
                exists = true;
                break;
            }
        }
        if (exists)
        {
            return EditorUtility.DisplayDialog("文件已存在", "输出目录中已有同名序列帧，是否覆盖？", "覆盖", "取消");
        }
        return true;
    }

    private void ConfigureFrameImporters(string folder, string prefix, int count, bool srgb, TextureImporter srcImporter)
    {
        if (!folder.StartsWith("Assets", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        for (int i = 0; i < count; i++)
        {
            string rel = (folder.TrimEnd('/') + "/" + prefix + "_" + i.ToString("00") + ".png").Replace('\\', '/');
            TextureImporter importer = AssetImporter.GetAtPath(rel) as TextureImporter;
            if (importer != null)
            {
                ApplyAlphaAwareImporter(importer, srgb, srcImporter);
            }
        }
    }

    /// <summary>按源贴图设置导入器，保留透明背景：Alpha 来源、alphaIsTransparency 与贴图类型一致，避免透明边黑边。</summary>
    private static void ApplyAlphaAwareImporter(TextureImporter importer, bool srgb, TextureImporter srcImporter)
    {
        importer.textureType = srcImporter != null ? srcImporter.textureType : TextureImporterType.Default;
        importer.sRGBTexture = srgb;
        importer.mipmapEnabled = false;
        importer.wrapMode = TextureWrapMode.Clamp;
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

    // ================= 合并 =================

    private void DrawSequenceMerge()
    {
        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("添加 Project 选中贴图（按文件名排序）", GUILayout.Height(22)))
        {
            AddSelectedTextures();
        }
        if (GUILayout.Button("移除选中行", GUILayout.Width(110), GUILayout.Height(22)))
        {
            RemoveSelectedFrameRows();
        }
        if (GUILayout.Button("清空", GUILayout.Width(70), GUILayout.Height(22)))
        {
            sqFrames.Clear();
        }
        EditorGUILayout.EndHorizontal();

        sqFrameScroll = EditorGUILayout.BeginScrollView(sqFrameScroll, GUILayout.MinHeight(120), GUILayout.MaxHeight(220));
        for (int i = 0; i < sqFrames.Count; i++)
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
            GUILayout.Label(i.ToString("00"), GUILayout.Width(28));
            sqFrames[i] = (Texture2D)EditorGUILayout.ObjectField(sqFrames[i], typeof(Texture2D), false, GUILayout.MinWidth(120));
            if (GUILayout.Button("↑", GUILayout.Width(24)))
            {
                SwapFrames(i, i - 1);
            }
            if (GUILayout.Button("↓", GUILayout.Width(24)))
            {
                SwapFrames(i, i + 1);
            }
            if (GUILayout.Button("×", GUILayout.Width(24)))
            {
                sqFrames.RemoveAt(i);
                GUIUtility.ExitGUI();
            }
            EditorGUILayout.EndHorizontal();
        }
        EditorGUILayout.EndScrollView();

        EditorGUILayout.Space(4);
        EditorGUILayout.BeginHorizontal();
        sqMergeCols = EditorGUILayout.IntField("列数", Mathf.Max(1, sqMergeCols));
        sqMergeRows = EditorGUILayout.IntField("行数", Mathf.Max(1, sqMergeRows));
        EditorGUILayout.EndHorizontal();
        int need = Mathf.Max(1, sqMergeCols) * Mathf.Max(1, sqMergeRows);
        EditorGUILayout.LabelField(
            string.Format("已载入 {0} 张，网格需要 {1} 张{2}", sqFrames.Count, need, sqFrames.Count == need ? "" : "（数量不一致）"),
            EditorStyles.miniLabel);

        EditorGUILayout.LabelField("输出设置", EditorStyles.boldLabel);
        sqSizeOption = EditorGUILayout.Popup("单帧尺寸", sqSizeOption, SqSizeOptions);
        if (sqSizeOption == 6)
        {
            EditorGUILayout.BeginHorizontal();
            sqCellW = EditorGUILayout.IntField("单帧宽", Mathf.Clamp(sqCellW, 1, 8192));
            sqCellH = EditorGUILayout.IntField("单帧高", Mathf.Clamp(sqCellH, 1, 8192));
            EditorGUILayout.EndHorizontal();
        }
        ComputeCellSize(out int cw, out int ch);
        EditorGUILayout.LabelField(
            string.Format("大图输出尺寸：{0} × {1}", cw * Mathf.Max(1, sqMergeCols), ch * Mathf.Max(1, sqMergeRows)),
            EditorStyles.miniLabel);
        sqMergeName = EditorGUILayout.TextField("导出文件名", sqMergeName);
        DrawVFXExportMode();

        EditorGUILayout.Space(4);
        EditorGUILayout.BeginHorizontal();
        EditorGUI.BeginDisabledGroup(sqFrames.Count != need);
        if (GUILayout.Button("生成预览", GUILayout.Height(26)))
        {
            BakeMergePreview();
        }
        if (GUILayout.Button("导出 PNG…", GUILayout.Height(26)))
        {
            ExportMergedSheet();
        }
        EditorGUI.EndDisabledGroup();
        EditorGUILayout.EndHorizontal();

        if (sqMergePreview != null)
        {
            EditorGUILayout.Space(6);
            float size = Mathf.Min(300f, position.width - 80f);
            float aspect = (float)sqMergePreview.height / sqMergePreview.width;
            float h = size * aspect;
            Rect r = GUILayoutUtility.GetRect(size, h, GUILayout.Width(size), GUILayout.Height(h));
            EditorGUI.DrawRect(r, new Color(0f, 0f, 0f, 0.35f));
            GUI.DrawTexture(r, sqMergePreview, ScaleMode.ScaleToFit, false);
        }
    }

    private void AddSelectedTextures()
    {
        List<Texture2D> picked = new List<Texture2D>();
        foreach (Object o in Selection.GetFiltered(typeof(Texture2D), SelectionMode.Assets))
        {
            Texture2D t = (Texture2D)o;
            if (t != null && !sqFrames.Contains(t) && !picked.Contains(t))
            {
                picked.Add(t);
            }
        }
        picked.Sort((a, b) =>
            string.CompareOrdinal(AssetDatabase.GetAssetPath(a), AssetDatabase.GetAssetPath(b)));
        sqFrames.AddRange(picked);
        SetStatus(string.Format("已添加 {0} 张贴图（按文件名排序）。", picked.Count));
    }

    private void RemoveSelectedFrameRows()
    {
        List<Texture2D> sel = new List<Texture2D>();
        foreach (Object o in Selection.GetFiltered(typeof(Texture2D), SelectionMode.Assets))
        {
            Texture2D t = (Texture2D)o;
            if (t != null && sqFrames.Contains(t))
            {
                sel.Add(t);
            }
        }
        foreach (Texture2D t in sel)
        {
            sqFrames.Remove(t);
        }
    }

    private void SwapFrames(int a, int b)
    {
        if (a < 0 || a >= sqFrames.Count || b < 0 || b >= sqFrames.Count)
        {
            return;
        }
        Texture2D tmp = sqFrames[a];
        sqFrames[a] = sqFrames[b];
        sqFrames[b] = tmp;
    }

    /// <summary>拆分-统一尺寸模式下，按尺寸选项解析单帧的一条边长（自动 = floor 取整值）。</summary>
    private static int ResolveSplitSize(int option, int customValue, int naturalValue)
    {
        switch (option)
        {
            case 1: return 64;
            case 2: return 128;
            case 3: return 256;
            case 4: return 512;
            case 5: return 1024;
            case 6: return Mathf.Clamp(customValue, 1, 8192);
            default: return Mathf.Max(1, naturalValue);
        }
    }

    private void ComputeCellSize(out int cellW, out int cellH)
    {        switch (sqSizeOption)
        {
            case 0:
                if (sqFrames.Count > 0 && sqFrames[0] != null)
                {
                    cellW = sqFrames[0].width;
                    cellH = sqFrames[0].height;
                }
                else
                {
                    cellW = 128;
                    cellH = 128;
                }
                break;
            case 1: cellW = cellH = 64; break;
            case 2: cellW = cellH = 128; break;
            case 3: cellW = cellH = 256; break;
            case 4: cellW = cellH = 512; break;
            case 5: cellW = cellH = 1024; break;
            default:
                cellW = Mathf.Clamp(sqCellW, 1, 8192);
                cellH = Mathf.Clamp(sqCellH, 1, 8192);
                break;
        }
    }

    private Texture2D RenderMergedSheet(int cellW, int cellH)
    {
        int cols = Mathf.Max(1, sqMergeCols);
        int rows = Mathf.Max(1, sqMergeRows);
        int w = cellW * cols;
        int h = cellH * rows;
        bool srgb = sqFrames.Count > 0 && IsSourceSRGB(sqFrames[0]);

        Shader sh = GetSequenceShader();
        if (sh == null || !sh.isSupported)
        {
            SetStatus("内部序列 Shader 创建/编译失败，请检查控制台。");
            return null;
        }

        RenderTexture rt = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32,
            srgb ? RenderTextureReadWrite.sRGB : RenderTextureReadWrite.Linear);
        rt.hideFlags = HideFlags.HideAndDontSave;
        rt.Create();
        RenderTexture prev = RenderTexture.active;
        RenderTexture.active = rt;
        GL.Clear(false, true, new Color(0f, 0f, 0f, 0f));

        Material mat = new Material(sh);
        mat.hideFlags = HideFlags.HideAndDontSave;
        try
        {
            for (int i = 0; i < sqFrames.Count && i < cols * rows; i++)
            {
                if (sqFrames[i] == null)
                {
                    continue;
                }
                int row = i / cols;
                int c = i % cols;
                mat.SetTexture("_Tex", sqFrames[i]);
                mat.SetVector("_HalfTexel", new Vector4(0.5f / sqFrames[i].width, 0.5f / sqFrames[i].height, 0f, 0f));
                // 大图 UV 中该格子区域 [c/cols,(c+1)/cols] 映射回单帧 UV 0..1
                mat.SetVector("_Crop", new Vector4(
                    cols,
                    rows,
                    -c,
                    row + 1 - rows));
                Graphics.Blit(null, rt, mat);
            }

            Texture2D output = new Texture2D(w, h, TextureFormat.RGBA32, false);
            output.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            output.Apply(false);
            return output;
        }
        finally
        {
            RenderTexture.active = prev;
            rt.Release();
            Object.DestroyImmediate(rt);
            Object.DestroyImmediate(mat);
        }
    }

    private void BakeMergePreview()
    {
        int cols = Mathf.Max(1, sqMergeCols);
        int rows = Mathf.Max(1, sqMergeRows);
        if (sqFrames.Count != cols * rows)
        {
            SetStatus("贴图数量与行列数不一致。");
            return;
        }
        ComputeCellSize(out int cw, out int ch);
        int pw = Mathf.Clamp(cw * cols, 1, 1024);
        int ph = Mathf.Clamp(ch * rows, 1, 1024);
        Texture2D full = RenderMergedSheet(cw, ch);
        if (full == null)
        {
            return;
        }
        // 预览缩到 1024 以内
        Texture2D preview = full;
        if (full.width > 1024 || full.height > 1024)
        {
            bool previewSRGB = sqFrames.Count > 0 && IsSourceSRGB(sqFrames[0]);
            RenderTexture srt = new RenderTexture(pw, ph, 0, RenderTextureFormat.ARGB32,
                previewSRGB ? RenderTextureReadWrite.sRGB : RenderTextureReadWrite.Linear);
            RenderTexture p = RenderTexture.active;
            Graphics.Blit(full, srt);
            RenderTexture.active = srt;
            preview = new Texture2D(pw, ph, TextureFormat.RGBA32, false);
            preview.ReadPixels(new Rect(0, 0, pw, ph), 0, 0);
            preview.Apply(false);
            RenderTexture.active = p;
            srt.Release();
            Object.DestroyImmediate(srt);
            Object.DestroyImmediate(full);
        }
        if (sqMergePreview != null)
        {
            DestroyImmediate(sqMergePreview);
        }
        sqMergePreview = preview;
        SetStatus("已生成合并预览。");
    }

    private void ExportMergedSheet()
    {
        int cols = Mathf.Max(1, sqMergeCols);
        int rows = Mathf.Max(1, sqMergeRows);
        if (sqFrames.Count != cols * rows)
        {
            SetStatus("贴图数量与行列数不一致，无法合并。");
            return;
        }
        ComputeCellSize(out int cw, out int ch);
        string fileName = string.IsNullOrEmpty(sqMergeName) ? "SequenceMerged" : sqMergeName;
        string sourcePath = sqFrames.Count > 0 && sqFrames[0] != null
            ? AssetDatabase.GetAssetPath(sqFrames[0])
            : null;
        string absPath = ResolveVFXExportPath("导出合并序列大图", fileName, "png", sourcePath, false);
        if (string.IsNullOrEmpty(absPath))
        {
            return;
        }

        Texture2D tex = RenderMergedSheet(cw, ch);
        if (tex == null)
        {
            return;
        }
        byte[] png = tex.EncodeToPNG();
        Object.DestroyImmediate(tex);
        File.WriteAllBytes(absPath, png);
        AssetDatabase.Refresh();

        string rel = ToAssetsRelative(absPath);
        if (rel != null)
        {
            bool srgb = sqFrames.Count > 0 && IsSourceSRGB(sqFrames[0]);
            TextureImporter frameImporter = null;
            if (sqFrames.Count > 0 && sqFrames[0] != null)
            {
                frameImporter = AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(sqFrames[0])) as TextureImporter;
            }
            TextureImporter importer = AssetImporter.GetAtPath(rel) as TextureImporter;
            if (importer != null)
            {
                ApplyAlphaAwareImporter(importer, srgb, frameImporter);
            }
            Texture2D loaded = AssetDatabase.LoadAssetAtPath<Texture2D>(rel);
            if (loaded != null)
            {
                EditorGUIUtility.PingObject(loaded);
            }
            SetStatus(string.Format("合并完成：{0} 帧合成 {1} × {2} 大图：{3}",
                cols * rows, cw * cols, ch * rows, rel));
        }
        else
        {
            SetStatus("已导出到项目外（未导入 Unity）：" + absPath);
        }
    }

    private static bool IsSourceSRGB(Texture2D tex)
    {
        if (tex == null)
        {
            return true;
        }
        string path = AssetDatabase.GetAssetPath(tex);
        TextureImporter importer = string.IsNullOrEmpty(path) ? null : AssetImporter.GetAtPath(path) as TextureImporter;
        return importer == null || importer.sRGBTexture;
    }
}
