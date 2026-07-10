using System.ComponentModel;
using System.Text.Json;

namespace DockerZip;

public partial class MainForm : Form
{
    private CancellationTokenSource? _cts;
    private List<PlatformInfo> _platforms = [];
    private AzureAuthService? _azureAuth;
    private BackgroundWorker _downloadWorker = null!;
    private DockerRegistryClient? _downloadClient;

    public MainForm()
    {
        InitializeComponent();
        InitDownloadWorker();
        cboPlatform.Items.Add("auto (linux/amd64)");
        cboPlatform.SelectedIndex = 0;
        LoadConfig();
        WireConfigAutoSave();
        UpdateButtons(idle: true);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        SaveConfig();
        _cts?.Cancel();
        _downloadWorker.Dispose();
        _downloadClient?.Dispose();
        _azureAuth?.Dispose();
        base.OnFormClosed(e);
    }

    // ── Fetch Info ────────────────────────────────────────────────────────────

    private async void btnFetchInfo_Click(object sender, EventArgs e)
    {
        if (!ValidateInputs()) return;

        _cts = new CancellationTokenSource();
        UpdateButtons(idle: false);
        ClearLog();
        ResetProgress();
        cboPlatform.Items.Clear();
        cboPlatform.Items.Add("auto (linux/amd64)");
        cboPlatform.SelectedIndex = 0;

        try
        {
            using var client = CreateClient();
            var mgr = new DownloadManager(client, new Progress<DownloadProgress>(OnProgress));
            var image = txtImage.Text.Trim();
            var tag   = txtTag.Text.Trim();
            var token = _cts.Token;

            var result = await Task.Run(() => mgr.FetchInfoAsync(image, tag, token));

            if (result.IsManifestList && result.Platforms.Count > 0)
            {
                _platforms = result.Platforms;
                foreach (var p in _platforms)
                    cboPlatform.Items.Add(p.ToString());

                Log($"Manifest list — {_platforms.Count} platform(s) available.");
                foreach (var p in _platforms)
                    Log($"  • {p}");
            }
            else
            {
                Log("Single-arch manifest fetched.");
            }
        }
        catch (OperationCanceledException)
        {
            Log("Cancelled.");
        }
        catch (Exception ex)
        {
            Log($"ERROR: {ex.Message}");
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            UpdateButtons(idle: true);
        }
    }

    // ── Download ──────────────────────────────────────────────────────────────

