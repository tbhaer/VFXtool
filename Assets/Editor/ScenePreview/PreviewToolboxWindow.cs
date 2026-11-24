using UnityEditor;
using UnityEngine;

/// <summary>
/// 预览工具箱（Tools / 预览工具箱）：把场景预览、预制体预览集成到同一个窗口，
/// 顶部工具栏切换，组织方式与 VFX 工具箱一致。
/// 宿主窗口只负责标签切换与生命周期转发；每个预览工具是一个独立的 PreviewPanel，
/// 保留各自的全部状态、持久化配置与渲染逻辑，互不干扰，便于以后继续加新预览工具。
/// </summary>
public class PreviewToolboxWindow : EditorWindow
{
    private static readonly string[] TabNames = { "场景预览", "预制体预览" };

    private int tab;
    private ScenePreviewPanel scenePanel;
    private PrefabPreviewPanel prefabPanel;
    private PreviewPanel[] panels;

    [MenuItem("Tools/预览工具箱")]
    public static void OpenToolbox()
    {
        OpenTab(0);
    }

    public static void OpenTab(int t)
    {
        PreviewToolboxWindow win = GetWindow<PreviewToolboxWindow>("预览工具箱");
        win.minSize = new Vector2(800, 480);
        win.tab = Mathf.Clamp(t, 0, TabNames.Length - 1);
        if (win.panels != null) win.SetActive(win.tab);
        win.Show();
    }

    private void OnEnable()
    {
        titleContent = new GUIContent("预览工具箱");
        scenePanel = new ScenePreviewPanel();
        prefabPanel = new PrefabPreviewPanel();
        panels = new PreviewPanel[] { scenePanel, prefabPanel };
        foreach (PreviewPanel p in panels)
        {
            p.Bind(this);
            p.OnEnable();
        }
        SetActive(tab);
    }

    private void OnDisable()
    {
        if (panels == null) return;
        foreach (PreviewPanel p in panels)
        {
            p.isActive = false;
            p.OnDisable();
        }
    }

    private void OnBecameVisible()
    {
        ActivePanel()?.OnBecameVisible();
    }

    private PreviewPanel ActivePanel()
    {
        return panels != null && tab >= 0 && tab < panels.Length ? panels[tab] : null;
    }

    private void SetActive(int t)
    {
        if (panels == null) return;
        t = Mathf.Clamp(t, 0, panels.Length - 1);
        for (int i = 0; i < panels.Length; i++)
            panels[i].isActive = i == t;
        panels[t].OnActivate();
        Repaint();
    }

    private void OnGUI()
    {
        int nt = GUILayout.Toolbar(tab, TabNames, EditorStyles.miniButton, GUILayout.Height(22));
        if (nt != tab)
        {
            tab = nt;
            SetActive(tab);
        }
        ActivePanel()?.OnGUI();
    }
}

/// <summary>
/// 预览面板基类：一个可被宿主窗口挂载、切换的独立预览工具。
/// 面板不是 EditorWindow，不单独开窗口；通过 host 请求重绘，仅在 isActive 时刷新与推进时间。
/// </summary>
internal abstract class PreviewPanel
{
    protected PreviewToolboxWindow host;
    public bool isActive;
    private bool activatedOnce;

    public abstract string Title { get; }

    public void Bind(PreviewToolboxWindow h)
    {
        host = h;
    }

    public virtual void OnEnable() { }
    public virtual void OnDisable() { }
    public virtual void OnBecameVisible() { }

    /// <summary>每次切到该标签时调用；首次激活额外调用 OnFirstActivate（用于恢复上次预览，避免后台标签预加载）。</summary>
    public void OnActivate()
    {
        if (!activatedOnce)
        {
            activatedOnce = true;
            OnFirstActivate();
        }
        OnBecameVisible();
    }

    /// <summary>首次成为活动标签时调用一次（延迟到下一帧由实现自行处理）。</summary>
    public virtual void OnFirstActivate() { }

    public abstract void OnGUI();

    /// <summary>请求宿主重绘；非活动面板不刷新（后台预览自动暂停，省 CPU）。</summary>
    protected void Repaint()
    {
        if (isActive && host != null) host.Repaint();
    }
}
