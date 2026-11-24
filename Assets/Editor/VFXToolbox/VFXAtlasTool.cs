using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// 模块十四：贴图拼合（PS 切片式画布）。
/// 画布可自由划分：一键按行列均分，或拖分割线任意切（格子可不等宽高）；
/// 从 Project 窗口把贴图拖到格子内，自动拉伸铺满该格（多选拖入按顺序依次填充）；
/// 最后用 GPU 合成一张 PNG，空格保持透明。不要求源贴图开启 Read/Write。
/// 常用于：把多张噪波/遮罩/元素图拼进一张图集，给一个 shader 取样不同的 UV 区域。
/// </summary>
public partial class VFXToolboxWindow
{
    // ===== 画布 =====
    [SerializeField] private int atlasCanvasW = 1024;
    [SerializeField] private int atlasCanvasH = 1024;
    // 输入框缓冲：聚焦时才编辑，失焦/切页签再应用，避免打字过程中反复缩放分割线
    [SerializeField] private int atlasEditW = 1024;
    [SerializeField] private int atlasEditH = 1024;
    [SerializeField] private int atlasSizePreset = 2;

    // 内部分割线（像素坐标，均从左上角起算；竖线存 x，横线存 y）
    [SerializeField] private List<int> atlasVx = new List<int>();
    [SerializeField] private List<int> atlasHy = new List<int>();

    // 每格贴图，索引 = row * 列数 + col
    [SerializeField] private List<Texture2D> atlasCells = new List<Texture2D>();

    [SerializeField] private int atlasEvenCols = 2;
    [SerializeField] private int atlasEvenRows = 2;
    [SerializeField] private bool atlasInitDone;

    // ===== 导出 =====
    [SerializeField] private string atlasAssetName = "Atlas";
    [SerializeField] private bool atlasSRGB = true;
    [SerializeField] private bool atlasMipmap;

    // ===== 交互状态（不序列化） =====
    private Rect atlasCanvasRect;
    private int atlasSelIndex = -1;
    private int atlasDragAxis;      // 0 = 无，1 = 拖竖线，2 = 拖横线
    private int atlasDragIdx = -1;
    private int atlasHoverCell = -1; // 拖贴图经过的格子
    private static Texture2D atlasChecker;
    private static Shader atlasShader;

    private static readonly string[] AtlasSizePresetNames =
    {
        "自定义", "512 × 512", "1024 × 1024", "2048 × 2048", "1024 × 512", "512 × 256", "256 × 256", "4096 × 4096"
    };