    private void btnDownload_Click(object sender, EventArgs e)
    {
        if (!ValidateInputs()) return;

        var outputDir = txtOutput.Text.Trim();
        if (string.IsNullOrWhiteSpace(outputDir))
        {
            MessageBox.Show("Please specify an output directory.", "Validation",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _cts = new CancellationTokenSource();
        UpdateButtons(idle: false);
        ClearLog();
        ResetProgress();

        // Resolve platform selection
        string? platform = null;
        if (cboPlatform.SelectedIndex > 0 && _platforms.Count >= cboPlatform.SelectedIndex)
            platform = _platforms[cboPlatform.SelectedIndex - 1].ToString();

        _downloadClient = CreateClient();
        var mgr = new DownloadManager(_downloadClient, new Progress<DownloadProgress>(OnProgress));

        Log($"Starting download \u2192 {outputDir}");
        Log($"Image:    {txtImage.Text.Trim()}:{txtTag.Text.Trim()}");
        Log($"Registry: {txtRegistry.Text.Trim()}");
        if (platform != null) Log($"Platform: {platform}");

        _downloadWorker.RunWorkerAsync(new DownloadArgs(
            Manager:   mgr,
            Image:     txtImage.Text.Trim(),
            Tag:       txtTag.Text.Trim(),
            Platform:  platform,
            OutputDir: outputDir,
            SaveAsTar: chkSaveAsTar.Checked,
            Token:     _cts.Token));
    }

    private void InitDownloadWorker()
    {
        _downloadWorker = new BackgroundWorker { WorkerSupportsCancellation = true };
        _downloadWorker.DoWork             += DownloadWorker_DoWork;
        _downloadWorker.RunWorkerCompleted += DownloadWorker_Completed;
    }

    private static void DownloadWorker_DoWork(object? sender, DoWorkEventArgs e)
    {
        var args = (DownloadArgs)e.Argument!;
        try
        {
            args.Manager.DownloadAsync(
                image:            args.Image,
                reference:        args.Tag,
                platformOverride: args.Platform,
                outputDir:        args.OutputDir,
                saveAsTar:        args.SaveAsTar,
                ct:               args.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            e.Cancel = true;
        }
        // Other exceptions propagate to RunWorkerCompleted via e.Error
    }

    private void DownloadWorker_Completed(object? sender, RunWorkerCompletedEventArgs e)
    {
        _downloadClient?.Dispose();
        _downloadClient = null;
        _cts?.Dispose();
        _cts = null;
        UpdateButtons(idle: true);

        if (e.Cancelled)
            Log("Download cancelled.");
        else if (e.Error != null)
        {
            Log($"ERROR: {e.Error.Message}");
            MessageBox.Show($"Download failed:\n\n{e.Error.Message}", "Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        else
        {
            Log("Download complete.");
            MessageBox.Show("Download finished successfully.", "Done",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    // ── Azure SSO ─────────────────────────────────────────────────────────────

    private void chkAzureSSO_CheckedChanged(object sender, EventArgs e)
    {
        var useAzure = chkAzureSSO.Checked;

        // Toggle credential controls
        lblUsername.Visible = !useAzure;
        txtUsername.Visible = !useAzure;
        lblPassword.Visible = !useAzure;
        txtPassword.Visible = !useAzure;

        btnAzureLogin.Visible = useAzure;
        lblAzureStatus.Visible = useAzure;

        if (!useAzure)
        {
            _azureAuth?.Dispose();
            _azureAuth = null;
            return;
        }

        // Initialise service and try silent refresh
        _azureAuth ??= new AzureAuthService();
        _ = TrySilentAzureRefreshAsync();
    }

    private async Task TrySilentAzureRefreshAsync()
    {
        if (_azureAuth == null) return;
        try
        {
            if (await _azureAuth.TrySilentRefreshAsync())
                UpdateAzureStatus();
        }
        catch { /* silent refresh failure is acceptable */ }
    }

    private async void btnAzureLogin_Click(object sender, EventArgs e)
    {
        if (_azureAuth == null) return;

        if (_azureAuth.IsLoggedIn)
        {
            // Already signed in → offer sign-out
            if (MessageBox.Show(
                    $"Sign out {_azureAuth.SignedInUser}?",
                    "Azure Sign Out",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question) == DialogResult.Yes)
            {
                await _azureAuth.LogoutAsync();
                UpdateAzureStatus();
            }
            return;
        }

        btnAzureLogin.Enabled = false;
        lblAzureStatus.Text = "Opening browser…";

        try
        {
            await _azureAuth.LoginInteractiveAsync(Handle);
            UpdateAzureStatus();
        }
        catch (Exception ex)
        {
            lblAzureStatus.Text = $"Login failed: {ex.Message}";
            Log($"Azure login error: {ex.Message}");
        }
        finally
        {
            btnAzureLogin.Enabled = true;
        }
    }

    private void UpdateAzureStatus()
    {
        if (_azureAuth == null) return;

        if (_azureAuth.IsLoggedIn)
        {
            lblAzureStatus.ForeColor = Color.DarkGreen;
            lblAzureStatus.Text = $"Signed in as {_azureAuth.SignedInUser}";
            btnAzureLogin.Text = "Sign out";
            Log($"Azure SSO: signed in as {_azureAuth.SignedInUser} (tenant {_azureAuth.SignedInTenant})");
        }
        else
        {
            lblAzureStatus.ForeColor = SystemColors.GrayText;
            lblAzureStatus.Text = "(not signed in)";
            btnAzureLogin.Text = "Sign in with Azure";
        }
    }

    // ── Cancel ────────────────────────────────────────────────────────────────

    private void btnCancel_Click(object sender, EventArgs e)
    {
        _cts?.Cancel();
        Log("Cancellation requested…");
    }

    // ── Browse ────────────────────────────────────────────────────────────────

    private void btnBrowse_Click(object sender, EventArgs e)
    {
        using var dlg = new FolderBrowserDialog
        {
            Description = "Select output directory",
            UseDescriptionForTitle = true,
            SelectedPath = txtOutput.Text.Trim()
        };
        if (dlg.ShowDialog(this) == DialogResult.OK)
            txtOutput.Text = dlg.SelectedPath;
    }

    // ── Progress ──────────────────────────────────────────────────────────────

    private void OnProgress(DownloadProgress p)
    {
        if (InvokeRequired) { Invoke(() => OnProgress(p)); return; }

        lblStatus.Text = p.Status;
        if (p.IsLogEntry) Log(p.Status);

        if (p.LayerTotal > 0)
        {
            progressBar.Maximum = p.LayerTotal;
            progressBar.Value = Math.Min(p.LayerCurrent, p.LayerTotal);
        }
        else
        {
            progressBar.Value = 0;
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private DockerRegistryClient CreateClient() => new(
        txtRegistry.Text.Trim(),
        string.IsNullOrWhiteSpace(txtUsername.Text) ? null : txtUsername.Text.Trim(),
        string.IsNullOrWhiteSpace(txtPassword.Text) ? null : txtPassword.Text,
        _azureAuth);

    private bool ValidateInputs()
    {
        if (string.IsNullOrWhiteSpace(txtRegistry.Text))
        { MessageBox.Show("Registry URL is required.", "Validation", MessageBoxButtons.OK, MessageBoxIcon.Warning); return false; }
        if (string.IsNullOrWhiteSpace(txtImage.Text))
        { MessageBox.Show("Image name is required.", "Validation", MessageBoxButtons.OK, MessageBoxIcon.Warning); return false; }
        if (string.IsNullOrWhiteSpace(txtTag.Text))
        { MessageBox.Show("Tag or digest is required.", "Validation", MessageBoxButtons.OK, MessageBoxIcon.Warning); return false; }
        return true;
    }

    private void UpdateButtons(bool idle)
    {
        btnFetchInfo.Enabled = idle;
        btnDownload.Enabled = idle;
        btnCancel.Enabled = !idle;
    }

    private void Log(string message)
    {
        if (InvokeRequired) { Invoke(() => Log(message)); return; }
        var time = DateTime.Now.ToString("HH:mm:ss");
        rtbLog.AppendText($"[{time}] {message}{Environment.NewLine}");
        rtbLog.ScrollToCaret();
    }

    private void ClearLog() => rtbLog.Clear();

    private void ResetProgress()
    {
        progressBar.Value = 0;
        lblStatus.Text = string.Empty;
    }

    private void WireConfigAutoSave()
    {
        EventHandler save = (_, _) => SaveConfig();
        txtRegistry.TextChanged  += save;
        txtImage.TextChanged     += save;
        txtTag.TextChanged       += save;
        txtOutput.TextChanged    += save;
        txtUsername.TextChanged  += save;
        chkSaveAsTar.CheckedChanged += save;
        chkAzureSSO.CheckedChanged  += save;
    }

    // ── Configuration persistence ─────────────────────────────────────────────

    private static string ConfigFilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                     "DockerZip", "settings.json");

    private void LoadConfig()
    {
        AppConfig cfg;
        try
        {
            cfg = File.Exists(ConfigFilePath)
                ? JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigFilePath)) ?? new AppConfig()
                : new AppConfig();
        }
        catch { cfg = new AppConfig(); }

        txtRegistry.Text = cfg.Registry;
        txtImage.Text = cfg.Image;
        txtTag.Text = cfg.Tag;
        txtOutput.Text = string.IsNullOrEmpty(cfg.OutputDir)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                           "Downloads", "docker-images")
            : cfg.OutputDir;
        txtUsername.Text = cfg.Username;
        chkSaveAsTar.Checked = cfg.SaveAsTar;
        // Defer Azure SSO toggle so InitializeComponent wiring is fully set up
        if (cfg.UseAzureSSO)
            chkAzureSSO.Checked = true;
    }

    private void SaveConfig()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigFilePath)!);
            var cfg = new AppConfig
            {
                Registry   = txtRegistry.Text.Trim(),
                Image      = txtImage.Text.Trim(),
                Tag        = txtTag.Text.Trim(),
                OutputDir  = txtOutput.Text.Trim(),
                Username   = txtUsername.Text.Trim(),
                SaveAsTar  = chkSaveAsTar.Checked,
                UseAzureSSO = chkAzureSSO.Checked,
            };
            File.WriteAllText(ConfigFilePath,
                JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* best effort */ }
    }

    private record DownloadArgs(
        DownloadManager   Manager,
        string            Image,
        string            Tag,
        string?           Platform,
        string            OutputDir,
        bool              SaveAsTar,
        CancellationToken Token);
}
