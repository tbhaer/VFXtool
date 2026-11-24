using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>
/// 特效预制辅助脚本（挂载型，由原 DelayAddTime 与 ParticleScalingModeApplier 合并）。
/// 功能一：预制启动延迟——总开关 useDelay 打开且 DelayTime > 0 时，OnEnable 先隐藏本物体，
///         等待 DelayTime 秒后自动激活；开关关闭则延迟时间完全失效，物体直接播放。
///         添加/清除延迟两个按钮始终按 DelayTime 生效，不受总开关影响。
/// 功能二：批量统一设置所有子粒子的 Scaling Mode（Hierarchy / Local / Shape）。
/// 编辑器按钮见文件底部的 ParticleDelayAndScalingEditor。
/// </summary>
[DisallowMultipleComponent]
public class ParticleDelayAndScaling : MonoBehaviour
{
    public enum ScalingMode
    {
        Hierarchy = 0,
        Local = 1,
        Shape = 2
    }

    [Tooltip("整个延迟功能的总开关：勾选时 OnEnable 的延迟生效；取消勾选时延迟时间完全失效，物体立即播放（不影响下方两个延迟按钮）")]
    public bool useDelay = true;

    [Tooltip("启用物体时若该值大于 0，会先隐藏本物体，等待该时间后再激活；Add Delay 也会把该值加到子粒子 Start Delay 上")]
    public float DelayTime = 0f;

    [Tooltip("点击 Inspector 按钮后，批量应用到所有子粒子")]
    public ScalingMode scalingMode = ScalingMode.Hierarchy;

    private bool isDelayCallback = false;

    private void OnEnable()
    {
        // 总开关关闭时：延迟时间完全失效，不做任何隐藏，物体按正常流程立即播放
        if (!useDelay)
        {
            CancelInvoke("DelayFunc");
            isDelayCallback = false;
            return;
        }

        if (!isDelayCallback && DelayTime > 0f)
        {
            isDelayCallback = true;
            gameObject.SetActive(false);
            Invoke("DelayFunc", DelayTime);
        }
    }

    private void DelayFunc()
    {
        gameObject.SetActive(true);
        isDelayCallback = false;
    }

    /// <summary>把 DelayTime 加到所有子粒子的 Start Delay（常量模式）上。</summary>
    public void AddDelay()
    {
        ParticleSystem[] particleSystems = GetComponentsInChildren<ParticleSystem>(true);
        foreach (ParticleSystem ps in particleSystems)
        {
            ParticleSystem.MainModule main = ps.main;
            main.startDelay = new ParticleSystem.MinMaxCurve(main.startDelay.constant + DelayTime);
        }
    }

    /// <summary>从所有子粒子的 Start Delay 中减去 DelayTime。</summary>
    public void ClearDelay()
    {
        ParticleSystem[] particleSystems = GetComponentsInChildren<ParticleSystem>(true);
        foreach (ParticleSystem ps in particleSystems)
        {
            ParticleSystem.MainModule main = ps.main;
            main.startDelay = new ParticleSystem.MinMaxCurve(Mathf.Max(0f, main.startDelay.constant - DelayTime));
        }
    }

#if UNITY_EDITOR
    [ContextMenu("Apply Scaling Mode To Children")]
    public void ApplyScalingMode()
    {
        ParticleSystemScalingMode psMode = ParticleSystemScalingMode.Hierarchy;
        switch (scalingMode)
        {
            case ScalingMode.Hierarchy:
                psMode = ParticleSystemScalingMode.Hierarchy;
                break;
            case ScalingMode.Local:
                psMode = ParticleSystemScalingMode.Local;
                break;
            case ScalingMode.Shape:
                psMode = ParticleSystemScalingMode.Shape;
                break;
        }

        ParticleSystem[] particles = GetComponentsInChildren<ParticleSystem>(true);
        int count = 0;
        foreach (ParticleSystem ps in particles)
        {
            ParticleSystem.MainModule main = ps.main;
            main.scalingMode = psMode;
            UnityEditor.EditorUtility.SetDirty(ps);
            count++;
        }

        Debug.Log(string.Format(
            "[ParticleDelayAndScaling] Applied scalingMode = {0} to {1} ParticleSystems under '{2}'",
            psMode, count, name));
    }
#endif
}