    private const string AtlasShaderSource = @"
Shader ""Hidden/VFXToolboxAtlas""
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
            float4 _Crop;      // xy = 缩放, zw = 偏移（把画布 UV 映射回该格贴图 UV）
            float4 _HalfTexel; // xy = 源贴图半纹素 1/(2w),1/(2h)
            fixed4 frag(v2f_img i) : SV_Target
            {
                float2 mapped = i.uv * _Crop.xy + _Crop.zw;
                // 只写当前格子：格子之外的像素丢弃，避免覆盖其他格
                if (mapped.x < 0.0 || mapped.x > 1.0 || mapped.y < 0.0 || mapped.y > 1.0) discard;
                // 边缘半纹素钳制，防止 Repeat 包裹采样到对侧内容
                float2 uv = clamp(mapped, _HalfTexel.xy, 1.0 - _HalfTexel.xy);
                return tex2D(_Tex, uv);
            }
            ENDCG
        }
    }
}";

    private void InitAtlasTool()
    {
        if (atlasVx == null)
        {
            atlasVx = new List<int>();
        }
        if (atlasHy == null)
        {
            atlasHy = new List<int>();
        }
        if (atlasCells == null)
        {
            atlasCells = new List<Texture2D>();
        }
        atlasCanvasW = Mathf.Clamp(atlasCanvasW < 8 ? 1024 : atlasCanvasW, 8, 8192);
        atlasCanvasH = Mathf.Clamp(atlasCanvasH < 8 ? 1024 : atlasCanvasH, 8, 8192);

        if (!atlasInitDone)
        {
            atlasInitDone = true;
            SetEvenSplit(2, 2); // 默认 2×2，打开就能直接拖
        }

        NormalizeSplits();
        EnsureCellCount();

        atlasEditW = Mathf.Clamp(atlasEditW < 8 ? atlasCanvasW : atlasEditW, 8, 8192);
        atlasEditH = Mathf.Clamp(atlasEditH < 8 ? atlasCanvasH : atlasEditH, 8, 8192);
    }

    private static Shader GetAtlasShader()
    {
        if (atlasShader == null)
        {
            atlasShader = ShaderUtil.CreateShaderAsset(AtlasShaderSource);
        }
        return atlasShader;
    }

    private static Texture2D GetAtlasChecker()
    {
        if (atlasChecker != null)
        {
            return atlasChecker;
        }
        Texture2D t = new Texture2D(16, 16, TextureFormat.RGBA32, false);
        t.hideFlags = HideFlags.HideAndDontSave;
        t.wrapMode = TextureWrapMode.Repeat;
        t.filterMode = FilterMode.Point;
        Color a = new Color(0.21f, 0.21f, 0.21f, 1f);
        Color b = new Color(0.29f, 0.29f, 0.29f, 1f);
        Color[] px = new Color[16 * 16];
        for (int y = 0; y < 16; y++)
        {
            for (int x = 0; x < 16; x++)
            {
                px[y * 16 + x] = (((x / 8) + (y / 8)) & 1) == 0 ? a : b;
            }
        }
        t.SetPixels(px);
        t.Apply(false);
        atlasChecker = t;
        return t;
    }

    // ================= 网格模型 =================

    private int AtlasCols { get { return atlasVx.Count + 1; } }
    private int AtlasRows { get { return atlasHy.Count + 1; } }

    private static void NormalizeLineList(List<int> list, int size)
    {
        list.RemoveAll(v => v <= 0 || v >= size);
        list.Sort();
        for (int i = list.Count - 1; i > 0; i--)
        {
            if (list[i] == list[i - 1])
            {
                list.RemoveAt(i);
            }
        }
    }

    private void NormalizeSplits()
    {
        NormalizeLineList(atlasVx, atlasCanvasW);
        NormalizeLineList(atlasHy, atlasCanvasH);
    }

    private void EnsureCellCount()
    {
        int need = AtlasCols * AtlasRows;
        while (atlasCells.Count < need)
        {
            atlasCells.Add(null);
        }
        while (atlasCells.Count > need)
        {
            atlasCells.RemoveAt(atlasCells.Count - 1);
        }
    }

    /// <summary>格子像素矩形（左上为原点，y 向下）。</summary>
    private Rect CellRectPx(int row, int col)
    {
        int x0 = col <= 0 ? 0 : atlasVx[col - 1];
        int x1 = col >= atlasVx.Count ? atlasCanvasW : atlasVx[col];
        int y0 = row <= 0 ? 0 : atlasHy[row - 1];
        int y1 = row >= atlasHy.Count ? atlasCanvasH : atlasHy[row];
        return new Rect(x0, y0, Mathf.Max(0, x1 - x0), Mathf.Max(0, y1 - y0));
    }

    private static int CellIndexAtPoint(float x, float y, List<int> vx, List<int> hy)
    {
        int col = 0;
        while (col < vx.Count && x >= vx[col])
        {
            col++;
        }
        int row = 0;
        while (row < hy.Count && y >= hy[row])
        {
            row++;
        }
        return row * (vx.Count + 1) + col;
    }

    private int AtlasCellIndexAt(Vector2 guiPos)
    {
        if (!atlasCanvasRect.Contains(guiPos) || atlasCanvasRect.width <= 0f || atlasCanvasRect.height <= 0f)
        {
            return -1;
        }
        float px = (guiPos.x - atlasCanvasRect.x) / atlasCanvasRect.width * atlasCanvasW;
        float py = (guiPos.y - atlasCanvasRect.y) / atlasCanvasRect.height * atlasCanvasH;
        return CellIndexAtPoint(px, py, atlasVx, atlasHy);
    }

    /// <summary>
    /// 结构变化（改尺寸 / 均分 / 增删分割线）后重排贴图：
    /// 每个新格的“中心点”按画布比例折算回旧坐标，落在哪个旧格就继承哪张图。
    /// 这样拆分格子会复制同一张图、合并格子会保留覆盖中心那一格的图，符合直觉。
    /// </summary>
    private void RemapByCenter(List<int> oldVx, List<int> oldHy, int oldW, int oldH)
    {
        int oldCols = oldVx.Count + 1;
        int oldRows = oldHy.Count + 1;
        int oldCount = oldCols * oldRows;
        List<Texture2D> oldCells = new List<Texture2D>(oldCount);
        for (int i = 0; i < oldCount; i++)
        {
            oldCells.Add(i < atlasCells.Count ? atlasCells[i] : null);
        }

        int newCols = AtlasCols;
        int newRows = AtlasRows;
        List<Texture2D> fresh = new List<Texture2D>(newCols * newRows);
        for (int i = 0; i < newCols * newRows; i++)
        {
            fresh.Add(null);
        }

        if (oldW > 0 && oldH > 0 && atlasCanvasW > 0 && atlasCanvasH > 0)
        {
            for (int r = 0; r < newRows; r++)
            {
                for (int c = 0; c < newCols; c++)
                {
                    Rect px = CellRectPx(r, c);
                    float ox = (px.x + px.width * 0.5f) * oldW / atlasCanvasW;
                    float oy = (px.y + px.height * 0.5f) * oldH / atlasCanvasH;
                    int oi = CellIndexAtPoint(ox, oy, oldVx, oldHy);
                    if (oi >= 0 && oi < oldCells.Count)
                    {
                        fresh[r * newCols + c] = oldCells[oi];
                    }
                }
            }
        }

        atlasCells = fresh;
        if (atlasSelIndex >= atlasCells.Count)
        {
            atlasSelIndex = -1;
        }
    }

    private void ApplyCanvasSize()
    {
        int nw = Mathf.Clamp(atlasEditW, 8, 8192);
        int nh = Mathf.Clamp(atlasEditH, 8, 8192);
        atlasEditW = nw;
        atlasEditH = nh;
        if (nw == atlasCanvasW && nh == atlasCanvasH)
        {
            return;
        }

        List<int> oldVx = new List<int>(atlasVx);
        List<int> oldHy = new List<int>(atlasHy);
        int oldW = atlasCanvasW;
        int oldH = atlasCanvasH;

        // 分割线按比例跟着缩放到新画布
        for (int i = 0; i < atlasVx.Count; i++)
        {
            atlasVx[i] = Mathf.RoundToInt(atlasVx[i] * (float)nw / oldW);
        }
        for (int i = 0; i < atlasHy.Count; i++)
        {
            atlasHy[i] = Mathf.RoundToInt(atlasHy[i] * (float)nh / oldH);
        }
        atlasCanvasW = nw;
        atlasCanvasH = nh;
        NormalizeSplits();
        RemapByCenter(oldVx, oldHy, oldW, oldH);
        SetStatus(string.Format("画布尺寸已改为 {0} × {1}，分割线按比例缩放。", nw, nh));
    }

    private void SetEvenSplit(int cols, int rows)
    {
        cols = Mathf.Clamp(cols, 1, 32);
        rows = Mathf.Clamp(rows, 1, 32);

        List<int> oldVx = new List<int>(atlasVx);
        List<int> oldHy = new List<int>(atlasHy);
        int oldW = atlasCanvasW;
        int oldH = atlasCanvasH;

        atlasVx.Clear();
        atlasHy.Clear();
        for (int i = 1; i < cols; i++)
        {
            int x = Mathf.RoundToInt(atlasCanvasW * (float)i / cols);
            if (x > 0 && x < atlasCanvasW)
            {
                atlasVx.Add(x);
            }
        }
        for (int i = 1; i < rows; i++)
        {
            int y = Mathf.RoundToInt(atlasCanvasH * (float)i / rows);
            if (y > 0 && y < atlasCanvasH)
            {
                atlasHy.Add(y);
            }
        }
        NormalizeSplits();
        RemapByCenter(oldVx, oldHy, oldW, oldH);
    }

    private void AddSplit(bool vertical)
    {
        Rect target = atlasSelIndex >= 0 && atlasSelIndex < atlasCells.Count
            ? CellRectPx(atlasSelIndex / AtlasCols, atlasSelIndex % AtlasCols)
            : new Rect(0f, 0f, atlasCanvasW, atlasCanvasH);

        int beforeV = atlasVx.Count;
        int beforeH = atlasHy.Count;
        List<int> oldVx = new List<int>(atlasVx);
        List<int> oldHy = new List<int>(atlasHy);

        if (vertical)
        {
            int at = Mathf.Clamp(Mathf.RoundToInt(target.x + target.width * 0.5f), 1, atlasCanvasW - 1);
            if (!atlasVx.Contains(at))
            {
                atlasVx.Add(at);
            }
        }
        else
        {
            int at = Mathf.Clamp(Mathf.RoundToInt(target.y + target.height * 0.5f), 1, atlasCanvasH - 1);
            if (!atlasHy.Contains(at))
            {
                atlasHy.Add(at);
            }
        }
        NormalizeSplits();

        if (atlasVx.Count == beforeV && atlasHy.Count == beforeH)
        {
            SetStatus("画布太小或该位置已有分割线，无法再切。");
            return;
        }
        RemapByCenter(oldVx, oldHy, atlasCanvasW, atlasCanvasH);
        SetStatus("已在选中格中间加一条" + (vertical ? "竖" : "横") + "分割线。");
    }

    private void RemoveSplit(int axis, int idx)
    {
        List<int> oldVx = new List<int>(atlasVx);
        List<int> oldHy = new List<int>(atlasHy);

        if (axis == 1)
        {
            if (idx < 0 || idx >= atlasVx.Count)
            {
                return;
            }
            atlasVx.RemoveAt(idx);
        }
        else if (axis == 2)
        {
            if (idx < 0 || idx >= atlasHy.Count)
            {
                return;
            }
            atlasHy.RemoveAt(idx);
        }
        else
        {
            return;
        }

        RemapByCenter(oldVx, oldHy, atlasCanvasW, atlasCanvasH);
        SetStatus("已删除该分割线（两格合并，保留覆盖中心那一格的贴图）。");
    }

    private void ClearSplits()
    {
        if (atlasVx.Count == 0 && atlasHy.Count == 0)
        {
            return;
        }
        List<int> oldVx = new List<int>(atlasVx);
        List<int> oldHy = new List<int>(atlasHy);
        atlasVx.Clear();
        atlasHy.Clear();
        RemapByCenter(oldVx, oldHy, atlasCanvasW, atlasCanvasH);
        SetStatus("已清空分割，画布恢复为单格。");
    }

    private void MoveSplit(int axis, int idx, int pixel)
    {
        if (axis == 1)
        {
            if (idx < 0 || idx >= atlasVx.Count)
            {
                return;
            }
            int lo = idx > 0 ? atlasVx[idx - 1] + 1 : 1;
            int hi = idx < atlasVx.Count - 1 ? atlasVx[idx + 1] - 1 : atlasCanvasW - 1;
            atlasVx[idx] = Mathf.Clamp(pixel, lo, Mathf.Max(lo, hi));
        }
        else if (axis == 2)
        {
            if (idx < 0 || idx >= atlasHy.Count)
            {
                return;
            }
            int lo = idx > 0 ? atlasHy[idx - 1] + 1 : 1;
            int hi = idx < atlasHy.Count - 1 ? atlasHy[idx + 1] - 1 : atlasCanvasH - 1;
            atlasHy[idx] = Mathf.Clamp(pixel, lo, Mathf.Max(lo, hi));
        }
    }

    private int AtlasFilledCount()
    {
        int n = 0;
        for (int i = 0; i < atlasCells.Count; i++)
        {
            if (atlasCells[i] != null)
            {
                n++;
            }
        }
        return n;
    }

    // ================= UI =================

    private void DrawAtlasTool()
    {
        EnsureCellCount();

        EditorGUILayout.LabelField("贴图拼合（画布切片）", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "① 设好画布尺寸，用“均分”快速切格，或反复点“＋分割线”把一个格再切开（类似 PS 切片）。\n" +
            "② 从 Project 窗口把贴图拖到格子上：自动拉伸铺满该格；一次多选拖入会按文件名顺序往后依次填充。\n" +
            "③ 直接拖分割线可改格子大小；右键分割线删除它；右键格子清空该格；选中格按 Delete 也能清空。\n" +
            "④ 导出为一张 PNG，空格保持透明（预览里的棋盘格代表透明）。",
            MessageType.None);

        DrawAtlasCanvasSettings();
        EditorGUILayout.Space(4);
        DrawAtlasCanvasView();
        EditorGUILayout.Space(4);
        DrawAtlasSelectionInfo();
        EditorGUILayout.Space(4);
        DrawAtlasExport();
    }

    private void DrawAtlasCanvasSettings()
    {
        EditorGUILayout.LabelField("画布", EditorStyles.boldLabel);

        EditorGUILayout.BeginHorizontal();
        int preset = EditorGUILayout.Popup("常用尺寸", atlasSizePreset, AtlasSizePresetNames);
        if (preset != atlasSizePreset)
        {
            atlasSizePreset = preset;
            Vector2Int? size = AtlasPresetSize(preset);
            if (size.HasValue && (size.Value.x != atlasCanvasW || size.Value.y != atlasCanvasH))
            {
                atlasEditW = size.Value.x;
                atlasEditH = size.Value.y;
                ApplyCanvasSize();
            }
        }
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.BeginHorizontal();
        GUI.SetNextControlName("AtlasCanvasW");
        atlasEditW = EditorGUILayout.IntField("画布宽", Mathf.Clamp(atlasEditW, 8, 8192));
        GUI.SetNextControlName("AtlasCanvasH");
        atlasEditH = EditorGUILayout.IntField("画布高", Mathf.Clamp(atlasEditH, 8, 8192));
        EditorGUILayout.EndHorizontal();

        // 输入框失焦 / 回车后才应用，避免打字过程中反复缩放分割线
        string focus = GUI.GetNameOfFocusedControl();
        bool editingSize = focus == "AtlasCanvasW" || focus == "AtlasCanvasH";
        if (editingSize && Event.current.type == EventType.KeyDown &&
            (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter))
        {
            atlasSizePreset = 0;
            ApplyCanvasSize();
            GUI.FocusControl(null);
            Event.current.Use();
            Repaint();
        }
        else if (!editingSize && (atlasEditW != atlasCanvasW || atlasEditH != atlasCanvasH))
        {
            atlasSizePreset = 0;
            ApplyCanvasSize();
        }

        EditorGUILayout.Space(2);
        EditorGUILayout.BeginHorizontal();
        atlasEvenCols = EditorGUILayout.IntField("列数", Mathf.Clamp(atlasEvenCols, 1, 32));
        atlasEvenRows = EditorGUILayout.IntField("行数", Mathf.Clamp(atlasEvenRows, 1, 32));
        if (GUILayout.Button("均分画布", GUILayout.Width(80)))
        {
            SetEvenSplit(atlasEvenCols, atlasEvenRows);
            SetStatus(string.Format("已均分为 {0} 列 × {1} 行。", AtlasCols, AtlasRows));
        }
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("＋ 竖分割线（切选中格）"))
        {
            AddSplit(true);
        }
        if (GUILayout.Button("＋ 横分割线（切选中格）"))
        {
            AddSplit(false);
        }
        if (GUILayout.Button("清空分割", GUILayout.Width(80)))
        {
            ClearSplits();
        }
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.LabelField(
            string.Format("{0} 列 × {1} 行 = {2} 格　已填 {3} 格　画布 {4} × {5}",
                AtlasCols, AtlasRows, AtlasCols * AtlasRows, AtlasFilledCount(), atlasCanvasW, atlasCanvasH),
            EditorStyles.miniLabel);
    }

    private static Vector2Int? AtlasPresetSize(int preset)
    {
        switch (preset)
        {
            case 1: return new Vector2Int(512, 512);
            case 2: return new Vector2Int(1024, 1024);
            case 3: return new Vector2Int(2048, 2048);
            case 4: return new Vector2Int(1024, 512);
            case 5: return new Vector2Int(512, 256);
            case 6: return new Vector2Int(256, 256);
            case 7: return new Vector2Int(4096, 4096);
            default: return null;
        }
    }

    private void DrawAtlasCanvasView()
    {
        float availW = Mathf.Min(Mathf.Max(160f, position.width - 46f), 900f);
        const float maxH = 460f;
        float scale = Mathf.Min(availW / atlasCanvasW, maxH / atlasCanvasH);
        scale = Mathf.Clamp(scale, 0.02f, 6f);
        float dw = Mathf.Max(16f, atlasCanvasW * scale);
        float dh = Mathf.Max(16f, atlasCanvasH * scale);

        Rect outer = GUILayoutUtility.GetRect(16f, dh, GUILayout.ExpandWidth(true), GUILayout.Height(dh));
        Rect rect = new Rect(outer.x + (outer.width - dw) * 0.5f, outer.y, dw, dh);
        atlasCanvasRect = rect;

        HandleAtlasCanvasEvents(rect);
        DrawAtlasCanvasContent(rect);

        EditorGUILayout.LabelField(
            string.Format("预览缩放 {0:0.#}%（导出为原始 {1} × {2} 像素）", scale * 100f, atlasCanvasW, atlasCanvasH),
            EditorStyles.miniLabel);
    }

    private void DrawAtlasCanvasContent(Rect rect)
    {
        float sx = rect.width / atlasCanvasW;
        float sy = rect.height / atlasCanvasH;
        int cols = AtlasCols;
        int rows = AtlasRows;

        EditorGUI.DrawRect(rect, new Color(0.13f, 0.13f, 0.13f, 1f));

        Texture2D checker = GetAtlasChecker();
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                Rect px = CellRectPx(r, c);
                Rect cr = new Rect(
                    rect.x + px.x * sx, rect.y + px.y * sy,
                    Mathf.Max(1f, px.width * sx), Mathf.Max(1f, px.height * sy));
                int idx = r * cols + c;
                Texture2D tex = idx < atlasCells.Count ? atlasCells[idx] : null;

                if (tex != null)
                {
                    GUI.DrawTexture(cr, tex, ScaleMode.StretchToFill, true);
                }
                else if (checker != null)
                {
                    GUI.DrawTextureWithTexCoords(cr, checker, new Rect(0f, 0f, cr.width / 16f, cr.height / 16f));
                }
                else
                {
                    EditorGUI.DrawRect(cr, new Color(0.24f, 0.24f, 0.24f, 1f));
                }

                if (idx == atlasSelIndex)
                {
                    DrawAtlasOutline(cr, new Color(0.35f, 1f, 0.45f, 1f), 2f);
                }
            }
        }

        // 拖贴图时高亮目标格
        if (atlasHoverCell >= 0 && atlasHoverCell < cols * rows)
        {
            Rect px = CellRectPx(atlasHoverCell / cols, atlasHoverCell % cols);
            Rect cr = new Rect(rect.x + px.x * sx, rect.y + px.y * sy, px.width * sx, px.height * sy);
            EditorGUI.DrawRect(cr, new Color(1f, 0.85f, 0.2f, 0.18f));
            DrawAtlasOutline(cr, new Color(1f, 0.85f, 0.2f, 1f), 2f);
        }

        // 分割线
        Handles.BeginGUI();
        Color oldColor = Handles.color;
        for (int i = 0; i < atlasVx.Count; i++)
        {
            float x = rect.x + atlasVx[i] * sx;
            bool active = atlasDragAxis == 1 && atlasDragIdx == i;
            Handles.color = active ? new Color(1f, 0.55f, 0.1f, 1f) : new Color(1f, 0.85f, 0.2f, 0.9f);
            Handles.DrawLine(new Vector3(x, rect.y), new Vector3(x, rect.yMax));
        }
        for (int i = 0; i < atlasHy.Count; i++)
        {
            float y = rect.y + atlasHy[i] * sy;
            bool active = atlasDragAxis == 2 && atlasDragIdx == i;
            Handles.color = active ? new Color(1f, 0.55f, 0.1f, 1f) : new Color(1f, 0.85f, 0.2f, 0.9f);
            Handles.DrawLine(new Vector3(rect.x, y), new Vector3(rect.xMax, y));
        }
        Handles.color = oldColor;
        Handles.EndGUI();

        for (int i = 0; i < atlasVx.Count; i++)
        {
            float x = rect.x + atlasVx[i] * sx;
            EditorGUIUtility.AddCursorRect(new Rect(x - 5f, rect.y, 10f, rect.height), MouseCursor.ResizeHorizontal);
        }
        for (int i = 0; i < atlasHy.Count; i++)
        {
            float y = rect.y + atlasHy[i] * sy;
            EditorGUIUtility.AddCursorRect(new Rect(rect.x, y - 5f, rect.width, 10f), MouseCursor.ResizeVertical);
        }

        DrawAtlasOutline(rect, new Color(0f, 0f, 0f, 0.85f), 1f);

        // 格子编号（格子够大才画）
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                Rect px = CellRectPx(r, c);
                if (px.width * sx < 30f || px.height * sy < 20f)
                {
                    continue;
                }
                Rect chip = new Rect(rect.x + px.x * sx + 3f, rect.y + px.y * sy + 3f, 24f, 13f);
                EditorGUI.DrawRect(chip, new Color(0f, 0f, 0f, 0.55f));
                GUI.Label(chip, (r * cols + c).ToString("00"), EditorStyles.whiteMiniLabel);
            }
        }
    }

    private static void DrawAtlasOutline(Rect r, Color color, float thickness)
    {
        EditorGUI.DrawRect(new Rect(r.x, r.y, r.width, thickness), color);
        EditorGUI.DrawRect(new Rect(r.x, r.yMax - thickness, r.width, thickness), color);
        EditorGUI.DrawRect(new Rect(r.x, r.y, thickness, r.height), color);
        EditorGUI.DrawRect(new Rect(r.xMax - thickness, r.y, thickness, r.height), color);
    }

    // ================= 画布交互 =================

    private bool AtlasHitSplit(Vector2 mouse, out int axis, out int idx)
    {
        axis = 0;
        idx = -1;
        if (atlasCanvasRect.width <= 0f || atlasCanvasRect.height <= 0f)
        {
            return false;
        }
        float sx = atlasCanvasRect.width / atlasCanvasW;
        float sy = atlasCanvasRect.height / atlasCanvasH;
        const float tol = 5f;
        float best = float.MaxValue;

        if (mouse.x >= atlasCanvasRect.x - tol && mouse.x <= atlasCanvasRect.xMax + tol &&
            mouse.y >= atlasCanvasRect.y && mouse.y <= atlasCanvasRect.yMax)
        {
            for (int i = 0; i < atlasVx.Count; i++)
            {
                float d = Mathf.Abs(mouse.x - (atlasCanvasRect.x + atlasVx[i] * sx));
                if (d <= tol && d < best)
                {
                    best = d;
                    axis = 1;
                    idx = i;
                }
            }
        }
        if (mouse.y >= atlasCanvasRect.y - tol && mouse.y <= atlasCanvasRect.yMax + tol &&
            mouse.x >= atlasCanvasRect.x && mouse.x <= atlasCanvasRect.xMax)
        {
            for (int i = 0; i < atlasHy.Count; i++)
            {
                float d = Mathf.Abs(mouse.y - (atlasCanvasRect.y + atlasHy[i] * sy));
                if (d <= tol && d < best)
                {
                    best = d;
                    axis = 2;
                    idx = i;
                }
            }
        }
        return axis != 0;
    }

    private void HandleAtlasCanvasEvents(Rect rect)
    {
        Event e = Event.current;
        switch (e.type)
        {
            case EventType.MouseDown:
                if (!rect.Contains(e.mousePosition))
                {
                    break;
                }
                // 点画布时先让尺寸输入框失焦，下一帧会自动应用新尺寸
                string focused = GUI.GetNameOfFocusedControl();
                if (focused == "AtlasCanvasW" || focused == "AtlasCanvasH")
                {
                    GUI.FocusControl(null);
                    Repaint();
                }
                atlasHoverCell = -1;
                // 兜底：上次在窗口外松开鼠标可能收不到 MouseUp，这里清掉残留的拖拽状态
                atlasDragAxis = 0;
                atlasDragIdx = -1;
                int ax;
                int ix;
                if (AtlasHitSplit(e.mousePosition, out ax, out ix))
                {
                    if (e.button == 0)
                    {
                        atlasDragAxis = ax;
                        atlasDragIdx = ix;
                        e.Use();
                        Repaint();
                    }
                    else if (e.button == 1)
                    {
                        RemoveSplit(ax, ix);
                        e.Use();
                        Repaint();
                    }
                    break;
                }

                int ci = AtlasCellIndexAt(e.mousePosition);
                if (ci < 0)
                {
                    break;
                }
                if (e.button == 0)
                {
                    atlasSelIndex = ci;
                    e.Use();
                    Repaint();
                }
                else if (e.button == 1)
                {
                    if (ci < atlasCells.Count && atlasCells[ci] != null)
                    {
                        atlasCells[ci] = null;
                        SetStatus("已清空第 " + (ci + 1) + " 格的贴图。");
                    }
                    atlasSelIndex = ci;
                    e.Use();
                    Repaint();
                }
                break;

            case EventType.MouseDrag:
                if (atlasDragAxis != 0 && atlasDragIdx >= 0 && rect.width > 0f && rect.height > 0f)
                {
                    float sx = rect.width / atlasCanvasW;
                    float sy = rect.height / atlasCanvasH;
                    float px = (e.mousePosition.x - rect.x) / sx;
                    float py = (e.mousePosition.y - rect.y) / sy;
                    MoveSplit(atlasDragAxis, atlasDragIdx, Mathf.RoundToInt(atlasDragAxis == 1 ? px : py));
                    e.Use();
                    Repaint();
                }
                break;

            case EventType.MouseUp:
                if (atlasDragAxis != 0 && e.button == 0)
                {
                    atlasDragAxis = 0;
                    atlasDragIdx = -1;
                    e.Use();
                    Repaint();
                }
                break;

            case EventType.DragUpdated:
            case EventType.DragPerform:
                if (!rect.Contains(e.mousePosition) || !AtlasDragHasTexture())
                {
                    if (atlasHoverCell >= 0)
                    {
                        atlasHoverCell = -1;
                        Repaint();
                    }
                    break;
                }
                DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
                atlasHoverCell = AtlasCellIndexAt(e.mousePosition);
                if (e.type == EventType.DragPerform)
                {
                    DragAndDrop.AcceptDrag();
                    AssignAtlasDropped(e.mousePosition, DragAndDrop.objectReferences);
                    atlasHoverCell = -1;
                    e.Use();
                }
                Repaint();
                break;

            case EventType.DragExited:
                if (atlasHoverCell >= 0)
                {
                    atlasHoverCell = -1;
                    Repaint();
                }
                break;

            case EventType.KeyDown:
                if ((e.keyCode == KeyCode.Delete || e.keyCode == KeyCode.Backspace) &&
                    !EditorGUIUtility.editingTextField &&
                    atlasSelIndex >= 0 && atlasSelIndex < atlasCells.Count)
                {
                    atlasCells[atlasSelIndex] = null;
                    SetStatus("已清空第 " + (atlasSelIndex + 1) + " 格的贴图。");
                    e.Use();
                    Repaint();
                }
                break;
        }
    }

    private static bool AtlasDragHasTexture()
    {
        UnityEngine.Object[] refs = DragAndDrop.objectReferences;
        if (refs != null)
        {
            for (int i = 0; i < refs.Length; i++)
            {
                if (refs[i] is Texture2D)
                {
                    return true;
                }
            }
        }
        string[] paths = DragAndDrop.paths;
        if (paths != null)
        {
            for (int i = 0; i < paths.Length; i++)
            {
                if (!string.IsNullOrEmpty(paths[i]) && paths[i].StartsWith("Assets", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private void AssignAtlasDropped(Vector2 guiPos, UnityEngine.Object[] objects)
    {
        List<Texture2D> picked = new List<Texture2D>();
        if (objects != null)
        {
            for (int i = 0; i < objects.Length; i++)
            {
                Texture2D t = objects[i] as Texture2D;
                if (t != null && !picked.Contains(t))
                {
                    picked.Add(t);
                }
            }
        }
        if (picked.Count == 0)
        {
            string[] paths = DragAndDrop.paths;
            if (paths != null)
            {
                for (int i = 0; i < paths.Length; i++)
                {
                    Texture2D t = AssetDatabase.LoadAssetAtPath<Texture2D>(paths[i]);
                    if (t != null && !picked.Contains(t))
                    {
                        picked.Add(t);
                    }
                }
            }
        }
        if (picked.Count == 0)
        {
            SetStatus("拖入的内容里没有贴图：请从 Project 窗口拖 Texture2D 进来。");
            return;
        }

        picked.Sort((a, b) => string.CompareOrdinal(AssetDatabase.GetAssetPath(a), AssetDatabase.GetAssetPath(b)));

        int start = AtlasCellIndexAt(guiPos);
        if (start < 0)
        {
            start = 0;
        }
        EnsureCellCount();

        int placed = 0;
        for (int i = 0; i < picked.Count && start + i < atlasCells.Count; i++)
        {
            atlasCells[start + i] = picked[i];
            placed++;
        }
        atlasSelIndex = start;

        if (placed < picked.Count)
        {
            SetStatus(string.Format("已从第 {0} 格起放入 {1} 张（{2} 张超出格子数被忽略）。",
                start + 1, placed, picked.Count - placed));
        }
        else
        {
            SetStatus(string.Format("已从第 {0} 格起放入 {1} 张贴图。", start + 1, placed));
        }
    }

    // ================= 选中格信息 =================

    private void DrawAtlasSelectionInfo()
    {
        EditorGUILayout.LabelField("选中格", EditorStyles.boldLabel);
        if (atlasSelIndex < 0 || atlasSelIndex >= atlasCells.Count)
        {
            EditorGUILayout.LabelField("在画布上点一个格子即可查看 / 指定该格贴图。", EditorStyles.miniLabel);
            return;
        }

        int cols = AtlasCols;
        int row = atlasSelIndex / cols;
        int col = atlasSelIndex % cols;
        Rect px = CellRectPx(row, col);

        EditorGUILayout.LabelField(
            string.Format("第 {0} 格（第 {1} 行 第 {2} 列），画布内像素 x{3} y{4}　{5} × {6}",
                atlasSelIndex + 1, row + 1, col + 1,
                Mathf.RoundToInt(px.x), Mathf.RoundToInt(px.y),
                Mathf.RoundToInt(px.width), Mathf.RoundToInt(px.height)),
            EditorStyles.miniLabel);

        EditorGUILayout.BeginHorizontal();
        atlasCells[atlasSelIndex] = (Texture2D)EditorGUILayout.ObjectField(
            "该格贴图", atlasCells[atlasSelIndex], typeof(Texture2D), false);
        if (GUILayout.Button("清空此格", GUILayout.Width(80)))
        {
            atlasCells[atlasSelIndex] = null;
            SetStatus("已清空第 " + (atlasSelIndex + 1) + " 格的贴图。");
        }
        EditorGUILayout.EndHorizontal();
    }

    // ================= 导出 =================

    private void DrawAtlasExport()
    {
        EditorGUILayout.LabelField("导出", EditorStyles.boldLabel);
        atlasAssetName = EditorGUILayout.TextField("文件名", atlasAssetName);
        EditorGUILayout.BeginHorizontal();
        atlasSRGB = EditorGUILayout.Toggle(new GUIContent("sRGB", "颜色贴图勾选；纯数据图集可取消"), atlasSRGB);
        atlasMipmap = EditorGUILayout.Toggle("生成 Mipmap", atlasMipmap);
        EditorGUILayout.EndHorizontal();
        DrawVFXExportMode();

        EditorGUILayout.BeginHorizontal();
        EditorGUI.BeginDisabledGroup(AtlasFilledCount() == 0);
        if (GUILayout.Button("导出 PNG…", GUILayout.Height(28)))
        {
            ExportAtlasPng();
        }
        EditorGUI.EndDisabledGroup();
        if (GUILayout.Button("清空所有格子贴图", GUILayout.Height(28), GUILayout.Width(150)))
        {
            for (int i = 0; i < atlasCells.Count; i++)
            {
                atlasCells[i] = null;
            }
            SetStatus("已清空所有格子的贴图（分割布局保留）。");
        }
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.LabelField(
            string.Format("输出 {0} × {1}，{2} 格有图 / 共 {3} 格，空格透明",
                atlasCanvasW, atlasCanvasH, AtlasFilledCount(), AtlasCols * AtlasRows),
            EditorStyles.miniLabel);
    }

    private void ExportAtlasPng()
    {
        string fileName = string.IsNullOrEmpty(atlasAssetName) ? "Atlas" : atlasAssetName.Trim();
        string sourcePath = null;
        if (atlasCells != null)
        {
            foreach (Texture2D cell in atlasCells)
            {
                if (cell != null)
                {
                    sourcePath = AssetDatabase.GetAssetPath(cell);
                    break;
                }
            }
        }
        string absPath = ResolveVFXExportPath("导出拼合贴图", fileName, "png", sourcePath, false);
        if (string.IsNullOrEmpty(absPath))
        {
            return;
        }

        Texture2D tex = RenderAtlasTexture();
        if (tex == null)
        {
            return;
        }
        byte[] png = tex.EncodeToPNG();
        UnityEngine.Object.DestroyImmediate(tex);
        if (png == null || png.Length == 0)
        {
            SetStatus("导出失败：PNG 编码为空。");
            return;
        }

        try
        {
            File.WriteAllBytes(absPath, png);
        }
        catch (Exception e)
        {
            SetStatus("写入 PNG 失败：" + e.Message);
            return;
        }
        AssetDatabase.Refresh();

        string rel = ToAssetsRelative(absPath);
        if (rel != null)
        {
            ConfigureAtlasImporter(rel);
            Texture2D loaded = AssetDatabase.LoadAssetAtPath<Texture2D>(rel);
            if (loaded != null)
            {
                EditorGUIUtility.PingObject(loaded);
            }
            SetStatus(string.Format("拼合完成：{0} 格贴图 → {1} × {2}：{3}",
                AtlasFilledCount(), atlasCanvasW, atlasCanvasH, rel));
        }
        else
        {
            SetStatus("已导出到项目外（未导入 Unity）：" + absPath);
        }
    }

    private Texture2D RenderAtlasTexture()
    {
        Shader sh = GetAtlasShader();
        if (sh == null || !sh.isSupported)
        {
            SetStatus("内部拼合 Shader 创建/编译失败，请查看控制台。");
            return null;
        }

        int w = atlasCanvasW;
        int h = atlasCanvasH;
        int cols = AtlasCols;
        int rows = AtlasRows;

        RenderTexture rt = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32,
            atlasSRGB ? RenderTextureReadWrite.sRGB : RenderTextureReadWrite.Linear);
        rt.hideFlags = HideFlags.HideAndDontSave;
        rt.Create();
        RenderTexture prev = RenderTexture.active;
        RenderTexture.active = rt;
        GL.Clear(false, true, new Color(0f, 0f, 0f, 0f));

        Material mat = new Material(sh);
        mat.hideFlags = HideFlags.HideAndDontSave;
        try
        {
            for (int r = 0; r < rows; r++)
            {
                for (int c = 0; c < cols; c++)
                {
                    int idx = r * cols + c;
                    Texture2D src = idx < atlasCells.Count ? atlasCells[idx] : null;
                    if (src == null)
                    {
                        continue;
                    }
                    Rect px = CellRectPx(r, c);
                    if (px.width < 1f || px.height < 1f)
                    {
                        continue;
                    }
                    // 画布 UV → 该格贴图 UV：拉伸铺满（整张贴图映射到格子）
                    float yBottom = h - (px.y + px.height);
                    mat.SetTexture("_Tex", src);
                    mat.SetVector("_HalfTexel", new Vector4(0.5f / src.width, 0.5f / src.height, 0f, 0f));
                    mat.SetVector("_Crop", new Vector4(
                        w / px.width, h / px.height,
                        -px.x / px.width, -yBottom / px.height));
                    Graphics.Blit(null, rt, mat);
                }
            }

            RenderTexture.active = rt;
            Texture2D output = new Texture2D(w, h, TextureFormat.RGBA32, false);
            output.ReadPixels(new Rect(0f, 0f, w, h), 0, 0);
            output.Apply(false);
            return output;
        }
        catch (Exception e)
        {
            SetStatus("拼合渲染失败：" + e.Message);
            return null;
        }
        finally
        {
            RenderTexture.active = prev;
            rt.Release();
            UnityEngine.Object.DestroyImmediate(rt);
            UnityEngine.Object.DestroyImmediate(mat);
        }
    }

    private void ConfigureAtlasImporter(string rel)
    {
        TextureImporter importer = AssetImporter.GetAtPath(rel) as TextureImporter;
        if (importer == null)
        {
            return;
        }
        importer.textureType = TextureImporterType.Default;
        importer.sRGBTexture = atlasSRGB;
        importer.mipmapEnabled = atlasMipmap;
        importer.wrapMode = TextureWrapMode.Clamp;
        importer.npotScale = TextureImporterNPOTScale.None;

        // 画布可能超过默认的 2048 上限，按需抬高 maxTextureSize，避免被缩小导入
        int need = Mathf.Max(atlasCanvasW, atlasCanvasH);
        int maxSize = 32;
        while (maxSize < need && maxSize < 8192)
        {
            maxSize *= 2;
        }
        importer.maxTextureSize = Mathf.Clamp(maxSize, 32, 8192);
        importer.SaveAndReimport();
    }
}
