using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

/// <summary>
/// 轴向渐变烘焙（独立工具窗口，菜单 Tools / 轴向渐变烘焙）。
/// 给模型一个沿 X / Y / Z 轴的渐变（灰度线性或自定义渐变颜色），把该渐变按模型原本的 UV
/// 烘焙成一张新贴图导出，可直接作为该模型的“轴向渐变溶解”贴图使用。
/// 无需事先把模型 UV 摆成渐变，即可得到沿轴向流动的溶解遮罩。
///
/// 功能：
///   · 3D 模型预览：用 PreviewRenderUtility 在隐藏场景里离屏渲染模型，左键拖拽旋转、滚轮缩放、双击重置；
///   · 噪波叠加：把内置程序噪波或自选噪波贴图叠加到渐变上，让溶解边缘带噪波扰动。
///
/// 实现：CPU 光栅化。把每个三角形按 UV 坐标投到贴图像素空间，用重心坐标插值出该 UV 处的
/// 沿轴位置，再归一化成 0→1 的渐变值；随后做边缘扩展（dilation）补 UV 接缝，避免贴图边缘
/// 双线性采样出黑边；最后可选叠加噪波。烘焙使用模型本地坐标（网格顶点），与模型在世界里的摆放无关。
/// </summary>
public class AxisGradientBakeWindow : EditorWindow
{
    private enum AxisGradientMode
    {
        Grayscale = 0, // 灰度线性（溶解数据）
        Gradient = 1    // 自定义渐变（颜色）
    }

    private static readonly string[] AxAxisNames = { "X", "Y", "Z" };
    private static readonly string[] AxModeNames = { "灰度线性（溶解数据）", "自定义渐变（颜色）" };
    private static readonly string[] AxNoiseSourceNames = { "内置程序噪波", "噪波贴图" };
    private static readonly string[] AxNoiseBlendNames = { "相加扰动", "相乘", "覆盖" };

    [SerializeField] private GameObject axModel;
    [SerializeField] private Mesh axMeshAsset;
    [SerializeField] private int axAxis;          // 0 = X, 1 = Y, 2 = Z
    [SerializeField] private int axMode;          // 0 = 灰度, 1 = 渐变颜色
    [SerializeField] private Gradient axGradient;
    [SerializeField] private bool axReverse;
    [SerializeField] private int axRangeMode;     // 0 = 自动（包围盒）, 1 = 手动
    [SerializeField] private float axMin;
    [SerializeField] private float axMax;

    [SerializeField] private bool axNoiseEnabled;
    [SerializeField] private int axNoiseSource;   // 0 = 内置, 1 = 贴图
    [SerializeField] private Texture2D axNoiseTexture;
    [SerializeField] private int axNoiseBlend;    // 0 = 相加扰动, 1 = 相乘, 2 = 覆盖
    [SerializeField] private float axNoiseStrength = 0.5f;
    [SerializeField] private float axNoiseTiling = 1f;

    [SerializeField] private int axResolution = 1024;
    [SerializeField] private int axBleed = 4;     // 边缘扩展像素，补 UV 接缝
    [SerializeField] private bool axWriteAlpha;
    [SerializeField] private bool axSRGB;
    [SerializeField] private bool axMipmap;
    [SerializeField] private string axOutputName = "AxisGradient";
    [SerializeField] private bool axInitDone;

    private Texture2D axPreview;
    private Vector2 axScroll;
    private string statusMessage = "";

    // 网格收集缓存：目标引用变化时才重新收集
    private List<Mesh> axMeshList;
    private Object axLastModelRef;
    private Object axLastMeshRef;

    // 3D 模型预览
    private PreviewRenderUtility axPru;
    private GameObject axPreviewInstance;
    private Bounds axPreviewBounds;
    private float axBaseDistance = 5f;
    private float axCamYaw = 30f;
    private float axCamPitch = -20f;
    private float axCamDistance = 5f;
    private bool axCamDragging;
    private bool axPreviewDirty = true;

    /// <summary>一次烘焙用到的单个三角形：UV 与三个顶点沿轴坐标（归一化后再插值）。</summary>
    private struct AxisBakeTriangle
    {
        public Vector2 uv0, uv1, uv2;
        public float a0, a1, a2;
    }

    /// <summary>噪波采样器：亮度数据 + 双线性 + 平铺（wrap）采样。</summary>
    private class NoiseData
    {
        public float[] lum;
        public int w, h;

        public float Sample(float u, float v)
        {
            u = u - Mathf.Floor(u);
            v = v - Mathf.Floor(v);
            float x = u * w - 0.5f;
            float y = v * h - 0.5f;
            int x0 = Mathf.FloorToInt(x);
            int y0 = Mathf.FloorToInt(y);
            float fx = x - x0;
            float fy = y - y0;
            int wx0 = Wrap(x0, w), wx1 = Wrap(x0 + 1, w);
            int wy0 = Wrap(y0, h), wy1 = Wrap(y0 + 1, h);
            float a = lum[wy0 * w + wx0];
            float b = lum[wy0 * w + wx1];
            float c = lum[wy1 * w + wx0];
            float d = lum[wy1 * w + wx1];
            float top = Mathf.Lerp(a, b, fx);
            float bot = Mathf.Lerp(c, d, fx);
            return Mathf.Lerp(top, bot, fy);
        }

