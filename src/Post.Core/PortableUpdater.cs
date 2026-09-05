using System.Diagnostics;
using System.Text;

namespace Post.Core;

/// <summary>
/// Replacing a portable Post with a newer one.
///
/// A running program cannot overwrite its own folder, so the work is handed to a small
/// script that waits for Post to close first. The script is written fresh each time into
/// the updates folder rather than kept beside the app: it would otherwise be inside the
/// very folder being replaced, so an update would overwrite the thing performing it, and
/// a stale copy from an older Post would be doing the work.
///
/// The folder is swapped rather than copied over. Copying file by file is where these go
/// wrong: one locked file and the folder is half new, half old, and Post no longer
/// starts. Renaming is instant and undoable, so a failure puts the old folder back
/// instead of leaving a wreck.
/// </summary>
public static class PortableUpdater
{
    public static string UpdatesFolder
    {
        get
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Post", "Updates");
            Directory.CreateDirectory(folder);
            return folder;
        }
    }

    /// <summary>
    /// True when the folder Post runs from can actually be replaced. A portable copy
    /// dropped somewhere protected cannot be, and it is better to say so than to close
    /// the app and fail out of sight.
    /// </summary>
    public static bool CanReplaceAppFolder(out string reason)
    {
        var folder = InstallKind.AppFolder;
        var parent = Path.GetDirectoryName(folder);
        if (parent is null) { reason = "Post is running from a drive root, which cannot be replaced this way."; return false; }

        try
        {
            // Writing beside the folder is what the swap needs: it renames within the parent.
            var probe = Path.Combine(parent, $".post-update-probe-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            reason = "";
            return true;
        }
        catch (Exception exception)
        {
            reason = $"Post cannot write to {parent}, so it cannot replace its own folder there. ({exception.GetType().Name})";
            return false;
        }
    }

    /// <summary>
    /// Starts the update and returns; the caller is expected to close Post immediately,
    /// which is what the script is waiting for.
    /// </summary>
    public static void Launch(string zipPath, string versionName)
    {
        var script = Path.Combine(UpdatesFolder, $"update-{Guid.NewGuid():N}.ps1");

        // With a byte order mark, and it matters: Windows PowerShell reads a script without
        // one as ANSI, so anything outside plain ASCII reaches the screen as mojibake.
        File.WriteAllText(script, BuildScript(zipPath, versionName, InstallKind.AppFolder, Environment.ProcessId), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        Process.Start(new ProcessStartInfo("powershell.exe")
        {
            // Bypass applies to this process only, so a locked-down machine is not changed.
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{script}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = UpdatesFolder,   // never inside the folder about to be renamed
        });
    }

    /// <summary>
    /// The folder to replace and the process to wait for are passed in rather than read
    /// here, so the script can be run against a throwaway folder and actually tested. A
    /// script that only ever runs after Post has closed is otherwise unverifiable.
    /// </summary>
    private static string BuildScript(string zipPath, string versionName, string appFolder, int processId)
    {
        var exePath = Path.Combine(appFolder, "Post.exe");

        // Single quotes throughout, so nothing in a path is taken as PowerShell syntax.
        static string Quote(string value) => "'" + value.Replace("'", "''") + "'";

        return $$"""
            $ErrorActionPreference = 'Stop'
            Add-Type -AssemblyName System.Windows.Forms
            Add-Type -AssemblyName System.Drawing

            $postPid  = {{processId}}
            $zip      = {{Quote(zipPath)}}
            $app      = {{Quote(appFolder)}}
            $exe      = {{Quote(exePath)}}
            $newer    = {{Quote(versionName)}}
            $stagingNew = "$app.new"
            $stagingOld = "$app.old"

            # Something on screen while this happens, since Post has just vanished.
            $window = New-Object System.Windows.Forms.Form
            $window.Text = 'Post'
            $window.FormBorderStyle = 'FixedDialog'
            $window.StartPosition = 'CenterScreen'
            $window.ControlBox = $false
            $window.TopMost = $true
            $window.Size = New-Object System.Drawing.Size(440, 150)
            $label = New-Object System.Windows.Forms.Label
            $label.AutoSize = $false
            $label.Dock = 'Fill'
            $label.Padding = New-Object System.Windows.Forms.Padding(18)
            $label.TextAlign = 'MiddleCenter'
            $label.Font = New-Object System.Drawing.Font('Segoe UI', 10)
            $label.Text = "Updating Post to $newer.`r`n`r`nPost will start again when this is done."
            $window.Controls.Add($label)
            $window.Show()
            [System.Windows.Forms.Application]::DoEvents()

            function Say($text) {
                $label.Text = $text
                [System.Windows.Forms.Application]::DoEvents()
            }

            try {
                # Wait for Post to let go of its own folder, but not for ever.
                for ($i = 0; $i -lt 150; $i++) {
                    if (-not (Get-Process -Id $postPid -ErrorAction SilentlyContinue)) { break }
                    Start-Sleep -Milliseconds 200
                    [System.Windows.Forms.Application]::DoEvents()
                }
                if (Get-Process -Id $postPid -ErrorAction SilentlyContinue) {
                    throw 'Post is still running after 30 seconds, so its folder cannot be replaced.'
                }

                Say "Unpacking $newer…"
                if (Test-Path $stagingNew) { Remove-Item $stagingNew -Recurse -Force }
                if (Test-Path $stagingOld) { Remove-Item $stagingOld -Recurse -Force }
                Expand-Archive -Path $zip -DestinationPath $stagingNew -Force

                # Zips of this kind sometimes hold one folder containing everything.
                $source = $stagingNew
                if (-not (Test-Path (Join-Path $source 'Post.exe'))) {
                    $inner = @(Get-ChildItem -LiteralPath $source -Directory)
                    if ($inner.Count -eq 1 -and (Test-Path (Join-Path $inner[0].FullName 'Post.exe'))) {
                        $source = $inner[0].FullName
                    }
                }
                if (-not (Test-Path (Join-Path $source 'Post.exe'))) {
                    throw 'The download does not contain Post.exe, so nothing has been changed.'
                }

                Say 'Replacing files…'
                Rename-Item -LiteralPath $app -NewName ([System.IO.Path]::GetFileName($stagingOld)) -Force
                try {
                    Move-Item -LiteralPath $source -Destination $app -Force
                }
                catch {
                    # Put it back exactly as it was rather than leave nothing behind.
                    Rename-Item -LiteralPath $stagingOld -NewName ([System.IO.Path]::GetFileName($app)) -Force
                    throw
                }

                Say 'Starting Post…'
                Start-Process -FilePath $exe
                Start-Sleep -Milliseconds 600
            }
            catch {
                $window.Hide()
                [System.Windows.Forms.MessageBox]::Show(
                    "Post could not be updated, and has been left as it was.`r`n`r`n$($_.Exception.Message)",
                    'Post', 'OK', 'Error') | Out-Null
                if ((Test-Path $stagingOld) -and -not (Test-Path $app)) {
                    try { Rename-Item -LiteralPath $stagingOld -NewName ([System.IO.Path]::GetFileName($app)) -Force } catch { }
                }
                try { Start-Process -FilePath $exe } catch { }
            }
            finally {
                $window.Close()
                foreach ($leftover in @($stagingOld, $stagingNew, $zip)) {
                    try { if (Test-Path $leftover) { Remove-Item $leftover -Recurse -Force } } catch { }
                }
                try { Remove-Item -LiteralPath $PSCommandPath -Force } catch { }
            }
            """;
    }
}