#if UNITY_EDITOR
/// <summary>中文紧凑排版的 Inspector：两个设置项 + 分区按钮。</summary>
[UnityEditor.CustomEditor(typeof(ParticleDelayAndScaling))]
public class ParticleDelayAndScalingEditor : UnityEditor.Editor
{
    private UnityEditor.SerializedProperty useDelayProp;
    private UnityEditor.SerializedProperty delayProp;
    private UnityEditor.SerializedProperty modeProp;

    private void OnEnable()
    {
        useDelayProp = serializedObject.FindProperty("useDelay");
        delayProp = serializedObject.FindProperty("DelayTime");
        modeProp = serializedObject.FindProperty("scalingMode");
    }

    public override void OnInspectorGUI()
    {
        serializedObject.Update();

        // 顶部脚本引用框（与默认 Inspector 一致，只读）
        using (new UnityEditor.EditorGUI.DisabledGroupScope(true))
        {
            UnityEditor.EditorGUILayout.PropertyField(serializedObject.FindProperty("m_Script"));
        }

        EditorGUILayout.Space(4);

        // —— 启动设置：延迟总开关 + 延迟时间 + 两个延迟按钮 ——
        UnityEditor.EditorGUILayout.LabelField("启动设置", UnityEditor.EditorStyles.boldLabel);
        UnityEditor.EditorGUILayout.PropertyField(useDelayProp, new GUIContent("使用延迟", "总开关：关闭时延迟时间无效，物体不做延迟直接播放；下方两个延迟按钮不受影响"));

        // 总开关关闭时，延迟时间置灰表示已失效（按钮仍可正常点击）
        bool delayDisabled = !useDelayProp.hasMultipleDifferentValues && !useDelayProp.boolValue;
        using (new UnityEditor.EditorGUI.DisabledGroupScope(delayDisabled))
        {
            UnityEditor.EditorGUILayout.PropertyField(delayProp, new GUIContent("延迟时间（秒）"));
        }

        UnityEditor.EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("添加粒子延迟", GUILayout.Height(22)))
        {
            ForEachTarget(t => { RecordChildren(t); t.AddDelay(); });
        }
        if (GUILayout.Button("清除粒子延迟", GUILayout.Height(22)))
        {
            ForEachTarget(t => { RecordChildren(t); t.ClearDelay(); });
        }
        UnityEditor.EditorGUILayout.EndHorizontal();

        EditorGUILayout.Space(6);

        // —— 缩放模式：枚举 + 应用按钮 ——
        UnityEditor.EditorGUILayout.LabelField("缩放模式", UnityEditor.EditorStyles.boldLabel);
        UnityEditor.EditorGUILayout.PropertyField(modeProp, new GUIContent("粒子缩放模式"));
        if (GUILayout.Button("应用到全部子粒子", GUILayout.Height(24)))
        {
            ForEachTarget(t => { RecordChildren(t); t.ApplyScalingMode(); });
        }

        serializedObject.ApplyModifiedProperties();
    }

    private void ForEachTarget(System.Action<ParticleDelayAndScaling> action)
    {
        foreach (Object targetObject in targets)
        {
            ParticleDelayAndScaling t = targetObject as ParticleDelayAndScaling;
            if (t != null)
            {
                action(t);
            }
        }
    }

    private static void RecordChildren(ParticleDelayAndScaling script)
    {
        ParticleSystem[] systems = script.GetComponentsInChildren<ParticleSystem>(true);
        UnityEditor.Undo.RecordObjects(systems, "ParticleDelayAndScaling");
        UnityEditor.Undo.RecordObject(script, "ParticleDelayAndScaling");
    }
}
#endif
