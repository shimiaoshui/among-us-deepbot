using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;

namespace DeepBotInstaller;

internal static class Program
{
#if HOST_INSTALLER
    internal const string Mode = "Host";
#elif CLIENT_INSTALLER
    internal const string Mode = "Client";
#else
#error InstallerMode must be Host or Client.
#endif

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--silent", StringComparer.OrdinalIgnoreCase))
        {
            var pathIndex = Array.FindIndex(args, value =>
                string.Equals(value, "--path", StringComparison.OrdinalIgnoreCase));
            if (pathIndex < 0 || pathIndex + 1 >= args.Length)
            {
                return 2;
            }

            try
            {
                var candidates = InstallerForm.FindGameDirectories(args[pathIndex + 1]);
                if (candidates.Count == 0) return 3;
                InstallerForm.InstallPayload(candidates[0], createShortcut: false);
                return 0;
            }
            catch (Exception ex)
            {
                try
                {
                    File.WriteAllText(
                        Path.Combine(Path.GetTempPath(), $"AmongUs-DeepBot-{Mode}-Installer-error.log"),
                        ex.ToString());
                }
                catch
                {
                    // Preserve the original installer failure exit code even if diagnostics cannot be written.
                }
                return 1;
            }
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new InstallerForm());
        return 0;
    }
}

internal sealed class InstallerForm : Form
{
    private readonly TextBox _path = new() { Dock = DockStyle.Fill };
    private readonly Label _target = new() { AutoSize = true, ForeColor = Color.DimGray };
    private readonly Label _status = new() { AutoSize = true, ForeColor = Color.DimGray };
    private readonly Button _install = new() { Text = "Install", AutoSize = true };
    private readonly CheckBox _shortcut = new() { Text = "Create a desktop shortcut", Checked = true, AutoSize = true };
    private readonly CheckBox _launch = new() { Text = "Launch Among Us after installation", Checked = false, AutoSize = true };

    internal InstallerForm()
    {
        Text = $"Among Us DeepBot {Program.Mode} Installer";
        Width = 720;
        Height = 320;
        MinimumSize = new Size(620, 300);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 10f);

        var title = new Label
        {
            Text = $"DeepBot {Program.Mode} installation",
            Font = new Font("Segoe UI Semibold", 16f),
            AutoSize = true
        };
        var explanation = new Label
        {
            Text = Program.Mode == "Host"
                ? "Installs TOR 4.6.0 and the host-authoritative DeepBot controller. The API key is not included."
                : "Installs TOR 4.6.0 and the passive LAN client. This client never creates or controls bots.",
            AutoSize = true,
            MaximumSize = new Size(650, 0)
        };
        var browse = new Button { Text = "Browse...", AutoSize = true };
        browse.Click += (_, _) => Browse();
        _path.TextChanged += (_, _) => RefreshTarget();
        _install.Click += async (_, _) => await InstallAsync();

