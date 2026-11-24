using UnityEditor;
using System;
using UnityEngine;
using Object = UnityEngine.Object;

/// <summary>
/// 材质属性绘制器（可选功能）：
/// 在 shader 的贴图属性前添加 [TextureCategoryPicker] 标记后，
/// 该贴图槽位右侧会出现一个 “☰” 按钮，点击可直接打开 VFX 资源分类选择器并赋值。
///
/// 用法（shader 内）：
///     [TextureCategoryPicker] _MainTex ("Main Tex", 2D) = "white" {}
///
/// 注意：该绘制器为简化版贴图字段（不含 Tiling/Offset 展开），如需展开编辑请使用默认贴图槽位。
/// </summary>
public class TextureCategoryPickerDrawer : MaterialPropertyDrawer
{
    private const float ButtonWidth = 22f;
    private const float Gap = 4f;

    public override float GetPropertyHeight(MaterialProperty prop, string label, MaterialEditor editor)
    {
        return EditorGUIUtility.singleLineHeight;
    }

    public override void OnGUI(Rect position, MaterialProperty prop, GUIContent label, MaterialEditor editor)
    {
        Rect fieldRect = new Rect(position.x, position.y, position.width - ButtonWidth - Gap, position.height);
        Rect buttonRect = new Rect(position.x + position.width - ButtonWidth, position.y, ButtonWidth, position.height);

        EditorGUI.BeginChangeCheck();
        Texture tex = (Texture)EditorGUI.ObjectField(fieldRect, label, prop.textureValue, typeof(Texture), false);
        if (EditorGUI.EndChangeCheck())
        {
            foreach (Object t in editor.targets)
            {
                Undo.RecordObject(t, "Assign Texture");
            }
            prop.textureValue = tex;
        }

        if (GUI.Button(buttonRect, new GUIContent("☰", "打开分类贴图选择器")))
        {
            string propertyName = prop.name;
            Texture currentTexture = prop.textureValue;
            Object[] targetObjects = editor.targets;
            Action<Object> apply = selected =>
            {
                Texture texture = selected as Texture;
                foreach (Object o in targetObjects)
                {
                    Material material = o as Material;
                    if (material == null || !material.HasProperty(propertyName)
                        || material.GetTexture(propertyName) == texture)
                    {
                        continue;
                    }
                    Undo.RecordObject(material, "Assign Texture");
                    material.SetTexture(propertyName, texture);
                    EditorUtility.SetDirty(material);
                }
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
    }
}
