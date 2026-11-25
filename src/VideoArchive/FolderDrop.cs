using System.IO;
using System.Windows;
using VideoArchive.Core;

namespace VideoArchive;

internal static class FolderDrop
{
    public static bool TryRead(IDataObject data, out string folder, out string error)
    {
        folder = "";
        error = "每次请拖入一个文件夹。";
        try
        {
            if (data.GetData(DataFormats.FileDrop, false) is not string[] { Length: 1 } paths) return false;
            if (!Path.IsPathFullyQualified(paths[0]) || !Directory.Exists(paths[0]))
            {
                error = "请拖入可访问的文件夹。";
                return false;
            }
            folder = Files.Normalize(paths[0]);
            if (new DirectoryInfo(folder).Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                error = "请拖入实际文件夹。";
                return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Runtime.InteropServices.COMException)
        {
            error = "无法访问拖入的文件夹。";
            return false;
        }
    }

    public static DragDropEffects SetEffect(DragEventArgs e, bool accept)
    {
        e.Handled = true;
        return e.Effects = !accept ? DragDropEffects.None
            : (e.AllowedEffects & DragDropEffects.Link) != 0 ? DragDropEffects.Link
            : e.AllowedEffects & DragDropEffects.Copy;
    }
}