        private static int Wrap(int i, int n) { return ((i % n) + n) % n; }
    }

    [MenuItem("Tools/轴向渐变烘焙")]
    public static void Open()
    {
        AxisGradientBakeWindow win = GetWindow<AxisGradientBakeWindow>("轴向渐变烘焙");
        win.minSize = new Vector2(460, 520);
        win.Show();
    }

    private void OnEnable()
    {
        titleContent = new GUIContent("轴向渐变烘焙");
        Init();
    }

    private void OnDisable()
    {
        if (axPru != null)
        {
            axPru.Cleanup();
            axPru = null;
        }
        axPreviewInstance = null;
    }

    private void OnGUI()
    {
        axScroll = EditorGUILayout.BeginScrollView(axScroll);
        DrawTool();
        EditorGUILayout.EndScrollView();

        if (!string.IsNullOrEmpty(statusMessage))
        {
            EditorGUILayout.HelpBox(statusMessage, MessageType.Info);
        }
    }

    private void Init()
    {
        if (!axInitDone)
        {
            axInitDone = true;
            axResolution = 1024;
            axBleed = 4;
            axOutputName = "AxisGradient";
            axSRGB = false; // 灰度数据默认线性
            axNoiseEnabled = false;
            axNoiseSource = 0;
            axNoiseBlend = 0;
            axNoiseStrength = 0.5f;
            axNoiseTiling = 1f;
        }

        if (axGradient == null)
        {
            axGradient = new Gradient();
            axGradient.SetKeys(
                new[]
                {
                    new GradientColorKey(Color.black, 0f),
                    new GradientColorKey(Color.white, 1f)
                },
                new[]
                {
                    new GradientAlphaKey(1f, 0f),
                    new GradientAlphaKey(1f, 1f)
                });
        }

        axResolution = Mathf.Clamp(axResolution, 8, 4096);
        axBleed = Mathf.Clamp(axBleed, 0, 64);
    }

