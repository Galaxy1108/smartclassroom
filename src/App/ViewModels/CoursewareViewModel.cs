using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using Avalonia.Media.Imaging;

namespace SmartClassroom.App.ViewModels;

public sealed record CoursewareItem
{
    public required string FileName { get; init; }
    public required string LocalPath { get; init; }
    public required string SizeText { get; init; }
    public required bool IsImage { get; init; }
    public required string TypeLabel { get; init; }
    public Bitmap? Thumbnail { get; init; }
}

/// <summary>课件弹窗视图模型：缩略图加载 + 双击打开。</summary>
public sealed class CoursewareViewModel : ViewModelBase
{
    private static readonly string[] ImageExts = [".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp"];

    public ObservableCollection<CoursewareItem> Items { get; } = new();

    public string Title => "您可能需要的课件";

    public bool IsEmpty => Items.Count == 0;

    public string EmptyHint => "今天还没有归档到课件。老师往群里发文件后，"
                             + "开启「群文件自动归档」即可在这里看到。";

    public static CoursewareViewModel FromFiles(IEnumerable<(string FileName, string LocalPath, long Size)> files)
    {
        var vm = new CoursewareViewModel();
        foreach (var (name, path, size) in files)
            vm.Items.Add(CreateItem(name, path, size));
        return vm;
    }

    internal static CoursewareItem CreateItem(string fileName, string localPath, long size)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        var isImage = ImageExts.Contains(ext) && File.Exists(localPath);
        Bitmap? thumb = null;
        if (isImage)
        {
            try
            {
                using var fs = File.OpenRead(localPath);
                thumb = Bitmap.DecodeToWidth(fs, 160);
            }
            catch { isImage = false; }
        }
        return new CoursewareItem
        {
            FileName = fileName,
            LocalPath = localPath,
            SizeText = FormatSize(size),
            IsImage = isImage,
            TypeLabel = ext.TrimStart('.').ToUpperInvariant(),
            Thumbnail = thumb
        };
    }

    public static void Open(string localPath)
    {
        Process.Start(new ProcessStartInfo(localPath) { UseShellExecute = true });
    }

    internal static string FormatSize(long bytes)
        => bytes switch
        {
            < 1024 => $"{bytes} B",
            < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
            < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:F1} MB",
            _ => $"{bytes / 1024.0 / 1024 / 1024:F1} GB"
        };
}
