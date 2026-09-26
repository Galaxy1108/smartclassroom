using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace SmartClassroom.Core.AI;

/// <summary>可下载的 Node 版本条目。</summary>
public sealed record NodeRelease(string Version, string LtsName);

/// <summary>
/// Node 运行时下载器（对齐 SnowLuma 下载器的做法）：
/// 从 nodejs.org/dist 拉 LTS 列表 → 下载对应平台压缩包 → 解压到用户数据目录。
/// 安装位置：&lt;LocalAppData&gt;/SmartClassroom/node（避开只读的 /opt 与 Program Files）。
/// </summary>
public sealed class NodeManager(HttpClient? http = null)
{
    public const string IndexUrl = "https://nodejs.org/dist/index.json";

    private readonly HttpClient _http = http ?? new HttpClient();

    /// <summary>安装根目录：解压后的 node 可执行文件最终位于其下。</summary>
    public static string InstallRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SmartClassroom", "node");

    public static string Rid()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return "win-x64";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "osx-arm64" : "osx-x64";
        return RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "linux-arm64" : "linux-x64";
    }

    public static string ArchiveName(string version, string rid)
    {
        var ext = rid.StartsWith("win") ? "zip" : "tar.gz";
        return $"node-{version}-{rid}.{ext}";
    }

    public static string DownloadUrl(string version, string rid)
        => $"https://nodejs.org/dist/{version}/{ArchiveName(version, rid)}";

    /// <summary>拉取 LTS 版本列表（新的在前）。</summary>
    public async Task<IReadOnlyList<NodeRelease>> ListLtsAsync(CancellationToken cancel = default)
    {
        using var res = await _http.GetAsync(IndexUrl, cancel).ConfigureAwait(false);
        res.EnsureSuccessStatusCode();
        var json = await res.Content.ReadAsStringAsync(cancel).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var list = new List<NodeRelease>();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            if (!item.TryGetProperty("lts", out var lts) || lts.ValueKind != JsonValueKind.String)
                continue;
            var version = item.TryGetProperty("version", out var v) ? v.GetString() ?? "" : "";
            if (version.Length > 0)
                list.Add(new NodeRelease(version, lts.GetString() ?? ""));
        }
        return list;
    }

    public async Task DownloadAsync(string url, string destFile, IProgress<double>? progress, CancellationToken cancel)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);
        using var res = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false);
        res.EnsureSuccessStatusCode();
        var total = res.Content.Headers.ContentLength ?? -1L;
        await using var src = await res.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
        await using var dst = File.Create(destFile);
        var buf = new byte[81920];
        long done = 0;
        int n;
        while ((n = await src.ReadAsync(buf, cancel).ConfigureAwait(false)) > 0)
        {
            await dst.WriteAsync(buf.AsMemory(0, n), cancel).ConfigureAwait(false);
            done += n;
            if (total > 0)
                progress?.Report((double)done / total);
        }
    }

    /// <summary>
    /// 解压并把压缩包里的根目录内容（node-vX-rid/）平铺到 <paramref name="installRoot"/>，
    /// 使可执行文件落在 &lt;installRoot&gt;/bin/node（Windows 为 &lt;installRoot&gt;/node.exe）。
    /// </summary>
    public static string ExtractFlattened(string archive, string installRoot)
    {
        var temp = installRoot + ".tmp";
        if (Directory.Exists(temp))
            Directory.Delete(temp, true);
        Directory.CreateDirectory(temp);

        if (archive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            ZipFile.ExtractToDirectory(archive, temp, overwriteFiles: true);
        else
        {
            using var fs = File.OpenRead(archive);
            using var gz = new GZipStream(fs, CompressionMode.Decompress);
            TarFile.ExtractToDirectory(gz, temp, overwriteFiles: true);
        }

        var inner = FlattenSource(temp);
        if (Directory.Exists(installRoot))
            Directory.Delete(installRoot, true);
        Directory.CreateDirectory(installRoot);
        foreach (var entry in Directory.EnumerateFileSystemEntries(inner))
        {
            var target = Path.Combine(installRoot, Path.GetFileName(entry));
            if (Directory.Exists(entry))
                Directory.Move(entry, target);
            else
                File.Copy(entry, target, overwrite: true);
        }
        Directory.Delete(temp, true);
        return installRoot;
    }

    /// <summary>找到压缩包解出的内容根：有 bin/node 或 node.exe 的那层。</summary>
    public static string FlattenSource(string root)
    {
        if (HasNodeExecutable(root))
            return root;
        foreach (var dir in Directory.EnumerateDirectories(root))
            if (HasNodeExecutable(dir))
                return dir;
        // 再深一层兜底
        foreach (var dir in Directory.EnumerateDirectories(root))
            foreach (var sub in Directory.EnumerateDirectories(dir))
                if (HasNodeExecutable(sub))
                    return sub;
        return root;
    }

    private static bool HasNodeExecutable(string dir)
        => File.Exists(Path.Combine(dir, "node.exe")) || File.Exists(Path.Combine(dir, "bin", "node"));
}
