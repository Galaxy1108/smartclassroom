using System.IO.Compression;

namespace SmartClassroom.App;

/// <summary>
/// 应用自更新（**仅 Windows**）。
///
/// Linux 下应用装在 <c>/opt</c> 并由包管理器管理，自己替换文件会和 pacman 的数据库
/// 不一致（下次更新/卸载就是一团乱），所以明确禁用，只提示用包管理器更新。
///
/// Windows 上正在运行的 exe 不能被覆盖，因此走"下载解压到 staging → 生成一个
/// 批处理 → 关掉应用后由它覆盖文件并重启"的常规做法。
/// </summary>
public static class UpdateInstaller
{
    public static bool CanSelfUpdate => OperatingSystem.IsWindows();

    /// <summary>下载 zip 并解压到 <c>&lt;appDir&gt;/update/&lt;version&gt;</c>，返回 staging 目录。</summary>
    public static async Task<string> StageAsync(
        string url, string version, string appDir,
        IProgress<double>? progress = null, CancellationToken cancel = default)
    {
        var safeVersion = string.Concat(version.Where(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_'));
        var root = Path.Combine(appDir, "update");
        var stage = Path.Combine(root, safeVersion.Length > 0 ? safeVersion : "latest");
        var zip = Path.Combine(root, $"download-{safeVersion}.zip");

        Directory.CreateDirectory(root);
        if (Directory.Exists(stage))
            Directory.Delete(stage, recursive: true);

        using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
        using (var res = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancel)
                   .ConfigureAwait(false))
        {
            res.EnsureSuccessStatusCode();
            var total = res.Content.Headers.ContentLength ?? 0;
            await using var src = await res.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
            await using var dst = File.Create(zip);
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await src.ReadAsync(buffer, cancel).ConfigureAwait(false)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read), cancel).ConfigureAwait(false);
                done += read;
                if (total > 0)
                    progress?.Report((double)done / total * 100);
            }
        }

        Directory.CreateDirectory(stage);
        ZipFile.ExtractToDirectory(zip, stage, overwriteFiles: true);
        try { File.Delete(zip); } catch { /* 删不掉不影响 */ }
        return stage;
    }

    /// <summary>
    /// 生成"关掉应用后自动覆盖并重启"的批处理，返回脚本路径。
    /// 脚本逻辑：等 SmartClassroom.App.exe 退出 → 覆盖应用目录 → 重启 → 自删。
    /// </summary>
    public static string WriteScript(string stageDir, string appDir)
    {
        var script = Path.Combine(Path.GetDirectoryName(stageDir) ?? appDir, "apply-update.cmd");
        var exe = Path.Combine(appDir, "SmartClassroom.App.exe");
        var lines = new[]
        {
            "@echo off",
            "chcp 65001 >nul",
            "rem 等应用自己退出（正在运行的 exe 不能被覆盖）",
            ":wait",
            "tasklist /fi \"imagename eq SmartClassroom.App.exe\" | find /i \"SmartClassroom.App.exe\" >nul",
            "if not errorlevel 1 (",
            "  timeout /t 1 /nobreak >nul",
            "  goto wait",
            ")",
            $"xcopy \"{stageDir}\\*\" \"{appDir}\\\" /E /Y /I >nul",
            $"start \"\" \"{exe}\"",
            $"rmdir /s /q \"{stageDir}\"",
            "del \"%~f0\""
        };
        File.WriteAllText(script, string.Join("\r\n", lines) + "\r\n");
        return script;
    }
}
