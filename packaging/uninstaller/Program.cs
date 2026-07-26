using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;

namespace DeepBotUninstaller;

internal static class Program
{
#if HOST_UNINSTALLER
    internal const string Mode = "Host";
#elif CLIENT_UNINSTALLER
    internal const string Mode = "Client";
#else
#error UninstallerMode must be Host or Client.
#endif

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--silent", StringComparer.OrdinalIgnoreCase))
        {
            var pathIndex = Array.FindIndex(args, value =>
                string.Equals(value, "--path", StringComparison.OrdinalIgnoreCase));
            if (pathIndex < 0 || pathIndex + 1 >= args.Length) return 2;
            try
            {
                var candidates = UninstallerForm.FindGameDirectories(args[pathIndex + 1]);
                if (candidates.Count == 0) return 3;
                var removeKey = args.Contains("--remove-key", StringComparer.OrdinalIgnoreCase);
                var result = UninstallerForm.Uninstall(candidates[0], removeKey);
                return result.Skipped == 0 ? 0 : 4;
            }
            catch (Exception ex)
            {
                try
                {
                    File.WriteAllText(
                        Path.Combine(Path.GetTempPath(), $"AmongUs-DeepBot-{Mode}-Uninstaller-error.log"),
                        ex.ToString());
                }
                catch { }
                return 1;
            }
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new UninstallerForm());
        return 0;
    }
}

internal sealed class UninstallerForm : Form
{
    private readonly TextBox _path = new() { Dock = DockStyle.Fill };
    private readonly Label _target = new() { AutoSize = true, ForeColor = Color.DimGray };
    private readonly Label _status = new() { AutoSize = true, ForeColor = Color.DimGray };
    private readonly Button _uninstall = new() { Text = "Uninstall", AutoSize = true };
    private readonly CheckBox _removeKey = new()
    {
        Text = "Also delete this Windows user's locally stored API key",
        Checked = false,
        AutoSize = true,
        Visible = Program.Mode == "Host"
    };

    internal UninstallerForm()
    {
        Text = $"Among Us DeepBot {Program.Mode} Uninstaller";
        Width = 720;
        Height = 300;
        MinimumSize = new Size(620, 280);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 10f);

        var browse = new Button { Text = "Browse...", AutoSize = true };
        browse.Click += (_, _) => Browse();
        _path.TextChanged += (_, _) => RefreshTarget();
        _uninstall.Click += async (_, _) => await UninstallAsync();