    private void DrawTool()
    {
        EditorGUILayout.LabelField("轴向渐变烘焙（烘焙到模型原 UV）", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "给模型一个沿 X / Y / Z 轴的渐变，把它烘焙到模型原本的 UV 上，导出一张新贴图，\n" +
            "可直接作为该模型的“轴向渐变溶解”贴图——无需事先把 UV 摆成渐变。\n" +
            "· 灰度线性：R = G = B = 沿轴归一化位置（0→1），是最常用的溶解数据，建议关闭 sRGB；\n" +
            "· 自定义渐变：把该位置映射到你调的颜色渐变上；\n" +
            "· 可叠加噪波让溶解边缘带扰动；烘焙用模型本地坐标，与模型在世界里的摆放无关。",
            MessageType.None);

        // —— 模型 / 网格来源 ——
        EditorGUILayout.BeginHorizontal();
        axModel = (GameObject)EditorGUILayout.ObjectField("模型", axModel, typeof(GameObject), true);
        if (GUILayout.Button("使用选中", GUILayout.Width(70)))
        {
            PickTarget();
        }
        EditorGUILayout.EndHorizontal();
        axMeshAsset = (Mesh)EditorGUILayout.ObjectField(
            new GUIContent("网格（可选）", "不指定模型时，可直接指定一个 Mesh 资源"),
            axMeshAsset, typeof(Mesh), false);

        List<Mesh> meshes = GetBakeMeshes();
        if (meshes.Count > 0)
        {
            EditorGUILayout.LabelField("将烘焙 " + meshes.Count + " 个 Mesh 的 UV", EditorStyles.miniLabel);
        }
        else
        {
            EditorGUILayout.LabelField("（尚未指定模型 / 网格）", EditorStyles.miniLabel);
        }

        // —— 3D 模型预览 ——
        DrawModelPreview();

        // —— 轴向与范围 ——
        EditorGUILayout.Space(4);
        EditorGUILayout.BeginHorizontal();
        axAxis = EditorGUILayout.Popup("轴向", axAxis, AxAxisNames, GUILayout.Width(200));
        axRangeMode = EditorGUILayout.Popup(axRangeMode, new[] { "自动范围（包围盒）", "手动范围" }, GUILayout.Width(180));
        EditorGUILayout.EndHorizontal();
        if (axRangeMode == 1)
        {
            EditorGUI.indentLevel++;
            axMin = EditorGUILayout.FloatField("最小值（沿轴）", axMin);
            axMax = EditorGUILayout.FloatField("最大值（沿轴）", axMax);
            if (axMax <= axMin)
            {
                axMax = axMin + 0.0001f;
            }
            EditorGUI.indentLevel--;
        }

        // —— 输出样式 ——
        int newMode = EditorGUILayout.Popup("输出样式", axMode, AxModeNames);
        if (newMode != axMode)
        {
            axMode = newMode;
            // 颜色渐变默认按 sRGB 存，灰度溶解数据默认按线性存
            axSRGB = axMode == (int)AxisGradientMode.Gradient;
        }
        if (axMode == (int)AxisGradientMode.Gradient)
        {
            EditorGUI.indentLevel++;
            axGradient = EditorGUILayout.GradientField("渐变", axGradient);
            EditorGUI.indentLevel--;
        }
        axReverse = EditorGUILayout.Toggle(new GUIContent("反向", "把 0→1 的位置映射反过来（溶解方向反向）"), axReverse);

        // —— 噪波叠加 ——
        EditorGUILayout.Space(4);
        axNoiseEnabled = EditorGUILayout.Toggle(new GUIContent("叠加噪波", "让轴向渐变溶解带噪波扰动"), axNoiseEnabled);
        if (axNoiseEnabled)
        {
            EditorGUI.indentLevel++;
            axNoiseSource = EditorGUILayout.Popup("噪波来源", axNoiseSource, AxNoiseSourceNames);
            if (axNoiseSource == 1)
            {
                axNoiseTexture = (Texture2D)EditorGUILayout.ObjectField(
                    new GUIContent("噪波贴图", "按亮度采样，按下方“平铺”缩放后叠加"),
                    axNoiseTexture, typeof(Texture2D), false);
            }
            axNoiseBlend = EditorGUILayout.Popup("叠加方式", axNoiseBlend, AxNoiseBlendNames);
            axNoiseStrength = EditorGUILayout.Slider("噪波强度", axNoiseStrength, 0f, 1f);
            axNoiseTiling = EditorGUILayout.Slider("噪波平铺", axNoiseTiling, 0.1f, 20f);
            EditorGUI.indentLevel--;
        }

        // —— 输出设置 ——
        EditorGUILayout.Space(4);
        axResolution = EditorGUILayout.IntField(
            new GUIContent("贴图分辨率", "正方形，单位像素（8~4096）"), Mathf.Clamp(axResolution, 8, 4096));
        axBleed = EditorGUILayout.IntSlider(
            new GUIContent("边缘扩展", "向 UV 岛外扩展的像素数，补接缝防止采样出黑边"), axBleed, 0, 64);
        EditorGUILayout.BeginHorizontal();
        axWriteAlpha = EditorGUILayout.Toggle(new GUIContent("写入 Alpha", "把渐变同时写进 Alpha 通道"), axWriteAlpha);
        axSRGB = EditorGUILayout.Toggle(new GUIContent("sRGB", "颜色贴图勾选；纯溶解数据建议取消"), axSRGB);
        axMipmap = EditorGUILayout.Toggle("生成 Mipmap", axMipmap);
        EditorGUILayout.EndHorizontal();
        axOutputName = EditorGUILayout.TextField("导出文件名", axOutputName);

        // —— 操作按钮 ——
        EditorGUILayout.Space(4);
        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("生成预览", GUILayout.Height(26)))
        {
            BakePreview();
        }
        GUI.enabled = meshes.Count > 0;
        if (GUILayout.Button("导出 PNG…", GUILayout.Height(26)))
        {
            ExportPng();
        }
        GUI.enabled = true;
        EditorGUILayout.EndHorizontal();

