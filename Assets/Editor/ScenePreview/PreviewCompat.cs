using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Playables;
// AnimatorController / AnimatorStateMachine 只存在于 UnityEditor.Animations，显式别名便于阅读
using AnimatorController = UnityEditor.Animations.AnimatorController;
using AnimatorStateMachine = UnityEditor.Animations.AnimatorStateMachine;

/// <summary>
/// 预览工具箱的 Unity 版本兼容层（目标：Unity 2019.4 LTS ~ Unity 6 / 6000.x，向下兼容到 2018.4）。
///
/// 兼容策略：
///   1. 不依赖 com.unity.timeline 包：不 using UnityEngine.Timeline，Timeline 只通过
///      PlayableDirector / PlayableAsset 这些内置 Playables API 间接驱动，
///      所以工程里没有 Timeline 包、或 Timeline 版本不同，都不会编译失败。
///   2. 对确实存在版本差异的 API 做条件编译：
///      PrefabUtility 源对象解析（2018.3）、PlayableDirector.timeUpdateMode / RebuildGraph（2018.1），
///      新版本走完整实现，老版本自动降级，而不是整份工具报错。
///   3. 查找被 Timeline 绑定的 Animator 只用 PlayableAsset.outputs + GetGenericBinding，
///      不碰 TimelineAsset.GetRootTracks()、PlayableGraph.GetOutputByType() 这类随版本变化的接口。
///   4. UI 图标名在不同版本可能缺失，缺失时回退成文字，不会出现空白按钮。
/// </summary>
internal static class PreviewCompat
{
    /// <summary>当前 Unity 版本字符串，方便在日志/状态栏里定位问题。</summary>
    public static string UnityVersion
    {
        get { return Application.unityVersion; }
    }

    // ================= 资产路径 =================

    public static string NormalizePath(string path)
    {
        return string.IsNullOrEmpty(path) ? "" : path.Replace('\\', '/');
    }