        var pathRow = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2 };
        pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        pathRow.Controls.Add(_path, 0, 0);
        pathRow.Controls.Add(browse, 1, 0);

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft
        };
        actions.Controls.Add(_uninstall);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(24),
            RowCount = 8,
            ColumnCount = 1
        };
        for (var i = 0; i < layout.RowCount; i++) layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label
        {
            Text = $"DeepBot {Program.Mode} removal",
            Font = new Font("Segoe UI Semibold", 16f),
            AutoSize = true
        });
        layout.Controls.Add(new Label
        {
            Text = "Select a Steam root or the exact Among Us folder. Files recorded by the installer are restored or removed only when their hashes still match; modified files are preserved.",
            AutoSize = true,
            MaximumSize = new Size(650, 0)
        });
        layout.Controls.Add(new Label { Text = "Steam folder or Among Us folder:", AutoSize = true, Padding = new Padding(0, 12, 0, 0) });
        layout.Controls.Add(pathRow);
        layout.Controls.Add(_target);
        layout.Controls.Add(_removeKey);
        layout.Controls.Add(_status);
        layout.Controls.Add(actions);
        Controls.Add(layout);

        _path.Text = FindDefaultSteamPath() ?? string.Empty;
        RefreshTarget();
    }

    private void Browse()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Select your Steam folder or the folder containing Among Us.exe",
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(_path.Text) ? _path.Text : string.Empty,
            ShowNewFolderButton = false
        };
        if (dialog.ShowDialog(this) == DialogResult.OK) _path.Text = dialog.SelectedPath;
    }

    private void RefreshTarget()
    {
        var candidates = FindGameDirectories(_path.Text);
        _target.Text = candidates.Count switch
        {
            0 => "Among Us.exe was not found under this location.",
            1 => $"Removal target: {candidates[0]}",
            _ => $"Multiple installations found. The preferred target is: {candidates[0]}"
        };
        _target.ForeColor = candidates.Count == 0 ? Color.Firebrick : Color.SeaGreen;
        _uninstall.Enabled = candidates.Count > 0;
    }

    private async Task UninstallAsync()
    {
        var candidates = FindGameDirectories(_path.Text);
        if (candidates.Count == 0)
        {
            MessageBox.Show(this, "Among Us.exe was not found.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var gameDirectory = candidates[0];
        var answer = MessageBox.Show(
            this,
            $"Remove DeepBot {Program.Mode} from this location?\n\n{gameDirectory}\n\nModified files will be kept.",
            Text,
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);
        if (answer != DialogResult.Yes) return;

        _uninstall.Enabled = false;
        _status.Text = "Removing...";
        UseWaitCursor = true;
        try
        {
            var result = await Task.Run(() => Uninstall(gameDirectory, _removeKey.Checked));
            _status.Text = result.Skipped == 0
                ? "Removal completed."
                : $"Removal completed with {result.Skipped} modified file(s) preserved.";
            MessageBox.Show(
                this,
                $"Restored: {result.Restored}\nRemoved: {result.Removed}\nPreserved because modified: {result.Skipped}",
                Text,
                MessageBoxButtons.OK,
                result.Skipped == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }
        catch (UnauthorizedAccessException ex)
        {
            _status.Text = "Removal failed: access denied.";
            MessageBox.Show(this, $"Run the uninstaller as administrator and try again.\n\n{ex.Message}", Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch (Exception ex)
        {
            _status.Text = "Removal failed.";
            MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            UseWaitCursor = false;
            _uninstall.Enabled = true;
        }
    }

    internal static UninstallResult Uninstall(string gameDirectory, bool removeKey)
    {
        var root = Path.GetFullPath(gameDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var receiptPath = Path.Combine(root, "DeepBot Installer Records", $"DeepBot-{Program.Mode}-Install.json");
        var receipt = LoadReceipt(receiptPath);
        var restored = 0;
        var removed = 0;
        var skipped = 0;

        if (receipt is not null && string.Equals(receipt.Mode, Program.Mode, StringComparison.OrdinalIgnoreCase))
        {
            var remaining = new List<InstalledFileReceipt>();
            foreach (var entry in receipt.Files.AsEnumerable().Reverse())
            {
                var destination = ResolveInsideRoot(root, entry.RelativePath);
                if (!File.Exists(destination)) continue;
                if (!string.Equals(ComputeSha256(destination), entry.InstalledSha256, StringComparison.OrdinalIgnoreCase))
                {
                    skipped++;
                    remaining.Add(entry);
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(entry.OriginalBackupRelativePath))
                {
                    var backup = ResolveInsideRoot(root, entry.OriginalBackupRelativePath!);
                    if (!File.Exists(backup) ||
                        !string.IsNullOrWhiteSpace(entry.OriginalBackupSha256) &&
                        !string.Equals(ComputeSha256(backup), entry.OriginalBackupSha256, StringComparison.OrdinalIgnoreCase))
                    {
                        skipped++;
                        remaining.Add(entry);
                        continue;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Copy(backup, destination, overwrite: true);
                    restored++;
                }
                else
                {
                    File.Delete(destination);
                    removed++;
                    PruneEmptyParents(Path.GetDirectoryName(destination), root);
                }
            }

            if (remaining.Count == 0)
            {
                File.Delete(receiptPath);
            }
            else
            {
                File.WriteAllText(
                    receiptPath,
                    JsonSerializer.Serialize(
                        receipt with { Files = remaining },
                        new JsonSerializerOptions { WriteIndented = true }));
            }
        }
        else
        {
            // Safe legacy fallback: remove only DeepBot-owned files. Shared TOR
            // and BepInEx runtime files are deliberately preserved without a
            // trustworthy installation receipt.
            foreach (var relative in LegacyDeepBotFiles())
            {
                var path = ResolveInsideRoot(root, relative);
                if (!File.Exists(path)) continue;
                File.Delete(path);
                removed++;
            }
        }

        var shortcut = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            $"Among Us DeepBot {Program.Mode}.lnk");
        if (File.Exists(shortcut)) File.Delete(shortcut);

        if (Program.Mode == "Host" && removeKey)
        {
            var keyPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AmongUsDeepSeekBots",
                "api-key.txt");
            if (File.Exists(keyPath)) File.Delete(keyPath);
        }

        File.AppendAllLines(
            Path.Combine(root, "DeepBot-Installer.log"),
            new[] { $"{DateTime.Now:O} mode={Program.Mode} uninstall restored={restored} removed={removed} skipped={skipped}" });
        return new UninstallResult(restored, removed, skipped);
    }

    private static InstallReceipt? LoadReceipt(string path)
    {
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<InstallReceipt>(File.ReadAllText(path)); }
        catch { return null; }
    }

    private static string ResolveInsideRoot(string root, string relative)
    {
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Unsafe receipt path: {relative}");
        }
        return path;
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void PruneEmptyParents(string? directory, string root)
    {
        while (!string.IsNullOrWhiteSpace(directory) &&
               directory.StartsWith(root, StringComparison.OrdinalIgnoreCase) &&
               !string.Equals(directory.TrimEnd(Path.DirectorySeparatorChar), root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            if (Directory.EnumerateFileSystemEntries(directory).Any()) return;
            Directory.Delete(directory);
            directory = Path.GetDirectoryName(directory);
        }
    }

    private static IEnumerable<string> LegacyDeepBotFiles()
    {
        yield return Path.Combine("BepInEx", "plugins", "AmongUsDeepSeekBots.dll");
        yield return Path.Combine("BepInEx", "config", "local.amongus.deepseekbots.cfg");
        yield return "README-DeepBot.txt";
        yield return "Start-DeepBot.ps1";
        yield return "Start-DeepBot.cmd";
#if HOST_UNINSTALLER
        yield return "Configure-DeepBot-Key.ps1";
        yield return "Configure-DeepBot-Key.cmd";
#endif
    }

    internal static List<string> FindGameDirectories(string input)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(input)) return result;
        string root;
        try { root = Path.GetFullPath(input.Trim().Trim('"')); }
        catch { return result; }
        if (!Directory.Exists(root)) return result;

        AddIfGame(root, result);
        var common = Directory.Exists(Path.Combine(root, "steamapps", "common"))
            ? Path.Combine(root, "steamapps", "common")
            : root.EndsWith(Path.Combine("steamapps", "common"), StringComparison.OrdinalIgnoreCase)
                ? root
                : null;
        if (common is not null)
        {
            foreach (var directory in EnumerateDirectories(common, 3)) AddIfGame(directory, result);
        }

        return result
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => string.Equals(Path.GetFileName(path), "Among Us", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(path => path.Length)
            .ToList();
    }

    private static IEnumerable<string> EnumerateDirectories(string root, int maxDepth)
    {
        var queue = new Queue<(string Path, int Depth)>();
        queue.Enqueue((root, 0));
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (current.Depth >= maxDepth) continue;
            IEnumerable<string> children;
            try { children = Directory.EnumerateDirectories(current.Path); }
            catch { continue; }
            foreach (var child in children)
            {
                yield return child;
                queue.Enqueue((child, current.Depth + 1));
            }
        }
    }

    private static void AddIfGame(string directory, ICollection<string> result)
    {
        if (File.Exists(Path.Combine(directory, "Among Us.exe"))) result.Add(Path.GetFullPath(directory));
    }

    private static string? FindDefaultSteamPath()
    {
        try
        {
            var value = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
            if (!string.IsNullOrWhiteSpace(value) && Directory.Exists(value)) return value;
        }
        catch { }
        var standard = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");
        return Directory.Exists(standard) ? standard : null;
    }
}

internal sealed record InstallReceipt(
    string Mode,
    DateTimeOffset InstalledAt,
    string BackupDirectory,
    List<InstalledFileReceipt> Files);

internal sealed record InstalledFileReceipt(
    string RelativePath,
    string InstalledSha256,
    string? OriginalBackupRelativePath,
    string? OriginalBackupSha256);

internal sealed record UninstallResult(int Restored, int Removed, int Skipped);
