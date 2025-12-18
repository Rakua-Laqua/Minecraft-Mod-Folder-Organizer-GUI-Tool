using System.IO;
using Microsoft.VisualBasic.FileIO;
using OrganizerTool.Models;

namespace OrganizerTool.Infrastructure;

public sealed class FileSystem
{
    public void EnsureDirectory(string path)
    {
        Directory.CreateDirectory(path);
    }

    public void MoveWithOverwrite(string sourcePath, string destinationPath, DeleteMode deleteMode)
    {
        if (File.Exists(destinationPath))
        {
            DeletePath(destinationPath, deleteMode);
        }
        else if (Directory.Exists(destinationPath))
        {
            DeletePath(destinationPath, deleteMode);
        }

        var destinationParent = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrWhiteSpace(destinationParent))
        {
            Directory.CreateDirectory(destinationParent);
        }

        if (File.Exists(sourcePath))
        {
            File.Move(sourcePath, destinationPath);
            return;
        }

        if (Directory.Exists(sourcePath))
        {
            Directory.Move(sourcePath, destinationPath);
            return;
        }

        throw new FileNotFoundException("Source path not found.", sourcePath);
    }

    public void DeletePath(string path, DeleteMode deleteMode)
    {
        if (File.Exists(path))
        {
            DeleteFile(path, deleteMode);
            return;
        }

        if (Directory.Exists(path))
        {
            DeleteDirectory(path, deleteMode);
            return;
        }

        // 既に無い場合は何もしない
    }

    private static void DeleteFile(string filePath, DeleteMode deleteMode)
    {
        if (deleteMode == DeleteMode.RecycleBin)
        {
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(filePath, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            return;
        }

        File.Delete(filePath);
    }

    private static void DeleteDirectory(string directoryPath, DeleteMode deleteMode)
    {
        if (deleteMode == DeleteMode.RecycleBin)
        {
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(directoryPath, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            return;
        }

        Directory.Delete(directoryPath, recursive: true);
    }
}
