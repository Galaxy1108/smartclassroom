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

    // ================= 重启即更新 =================

    private static string PendingFile(string appDir) => Path.Combine(appDir, "update", "pending.json");

    /// <summary>记录"下次启动时完成更新"（用户点「重启以更新」前调用）。</summary>
    public static void MarkPending(string appDir, string stageDir, string version)
    {
        var file = PendingFile(appDir);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, System.Text.Json.JsonSerializer.Serialize(new PendingUpdate(stageDir, version)));
    }

    /// <summary>读出待完成的更新（没有则返回 null）。</summary>
    public static PendingUpdate? ReadPending(string appDir)
    {
        try
        {
            var file = PendingFile(appDir);
            if (!File.Exists(file))
                return null;
            return System.Text.Json.JsonSerializer.Deserialize<PendingUpdate>(File.ReadAllText(file));
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// 启动时把 staging 里的新版本覆盖到应用目录（在"正在更新"窗口里后台跑）。
    /// 返回成功覆盖的文件数；被占用（正在运行的 exe 等）的文件跳过，由重启脚本处理。
    /// </summary>
    public static (int Copied, int Skipped) ApplyPending(string appDir, IProgress<double>? progress = null)
    {
        var pending = ReadPending(appDir);
        if (pending is null || !Directory.Exists(pending.StageDir))
            return (0, 0);

        var files = Directory.GetFiles(pending.StageDir, "*", SearchOption.AllDirectories);
        var copied = 0;
        var skipped = 0;
        for (var i = 0; i < files.Length; i++)
        {
            var rel = Path.GetRelativePath(pending.StageDir, files[i]);
            var dest = Path.Combine(appDir, rel);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(files[i], dest, overwrite: true);
                copied++;
            }
            catch (IOException)
            {
                skipped++;   // 正在使用中（例如当前 exe），交给重启脚本
            }
            catch (UnauthorizedAccessException)
            {
                skipped++;
            }
            progress?.Report((double)(i + 1) / files.Length * 100);
        }

        try { File.Delete(PendingFile(appDir)); } catch { /* 删不掉不影响 */ }
        return (copied, skipped);
    }

    /// <summary>生成脚本、启动它，然后让调用方退出应用（脚本会等我们退出后覆盖并重启）。</summary>
    public static bool LaunchScriptAndExit(string appDir)
    {
        var pending = ReadPending(appDir);
        var stage = pending?.StageDir;
        if (stage is null || !Directory.Exists(stage))
            return false;
        var script = WriteScript(stage, appDir);
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(script)
            {
                UseShellExecute = true,
                CreateNoWindow = true,
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
            });
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // ================= Linux：交给包管理器 =================

    /// <summary>Linux 能否在应用内安装（下载 pacman 包 + 输一次系统密码）。</summary>
    public static bool CanInstallPackage =>
        OperatingSystem.IsLinux() && File.Exists("/usr/bin/pacman");

    /// <summary>下载安装包到临时目录，返回文件路径。</summary>
    public static async Task<string> DownloadPackageAsync(string url, string version,
        IProgress<double>? progress = null, CancellationToken cancel = default)
    {
        var dir = Path.Combine(Path.GetTempPath(), "smartclassroom-update");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, $"smartclassroom-{version}-x86_64.pkg.tar.zst");
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        using var res = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancel)
            .ConfigureAwait(false);
        res.EnsureSuccessStatusCode();
        var total = res.Content.Headers.ContentLength ?? 0;
        await using var src = await res.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
        await using var dst = File.Create(file);
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
        return file;
    }

    /// <summary>
    /// 用 pacman 安装下载好的包。密码通过 stdin 交给 sudo（不进命令行，避免出现在进程列表里）。
    /// 返回 (是否成功, 输出)。
    /// </summary>
    public static async Task<(bool Ok, string Output)> InstallPackageAsync(string pkgPath, string password,
        CancellationToken cancel = default)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("sudo")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            psi.ArgumentList.Add("-S");
            psi.ArgumentList.Add("-p");
            psi.ArgumentList.Add("");
            psi.ArgumentList.Add("pacman");
            psi.ArgumentList.Add("-U");
            psi.ArgumentList.Add("--noconfirm");
            psi.ArgumentList.Add(pkgPath);

            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null)
                return (false, "无法启动 sudo");
            await p.StandardInput.WriteLineAsync(password).ConfigureAwait(false);
            await p.StandardInput.FlushAsync(cancel).ConfigureAwait(false);
            p.StandardInput.Close();
            var output = await p.StandardOutput.ReadToEndAsync(cancel).ConfigureAwait(false);
            var error = await p.StandardError.ReadToEndAsync(cancel).ConfigureAwait(false);
            await p.WaitForExitAsync(cancel).ConfigureAwait(false);
            return (p.ExitCode == 0, (output + "\n" + error).Trim());
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}

/// <summary>待完成的更新：staging 目录 + 目标版本。</summary>
public sealed record PendingUpdate(string StageDir, string Version);
