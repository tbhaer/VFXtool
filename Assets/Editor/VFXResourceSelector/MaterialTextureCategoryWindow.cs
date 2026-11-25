using System.Collections.Generic;
using System;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

/// <summary>
/// 材质贴图分类工具窗口：绑定一个材质后列出其全部贴图属性，
/// 每个属性可通过“分类选择…”按钮打开 VFX 资源分类选择器并直接赋值。
/// 打开方式：菜单 Tools / Material Texture Category Selector，或材质检视面板底部的“贴图分类选择器…”按钮。
/// </summary>
public class MaterialTextureCategoryWindow : EditorWindow
{
    private Material targetMaterial;
    private class TexturePropertyInfo
    {
        public string name;
        public string displayName;
    }

    private List<TexturePropertyInfo> textureProperties = new List<TexturePropertyInfo>();
    private Vector2 scroll;
    private Shader cachedShader;

    [MenuItem("Tools/Material Texture Category Selector")]
    public static void OpenFromMenu()
    {
        Material mat = Selection.activeObject as Material;
        Open(mat);
    }

    public static void Open(Material material)
    {
        MaterialTextureCategoryWindow window = GetWindow<MaterialTextureCategoryWindow>("材质贴图分类选择器");
        window.targetMaterial = material;
        window.RefreshProperties();
        if (material != null)
        {
            window.titleContent = new GUIContent("材质贴图分类选择器 - " + material.name);
        }
    }

    private void OnEnable()
    {
        minSize = new Vector2(480, 320);
    }

    private void OnSelectionChange()
    {
        if (targetMaterial == null)
        {
            RefreshProperties();
            Repaint();
        }
    }

    private void RefreshProperties()
    {
        if (targetMaterial == null)
        {
            cachedShader = null;
            textureProperties.Clear();
            return;
        }
        Shader shader = targetMaterial.shader;
        if (shader == cachedShader)
        {
            return;
        }
        cachedShader = shader;
        textureProperties.Clear();
        if (shader == null)
        {
            return;
        }

        int propertyCount = ShaderUtil.GetPropertyCount(shader);
        for (int i = 0; i < propertyCount; i++)
        {
            if (ShaderUtil.GetPropertyType(shader, i) != ShaderUtil.ShaderPropertyType.TexEnv)
            {
                continue;
            }
            textureProperties.Add(new TexturePropertyInfo
            {
                name = ShaderUtil.GetPropertyName(shader, i),
                displayName = ShaderUtil.GetPropertyDescription(shader, i)
            });
        }
    }

    private void OnGUI()
    {
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField("材质", GUILayout.Width(60));
        Material newMat = (Material)EditorGUILayout.ObjectField(targetMaterial, typeof(Material), false);
        if (newMat != targetMaterial)
        {
            targetMaterial = newMat;
            RefreshProperties();
        }
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.Space(4);

        if (targetMaterial == null)
        {
            EditorGUILayout.HelpBox(
                "请先在场景/项目中选中一个材质，或在上方指定材质。\n" +
                "也可以直接打开分类贴图浏览器浏览资源。",
                MessageType.Info);
            if (GUILayout.Button("打开分类贴图浏览器…", GUILayout.Height(28)))
            {
                VFXResourceSelectorWindow.OpenPicker("Texture", null, null, null);
            }
            return;
        }

        // MaterialProperty 由 Unity 的 SerializedObject 临时拥有，材质检视器刷新后
        // 旧实例可能已经被 Dispose。每帧重新取得一组有效的属性包装对象。
        RefreshProperties();

        EditorGUILayout.LabelField("贴图属性", EditorStyles.boldLabel);
        if (textureProperties.Count == 0)
        {
            EditorGUILayout.HelpBox("该材质没有贴图属性。", MessageType.Info);
            return;
        }

        scroll = EditorGUILayout.BeginScrollView(scroll);
        for (int i = 0; i < textureProperties.Count; i++)
        {
            TexturePropertyInfo prop = textureProperties[i];
            EditorGUILayout.BeginHorizontal();

            EditorGUILayout.LabelField(prop.displayName, GUILayout.Width(150));

            Texture currentTexture = targetMaterial.GetTexture(prop.name);
            Texture tex = (Texture)EditorGUILayout.ObjectField(currentTexture, typeof(Texture), false);
            if (tex != currentTexture)
            {
                Undo.RecordObject(targetMaterial, "Assign Texture");
                targetMaterial.SetTexture(prop.name, tex);
                EditorUtility.SetDirty(targetMaterial);
            }

            if (GUILayout.Button("分类选择…", GUILayout.Width(90)))
            {
                Material material = targetMaterial;
                string propertyName = prop.name;
                currentTexture = material.GetTexture(propertyName);
                Action<Object> apply = selected =>
                {
                    Texture texture = selected as Texture;
                    if (material == null || !material.HasProperty(propertyName)
                        || material.GetTexture(propertyName) == texture)
                    {
                        return;
                    }
                    Undo.RecordObject(material, "Assign Texture");
                    material.SetTexture(propertyName, texture);
                    EditorUtility.SetDirty(material);
                };
                VFXResourceSelectorWindow.OpenPicker("Texture", currentTexture, delegate (Object selected, bool canceled)
                {
                    // 第二个参数是“是否取消”
                    if (canceled)
                    {
                        return;
                    }
                    apply(selected);
                }, apply);
            }

            EditorGUILayout.EndHorizontal();
        }
        EditorGUILayout.EndScrollView();
    }
}
