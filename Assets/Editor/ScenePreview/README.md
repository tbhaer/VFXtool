# 预览工具箱（ScenePreview）

入口：Unity 菜单 **Tools / 预览工具箱**，窗口顶部切换「场景预览 / 预制体预览」两个标签。

---

## 一、拖拽添加 + 多选快捷归类/删除

列表条目可直接拖到其它分类标题或该分类内任一条目上完成归类；已多选时，拖动任一选中项会整批移动。右键菜单也使用同一套批量移动逻辑。

| 从哪里拖 | 拖什么 | 结果 |
| --- | --- | --- |
| Project 窗口 | 一个或多个 `.unity` / `.prefab` | 加入预览列表 |
| Project 窗口 | 一个文件夹 | 递归收编文件夹内所有场景 / 预制体 |
| Hierarchy | 预制体实例 | 解析成对应的源预制体资源后加入 |
| Hierarchy | 场景里的物体 | 场景预览标签下会加入该物体所属的场景 |

细节：

- **从 Project/Hierarchy 拖入**：拖到左侧分类标题 / 分类内条目上会归入该分类；拖到窗口其它地方（包括右侧预览区）归入「未分类」，只拖入 1 个时还会自动打开预览。
- **多选**：
  - `Ctrl/Cmd + 单击` 加选/减选，`Shift + 单击` 从上次点击处连选；
  - 列表上方工具条显示「已选 N 个」，提供「全选 / 清空 / 删除」；`Ctrl/Cmd + A` 全选当前可见项；`Esc` 清空；
  - 多选后右键任意选中项 →「移动选中的 N 个到/…」或「删除选中的 N 个」；
  - 多选后拖动任意选中项到其它分类 → 整批移动；
  - 删除只从预览列表移除，不会删除磁盘资源；移除当前预览项时会同时关闭预览。
- **右键归类**：右键任意条目 →「移动到分类/未分类」或「移动到分类/A/子」，立即移动；目标分类折叠会自动展开。
- **拖拽归类**：列表条目可拖到分类标题或分类内条目；从 Project 再次拖入一个已经收录的资源时，也会直接移动到投放分类，不再只提示“重复”。
- **单击条目**打开预览；Ctrl/Shift 单击只改选择、不打开预览；场景标签下双击会在编辑器中正式打开场景。
- **高亮提示**：外部资源拖动时目标分类会高亮，并显示「添加 N 个场景到「XXX」」；无效资源显示红色提示。
- **批量保护**：一次拖入超过 100 个资源时会先弹窗确认。
- **自动预览**：只添加 1 个新资源时，会顺手打开它的预览；一次添加多个只入列表，避免重复加载。
- **去重**：已在目标分类中的资源会自动跳过；已在其它分类中的资源会移动到投放分类。
- **右键菜单**：移动分类、定位、正式打开、从预览列表移除。
- **预览坐标系**：预览右下角显示随相机朝向变化的世界坐标轴，并明确标注左 `-X`、右 `+X`、前 `+Z`。

### 两级分类

分类最多支持两级，条目可以直接放在一级分类下，也可以放在二级分类下：

```
未分类
A
├─ Demo5            ← 直接挂在一级分类 A
├─ ▼ A-子分类
│    ├─ Demo6
│    └─ Demo7
└─ ▼ A-另一个子分类
```

- 一级分类：点顶部「分类＋」新建；
- 二级分类：点一级分类标题右侧的「+」，或右键一级分类标题 →「新建子分类」；
- 条目归类：右键条目 →「移动到分类/A/子」；从 Project 拖入时也可以直接放到分类标题上；
- 一级分类标题上的数字 = 它自己的条目 + 所有二级分类的条目总数；
- 折叠/展开：一级、二级分类各自独立记忆，保存在布局 JSON 里；
- 删除一级分类会连同它的二级分类一起删除，条目移到「未分类」；
  删除二级分类只删它自己，条目移到它的一级分类里；
- 旧版本配置文件（没有 `parentId`）会自动当作全部一级分类，升级不会丢数据。

> 列表顺序 = 添加顺序，数据保存在 `ProjectSettings/ScenePreviewLayout.json` 与
> `ProjectSettings/PrefabPreviewLayout.json`，不写入任何资源文件。

---

## 二、兼容不同 Unity 版本

目标范围：**Unity 2019.4 LTS ~ Unity 6（6000.x）**，同时向下兼容到 **Unity 2018.4**。

已用真实编辑器程序集编译验证：