        // —— 2D 烘焙结果预览 ——
        if (axPreview != null)
        {
            EditorGUILayout.Space(6);
            float size = Mathf.Min(256f, position.width - 60f);
            Rect r = GUILayoutUtility.GetRect(size, size, GUILayout.Width(size), GUILayout.Height(size));
            EditorGUI.DrawRect(r, new Color(0f, 0f, 0f, 0.35f));
            GUI.DrawTexture(r, axPreview, ScaleMode.ScaleToFit, false);
            EditorGUILayout.LabelField(
                axPreview.width + " × " + axPreview.height + "（预览缩放，导出为 " +
                Mathf.Clamp(axResolution, 8, 4096) + " × " + Mathf.Clamp(axResolution, 8, 4096) + "）",
                EditorStyles.miniLabel);
        }
    }

    // ================= 3D 模型预览 =================

    private void DrawModelPreview()
    {
        if (axModel == null && axMeshAsset == null)
        {
            return;
        }

        EnsurePreviewUpToDate();

        Rect view = GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none,
            GUILayout.ExpandWidth(true), GUILayout.Height(220f));
        EditorGUI.DrawRect(view, new Color(0.12f, 0.12f, 0.13f));

        if (axPreviewInstance != null)
        {
            HandlePreviewInput(view);
            RenderModelPreview(view);
        }
        else
        {
            GUI.Label(view, "（未生成 3D 预览，可能是场景对象或网格读取失败）", EditorStyles.centeredGreyMiniLabel);
        }
        EditorGUILayout.Space(2);
    }

    private void EnsurePreviewUpToDate()
    {
        if (axPreviewDirty || axPru == null)
        {
            axPreviewDirty = false;
            RebuildPreview();
        }
    }

    private void RebuildPreview()
    {
        if (axPru != null)
        {
            axPru.Cleanup();
            axPru = null;
        }
        axPreviewInstance = null;

        axPru = new PreviewRenderUtility();
        axPru.cameraFieldOfView = 30f;
        axPru.camera.nearClipPlane = 0.01f;
        axPru.camera.farClipPlane = 5000f;
        axPru.ambientColor = new Color(0.5f, 0.5f, 0.55f, 1f);
        if (axPru.lights != null && axPru.lights.Length > 0 && axPru.lights[0] != null)
        {
            axPru.lights[0].intensity = 1.2f;
            axPru.lights[0].transform.rotation = Quaternion.Euler(50f, -30f, 0f);
        }
        if (axPru.lights != null && axPru.lights.Length > 1 && axPru.lights[1] != null)
        {
            axPru.lights[1].intensity = 0.5f;
            axPru.lights[1].transform.rotation = Quaternion.Euler(-30f, 130f, 0f);
        }

        if (axModel != null)
        {
            try
            {
                axPreviewInstance = axPru.InstantiatePrefabInScene(axModel);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[轴向渐变烘焙] 模型预览实例化失败：" + e.Message);
                axPreviewInstance = null;
            }
        }
        else if (axMeshAsset != null)
        {
            try
            {
                GameObject source = new GameObject("tmp_axis_preview");
                source.hideFlags = HideFlags.HideAndDontSave;
                MeshFilter mf = source.AddComponent<MeshFilter>();
                mf.sharedMesh = axMeshAsset;
                MeshRenderer mr = source.AddComponent<MeshRenderer>();
                Material mat = AssetDatabase.GetBuiltinExtraResource<Material>("Default-Material.mat");
                mr.sharedMaterial = mat != null ? mat : new Material(Shader.Find("Standard"));
                axPreviewInstance = axPru.InstantiatePrefabInScene(source);
                DestroyImmediate(source);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[轴向渐变烘焙] 网格预览构建失败：" + e.Message);
                axPreviewInstance = null;
            }
        }

        ComputePreviewBounds();
        ResetPreviewCamera();
    }

    private void ComputePreviewBounds()
    {
        Bounds b = new Bounds(Vector3.zero, Vector3.zero);
        bool has = false;

        Renderer[] renderers = axPreviewInstance != null
            ? axPreviewInstance.GetComponentsInChildren<Renderer>(true)
            : null;
        if (renderers != null)
        {
            foreach (Renderer r in renderers)
            {
                if (r is ParticleSystemRenderer)
                {
                    continue;
                }
                if (!has) { b = r.bounds; has = true; }
                else b.Encapsulate(r.bounds);
            }
        }

        if (!has) b = new Bounds(Vector3.zero, Vector3.one * 2f);
        if (b.size.sqrMagnitude < 0.001f) b.size = Vector3.one * 2f;
        axPreviewBounds = b;

        float radius = b.extents.magnitude;
        axBaseDistance = radius / Mathf.Tan(axPru.cameraFieldOfView * 0.5f * Mathf.Deg2Rad) * 1.4f;
        axBaseDistance = Mathf.Clamp(axBaseDistance, 0.1f, 200f);
    }

    private void ResetPreviewCamera()
    {
        axCamYaw = 30f;
        axCamPitch = -20f;
        axCamDistance = axBaseDistance;
    }

    private void HandlePreviewInput(Rect view)
    {
        Event e = Event.current;

        if (e.type == EventType.MouseDown && e.button == 0 && view.Contains(e.mousePosition))
        {
            if (e.clickCount == 2)
            {
                ResetPreviewCamera();
                e.Use();
            }
            else
            {
                axCamDragging = true;
                e.Use();
            }
        }
        else if (e.type == EventType.MouseDrag && axCamDragging)
        {
            axCamYaw += e.delta.x * 0.4f;
            axCamPitch = Mathf.Clamp(axCamPitch - e.delta.y * 0.4f, -89f, 89f);
            Repaint();
            e.Use();
        }
        else if (e.type == EventType.MouseUp && axCamDragging)
        {
            axCamDragging = false;
            e.Use();
        }
        else if (e.type == EventType.ScrollWheel && view.Contains(e.mousePosition))
        {
            float factor = Mathf.Pow(1.12f, Mathf.Clamp(e.delta.y, -3f, 3f));
            axCamDistance = Mathf.Clamp(axCamDistance * factor, axBaseDistance * 0.15f, axBaseDistance * 8f);
            Repaint();
            e.Use();
        }
    }

    private void RenderModelPreview(Rect view)
    {
        if (axPru == null || axPreviewInstance == null)
        {
            return;
        }

        Bounds b = axPreviewBounds;
        float yawRad = axCamYaw * Mathf.Deg2Rad;
        float pitchRad = axCamPitch * Mathf.Deg2Rad;
        Vector3 dir = new Vector3(
            Mathf.Cos(pitchRad) * Mathf.Sin(yawRad),
            Mathf.Sin(pitchRad),
            Mathf.Cos(pitchRad) * Mathf.Cos(yawRad));
        Vector3 camPos = b.center + dir * axCamDistance;

        Rect renderRect = new Rect(0f, 0f, Mathf.Max(64f, view.width), Mathf.Max(64f, view.height));
        Texture tex = null;
        axPru.BeginPreview(renderRect, GUIStyle.none);
        axPru.camera.transform.position = camPos;
        axPru.camera.transform.LookAt(b.center);
        axPru.camera.backgroundColor = new Color(0.12f, 0.12f, 0.13f, 1f);
        axPru.camera.clearFlags = CameraClearFlags.SolidColor;
        axPru.Render(false);
        tex = axPru.EndPreview();

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

    // ================= 目标选取与网格收集 =================

    private void PickTarget()
    {
        GameObject go = Selection.activeGameObject;
        if (go != null)
        {
            axModel = go;
            return;
        }

        Object o = Selection.activeObject;
        if (o is Mesh mesh)
        {
            axMeshAsset = mesh;
            return;
        }
        if (o is GameObject g)
        {
            axModel = g;
            return;
        }
        SetStatus("未选中对象：请在场景或 Project 窗口选中模型 / 网格。");
    }

    private List<Mesh> CollectBakeMeshes()
    {
        List<Mesh> result = new List<Mesh>();
        HashSet<Mesh> seen = new HashSet<Mesh>();

        if (axMeshAsset != null && seen.Add(axMeshAsset))
        {
            result.Add(axMeshAsset);
        }

        if (axModel != null)
        {
            foreach (MeshFilter mf in axModel.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh != null && seen.Add(mf.sharedMesh))
                {
                    result.Add(mf.sharedMesh);
                }
            }
            foreach (SkinnedMeshRenderer smr in axModel.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (smr.sharedMesh != null && seen.Add(smr.sharedMesh))
                {
                    result.Add(smr.sharedMesh);
                }
            }
        }
        return result;
    }

    private List<Mesh> GetBakeMeshes()
    {
        if (axMeshList == null || axLastModelRef != axModel || axLastMeshRef != axMeshAsset)
        {
            axLastModelRef = axModel;
            axLastMeshRef = axMeshAsset;
            axMeshList = CollectBakeMeshes();
            axPreviewDirty = true;
        }
        return axMeshList;
    }

    // ================= 烘焙流程 =================

    private void BakePreview()
    {
        List<Mesh> meshes = GetBakeMeshes();
        if (meshes.Count == 0)
        {
            SetStatus("请先指定模型或网格。");
            return;
        }

        int size = Mathf.Min(axResolution, 384);
        Texture2D tex = BakeTexture(meshes, size);
        if (tex == null)
        {
            return;
        }

        if (axPreview != null)
        {
            DestroyImmediate(axPreview);
        }
        axPreview = tex;
        SetStatus("已生成轴向渐变烘焙预览（" + size + " × " + size + "）。");
    }

    /// <summary>
    /// 核心烘焙：把模型沿轴的渐变（可叠加噪波）按 UV 光栅化成一张 size × size 贴图。
    /// </summary>
    private Texture2D BakeTexture(List<Mesh> meshes, int size)
    {
        bool showProgress = size >= 1024;
        try
        {
            // 1) 收集三角形（UV + 沿轴坐标），并统计自动范围
            List<AxisBakeTriangle> triangles = new List<AxisBakeTriangle>();
            float rangeMin = float.PositiveInfinity;
            float rangeMax = float.NegativeInfinity;
            bool anyUv = false;

            foreach (Mesh mesh in meshes)
            {
                if (mesh == null)
                {
                    continue;
                }

                Vector3[] verts;
                Vector2[] uvs;
                try
                {
                    verts = mesh.vertices;
                    uvs = mesh.uv;
                }
                catch (Exception e)
                {
                    SetStatus("读取网格 " + mesh.name + " 失败（可能未开启 Read/Write）：" + e.Message);
                    continue;
                }

                if (verts == null || verts.Length == 0)
                {
                    continue;
                }
                if (uvs == null || uvs.Length == 0)
                {
                    SetStatus("网格 " + mesh.name + " 没有 UV，已跳过。");
                    continue;
                }
                anyUv = true;

                int subCount = Mathf.Max(1, mesh.subMeshCount);
                for (int s = 0; s < subCount; s++)
                {
                    int[] idx;
                    try
                    {
                        idx = mesh.GetTriangles(s);
                    }
                    catch (Exception e)
                    {
                        SetStatus("读取网格 " + mesh.name + " 子网格 " + s + " 失败：" + e.Message);
                        continue;
                    }
                    if (idx == null)
                    {
                        continue;
                    }

                    for (int i = 0; i + 2 < idx.Length; i += 3)
                    {
                        int ia = idx[i];
                        int ib = idx[i + 1];
                        int ic = idx[i + 2];
                        if (ia >= verts.Length || ib >= verts.Length || ic >= verts.Length ||
                            ia >= uvs.Length || ib >= uvs.Length || ic >= uvs.Length)
                        {
                            continue;
                        }

                        AxisBakeTriangle tri;
                        tri.uv0 = uvs[ia];
                        tri.uv1 = uvs[ib];
                        tri.uv2 = uvs[ic];
                        tri.a0 = verts[ia][axAxis];
                        tri.a1 = verts[ib][axAxis];
                        tri.a2 = verts[ic][axAxis];
                        triangles.Add(tri);

                        if (axRangeMode == 0)
                        {
                            if (tri.a0 < rangeMin) rangeMin = tri.a0;
                            if (tri.a1 < rangeMin) rangeMin = tri.a1;
                            if (tri.a2 < rangeMin) rangeMin = tri.a2;
                            if (tri.a0 > rangeMax) rangeMax = tri.a0;
                            if (tri.a1 > rangeMax) rangeMax = tri.a1;
                            if (tri.a2 > rangeMax) rangeMax = tri.a2;
                        }
                    }
                }
            }

            if (!anyUv || triangles.Count == 0)
            {
                SetStatus("没有可烘焙的网格数据（缺 UV 或缺三角形）。");
                return null;
            }

            if (axRangeMode == 1)
            {
                rangeMin = axMin;
                rangeMax = axMax;
            }
            if (rangeMax - rangeMin < 1e-6f)
            {
                rangeMax = rangeMin + 1f;
            }
            float invRange = 1f / (rangeMax - rangeMin);

            // 2) 光栅化到 UV 像素空间
            float[] gbuf = new float[size * size];
            byte[] mask = new byte[size * size];

            for (int t = 0; t < triangles.Count; t++)
            {
                if (showProgress && (t & 255) == 0)
                {
                    EditorUtility.DisplayProgressBar("烘焙轴向渐变",
                        "光栅化三角形 " + (t + 1) + "/" + triangles.Count, (float)t / triangles.Count);
                }
                AxisBakeTriangle tri = triangles[t];
                float g0 = Mathf.Clamp01((tri.a0 - rangeMin) * invRange);
                float g1 = Mathf.Clamp01((tri.a1 - rangeMin) * invRange);
                float g2 = Mathf.Clamp01((tri.a2 - rangeMin) * invRange);
                RasterizeTriangle(tri.uv0, tri.uv1, tri.uv2, g0, g1, g2, size, gbuf, mask);
            }

            // 3) 边缘扩展，补 UV 接缝
            Dilate(gbuf, mask, size, axBleed, showProgress);

            // 4) 准备噪波
            NoiseData noise = null;
            if (axNoiseEnabled)
            {
                noise = BuildNoiseData();
            }

            // 5) 渐变值（+ 噪波）→ 像素颜色
            Color[] pixels = new Color[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    int i = y * size + x;
                    float g = gbuf[i];
                    if (axReverse)
                    {
                        g = 1f - g;
                    }
                    g = Mathf.Clamp01(g);

                    if (noise != null && mask[i] != 0)
                    {
                        float u = (x + 0.5f) / size;
                        float v = (y + 0.5f) / size;
                        float n = noise.Sample(u * axNoiseTiling, v * axNoiseTiling);
                        g = Mathf.Clamp01(ApplyNoiseBlend(g, n, axNoiseStrength, axNoiseBlend));
                    }

                    Color col;
                    if (axMode == (int)AxisGradientMode.Grayscale)
                    {
                        col = new Color(g, g, g, axWriteAlpha ? g : 1f);
                    }
                    else
                    {
                        col = axGradient != null ? axGradient.Evaluate(g) : new Color(g, g, g, 1f);
                        if (axWriteAlpha)
                        {
                            col.a = g;
                        }
                    }
                    pixels[i] = col;
                }
            }

            Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.SetPixels(pixels);
            tex.Apply(false);
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Bilinear;
            return tex;
        }
        catch (Exception e)
        {
            Debug.LogException(e);
            SetStatus("轴向渐变烘焙失败：" + e.Message);
            return null;
        }
        finally
        {
            if (showProgress)
            {
                EditorUtility.ClearProgressBar();
            }
        }
    }

    /// <summary>把单个三角形按 UV 光栅化进 gbuf/mask，重心坐标插值出渐变值。</summary>
    private static void RasterizeTriangle(
        Vector2 uva, Vector2 uvb, Vector2 uvc,
        float g0, float g1, float g2,
        int size, float[] gbuf, byte[] mask)
    {
        // UV 钳制到 [0,1]，越界按边界处理（覆盖绝大多数模型；大范围平铺 UV 建议先用其它工具规整）
        Vector2 A = new Vector2(Mathf.Clamp01(uva.x), Mathf.Clamp01(uva.y));
        Vector2 B = new Vector2(Mathf.Clamp01(uvb.x), Mathf.Clamp01(uvb.y));
        Vector2 C = new Vector2(Mathf.Clamp01(uvc.x), Mathf.Clamp01(uvc.y));

        float f = size - 1;
        float x0 = A.x * f, y0 = A.y * f;
        float x1 = B.x * f, y1 = B.y * f;
        float x2 = C.x * f, y2 = C.y * f;

        int minX = Mathf.Clamp(Mathf.FloorToInt(Mathf.Min(x0, Mathf.Min(x1, x2))) - 1, 0, size - 1);
        int maxX = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(x0, Mathf.Max(x1, x2))) + 1, 0, size - 1);
        int minY = Mathf.Clamp(Mathf.FloorToInt(Mathf.Min(y0, Mathf.Min(y1, y2))) - 1, 0, size - 1);
        int maxY = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(y0, Mathf.Max(y1, y2))) + 1, 0, size - 1);

        if (minX > maxX || minY > maxY)
        {
            return;
        }

        float invS = 1f / size;
        for (int py = minY; py <= maxY; py++)
        {
            for (int px = minX; px <= maxX; px++)
            {
                Vector2 p = new Vector2((px + 0.5f) * invS, (py + 0.5f) * invS);
                float w0, w1, w2;
                if (!Barycentric(p, A, B, C, out w0, out w1, out w2))
                {
                    continue;
                }
                int idx = py * size + px;
                gbuf[idx] = w0 * g0 + w1 * g1 + w2 * g2;
                mask[idx] = 1;
            }
        }
    }

    /// <summary>重心坐标（对任意顶点绕向都成立）；点在三角形内返回 true。</summary>
    private static bool Barycentric(Vector2 p, Vector2 a, Vector2 b, Vector2 c,
        out float w0, out float w1, out float w2)
    {
        Vector2 v0 = b - a;
        Vector2 v1 = c - a;
        Vector2 v2 = p - a;
        float d00 = Vector2.Dot(v0, v0);
        float d01 = Vector2.Dot(v0, v1);
        float d11 = Vector2.Dot(v1, v1);
        float d20 = Vector2.Dot(v2, v0);
        float d21 = Vector2.Dot(v2, v1);
        float denom = d00 * d11 - d01 * d01;
        if (Mathf.Abs(denom) < 1e-12f)
        {
            w0 = w1 = w2 = 0f;
            return false;
        }

        float invD = 1f / denom;
        w1 = (d11 * d20 - d01 * d21) * invD; // 顶点 b 的权重
        w2 = (d00 * d21 - d01 * d20) * invD; // 顶点 c 的权重
        w0 = 1f - w1 - w2;                   // 顶点 a 的权重

        // 小负容差，让相邻三角形共享边上的像素也能被填充，避免接缝漏点
        const float eps = -1e-4f;
        return w0 >= eps && w1 >= eps && w2 >= eps;
    }

    /// <summary>把已填充像素向空像素扩散若干轮（8 邻域取均值），补 UV 岛边缘接缝。原地修改 gbuf/mask。</summary>
    private static void Dilate(float[] gbuf, byte[] mask, int size, int iterations, bool showProgress)
    {
        if (iterations <= 0)
        {
            return;
        }

        for (int it = 0; it < iterations; it++)
        {
            if (showProgress)
            {
                EditorUtility.DisplayProgressBar("烘焙轴向渐变",
                    "边缘扩展 " + (it + 1) + "/" + iterations, (float)it / iterations);
            }

            float[] src = (float[])gbuf.Clone();
            byte[] srcMask = (byte[])mask.Clone();
            bool any = false;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    int idx = y * size + x;
                    if (srcMask[idx] != 0)
                    {
                        continue;
                    }

                    float sum = 0f;
                    int cnt = 0;
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        int yy = y + dy;
                        if (yy < 0 || yy >= size)
                        {
                            continue;
                        }
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            if (dx == 0 && dy == 0)
                            {
                                continue;
                            }
                            int xx = x + dx;
                            if (xx < 0 || xx >= size)
                            {
                                continue;
                            }
                            int n = yy * size + xx;
                            if (srcMask[n] != 0)
                            {
                                sum += src[n];
                                cnt++;
                            }
                        }
                    }

                    if (cnt > 0)
                    {
                        gbuf[idx] = sum / cnt;
                        mask[idx] = 1;
                        any = true;
                    }
                }
            }

            if (!any)
            {
                break;
            }
        }
    }

    // ================= 噪波 =================

    private NoiseData BuildNoiseData()
    {
        if (axNoiseSource == 1)
        {
            if (axNoiseTexture != null)
            {
                NoiseData nd = ReadNoiseTexture(axNoiseTexture);
                if (nd != null)
                {
                    return nd;
                }
            }
            SetStatus("噪波贴图无效，改用内置程序噪波。");
        }
        return GenerateProceduralNoise(256);
    }

    private NoiseData ReadNoiseTexture(Texture2D tex)
    {
        int w = Mathf.Max(1, tex.width);
        int h = Mathf.Max(1, tex.height);
        RenderTexture rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
        if (rt == null)
        {
            return null;
        }

        RenderTexture prev = RenderTexture.active;
        Texture2D read = new Texture2D(w, h, TextureFormat.RGBA32, false);
        try
        {
            Graphics.Blit(tex, rt);
            RenderTexture.active = rt;
            read.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            read.Apply(false);
            Color[] px = read.GetPixels();
            NoiseData nd = new NoiseData { lum = new float[px.Length], w = w, h = h };
            for (int i = 0; i < px.Length; i++)
            {
                Color c = px[i];
                nd.lum[i] = 0.299f * c.r + 0.587f * c.g + 0.114f * c.b;
            }
            return nd;
        }
        catch (Exception e)
        {
            SetStatus("读取噪波贴图失败：" + e.Message);
            return null;
        }
        finally
        {
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
            DestroyImmediate(read);
        }
    }

    private NoiseData GenerateProceduralNoise(int size)
    {
        int w = size;
        int h = size;
        NoiseData nd = new NoiseData { lum = new float[w * h], w = w, h = h };

        const int octaves = 5;
        const int basePeriod = 8;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                float u = (float)x / w;
                float v = (float)y / h;
                nd.lum[y * w + x] = Fbm(u, v, basePeriod, octaves);
            }
        }
        return nd;
    }

    /// <summary>分形值噪声（fBM），逐八度都按周期平铺，保证贴图无缝。</summary>
    private static float Fbm(float u, float v, int basePeriod, int octaves)
    {
        float sum = 0f;
        float amp = 1f;
        float norm = 0f;
        for (int o = 0; o < octaves; o++)
        {
            int freq = 1 << o;
            int period = basePeriod * freq;
            sum += amp * ValueNoise(u * basePeriod * freq, v * basePeriod * freq, period);
            norm += amp;
            amp *= 0.5f;
        }
        return sum / norm;
    }

    private static float ValueNoise(float x, float y, int period)
    {
        int x0 = Mathf.FloorToInt(x);
        int y0 = Mathf.FloorToInt(y);
        float fx = x - x0;
        float fy = y - y0;
        float sx = fx * fx * (3f - 2f * fx);
        float sy = fy * fy * (3f - 2f * fy);

        int hx0 = Mod(x0, period), hx1 = Mod(x0 + 1, period);
        int hy0 = Mod(y0, period), hy1 = Mod(y0 + 1, period);

        float a = Hash01(hx0, hy0);
        float b = Hash01(hx1, hy0);
        float c = Hash01(hx0, hy1);
        float d = Hash01(hx1, hy1);

        float top = Mathf.Lerp(a, b, sx);
        float bot = Mathf.Lerp(c, d, sx);
        return Mathf.Lerp(top, bot, sy);
    }

    private static int Mod(int a, int b) { return (a % b + b) % b; }

    private static float Hash01(int x, int y)
    {
        unchecked
        {
            int n = x * 374761393 + y * 668265263;
            n = (n ^ (n >> 13)) * 1274126177;
            n = n ^ (n >> 16);
            return (n & 0x00ffffff) / 16777215f;
        }
    }

    private static float ApplyNoiseBlend(float g, float n, float s, int mode)
    {
        switch (mode)
        {
            case 0: // 相加扰动
                return g + (n - 0.5f) * 2f * s;
            case 1: // 相乘
                return g * Mathf.Lerp(1f, n, s);
            case 2: // 覆盖
            {
                float t = Mathf.Lerp(0.5f, n, s);
                return g < 0.5f ? 2f * g * t : 1f - 2f * (1f - g) * (1f - t);
            }
            default:
                return g;
        }
    }

    // ================= 导出 =================

    private void ExportPng()
    {
        List<Mesh> meshes = GetBakeMeshes();
        if (meshes.Count == 0)
        {
            SetStatus("请先指定模型或网格。");
            return;
        }

        string baseName = axModel != null
            ? axModel.name
            : (axMeshAsset != null ? axMeshAsset.name : "AxisGradient");
        string fileName = string.IsNullOrEmpty(axOutputName) ? baseName + "_AxisGradient" : axOutputName.Trim();
        string absPath = EditorUtility.SaveFilePanel("导出轴向渐变烘焙 PNG", "Assets", fileName, "png");
        if (string.IsNullOrEmpty(absPath))
        {
            return;
        }

        int size = Mathf.Clamp(axResolution, 8, 4096);
        Texture2D tex = BakeTexture(meshes, size);
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
            TextureImporter importer = AssetImporter.GetAtPath(rel) as TextureImporter;
            if (importer != null)
            {
                importer.textureType = TextureImporterType.Default;
                importer.sRGBTexture = axSRGB;
                importer.mipmapEnabled = axMipmap;
                importer.wrapMode = TextureWrapMode.Clamp;
                importer.npotScale = TextureImporterNPOTScale.None;
                importer.alphaSource = TextureImporterAlphaSource.FromInput;
                importer.alphaIsTransparency = false;

                int maxSize = 32;
                while (maxSize < size && maxSize < 8192)
                {
                    maxSize *= 2;
                }
                importer.maxTextureSize = Mathf.Clamp(maxSize, 32, 8192);
                importer.SaveAndReimport();
            }

            Texture2D loaded = AssetDatabase.LoadAssetAtPath<Texture2D>(rel);
            if (loaded != null)
            {
                EditorGUIUtility.PingObject(loaded);
            }
            SetStatus("轴向渐变已烘焙并导出：" + rel + "（" + size + " × " + size + "）");
        }
        else
        {
            SetStatus("已导出到项目外（未导入 Unity）：" + absPath);
        }
    }

    // ================= 公共工具 =================

    private void SetStatus(string msg)
    {
        statusMessage = msg;
        Debug.Log("[轴向渐变烘焙] " + msg);
    }

    private static string ToAssetsRelative(string absPath)
    {
        string dataPath = Application.dataPath.Replace('\\', '/');
        string p = absPath.Replace('\\', '/');
        if (p.StartsWith(dataPath, StringComparison.OrdinalIgnoreCase))
        {
            return "Assets" + p.Substring(dataPath.Length);
        }
        return null;
    }
}