    public static bool IsScenePath(string path)
    {
        path = NormalizePath(path);
        return path.Length > 0 && path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsPrefabPath(string path)
    {
        path = NormalizePath(path);
        return path.Length > 0 && path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsUnderAssets(string path)
    {
        path = NormalizePath(path);
        return path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>对象在工程内的预制体资源路径（支持从 Hierarchy 拖入的预制体实例）；不适用返回 null。</summary>
    public static string PrefabPathOf(UnityEngine.Object obj)
    {
        GameObject go = obj as GameObject;
        if (go == null) return null;

        GameObject source = null;
        try
        {
#if UNITY_2018_3_OR_NEWER
            source = PrefabUtility.GetCorrespondingObjectFromSource(go);
#else
            source = PrefabUtility.GetPrefabParent(go) as GameObject;
#endif
        }
        catch (Exception)
        {
            source = null;
        }

        if (source == null) return null;
        string path = NormalizePath(AssetDatabase.GetAssetPath(source));
        return IsPrefabPath(path) ? path : null;
    }

    /// <summary>Hierarchy 物体所属的已保存场景路径；未保存场景/预制体编辑模式返回 null。</summary>
    public static string ScenePathOf(UnityEngine.Object obj)
    {
        GameObject go = obj as GameObject;
        if (go == null) return null;

        string path = "";
        try { path = NormalizePath(go.scene.path); }
        catch (Exception) { path = ""; }

        return IsScenePath(path) ? path : null;
    }

    // ================= Timeline / Playables =================

    /// <summary>把 PlayableDirector 切成手动更新（避免编辑器按真实时间偷偷推进），并可选重建播放图。</summary>
    public static void PrepareDirector(PlayableDirector director, bool rebuildGraph)
    {
        if (director == null) return;
        try
        {
#if UNITY_2018_1_OR_NEWER
            director.timeUpdateMode = DirectorUpdateMode.Manual;
            if (rebuildGraph) director.RebuildGraph();
#else
            // 2018.1 以前的 PlayableDirector 没有 timeUpdateMode / RebuildGraph，保持编辑器默认行为
#endif
        }
        catch (Exception e)
        {
            Debug.LogWarning("[预览工具箱] 准备 Timeline 失败（已忽略，预览会尽量继续）：" + e.Message);
        }
    }

    /// <summary>播放图未构建时补建一次，保证 PlayableDirector.Evaluate() 能真正驱动画面。</summary>
    public static void EnsureDirectorGraph(PlayableDirector director)
    {
        if (director == null || director.playableAsset == null) return;
        try
        {
            if (!director.playableGraph.IsValid()) director.RebuildGraph();
        }
        catch (Exception e)
        {
            Debug.LogWarning("[预览工具箱] 重建 Timeline 播放图失败（已忽略）：" + e.Message);
        }
    }

    /// <summary>收集被 Timeline（或任意 PlayableAsset）绑定/驱动的 Animator，用于避免重复驱动。</summary>
    public static void CollectBoundAnimators(PlayableDirector director, HashSet<Animator> bound)
    {
        if (director == null || bound == null) return;

        // 只遍历 PlayableAsset.outputs，用 GetGenericBinding 反查绑定对象。
        // TimelineAsset / 自定义 PlayableAsset 都走这套内置 API：
        //   · 不引用 UnityEngine.Timeline，工程没装 Timeline 包也能编译；
        //   · 不依赖 PlayableGraph.GetOutputByType（它的返回类型在各版本之间有差异）；
        //   · TimelineAsset.outputs 本身已包含分组内的所有输出轨道。
        try
        {
            PlayableAsset asset = director.playableAsset;
            if (asset == null) return;

            foreach (PlayableBinding binding in asset.outputs)
            {
                UnityEngine.Object source = binding.sourceObject;
                if (source == null) continue;

                Animator animator = director.GetGenericBinding(source) as Animator;
                if (animator != null) bound.Add(animator);
            }
        }
        catch (Exception)
        {
        }
    }

    // ================= 动画 =================

    /// <summary>
    /// 取 Animator 用于预览的默认动画片段：
    /// 优先动画控制器默认状态的 motion，其次控制器里的第一个片段
    /// （兼容 AnimatorOverrideController、以及默认状态不是 AnimationClip 的情况）。
    /// </summary>
    public static AnimationClip GetDefaultClip(Animator animator)
    {
        if (animator == null) return null;

        try
        {
            AnimatorController controller = animator.runtimeAnimatorController as AnimatorController;
            if (controller != null && controller.layers != null && controller.layers.Length > 0)
            {
                AnimatorStateMachine machine = controller.layers[0].stateMachine;
                if (machine != null && machine.defaultState != null)
                {
                    AnimationClip clip = machine.defaultState.motion as AnimationClip;
                    if (clip != null) return clip;
                }
            }

            RuntimeAnimatorController runtimeController = animator.runtimeAnimatorController;
            if (runtimeController != null)
            {
                AnimationClip[] clips = runtimeController.animationClips;
                if (clips != null)
                {
                    for (int i = 0; i < clips.Length; i++)
                        if (clips[i] != null) return clips[i];
                }
            }
        }
        catch (Exception)
        {
        }

        return null;
    }

    // ================= 编辑器 UI =================

    private static GUIContent playIcon;
    private static GUIContent pauseIcon;

    /// <summary>播放/暂停按钮图标；不同 Unity 版本图标名缺失时回退为文字。</summary>
    public static GUIContent PlayPauseIcon(bool playing)
    {
        GUIContent cached = playing ? pauseIcon : playIcon;
        if (cached != null) return cached;

        GUIContent content = null;
        try
        {
            content = EditorGUIUtility.IconContent(playing ? "PauseButton" : "PlayButton");
        }
        catch (Exception)
        {
            content = null;
        }

        if (content == null || content.image == null)
            content = new GUIContent(playing ? "暂停" : "播放");
        else
            content = new GUIContent(content.image, content.tooltip);

        if (playing) pauseIcon = content;
        else playIcon = content;
        return content;
    }

    // ================= 预览坐标系 =================

    private static GUIStyle axisTitleStyle;
    private static GUIStyle axisHintStyle;

    /// <summary>
    /// 在预览右下角绘制随相机旋转的世界坐标轴。Unity 约定：右 = +X、上 = +Y、前 = +Z、左 = -X。
    /// </summary>
    public static void DrawOrientationGizmo(Rect view, Quaternion cameraRotation)
    {
        Event e = Event.current;
        if (e == null || e.type != EventType.Repaint || view.width < 150f || view.height < 120f) return;

        const float width = 132f;
        const float height = 92f;
        Rect panel = new Rect(view.xMax - width - 8f, view.yMax - height - 8f, width, height);
        EditorGUI.DrawRect(panel, new Color(0f, 0f, 0f, 0.55f));

        if (axisTitleStyle == null)
        {
            axisTitleStyle = new GUIStyle(EditorStyles.miniBoldLabel) { alignment = TextAnchor.MiddleCenter };
            axisTitleStyle.normal.textColor = new Color(1f, 1f, 1f, 0.92f);
        }
        if (axisHintStyle == null)
        {
            axisHintStyle = new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleCenter };
            axisHintStyle.normal.textColor = new Color(0.9f, 0.9f, 0.9f, 0.88f);
        }

        GUI.Label(new Rect(panel.x, panel.y + 2f, panel.width, 16f), "世界坐标", axisTitleStyle);

        Vector2 origin = new Vector2(panel.center.x, panel.y + 50f);
        Quaternion inverseCamera = Quaternion.Inverse(cameraRotation);

        Handles.BeginGUI();
        Color old = Handles.color;
        DrawAxis(origin, inverseCamera * Vector3.right, new Color(1f, 0.28f, 0.25f, 1f), "X");
        DrawAxis(origin, inverseCamera * Vector3.up, new Color(0.35f, 0.95f, 0.4f, 1f), "Y");
        DrawAxis(origin, inverseCamera * Vector3.forward, new Color(0.3f, 0.62f, 1f, 1f), "Z");
        Handles.color = old;
        Handles.EndGUI();

        GUI.Label(new Rect(panel.x + 2f, panel.yMax - 19f, panel.width - 4f, 16f), "左 -X  ·  右 +X  ·  前 +Z", axisHintStyle);
    }

    private static void DrawAxis(Vector2 origin, Vector3 cameraSpaceAxis, Color color, string label)
    {
        Vector2 flat = new Vector2(cameraSpaceAxis.x, -cameraSpaceAxis.y);
        float visibility = Mathf.Clamp01(flat.magnitude);
        Vector2 direction = visibility > 0.02f ? flat / flat.magnitude : new Vector2(0f, -1f);
        float length = Mathf.Lerp(7f, 25f, visibility);
        Vector2 positive = origin + direction * length;
        Vector2 negative = origin - direction * length * 0.65f;

        Handles.color = new Color(color.r, color.g, color.b, 0.35f);
        Handles.DrawAAPolyLine(1.5f, new Vector3(negative.x, negative.y), new Vector3(origin.x, origin.y));

        Handles.color = color;
        Handles.DrawAAPolyLine(2.5f, new Vector3(origin.x, origin.y), new Vector3(positive.x, positive.y));

        Vector2 perpendicular = new Vector2(-direction.y, direction.x);
        Vector2 arrowBase = positive - direction * 5f;
        Handles.DrawAAPolyLine(2f,
            new Vector3(positive.x, positive.y),
            new Vector3(arrowBase.x + perpendicular.x * 3f, arrowBase.y + perpendicular.y * 3f));
        Handles.DrawAAPolyLine(2f,
            new Vector3(positive.x, positive.y),
            new Vector3(arrowBase.x - perpendicular.x * 3f, arrowBase.y - perpendicular.y * 3f));

        GUIStyle labelStyle = new GUIStyle(EditorStyles.miniBoldLabel) { alignment = TextAnchor.MiddleCenter };
        labelStyle.normal.textColor = color;
        GUI.Label(new Rect(positive.x - 9f, positive.y - 9f, 18f, 18f), label, labelStyle);
    }
}
