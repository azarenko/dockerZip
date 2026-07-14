namespace DockerZip;

partial class MainForm
{
    private System.ComponentModel.IContainer components = null;

    // Controls
    private GroupBox grpSource;
    private Label lblRegistry;
    private TextBox txtRegistry;
    private Label lblImage;
    private TextBox txtImage;
    private Label lblTag;
    private TextBox txtTag;

    private GroupBox grpAuth;
    private CheckBox chkAzureSSO;
    private Button btnAzureLogin;
    private Label lblAzureStatus;
    private Label lblUsername;
    private TextBox txtUsername;
    private Label lblPassword;
    private TextBox txtPassword;
    private GroupBox grpSettings;
    private Label lblPlatform;
    private ComboBox cboPlatform;
    private Label lblOutput;
    private TextBox txtOutput;
    private Button btnBrowse;
    private CheckBox chkSaveAsTar;

    private Button btnFetchInfo;
    private Button btnDownload;
    private Button btnCancel;

    private Label lblStatus;
    private ProgressBar progressBar;

    private GroupBox grpLog;
    private RichTextBox rtbLog;

    protected override void Dispose(bool disposing)
    {
        if (disposing && components != null) components.Dispose();
        base.Dispose(disposing);
    }

    private void InitializeComponent()
    {
        components = new System.ComponentModel.Container();
        SuspendLayout();

        // ── Form ──────────────────────────────────────────────────────────────
        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(700, 710);
        MinimumSize = new Size(716, 749);
        Text = "Docker Image Downloader";
        StartPosition = FormStartPosition.CenterScreen;

        // ── Image Source ──────────────────────────────────────────────────────
        grpSource = new GroupBox { Text = "Image Source", Location = new Point(12, 8), Size = new Size(676, 120), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };

        lblRegistry = new Label { Text = "Registry:", Location = new Point(10, 24), Size = new Size(70, 23), TextAlign = ContentAlignment.MiddleRight };
        txtRegistry = new TextBox { Location = new Point(86, 21), Size = new Size(576, 23), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };

        lblImage = new Label { Text = "Image:", Location = new Point(10, 54), Size = new Size(70, 23), TextAlign = ContentAlignment.MiddleRight };
        txtImage = new TextBox { Location = new Point(86, 51), Size = new Size(576, 23), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };

        lblTag = new Label { Text = "Tag / Digest:", Location = new Point(10, 84), Size = new Size(70, 23), TextAlign = ContentAlignment.MiddleRight };
        txtTag = new TextBox { Location = new Point(86, 81), Size = new Size(220, 23) };

        grpSource.Controls.AddRange([lblRegistry, txtRegistry, lblImage, txtImage, lblTag, txtTag]);

        // ── Authentication ────────────────────────────────────────────────────
        grpAuth = new GroupBox { Text = "Authentication", Location = new Point(12, 136), Size = new Size(676, 120), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };

        chkAzureSSO = new CheckBox
        {
            Text = "Use Azure SSO  (for Azure Container Registry — azurecr.io)",
            Location = new Point(10, 22), AutoSize = true
        };
        chkAzureSSO.CheckedChanged += chkAzureSSO_CheckedChanged;

        lblUsername = new Label { Text = "Username:", Location = new Point(10, 57), Size = new Size(70, 23), TextAlign = ContentAlignment.MiddleRight };
        txtUsername = new TextBox { Location = new Point(86, 54), Size = new Size(180, 23) };
        lblPassword = new Label { Text = "Password:", Location = new Point(296, 57), Size = new Size(68, 23), TextAlign = ContentAlignment.MiddleRight };
        txtPassword = new TextBox { Location = new Point(370, 54), Size = new Size(180, 23), UseSystemPasswordChar = true };

        btnAzureLogin = new Button { Text = "Sign in with Azure", Location = new Point(86, 54), Size = new Size(150, 27), Visible = false };
        btnAzureLogin.Click += btnAzureLogin_Click;

        lblAzureStatus = new Label { Text = "(not signed in)", Location = new Point(250, 58), Size = new Size(400, 20), ForeColor = SystemColors.GrayText, Visible = false };

        grpAuth.Controls.AddRange([chkAzureSSO, lblUsername, txtUsername, lblPassword, txtPassword, btnAzureLogin, lblAzureStatus]);

        // ── Download Settings ─────────────────────────────────────────────────
        grpSettings = new GroupBox { Text = "Download Settings", Location = new Point(12, 264), Size = new Size(676, 110), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };

        lblPlatform = new Label { Text = "Platform:", Location = new Point(10, 28), Size = new Size(70, 23), TextAlign = ContentAlignment.MiddleRight };
        cboPlatform = new ComboBox { Location = new Point(86, 25), Size = new Size(220, 23), DropDownStyle = ComboBoxStyle.DropDownList };

        lblOutput = new Label { Text = "Output dir:", Location = new Point(10, 62), Size = new Size(70, 23), TextAlign = ContentAlignment.MiddleRight };
        txtOutput = new TextBox { Location = new Point(86, 59), Size = new Size(488, 23), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
        btnBrowse = new Button { Text = "Browse…", Location = new Point(580, 57), Size = new Size(82, 27), Anchor = AnchorStyles.Top | AnchorStyles.Right };
        btnBrowse.Click += btnBrowse_Click;

        chkSaveAsTar = new CheckBox { Text = "Save as flattened filesystem .tar  (merged layers, extract with tar -xf)", Location = new Point(86, 90), AutoSize = true, Checked = true };

        grpSettings.Controls.AddRange([lblPlatform, cboPlatform, lblOutput, txtOutput, btnBrowse, chkSaveAsTar]);

        // ── Action Buttons ────────────────────────────────────────────────────
        btnFetchInfo = new Button { Text = "Fetch Info", Location = new Point(12, 386), Size = new Size(110, 32) };
        btnFetchInfo.Click += btnFetchInfo_Click;

        btnDownload = new Button { Text = "Download", Location = new Point(132, 386), Size = new Size(110, 32), Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold) };
        btnDownload.Click += btnDownload_Click;

        btnCancel = new Button { Text = "Cancel", Location = new Point(252, 386), Size = new Size(80, 32) };
        btnCancel.Click += btnCancel_Click;

        // ── Progress ──────────────────────────────────────────────────────────
        lblStatus = new Label { Location = new Point(12, 428), Size = new Size(676, 20), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right, ForeColor = SystemColors.GrayText };
        progressBar = new ProgressBar { Location = new Point(12, 451), Size = new Size(676, 22), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };

        // ── Log ───────────────────────────────────────────────────────────────
        grpLog = new GroupBox { Text = "Log", Location = new Point(12, 482), Size = new Size(676, 210), Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right };
        rtbLog = new RichTextBox { Location = new Point(8, 20), Size = new Size(660, 225), Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right, ReadOnly = true, BackColor = SystemColors.Window, ScrollBars = RichTextBoxScrollBars.Vertical, Font = new Font("Consolas", 8.5f) };
        grpLog.Controls.Add(rtbLog);

        // ── Add all to form ───────────────────────────────────────────────────
        Controls.AddRange([grpSource, grpAuth, grpSettings, btnFetchInfo, btnDownload, btnCancel, lblStatus, progressBar, grpLog]);

        ResumeLayout(false);
    }
}