        var pathRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 2
        };
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
        actions.Controls.Add(_install);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(24),
            RowCount = 9,
            ColumnCount = 1
        };
        layout.RowStyles.Clear();
        for (var i = 0; i < layout.RowCount; i++)
        {
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        }
        layout.Controls.Add(title);
        layout.Controls.Add(explanation);
        layout.Controls.Add(new Label { Text = "Steam folder or Among Us folder:", AutoSize = true, Padding = new Padding(0, 12, 0, 0) });
        layout.Controls.Add(pathRow);
        layout.Controls.Add(_target);
        layout.Controls.Add(_shortcut);
        layout.Controls.Add(_launch);
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
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _path.Text = dialog.SelectedPath;
        }
    }

    private void RefreshTarget()
    {
        var candidates = FindGameDirectories(_path.Text);
        _target.Text = candidates.Count switch
        {
            0 => "Among Us.exe was not found under this location.",
            1 => $"Install target: {candidates[0]}",
            _ => $"Multiple installations found. The preferred target is: {candidates[0]}"
        };
        _target.ForeColor = candidates.Count == 0 ? Color.Firebrick : Color.SeaGreen;
        _install.Enabled = candidates.Count > 0;
    }

    private async Task InstallAsync()
    {
        var candidates = FindGameDirectories(_path.Text);
        if (candidates.Count == 0)
        {
            MessageBox.Show(this, "Among Us.exe was not found.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var gameDirectory = candidates[0];
        if (candidates.Count > 1)
        {
            var answer = MessageBox.Show(
                this,
                $"Several Among Us installations were found. Install to this location?\n\n{gameDirectory}\n\nChoose No to browse directly to another game folder.",
                Text,
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);
            if (answer != DialogResult.Yes)
            {
                Browse();
                return;
            }
        }

        _install.Enabled = false;
        _status.Text = "Installing...";
        UseWaitCursor = true;
        try
        {
            var shortcut = _shortcut.Checked;
            await Task.Run(() => InstallPayload(gameDirectory, shortcut));
            _status.Text = $"Installed successfully to {gameDirectory}";
            MessageBox.Show(
                this,
                Program.Mode == "Host"
                    ? "Installation completed. Start the game, open Local mode, and set the AI count in the lobby options. Configure your API key locally before using LLM meetings."
                    : "Installation completed. Join the host through Local mode. Bot creation and AI decisions remain host-authoritative.",
                Text,
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            if (_launch.Checked)
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = Path.Combine(gameDirectory, "Among Us.exe"),
                    WorkingDirectory = gameDirectory,
                    UseShellExecute = true
                });
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            _status.Text = "Installation failed: access denied.";
            MessageBox.Show(this, $"Windows denied write access. Run the installer as administrator and try again.\n\n{ex.Message}", Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch (Exception ex)
        {
            _status.Text = "Installation failed.";
            MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            UseWaitCursor = false;
            _install.Enabled = true;
        }
    }

    internal static void InstallPayload(string gameDirectory, bool createShortcut)
    {
        var backupDirectory = Path.Combine(
            gameDirectory,
            "DeepBot Installer Backups",
            DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        using var payload = Assembly.GetExecutingAssembly().GetManifestResourceStream("DeepBot.Payload.zip")
                            ?? throw new InvalidOperationException("The embedded payload is missing.");
        using var archive = new ZipArchive(payload, ZipArchiveMode.Read, leaveOpen: false);
        var root = Path.GetFullPath(gameDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var receiptDirectory = Path.Combine(gameDirectory, "DeepBot Installer Records");
        var receiptPath = Path.Combine(receiptDirectory, $"DeepBot-{Program.Mode}-Install.json");
        var previousReceipt = LoadReceipt(receiptPath);
        var previousEntries = previousReceipt?.Files.ToDictionary(
            entry => entry.RelativePath,
            StringComparer.OrdinalIgnoreCase) ?? new Dictionary<string, InstalledFileReceipt>(StringComparer.OrdinalIgnoreCase);
        var installed = new List<InstalledFileReceipt>();

        foreach (var entry in archive.Entries)
        {
            var relative = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
            if (string.IsNullOrWhiteSpace(relative))
            {
                continue;
            }

            var destination = Path.GetFullPath(Path.Combine(root, relative));
            if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Unsafe payload path: {entry.FullName}");
            }

            // ZIP producers are allowed to preserve Windows separators for
            // directory entries.  Checking only '/' treated entries such as
            // "BepInEx\\patchers\\" as files and failed before extracting the
            // first payload.  An empty entry Name is the portable directory
            // test used by ZipArchive.
            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(destination);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            string? originalBackupRelativePath = null;
            string? originalBackupSha256 = null;
            if (previousEntries.TryGetValue(relative, out var previousEntry))
            {
                // Preserve the pre-DeepBot origin across upgrades. Otherwise
                // uninstalling a newer build would merely restore the previous
                // DeepBot build instead of the user's original file.
                originalBackupRelativePath = previousEntry.OriginalBackupRelativePath;
                originalBackupSha256 = previousEntry.OriginalBackupSha256;
            }
            if (File.Exists(destination))
            {
                if (!previousEntries.ContainsKey(relative))
                {
                    var backup = Path.Combine(backupDirectory, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                    File.Copy(destination, backup, overwrite: true);
                    originalBackupRelativePath = Path.GetRelativePath(root, backup);
                    originalBackupSha256 = ComputeSha256(backup);
                }
            }

            using (var source = entry.Open())
            using (var target = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                source.CopyTo(target);
            }
            installed.Add(new InstalledFileReceipt(
                relative,
                ComputeSha256(destination),
                originalBackupRelativePath,
                originalBackupSha256));
        }

        Directory.CreateDirectory(receiptDirectory);
        var receipt = new InstallReceipt(
            Program.Mode,
            DateTimeOffset.Now,
            backupDirectory,
            installed);
        var receiptTempPath = receiptPath + ".tmp";
        File.WriteAllText(
            receiptTempPath,
            JsonSerializer.Serialize(receipt, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(receiptTempPath, receiptPath, overwrite: true);

        if (createShortcut)
        {
            CreateDesktopShortcut(gameDirectory);
        }

        File.AppendAllLines(
            Path.Combine(gameDirectory, "DeepBot-Installer.log"),
            new[]
            {
                $"{DateTime.Now:O} mode={Program.Mode} files={installed.Count} receipt={receiptPath}",
                $"target={gameDirectory}",
                $"backup={backupDirectory}"
            });
    }

    private static InstallReceipt? LoadReceipt(string receiptPath)
    {
        if (!File.Exists(receiptPath))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<InstallReceipt>(File.ReadAllText(receiptPath));
        }
        catch
        {
            // A corrupt old receipt must not prevent installation. Existing
            // files are backed up as a fresh origin in this case.
            return null;
        }
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void CreateDesktopShortcut(string gameDirectory)
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var shortcutPath = Path.Combine(desktop, $"Among Us DeepBot {Program.Mode}.lnk");
        var shellType = Type.GetTypeFromProgID("WScript.Shell")
                        ?? throw new InvalidOperationException("Windows Script Host is unavailable.");
        object? shell = null;
        object? shortcut = null;
        try
        {
            shell = Activator.CreateInstance(shellType);
            shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { shortcutPath });
            var type = shortcut!.GetType();
            type.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, new object[] { Path.Combine(gameDirectory, "Among Us.exe") });
            type.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, new object[] { gameDirectory });
            type.InvokeMember("Description", BindingFlags.SetProperty, null, shortcut, new object[] { $"Launch Among Us DeepBot {Program.Mode}" });
            type.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
        }
        finally
        {
            if (shortcut is not null && Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut);
            if (shell is not null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
        }
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
            foreach (var directory in EnumerateDirectories(common, 3))
            {
                AddIfGame(directory, result);
            }
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
