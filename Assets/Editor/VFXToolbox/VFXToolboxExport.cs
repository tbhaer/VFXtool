using System;
using System.IO;
using UnityEditor;
using UnityEngine;

public partial class VFXToolboxWindow
{
    private enum VFXExportMode
    {
        SaveBesideSourceUnique = 0,
        Overwrite = 1,
        ChooseLocation = 2
    }

    [SerializeField] private VFXExportMode vfxExportMode = VFXExportMode.ChooseLocation;

    private static readonly string[] VFXExportModeNames =
    {
        "原址另存（自动编号）", "原址覆盖", "自选位置（原方式）"
    };

    private void DrawVFXExportMode()
    {
        vfxExportMode = (VFXExportMode)EditorGUILayout.Popup(
            new GUIContent("导出方式", "原址指源资源所在目录；另存自动递增编号。改色/渐变映射的覆盖模式会覆盖源 PNG，生成类工具会覆盖同名输出；没有源资源时使用 Assets 目录。"),
            (int)vfxExportMode, VFXExportModeNames);
    }

    private string ResolveVFXExportPath(
        string title,
        string fileName,
        string extension,
        string sourceAssetPath,
        bool overwriteSource)
    {
        extension = (extension ?? string.Empty).TrimStart('.');
        if (string.IsNullOrEmpty(extension)) extension = "png";
        fileName = Path.GetFileNameWithoutExtension(string.IsNullOrEmpty(fileName) ? "Export" : fileName.Trim());
        if (string.IsNullOrEmpty(fileName)) fileName = "Export";

        string sourceAbsolute = GetVFXAbsoluteAssetPath(sourceAssetPath);
        string sourceDirectory = !string.IsNullOrEmpty(sourceAbsolute) && File.Exists(sourceAbsolute)
            ? Path.GetDirectoryName(sourceAbsolute)
            : !string.IsNullOrEmpty(sourceAbsolute) && Directory.Exists(sourceAbsolute)
                ? sourceAbsolute
                : GetVFXAbsoluteAssetPath("Assets");

        if (vfxExportMode == VFXExportMode.ChooseLocation)
        {
            return EditorUtility.SaveFilePanel(title, sourceDirectory, fileName, extension);
        }

        if (vfxExportMode == VFXExportMode.Overwrite)
        {
            string destination = sourceAbsolute;
            if (!overwriteSource || string.IsNullOrEmpty(destination))
            {
                destination = Path.Combine(sourceDirectory, fileName + "." + extension);
            }
            if (overwriteSource && !string.Equals(Path.GetExtension(destination), "." + extension, StringComparison.OrdinalIgnoreCase))
            {
                SetStatus("无法覆盖源文件：当前导出格式为 ." + extension + "，源文件格式为 " + Path.GetExtension(destination) + "。请使用另存或自选位置。");
                return null;
            }
            if (File.Exists(destination) && !EditorUtility.DisplayDialog(
                "确认覆盖文件", "将覆盖以下文件：\n" + destination, "覆盖", "取消"))
            {
                SetStatus("已取消导出。");
                return null;
            }
            return destination;
        }

        string candidate;
        int sequence = 1;
        do
        {
            candidate = Path.Combine(sourceDirectory, fileName + "_" + sequence.ToString("000") + "." + extension);
            sequence++;
        }
        while (File.Exists(candidate));
        return candidate;
    }

    private string ResolveVFXBatchFolder(string currentFolder, string sourceAssetPath, ref string baseName, string[] outputNames)
    {
        string sourceAbsolute = GetVFXAbsoluteAssetPath(sourceAssetPath);
        string sourceDirectory = !string.IsNullOrEmpty(sourceAbsolute) && File.Exists(sourceAbsolute)
            ? Path.GetDirectoryName(sourceAssetPath).Replace('\\', '/')
            : "Assets";

        if (vfxExportMode == VFXExportMode.ChooseLocation)
        {
            return currentFolder;
        }

        if (vfxExportMode == VFXExportMode.Overwrite)
        {
            return sourceDirectory;
        }

        int sequence = 1;
        string uniqueBase;
        bool collision;
        do
        {
            uniqueBase = baseName + "_" + sequence.ToString("000");
            collision = false;
            foreach (string outputName in outputNames)
            {
                string fileName = outputName.Replace("{name}", uniqueBase);
                if (File.Exists(GetVFXAbsoluteAssetPath(Path.Combine(sourceDirectory, fileName))))
                {
                    collision = true;
                    break;
                }
            }
            sequence++;
        }
        while (collision);

        baseName = uniqueBase;
        return sourceDirectory;
    }

    private static string GetVFXAbsoluteAssetPath(string assetPath)
    {
        if (string.IsNullOrEmpty(assetPath)) return null;
        if (Path.IsPathRooted(assetPath)) return assetPath;
        string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        return Path.GetFullPath(Path.Combine(projectRoot, assetPath));
    }
}