| Unity | 验证方式 | 结果 |
| --- | --- | --- |
| 2021.3.9f1（本工程） | 引用该版本 Editor / UnityEngine 程序集整体编译 | 通过 |
| 2022.3.62f2 | 同上 | 通过 |
| 6000.3.23f1（Unity 6） | 同上 | 通过 |
| 2019.4 / 2018.4 | 本机未安装这两个版本；只校验了 `#else` 条件分支能编译 | 首次导入时请留意控制台 |

具体做了这些兼容处理，集中在 `PreviewCompat.cs`：

1. **不再依赖 `com.unity.timeline` 包**
   - 删除了 `using UnityEngine.Timeline;`、`TimelineAsset.GetRootTracks()`、`TrackAsset.GetChildTracks()`。
   - Timeline 改为通过内置的 `PlayableDirector` / `PlayableAsset.outputs` / `GetGenericBinding` 驱动，
     工程里没装 Timeline 包、或 Timeline 版本不同，都不会「缺少程序集」编译失败。
   - `TimelineAsset.outputs` 已包含分组内的所有输出轨道，绑定判定与以前等价。
2. **PrefabUtility 跨版本**
   - Unity 2018.3+ 用 `GetCorrespondingObjectFromSource`，
     老版本走 `GetPrefabParent`（`#if UNITY_2018_3_OR_NEWER`）。
3. **PlayableDirector 跨版本**
   - `timeUpdateMode` / `RebuildGraph` 在 `UNITY_2018_1_OR_NEWER` 下启用，更老的版本保持默认行为。
4. **避免随版本变化的 API**
   - 不再使用 `PlayableGraph.GetOutputByType<T>()`（其在各版本返回类型不一致，实测会编译失败），
     统一走 `PlayableAsset.outputs` + `GetGenericBinding`。
5. **图标缺失回退**
   - 播放/暂停图标名在新旧版本可能缺失，取不到时自动回退为「播放 / 暂停」文字，不会出现空白按钮。
6. **空值保护**
   - 预览会话 Dispose、相机/粒子/Timeline 数组都加了空值保护，旧版本在异常路径上不会因空引用打断关闭流程。
7. **动画默认片段兼容**
   - 除动画控制器默认状态外，补充了 `RuntimeAnimatorController.animationClips` 兜底，
     兼容 `AnimatorOverrideController`、默认状态不是 AnimationClip 的情况。

`PreviewRenderUtility` 相关接口（`cameraFieldOfView`、`lights`、`InstantiatePrefabInScene`、
`BeginPreview` / `Render` / `EndPreview`）在 2019.4 与 Unity 6 的官方源码中一致，未做改动。

---

## 三、文件说明

| 文件 | 作用 |
| --- | --- |
| `PreviewToolboxWindow.cs` | 工具箱窗口与 `PreviewPanel` 基类，标签切换、生命周期转发 |
| `ScenePreviewPanel.cs` | 场景预览：分类列表、Additive 加载、摄像机离屏渲染、粒子/Timeline/动画驱动 |
| `PrefabPreviewPanel.cs` | 预制体预览：`PreviewRenderUtility` 离屏渲染、轨道相机、粒子/Timeline/动画驱动 |
| `PreviewCompat.cs` | 版本兼容层（资产解析、Playables、动画片段、图标） |
| `PreviewDragDrop.cs` | 拖放控制器（Project/Hierarchy 拖入、文件夹展开、分类高亮、条目移动） |

---

## 四、已知限制

- 运行时脚本逻辑（`Update` / 协程 / 物理 / 自定义 `MonoBehaviour` 动画）仍需进入 Play Mode；
  预览只驱动粒子、Timeline、Animator、时间 Shader。
- 场景预览通过 Additive 打开目标场景并临时隐藏其它场景根节点，关闭/切换时还原，不会保存场景。
- 预制体预览的粒子只对根粒子做 `Simulate(..., withChildren: true)`，子粒子由级联驱动。
- 拖入的 `.fbx` 模型预制体实例（源资源后缀是 `.fbx`）不会被当作预制体接收；
  请直接拖 Project 里的 `.prefab` 资源。

---

## 五、自测建议

1. 打开 **Tools / 预览工具箱**，在 Project 里多选几个 `.unity` 拖到左侧栏 → 应高亮并提示数量，松手后入列表。
2. 拖一个文件夹到分类标题上 → 该分类下出现文件夹内的全部场景。
3. 从 Hierarchy 拖一个预制体实例到「预制体预览」左侧 → 列表出现对应 `.prefab`。
4. 按住左侧某个条目拖到另一个分类标题 → 松手后条目移动，`ProjectSettings/*.json` 随之更新。
5. 拖入非当前标签的资源（例如在场景标签拖预制体）→ 显示拒绝光标，状态提示「只接受场景」。
6. 删除 `ProjectSettings/ScenePreviewLayout.json` 后重开窗口 → 应回退为空列表而不报错。
