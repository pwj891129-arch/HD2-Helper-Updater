using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HD2_Helper_Updater;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (TryRunSelfUpdateBootstrap(args)) return;
        CleanupSelfUpdateDownload(args);

        bool isSteamAutostart = args.Any(arg => arg.Equals("--steam-autostart", StringComparison.OrdinalIgnoreCase));
        using var mutex = new Mutex(true, "HD2_Helper_Updater_Unique", out bool createdNew);
        if (!createdNew)
        {
            // Steam 실행 옵션의 반복 호출은 사용자 조작이 아니므로 중복 실행 알림을 띄우지 않는다.
            if (!isSteamAutostart)
                MessageBox.Show("HD2 Helper Updater가 이미 실행 중입니다.", "HD2 Helper", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new UpdaterForm());
    }

    private static bool TryRunSelfUpdateBootstrap(string[] args)
    {
        if (!args.Any(arg => arg.Equals("--self-update-bootstrap", StringComparison.OrdinalIgnoreCase))) return false;

        try
        {
            int parentPid = int.Parse(GetArgumentValue(args, "--parent-pid=") ?? throw new InvalidDataException("기존 업데이터 PID가 없습니다."));
            string launchTarget = GetArgumentValue(args, "--launch-target=") ?? throw new InvalidDataException("재실행할 업데이터 경로가 없습니다.");
            string[] targets = args
                .Where(arg => arg.StartsWith("--replace-target=", StringComparison.OrdinalIgnoreCase))
                .Select(arg => arg["--replace-target=".Length..])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (targets.Length == 0) throw new InvalidDataException("교체할 업데이터 경로가 없습니다.");

            try
            {
                using Process parent = Process.GetProcessById(parentPid);
                parent.WaitForExit(30000);
            }
            catch (ArgumentException)
            {
                // 기존 프로세스가 이미 종료된 경우에는 곧바로 파일 교체를 진행한다.
            }

            string replacementSource = Path.GetFullPath(Application.ExecutablePath);
            foreach (string rawTarget in targets)
            {
                string target = Path.GetFullPath(rawTarget);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                string temporaryTarget = target + ".self-update-new";
                File.Copy(replacementSource, temporaryTarget, true);
                File.Move(temporaryTarget, target, true);
            }

            var restartInfo = new ProcessStartInfo
            {
                FileName = Path.GetFullPath(launchTarget),
                UseShellExecute = true
            };
            restartInfo.ArgumentList.Add("--self-updated");
            restartInfo.ArgumentList.Add($"--cleanup-self-update={replacementSource}");
            restartInfo.ArgumentList.Add($"--cleanup-parent-pid={Environment.ProcessId}");
            Process.Start(restartInfo);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"업데이터 자체 업데이트를 완료하지 못했습니다.\n\n{ex.Message}", "HD2 Helper Updater", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        return true;
    }

    private static string? GetArgumentValue(IEnumerable<string> args, string prefix) =>
        args.FirstOrDefault(arg => arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))?[prefix.Length..];

    private static void CleanupSelfUpdateDownload(string[] args)
    {
        string? source = GetArgumentValue(args, "--cleanup-self-update=");
        if (string.IsNullOrWhiteSpace(source)) return;

        if (int.TryParse(GetArgumentValue(args, "--cleanup-parent-pid="), out int parentPid))
        {
            try
            {
                using Process parent = Process.GetProcessById(parentPid);
                parent.WaitForExit(10000);
            }
            catch { }
        }

        try
        {
            string fullSource = Path.GetFullPath(source);
            File.Delete(fullSource);
            string? directory = Path.GetDirectoryName(fullSource);
            if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory)
                && !Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
        }
        catch
        {
            // 임시 파일 정리 실패는 새 업데이터 실행을 막을 이유가 없으므로 다음 업데이트 때 덮어쓴다.
        }
    }
}

internal sealed class UpdaterForm : Form
{
    private const string HelperFileName = "HD2 Helper.exe";
    private const string UpdaterFileName = "HD2 Helper Updater.exe";
    private const string PackageFileName = "HD2.Helper.zip";
    private const string UpdaterAssetFileName = "HD2.Helper.Updater.exe";
    private const string UpdaterReleaseTagPrefix = "updater-v";
    // 테스트 코드는 해시만 보관한다. 공개 릴리스 주소의 접근 자체를 막지는 않지만 일반 목록에서는 시험판을 분리한다.
    private const string TestChannelCodeHash = "d85cd20564ce310a8d398b7fb4a4998f1bef15bbc1cda840a88a780b9f5838c0";
    private const string StateFileName = "updater-state.json";
    private const string SettingsFileName = "updater-settings.json";
    private const string DeleteListFileName = "update-delete.txt";
    private const string HelperGitHubRepository = "pwj891129-arch/HD2-Helper";
    private const string UpdaterGitHubRepository = "pwj891129-arch/HD2-Helper-Updater";
    private const string HelperReleasesApiUrl = "https://api.github.com/repos/" + HelperGitHubRepository + "/releases?per_page=100";
    private const string HelperLatestReleaseApiUrl = "https://api.github.com/repos/" + HelperGitHubRepository + "/releases/latest";
    private const string UpdaterReleasesApiUrl = "https://api.github.com/repos/" + UpdaterGitHubRepository + "/releases?per_page=100";
    private const string UpdaterLatestReleaseApiUrl = "https://api.github.com/repos/" + UpdaterGitHubRepository + "/releases/latest";
    private const string UpdaterDownloadPageUrl = "https://github.com/" + UpdaterGitHubRepository + "/releases/latest";
    private const int HeaderHeight = 42;
    private const int FooterHeight = 34;
    private const int InitialHelperWidth = 950;
    private const int InitialHelperHeight = 590;
    private const int BaseReferenceWidth = 1920;
    private const int BaseReferenceHeight = 1080;
    private const double MinClientScale = 0.5;
    private static readonly TimeSpan GitHubApiCacheLifetime = TimeSpan.FromMinutes(5);

    private static readonly HttpClient Http = CreateHttpClient();
    private static readonly object GitHubApiCacheLock = new();
    private static readonly Dictionary<string, GitHubApiCacheEntry> GitHubApiCache = new(StringComparer.Ordinal);
    private static readonly string UpdaterDataDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "HD2 Helper"
    );
    private readonly string _updaterDirectory = AppContext.BaseDirectory;
    private string _installDirectory = "";
    private readonly SemaphoreSlim _updateGate = new(1, 1);
    private readonly Panel _hostPanel = new();
    private readonly Panel _progressTrack = new();
    private readonly Panel _progressFill = new();
    private readonly Label _statusLabel = new();
    private readonly Label _titleLabel = new();
    private readonly Label _helperVersionLabel = new();
    private readonly Button _checkButton = new();
    private readonly Button _historyButton = new();
    private readonly Button _updaterUpdateButton = new();
    private readonly Button _installPathButton = new();
    private readonly Button _helpButton = new();
    private readonly System.Windows.Forms.Timer _periodicCheckTimer = new();
    private readonly System.Windows.Forms.Timer _embeddedLayoutTimer = new();

    private Process? _helperProcess;
    private IntPtr _embeddedHelperWindow = IntPtr.Zero;
    private bool _busy;
    private bool _closing;
    private bool _stoppingHelper;
    private Size _lastEmbeddedSize;
    private List<GitHubReleaseInfo> _lastReleases = new();
    private GitHubUpdaterInfo? _latestUpdaterRelease;

    private string HelperPath => Path.Combine(_installDirectory, HelperFileName);
    private static string StatePath => Path.Combine(UpdaterDataDirectory, StateFileName);
    private static string SettingsPath => Path.Combine(UpdaterDataDirectory, SettingsFileName);

    public UpdaterForm()
    {
        Text = "HD2 Helper";
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        BackColor = Color.FromArgb(17, 17, 17);
        ForeColor = Color.WhiteSmoke;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.None;

        // 헬퍼가 사용하는 해상도 비율 계산과 동일한 크기로 외곽 창과 호스트 패널을 먼저 만든다.
        Size initialHelperSize = GetInitialHelperSize();
        _lastEmbeddedSize = initialHelperSize;
        ClientSize = new Size(initialHelperSize.Width, HeaderHeight + initialHelperSize.Height + FooterHeight);
        MinimumSize = new Size(760, 520);

        BuildInterface();

        Shown += async (_, _) => await InitializeAsync();
        FormClosing += OnUpdaterClosing;
        Activated += (_, _) => _embeddedLayoutTimer.Start();
        Deactivate += (_, _) => _embeddedLayoutTimer.Stop();

        _periodicCheckTimer.Interval = 6 * 60 * 60 * 1000;
        _periodicCheckTimer.Tick += async (_, _) =>
        {
            if (IsHelldiversActive()) return;
            await RefreshUpdateStatusAsync(interactive: false);
        };

        // 임베드된 헬퍼가 설정 패널을 펼쳐 자체 크기를 바꿀 때만 바깥 창 크기를 따라간다.
        _embeddedLayoutTimer.Interval = 250;
        _embeddedLayoutTimer.Tick += (_, _) => SynchronizeEmbeddedWindowSize();
    }

    private void BuildInterface()
    {
        // 폼에 붙기 전 기본 패널 폭(200px)으로 오른쪽 앵커가 계산되면 버튼들이 화면 밖으로 밀리므로 실제 폭을 먼저 지정한다.
        var header = new Panel
        {
            Dock = DockStyle.Top,
            Size = new Size(ClientSize.Width, HeaderHeight),
            BackColor = Color.FromArgb(28, 28, 28)
        };
        header.MouseDown += DragWindow;

        _titleLabel.Text = $"HD2 HELPER  •  UPDATER {GetUpdaterDisplayVersion()}";
        _titleLabel.Font = new Font("Segoe UI", 11, FontStyle.Bold);
        _titleLabel.ForeColor = Color.FromArgb(255, 216, 0);
        _titleLabel.AutoEllipsis = true;
        _titleLabel.Size = new Size(Math.Max(180, ClientSize.Width - 630), 24);
        _titleLabel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _titleLabel.Location = new Point(14, 11);
        _titleLabel.MouseDown += DragWindow;
        header.Controls.Add(_titleLabel);

        // 업데이터 버전과 설치된 헬퍼 버전을 분리해 보여 준다.
        _helperVersionLabel.Font = new Font("Segoe UI", 8, FontStyle.Regular);
        _helperVersionLabel.ForeColor = Color.Gainsboro;
        _helperVersionLabel.BackColor = header.BackColor;
        _helperVersionLabel.AutoSize = true;
        _helperVersionLabel.Visible = true;
        _helperVersionLabel.MouseDown += DragWindow;
        header.Controls.Add(_helperVersionLabel);

        Button closeButton = CreateHeaderButton("X", 38);
        closeButton.Click += (_, _) => Close();
        header.Controls.Add(closeButton);

        Button minimizeButton = CreateHeaderButton("_", 74);
        minimizeButton.Click += (_, _) => WindowState = FormWindowState.Minimized;
        header.Controls.Add(minimizeButton);

        ConfigureCommandButton(_installPathButton, "설치 경로", 264, 96);
        _installPathButton.Click += async (_, _) => await ChangeInstallDirectoryAsync();
        header.Controls.Add(_installPathButton);

        // 설정 화면에 드러나지 않는 프리셋 편집 단축 동작만 따로 안내한다.
        ConfigureCommandButton(_helpButton, "도움말", 168, 88);
        _helpButton.Click += (_, _) => ShowShortcutHelp();
        header.Controls.Add(_helpButton);

        ConfigureCommandButton(_checkButton, "버전 선택", 362, 92);
        _checkButton.Click += async (_, _) => await ShowVersionSelectionMenuAsync();
        header.Controls.Add(_checkButton);

        ConfigureCommandButton(_historyButton, "업데이트 내역", 470, 98);
        _historyButton.Click += async (_, _) => await ShowUpdateHistoryAsync();
        header.Controls.Add(_historyButton);

        // 업데이터 파일은 사용자가 명시적으로 승인했을 때만 교체한다.
        ConfigureCommandButton(_updaterUpdateButton, "업데이터 갱신", 590, 108);
        _updaterUpdateButton.Click += async (_, _) => await ApplyUpdaterUpdateManuallyAsync();
        header.Controls.Add(_updaterUpdateButton);

        // 넓은 제목 라벨에 가려지지 않도록 헬퍼 버전 라벨을 헤더의 최상단에 둔다.
        UpdateHeaderHelperVersionLabel();
        _helperVersionLabel.BringToFront();

        _hostPanel.Dock = DockStyle.Fill;
        _hostPanel.BackColor = Color.Black;

        var footer = new Panel
        {
            Dock = DockStyle.Bottom,
            Size = new Size(ClientSize.Width, FooterHeight),
            BackColor = Color.FromArgb(28, 28, 28)
        };
        _statusLabel.Text = "시작 준비 중";
        _statusLabel.ForeColor = Color.Gainsboro;
        _statusLabel.Font = new Font("Segoe UI", 9);
        _statusLabel.AutoEllipsis = true;
        _statusLabel.Location = new Point(12, 7);
        _statusLabel.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
        _statusLabel.Size = new Size(ClientSize.Width - 24, 18);
        footer.Controls.Add(_statusLabel);

        _progressTrack.Dock = DockStyle.Bottom;
        _progressTrack.Height = 3;
        _progressTrack.BackColor = Color.FromArgb(65, 65, 65);
        _progressFill.Height = 3;
        _progressFill.Width = 0;
        _progressFill.BackColor = Color.FromArgb(255, 216, 0);
        _progressTrack.Controls.Add(_progressFill);
        footer.Controls.Add(_progressTrack);

        Controls.Add(_hostPanel);
        Controls.Add(footer);
        Controls.Add(header);
    }

    private Button CreateHeaderButton(string text, int rightOffset) => new()
    {
        Text = text,
        FlatStyle = FlatStyle.Flat,
        FlatAppearance = { BorderSize = 0, MouseOverBackColor = Color.FromArgb(60, 60, 60) },
        BackColor = Color.Transparent,
        ForeColor = Color.WhiteSmoke,
        Font = new Font("Segoe UI", 10, FontStyle.Bold),
        Size = new Size(36, HeaderHeight),
        Location = new Point(ClientSize.Width - rightOffset, 0),
        Anchor = AnchorStyles.Top | AnchorStyles.Right,
        TabStop = false
    };

    private static Size GetInitialHelperSize()
    {
        Rectangle bounds = Screen.PrimaryScreen!.Bounds;
        double scale = Math.Min(
            (double)bounds.Width / BaseReferenceWidth,
            (double)bounds.Height / BaseReferenceHeight
        );
        scale = Math.Max(scale, MinClientScale);

        return new Size(
            (int)Math.Round(InitialHelperWidth * scale),
            (int)Math.Round(InitialHelperHeight * scale)
        );
    }

    private void ConfigureCommandButton(Button button, string text, int rightOffset, int width)
    {
        button.Text = text;
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderColor = Color.FromArgb(100, 100, 100);
        button.FlatAppearance.MouseOverBackColor = Color.FromArgb(55, 55, 55);
        button.BackColor = Color.FromArgb(32, 32, 32);
        button.ForeColor = Color.WhiteSmoke;
        button.Font = new Font("Segoe UI", 9);
        button.Size = new Size(width, 28);
        button.Location = new Point(ClientSize.Width - rightOffset, 7);
        button.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        button.TabStop = false;
    }

    private void ShowShortcutHelp()
    {
        using Form dialog = CreateShortcutHelpDialog();
        dialog.ShowDialog(this);
    }

    private static Form CreateShortcutHelpDialog()
    {
        var dialog = new Form
        {
            Text = "도움말",
            StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(520, 390),
            MinimumSize = new Size(460, 330),
            BackColor = Color.FromArgb(24, 24, 24),
            ForeColor = Color.WhiteSmoke,
            FormBorderStyle = FormBorderStyle.Sizable,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false
        };
        var title = new Label
        {
            Text = "숨은 조작 도움말",
            Dock = DockStyle.Top,
            Height = 42,
            Padding = new Padding(14, 10, 0, 0),
            Font = new Font("Segoe UI", 11, FontStyle.Bold),
            ForeColor = Color.FromArgb(255, 216, 0),
            BackColor = Color.FromArgb(32, 32, 32)
        };
        var contents = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            BackColor = Color.FromArgb(24, 24, 24),
            ForeColor = Color.Gainsboro,
            Font = new Font("Segoe UI", 10),
            ScrollBars = RichTextBoxScrollBars.Vertical,
            Margin = new Padding(14),
            Padding = new Padding(14),
            Text =
                "프리셋 탭 (스트라타젬 / 장비 공통)\n\n" +
                "Shift + 좌클릭  : 현재 탭 뒤에 새 프리셋 추가\n" +
                "Ctrl + 좌클릭   : 현재 프리셋 삭제\n" +
                "Shift + 우클릭  : 현재 프리셋 복제\n" +
                "Ctrl + 우클릭   : 현재 프리셋 삭제\n" +
                "더블클릭        : 프리셋 이름 변경\n" +
                "Ctrl + S        : 현재 선택한 프리셋 저장\n\n" +
                "스트라타젬 슬롯\n\n" +
                "Shift + 좌클릭  : 오버레이 휠 표시 상태 전환\n" +
                "초록 테두리     : 휠에 표시\n" +
                "빨강 테두리     : 휠에서 비표시\n" +
                "회색 테두리     : 스트라타젬 프리셋 미선택 상태\n\n" +
                "표시 상태는 스트라타젬 프리셋마다 따로 저장됩니다."
        };

        dialog.Controls.Add(contents);
        dialog.Controls.Add(title);
        return dialog;
    }

    private async Task InitializeAsync()
    {
        if (!EnsureInstallDirectory())
        {
            Close();
            return;
        }

        // 새 버전은 조회만 한다. 업데이터와 헬퍼 모두 사용자가 버튼이나 버전 목록에서 선택할 때만 적용한다.
        await RefreshUpdateStatusAsync(interactive: false);
        if (File.Exists(HelperPath))
            await LaunchHelperEmbeddedAsync();
        else
            SetStatus("설치할 버전을 선택하세요.");
        _periodicCheckTimer.Start();
    }

    private bool EnsureInstallDirectory()
    {
        string? savedDirectory = LoadUpdaterSettings().InstallDirectory;
        if (!string.IsNullOrWhiteSpace(savedDirectory))
        {
            try
            {
                _installDirectory = Path.GetFullPath(savedDirectory);
                Directory.CreateDirectory(_installDirectory);
                CopyUpdaterIntoInstallDirectory();
                SetStatus($"설치 위치: {_installDirectory}");
                return true;
            }
            catch
            {
                // 저장 경로가 삭제되었거나 접근할 수 없으면 아래의 최초 설치 선택창으로 다시 안내한다.
            }
        }

        string currentDirectory = Path.GetFullPath(_updaterDirectory);
        if (File.Exists(Path.Combine(currentDirectory, HelperFileName)))
        {
            // 기존 배포 폴더에서 처음 실행한 사용자는 현재 위치를 설치 경로로 자동 이전한다.
            _installDirectory = currentDirectory;
            SaveUpdaterSettings(new UpdaterSettings { InstallDirectory = _installDirectory });
            SetStatus($"기존 설치 위치 등록: {_installDirectory}");
            return true;
        }

        string defaultDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs",
            "HD2 Helper"
        );

        using var dialog = new FolderBrowserDialog
        {
            Description = "HD2 Helper를 설치할 폴더를 선택하세요.",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
            SelectedPath = defaultDirectory
        };

        if (dialog.ShowDialog(this) != DialogResult.OK || string.IsNullOrWhiteSpace(dialog.SelectedPath))
            return false;

        try
        {
            _installDirectory = Path.GetFullPath(dialog.SelectedPath);
            Directory.CreateDirectory(_installDirectory);
            CopyUpdaterIntoInstallDirectory();
            SaveUpdaterSettings(new UpdaterSettings { InstallDirectory = _installDirectory });
            SetStatus($"설치 위치 저장 완료: {_installDirectory}");
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"선택한 폴더를 설치 위치로 사용할 수 없습니다.\n\n{ex.Message}", "설치 경로 오류", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
    }

    private void CopyUpdaterIntoInstallDirectory()
    {
        string installedUpdaterPath = Path.Combine(_installDirectory, UpdaterFileName);
        string currentUpdaterPath = Path.GetFullPath(Application.ExecutablePath);
        if (string.Equals(currentUpdaterPath, Path.GetFullPath(installedUpdaterPath), StringComparison.OrdinalIgnoreCase))
            return;

        // 다운로드한 업데이터 한 파일만으로도 이후 실행에 필요한 업데이터가 설치 폴더에 남도록 복사한다.
        if (File.Exists(installedUpdaterPath) && CompareFileVersions(installedUpdaterPath, currentUpdaterPath) > 0)
            return;

        string temporaryPath = installedUpdaterPath + ".install-new";
        File.Copy(currentUpdaterPath, temporaryPath, true);
        File.Move(temporaryPath, installedUpdaterPath, true);
    }

    private async Task ChangeInstallDirectoryAsync()
    {
        if (_busy) return;

        using var dialog = new FolderBrowserDialog
        {
            Description = "HD2 Helper를 사용할 설치 폴더를 선택하세요.",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
            SelectedPath = _installDirectory
        };
        if (dialog.ShowDialog(this) != DialogResult.OK || string.IsNullOrWhiteSpace(dialog.SelectedPath)) return;

        string sourceDirectory = Path.GetFullPath(_installDirectory);
        string destinationDirectory = Path.GetFullPath(dialog.SelectedPath);
        if (string.Equals(sourceDirectory.TrimEnd(Path.DirectorySeparatorChar), destinationDirectory.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            SetStatus($"현재 설치 위치: {_installDirectory}");
            return;
        }

        // 원본과 대상이 서로 포함되면 이동 중 같은 파일을 다시 순회할 수 있으므로 별도 위치만 허용한다.
        if (IsSameOrChildPath(sourceDirectory, destinationDirectory) || IsSameOrChildPath(destinationDirectory, sourceDirectory))
        {
            MessageBox.Show(this, "현재 설치 폴더의 내부 또는 상위 폴더로는 이동할 수 없습니다.\n서로 분리된 폴더를 선택하세요.", "설치 경로", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        DialogResult mode = MessageBox.Show(
            this,
            "기존 HD2 Helper 파일도 선택한 폴더로 이동하시겠습니까?\n\n예: 기존 파일을 새 폴더로 이동\n아니요: 설치 경로만 변경\n취소: 변경하지 않음",
            "설치 경로 변경",
            MessageBoxButtons.YesNoCancel,
            MessageBoxIcon.Question);
        if (mode == DialogResult.Cancel) return;

        bool moveFiles = mode == DialogResult.Yes;
        bool restartAfterChange = _helperProcess is { HasExited: false };
        SetBusy(true);
        try
        {
            SetStatus(moveFiles ? "헬퍼를 종료하고 설치 파일을 이동하는 중..." : "설치 경로를 변경하는 중...");
            await StopAllLocalHelpersAsync();
            Directory.CreateDirectory(destinationDirectory);

            if (moveFiles)
                await Task.Run(() => MoveInstallContents(sourceDirectory, destinationDirectory));

            _installDirectory = destinationDirectory;
            CopyUpdaterIntoInstallDirectory();
            SaveUpdaterSettings(new UpdaterSettings { InstallDirectory = _installDirectory });

            if (!moveFiles)
            {
                // 경로만 변경하면 이전 폴더의 릴리스 식별자를 새 폴더에 잘못 적용하지 않도록 설치 상태를 다시 판정한다.
                UpdaterState state = LoadState();
                state.InstalledReleaseId = 0;
                state.InstalledAssetId = 0;
                state.InstalledTag = "";
                state.InstalledVersion = GetHelperVersion();
                SaveState(state);
            }

            if (restartAfterChange && File.Exists(HelperPath))
                await LaunchHelperEmbeddedAsync();

            SetStatus(moveFiles
                ? $"설치 위치 이동 완료: {_installDirectory}"
                : $"설치 위치 변경 완료: {_installDirectory}");
        }
        catch (Exception ex)
        {
            _installDirectory = sourceDirectory;
            SaveUpdaterSettings(new UpdaterSettings { InstallDirectory = _installDirectory });
            SetStatus($"설치 경로 변경 실패: {ex.Message}");
            MessageBox.Show(this, ex.Message, "설치 경로 오류", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            if (restartAfterChange && File.Exists(HelperPath))
            {
                try { await LaunchHelperEmbeddedAsync(); } catch { }
            }
        }
        finally
        {
            SetBusy(false);
        }
    }

    private static bool IsSameOrChildPath(string parentPath, string candidatePath)
    {
        string parent = Path.GetFullPath(parentPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string candidate = Path.GetFullPath(candidatePath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return candidate.StartsWith(parent, StringComparison.OrdinalIgnoreCase);
    }

    private static void MoveInstallContents(string sourceDirectory, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);
        string runningUpdaterPath = Path.GetFullPath(Application.ExecutablePath);

        foreach (string sourceSubdirectory in Directory.EnumerateDirectories(sourceDirectory).ToArray())
        {
            if (ShouldSkipInstallMoveDirectory(sourceSubdirectory)) continue;
            string destinationSubdirectory = Path.Combine(destinationDirectory, Path.GetFileName(sourceSubdirectory));
            MoveDirectoryContents(sourceSubdirectory, destinationSubdirectory);
        }

        foreach (string sourceFile in Directory.EnumerateFiles(sourceDirectory).ToArray())
        {
            string destinationFile = Path.Combine(destinationDirectory, Path.GetFileName(sourceFile));
            if (string.Equals(Path.GetFullPath(sourceFile), runningUpdaterPath, StringComparison.OrdinalIgnoreCase))
            {
                // 실행 중인 업데이터는 Windows가 잠글 수 있으므로 새 위치에 복사하고 원본은 다음 정리 때까지 남긴다.
                File.Copy(sourceFile, destinationFile, true);
                continue;
            }

            MoveFileReplacing(sourceFile, destinationFile);
        }
    }

    private static bool ShouldSkipInstallMoveDirectory(string directoryPath)
    {
        // 개발 PC처럼 설치 폴더 안에 소스 프로젝트가 함께 있어도 배포 경로 변경으로 소스까지 이동하지 않는다.
        return Path.GetFileName(directoryPath).Equals("제작", StringComparison.OrdinalIgnoreCase)
            && File.Exists(Path.Combine(directoryPath, "HD2 Helper.csproj"));
    }

    private static void MoveDirectoryContents(string sourceDirectory, string destinationDirectory)
    {
        if (!Directory.Exists(destinationDirectory))
        {
            try
            {
                Directory.Move(sourceDirectory, destinationDirectory);
                return;
            }
            catch (IOException)
            {
                // 다른 드라이브로 옮기는 경우에는 디렉터리 이름 변경이 불가능하므로 파일 단위 이동으로 전환한다.
            }
        }

        Directory.CreateDirectory(destinationDirectory);
        foreach (string childDirectory in Directory.EnumerateDirectories(sourceDirectory).ToArray())
            MoveDirectoryContents(childDirectory, Path.Combine(destinationDirectory, Path.GetFileName(childDirectory)));
        foreach (string sourceFile in Directory.EnumerateFiles(sourceDirectory).ToArray())
            MoveFileReplacing(sourceFile, Path.Combine(destinationDirectory, Path.GetFileName(sourceFile)));
        if (!Directory.EnumerateFileSystemEntries(sourceDirectory).Any()) Directory.Delete(sourceDirectory);
    }

    private static void MoveFileReplacing(string sourceFile, string destinationFile)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);
        try
        {
            File.Move(sourceFile, destinationFile, true);
        }
        catch (IOException)
        {
            File.Copy(sourceFile, destinationFile, true);
            File.Delete(sourceFile);
        }
    }

    private static int CompareFileVersions(string leftPath, string rightPath)
    {
        Version.TryParse(FileVersionInfo.GetVersionInfo(leftPath).FileVersion, out Version? leftVersion);
        Version.TryParse(FileVersionInfo.GetVersionInfo(rightPath).FileVersion, out Version? rightVersion);
        return (leftVersion ?? new Version()).CompareTo(rightVersion ?? new Version());
    }

    private static UpdaterSettings LoadUpdaterSettings()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return new UpdaterSettings();
            return JsonSerializer.Deserialize<UpdaterSettings>(File.ReadAllText(SettingsPath)) ?? new UpdaterSettings();
        }
        catch
        {
            return new UpdaterSettings();
        }
    }

    private static void SaveUpdaterSettings(UpdaterSettings settings)
    {
        Directory.CreateDirectory(UpdaterDataDirectory);
        string temporaryPath = SettingsPath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporaryPath, SettingsPath, true);
    }

    private async Task RefreshUpdateStatusAsync(bool interactive)
    {
        if (!await _updateGate.WaitAsync(0)) return;

        try
        {
            SetBusy(true);
            SetProgress(0);
            SetStatus("GitHub Releases에서 버전 목록을 확인하는 중...");
            UpdateHeaderHelperVersionLabel();

            GitHubReleaseCatalog catalog = await GetReleaseCatalogAsync();
            _lastReleases = catalog.StableHelperReleases;
            _latestUpdaterRelease = catalog.LatestUpdater;
            GitHubReleaseInfo? latest = _lastReleases.FirstOrDefault();
            UpdaterState state = LoadState();
            bool hasUnreadHistory = _lastReleases.Any(release => release.ReleaseId > state.LastReadUpdateReleaseId)
                || (_latestUpdaterRelease?.ReleaseId ?? 0) > state.LastReadUpdateReleaseId;
            state.InstalledUpdaterVersion = Application.ProductVersion;
            state.LatestUpdaterVersion = _latestUpdaterRelease?.Version.ToString() ?? state.LatestUpdaterVersion;
            if (latest == null)
            {
                SaveState(state);
                SetNewVersionAvailable(false);
                SetUpdateHistoryAvailable(hasUnreadHistory);
                bool updaterUpdateAvailable = _latestUpdaterRelease != null && IsUpdaterUpdateAvailable(_latestUpdaterRelease);
                SetUpdaterUpdateAvailable(updaterUpdateAvailable);
                SetStatus(updaterUpdateAvailable
                    ? $"업데이터 {_latestUpdaterRelease!.Version} 업데이트를 준비합니다."
                    : "GitHub 릴리스에 HD2.Helper.zip 패키지가 아직 없습니다.");
                return;
            }

            bool hasNewVersion = IsNewReleaseAvailable(latest, state);
            bool hasUpdaterUpdate = _latestUpdaterRelease != null && IsUpdaterUpdateAvailable(_latestUpdaterRelease);
            if (state.LatestReleaseId != latest.ReleaseId || state.LatestAssetId != latest.AssetId)
            {
                state.LatestReleaseId = latest.ReleaseId;
                state.LatestAssetId = latest.AssetId;
            }
            SaveState(state);
            SetNewVersionAvailable(hasNewVersion);
            SetUpdateHistoryAvailable(hasUnreadHistory);
            SetUpdaterUpdateAvailable(hasUpdaterUpdate);
            SetStatus(hasUpdaterUpdate
                ? $"업데이터 {Application.ProductVersion} → {_latestUpdaterRelease!.Version} 업데이트를 준비합니다."
                : hasNewVersion
                    ? $"새 헬퍼 버전이 있습니다. 업데이터 {Application.ProductVersion}"
                    : $"최신 상태입니다. 헬퍼 {GetHelperVersion()} • 업데이터 {Application.ProductVersion}");
        }
        catch (Exception ex)
        {
            SetStatus($"버전 확인 실패: {ex.Message}");
            if (interactive)
                MessageBox.Show(this, ex.Message, "버전 확인 오류", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            SetProgress(0);
            SetBusy(false);
            _updateGate.Release();
        }
    }

    private async Task ShowVersionSelectionMenuAsync()
    {
        if (_busy) return;

        SetBusy(true);
        SetStatus("사용 가능한 버전 목록을 불러오는 중...");
        try
        {
            GitHubReleaseInfo[] testReleaseSnapshot = Array.Empty<GitHubReleaseInfo>();
            try
            {
                GitHubReleaseCatalog catalog = await GetReleaseCatalogAsync();
                _lastReleases = catalog.StableHelperReleases;
                testReleaseSnapshot = catalog.TestHelperReleases.ToArray();
                _latestUpdaterRelease = catalog.LatestUpdater;
                GitHubReleaseInfo? latest = _lastReleases.FirstOrDefault();
                UpdaterState state = LoadState();
                SetNewVersionAvailable(latest != null && IsNewReleaseAvailable(latest, state));
                SetUpdateHistoryAvailable(_lastReleases.Any(release => release.ReleaseId > state.LastReadUpdateReleaseId)
                    || (_latestUpdaterRelease?.ReleaseId ?? 0) > state.LastReadUpdateReleaseId);
                SetUpdaterUpdateAvailable(_latestUpdaterRelease != null && IsUpdaterUpdateAvailable(_latestUpdaterRelease));
            }
            catch (Exception ex)
            {
                // 네트워크가 끊겨도 설치 폴더에 보관된 이전 버전은 선택할 수 있게 한다.
                _lastReleases.Clear();
                SetStatus($"온라인 버전 확인 실패. 로컬 버전만 표시합니다: {ex.Message}");
            }

            GitHubReleaseInfo[] releaseSnapshot = _lastReleases.ToArray();
            List<VersionChoice> onlineChoices = await Task.Run(() => BuildOnlineVersionChoices(releaseSnapshot));
            List<VersionChoice> testChoices = IsTestChannelUnlocked()
                ? await Task.Run(() => BuildOnlineVersionChoices(testReleaseSnapshot))
                : new List<VersionChoice>();
            List<VersionChoice> localChoices = await Task.Run(BuildLocalVersionChoices);
            if (onlineChoices.Count == 0 && localChoices.Count == 0)
            {
                MessageBox.Show(this, "선택할 수 있는 버전이 없습니다.", "버전 선택", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var menu = new ContextMenuStrip
            {
                ShowImageMargin = false,
                BackColor = Color.FromArgb(30, 30, 30),
                ForeColor = Color.WhiteSmoke,
                Font = new Font("Segoe UI", 9),
                Renderer = new ToolStripProfessionalRenderer(new DarkMenuColorTable())
            };

            foreach (VersionChoice choice in onlineChoices)
            {
                menu.Items.Add(CreateVersionChoiceMenuItem(choice));
            }

            AddTestChannelMenuItems(menu, testChoices);

            if (onlineChoices.Count > 0) menu.Items.Add(new ToolStripSeparator());
            var localMenu = new ToolStripMenuItem("로컬 저장 버전 보기")
            {
                AutoSize = false,
                Size = new Size(310, 30),
                BackColor = Color.FromArgb(30, 30, 30),
                ForeColor = localChoices.Count > 0 ? Color.Gainsboro : Color.DimGray,
                Enabled = localChoices.Count > 0
            };
            // 이전 버전 ZIP과 현재 설치 패키지는 별도 하위 메뉴를 열었을 때만 표시해 온라인 목록을 간결하게 유지한다.
            foreach (VersionChoice choice in localChoices)
                localMenu.DropDownItems.Add(CreateVersionChoiceMenuItem(choice));
            menu.Items.Add(localMenu);

            // WinForms의 메뉴 필터가 Closed 이벤트 뒤에도 드롭다운을 참조하므로 다음 메시지 루프에서 폐기한다.
            menu.Closed += (_, _) =>
            {
                if (!IsHandleCreated || IsDisposed || Disposing) return;
                BeginInvoke(new Action(() =>
                {
                    if (!menu.IsDisposed) menu.Dispose();
                }));
            };
            menu.Show(_checkButton, new Point(_checkButton.Width - 310, _checkButton.Height + 3));
            SetStatus($"온라인 버전 {onlineChoices.Count}개를 최신순으로 표시했습니다.");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task ShowUpdateHistoryAsync()
    {
        if (_busy) return;

        try
        {
            SetBusy(true);
            SetStatus("GitHub Releases에서 업데이트 내역을 불러오는 중...");
            List<UpdateHistoryItem> history = await GetUpdateHistoryAsync();
            UpdaterState state = LoadState();
            long lastReadReleaseId = state.LastReadUpdateReleaseId;

            using Form dialog = CreateUpdateHistoryDialog(history, lastReadReleaseId);
            dialog.ShowDialog(this);
            long newestReleaseId = history.Count == 0 ? lastReadReleaseId : Math.Max(lastReadReleaseId, history.Max(item => item.ReleaseId));
            if (newestReleaseId != lastReadReleaseId)
            {
                state.LastReadUpdateReleaseId = newestReleaseId;
                SaveState(state);
            }
            SetUpdateHistoryAvailable(false);
            SetStatus($"업데이트 내역 {history.Count}개를 표시했습니다.");
        }
        catch (Exception ex)
        {
            SetStatus($"업데이트 내역을 불러오지 못했습니다: {ex.Message}");
            MessageBox.Show(this, ex.Message, "업데이트 내역 오류", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task<List<UpdateHistoryItem>> GetUpdateHistoryAsync()
    {
        var history = new List<UpdateHistoryItem>();
        bool includeTestReleases = IsTestChannelUnlocked();
        using JsonDocument helperDocument = await GetGitHubJsonAsync(HelperReleasesApiUrl);
        foreach (JsonElement releaseElement in helperDocument.RootElement.EnumerateArray())
            AddUpdateHistoryItem(releaseElement, history, includeTestReleases);

        try
        {
            // 목록 API의 반영이 늦어도 최신 헬퍼 릴리스의 변경 내역은 바로 볼 수 있게 병합한다.
            using JsonDocument latestDocument = await GetGitHubJsonAsync(HelperLatestReleaseApiUrl);
            AddUpdateHistoryItem(latestDocument.RootElement, history, includeTestReleases);
        }
        catch (HttpRequestException)
        {
            // 최신 항목만 조회하지 못해도 기존 릴리스 내역은 그대로 표시한다.
        }

        using JsonDocument updaterDocument = await GetGitHubJsonAsync(UpdaterReleasesApiUrl);
        foreach (JsonElement releaseElement in updaterDocument.RootElement.EnumerateArray())
            AddUpdateHistoryItem(releaseElement, history, includeTestReleases);

        try
        {
            using JsonDocument latestDocument = await GetGitHubJsonAsync(UpdaterLatestReleaseApiUrl);
            AddUpdateHistoryItem(latestDocument.RootElement, history, includeTestReleases);
        }
        catch (HttpRequestException)
        {
            // 업데이터 저장소의 latest 조회가 늦어도 목록 API 결과는 그대로 사용한다.
        }

        return history
            .OrderByDescending(item => item.PublishedAtUtc)
            .ThenByDescending(item => item.ReleaseId)
            .ToList();
    }

    private static void AddUpdateHistoryItem(JsonElement releaseElement, List<UpdateHistoryItem> history, bool includeTestReleases)
    {
        if (releaseElement.GetProperty("draft").GetBoolean()) return;
        if (!includeTestReleases
            && releaseElement.TryGetProperty("prerelease", out JsonElement prerelease)
            && prerelease.GetBoolean()) return;
        long releaseId = releaseElement.GetProperty("id").GetInt64();
        if (history.Any(item => item.ReleaseId == releaseId)) return;

        string tag = releaseElement.GetProperty("tag_name").GetString() ?? "알 수 없는 버전";
        string title = releaseElement.GetProperty("name").GetString() ?? tag;
        string body = releaseElement.TryGetProperty("body", out JsonElement bodyElement)
            ? bodyElement.GetString() ?? ""
            : "";
        DateTime published = releaseElement.TryGetProperty("published_at", out JsonElement publishedElement)
            && publishedElement.ValueKind == JsonValueKind.String
            && publishedElement.TryGetDateTime(out DateTime parsedPublished)
                ? parsedPublished.ToUniversalTime()
                : DateTime.MinValue;
        bool isUpdater = tag.StartsWith(UpdaterReleaseTagPrefix, StringComparison.OrdinalIgnoreCase);
        history.Add(new UpdateHistoryItem(releaseId, tag, title, published, isUpdater, body.Trim()));
    }

    private static Form CreateUpdateHistoryDialog(IReadOnlyList<UpdateHistoryItem> history, long lastReadReleaseId)
    {
        var dialog = new Form
        {
            Text = "업데이트 내역",
            StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(760, 560),
            MinimumSize = new Size(620, 420),
            BackColor = Color.FromArgb(24, 24, 24),
            ForeColor = Color.WhiteSmoke,
            FormBorderStyle = FormBorderStyle.Sizable,
            MaximizeBox = true,
            MinimizeBox = false,
            ShowInTaskbar = false
        };
        var title = new Label
        {
            Text = "업데이트 내역",
            Dock = DockStyle.Top,
            Height = 42,
            Padding = new Padding(14, 10, 0, 0),
            Font = new Font("Segoe UI", 11, FontStyle.Bold),
            ForeColor = Color.FromArgb(255, 216, 0),
            BackColor = Color.FromArgb(32, 32, 32)
        };
        var contents = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            BackColor = Color.FromArgb(24, 24, 24),
            ForeColor = Color.Gainsboro,
            Font = new Font("Segoe UI", 10),
            DetectUrls = true,
            ScrollBars = RichTextBoxScrollBars.Vertical,
            Margin = new Padding(12),
            Padding = new Padding(12)
        };

        if (history.Count == 0)
        {
            contents.Text = "표시할 공개 릴리스가 없습니다.";
        }
        else
        {
            foreach (UpdateHistoryItem item in history)
            {
                string category = item.IsUpdater ? "업데이터" : "헬퍼";
                bool isUnread = item.ReleaseId > lastReadReleaseId;
                contents.SelectionColor = isUnread
                    ? Color.FromArgb(76, 220, 112)
                    : item.IsUpdater ? Color.FromArgb(125, 200, 255) : Color.FromArgb(255, 216, 0);
                contents.SelectionFont = new Font(contents.Font, FontStyle.Bold);
                contents.AppendText($"[{(isUnread ? "NEW · " : "확인함 · ")}{category}] {item.Title}\n");
                contents.SelectionColor = Color.Silver;
                contents.SelectionFont = new Font(contents.Font, FontStyle.Regular);
                contents.AppendText($"{item.TagName}  •  {item.PublishedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm}\n");
                contents.SelectionColor = Color.Gainsboro;
                contents.AppendText(string.IsNullOrWhiteSpace(item.Body) ? "등록된 변경 내역이 없습니다." : item.Body);
                contents.AppendText("\n\n");
            }
        }

        // 내용 생성 뒤 커서를 처음으로 옮겨 창을 열 때 항상 최신 항목부터 보이게 한다.
        contents.Select(0, 0);
        dialog.Controls.Add(contents);
        dialog.Controls.Add(title);
        return dialog;
    }

    private ToolStripMenuItem CreateVersionChoiceMenuItem(VersionChoice choice)
    {
        var item = new ToolStripMenuItem(choice.DisplayText)
        {
            AutoSize = false,
            Size = new Size(310, 30),
            ForeColor = choice.IsLatest ? Color.FromArgb(76, 220, 112) : Color.WhiteSmoke,
            BackColor = Color.FromArgb(30, 30, 30),
            Font = new Font("Segoe UI", 9, choice.IsInstalled ? FontStyle.Bold : FontStyle.Regular)
        };
        item.Click += async (_, _) => await ApplySelectedVersionAsync(choice);
        return item;
    }

    private void AddTestChannelMenuItems(ContextMenuStrip menu, IReadOnlyList<VersionChoice> testChoices)
    {
        menu.Items.Add(new ToolStripSeparator());
        if (!IsTestChannelUnlocked())
        {
            var unlockItem = new ToolStripMenuItem("테스트 코드 입력...")
            {
                AutoSize = false,
                Size = new Size(310, 30),
                ForeColor = Color.FromArgb(255, 216, 0),
                BackColor = Color.FromArgb(30, 30, 30)
            };
            unlockItem.Click += (_, _) =>
            {
                if (ShowTestChannelCodeDialog())
                    SetStatus("테스트 채널이 해제되었습니다. 버전 선택을 다시 열어주세요.");
            };
            menu.Items.Add(unlockItem);
            return;
        }

        var testMenu = new ToolStripMenuItem("테스트 버전")
        {
            AutoSize = false,
            Size = new Size(310, 30),
            ForeColor = Color.FromArgb(255, 216, 0),
            BackColor = Color.FromArgb(30, 30, 30),
            Enabled = testChoices.Count > 0
        };
        foreach (VersionChoice choice in testChoices)
            testMenu.DropDownItems.Add(CreateVersionChoiceMenuItem(choice));

        if (testChoices.Count > 0)
            testMenu.DropDownItems.Add(new ToolStripSeparator());

        var lockItem = new ToolStripMenuItem("테스트 채널 해제");
        lockItem.Click += (_, _) =>
        {
            UpdaterSettings settings = LoadUpdaterSettings();
            settings.TestChannelUnlocked = false;
            SaveUpdaterSettings(settings);
            SetStatus("테스트 채널을 해제했습니다.");
        };
        testMenu.DropDownItems.Add(lockItem);
        menu.Items.Add(testMenu);
    }

    private static bool IsTestChannelUnlocked() => LoadUpdaterSettings().TestChannelUnlocked;

    private static bool IsTestChannelCodeValid(string value)
    {
        string normalized = value.Trim().ToUpperInvariant();
        if (normalized.Length == 0) return false;

        byte[] actualHash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        byte[] expectedHash = Convert.FromHexString(TestChannelCodeHash);
        return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
    }

    private bool ShowTestChannelCodeDialog()
    {
        using var dialog = new Form
        {
            Text = "테스트 채널",
            StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(360, 155),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
            BackColor = Color.FromArgb(28, 28, 28),
            ForeColor = Color.WhiteSmoke
        };
        var label = new Label
        {
            Text = "테스트 코드",
            Location = new Point(18, 20),
            Size = new Size(320, 22),
            ForeColor = Color.Gainsboro
        };
        var input = new TextBox
        {
            Location = new Point(18, 48),
            Size = new Size(324, 27),
            UseSystemPasswordChar = true,
            BackColor = Color.FromArgb(18, 18, 18),
            ForeColor = Color.WhiteSmoke,
            BorderStyle = BorderStyle.FixedSingle
        };
        var applyButton = new Button
        {
            Text = "확인",
            DialogResult = DialogResult.OK,
            Location = new Point(184, 100),
            Size = new Size(76, 30)
        };
        var cancelButton = new Button
        {
            Text = "취소",
            DialogResult = DialogResult.Cancel,
            Location = new Point(266, 100),
            Size = new Size(76, 30)
        };
        dialog.AcceptButton = applyButton;
        dialog.CancelButton = cancelButton;
        dialog.Controls.AddRange(new Control[] { label, input, applyButton, cancelButton });

        if (dialog.ShowDialog(this) != DialogResult.OK)
            return false;

        if (!IsTestChannelCodeValid(input.Text))
        {
            MessageBox.Show(this, "테스트 코드가 맞지 않습니다.", "테스트 채널", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        UpdaterSettings settings = LoadUpdaterSettings();
        settings.TestChannelUnlocked = true;
        SaveUpdaterSettings(settings);
        return true;
    }

    private List<VersionChoice> BuildOnlineVersionChoices(IReadOnlyList<GitHubReleaseInfo> releases)
    {
        var choices = new List<VersionChoice>();
        UpdaterState state = LoadState();

        for (int index = 0; index < releases.Count; index++)
        {
            GitHubReleaseInfo release = releases[index];
            bool installed = IsReleaseInstalled(release, state);
            string releaseName = string.IsNullOrWhiteSpace(release.DisplayName)
                ? release.TagName
                : release.DisplayName;
            string prerelease = release.Prerelease ? "  •  시험판" : "";
            choices.Add(new VersionChoice(
                $"{releaseName}  •  {release.PublishedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm}{prerelease}" + (installed ? "  •  사용 중" : ""),
                null,
                release,
                IsLatest: index == 0,
                IsInstalled: installed));
        }

        return choices;
    }

    private List<VersionChoice> BuildLocalVersionChoices()
    {
        var choices = new List<VersionChoice>();
        UpdaterState state = LoadState();
        bool hasInstalledRemote = _lastReleases.Any(release => IsReleaseInstalled(release, state));

        string currentPackage = Path.Combine(_installDirectory, PackageFileName);
        if (File.Exists(currentPackage) && !hasInstalledRemote)
        {
            choices.Add(new VersionChoice(
                $"v{GetHelperVersion()}  •  현재 설치됨 (로컬)",
                currentPackage,
                null,
                IsLatest: false,
                IsInstalled: true));
        }

        string previousDirectory = Path.Combine(_installDirectory, "이전버전");
        if (Directory.Exists(previousDirectory))
        {
            foreach (FileInfo archive in new DirectoryInfo(previousDirectory).GetFiles("*.zip")
                         .OrderByDescending(file => file.LastWriteTimeUtc).Take(20))
            {
                choices.Add(new VersionChoice(
                    $"이전 버전  •  {archive.LastWriteTime:yyyy-MM-dd HH:mm}  •  로컬 보관",
                    archive.FullName,
                    null,
                    IsLatest: false,
                    IsInstalled: false));
            }
        }

        return choices;
    }

    private async Task ApplySelectedVersionAsync(VersionChoice choice)
    {
        if (choice.IsInstalled)
        {
            SetStatus("이미 사용 중인 버전입니다.");
            return;
        }

        DialogResult confirmation = MessageBox.Show(
            this,
            $"다음 버전을 설치하시겠습니까?\n\n{choice.DisplayText}",
            "버전 설치",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);
        if (confirmation != DialogResult.Yes) return;
        if (!await _updateGate.WaitAsync(0)) return;

        try
        {
            SetBusy(true);
            SetProgress(0);
            if (choice.Remote != null)
                await DownloadAndApplyUpdateAsync(choice.Remote);
            else if (!string.IsNullOrWhiteSpace(choice.PackagePath))
                await ApplyLocalPackageAsync(choice.PackagePath);
            else
                throw new InvalidOperationException("선택한 버전의 패키지 경로를 찾지 못했습니다.");

            SetProgress(98);
            SetStatus("선택한 버전 설치 완료. 헬퍼를 다시 시작합니다...");
            await LaunchHelperEmbeddedAsync();
            SetProgress(100);
            SetStatus($"헬퍼 {GetHelperVersion()} 업데이트 완료");
            await Task.Delay(350);
        }
        catch (Exception ex)
        {
            SetStatus($"버전 설치 실패: {ex.Message}");
            MessageBox.Show(this, ex.Message, "버전 설치 오류", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            SetProgress(0);
            SetBusy(false);
            _updateGate.Release();
        }

        await RefreshUpdateStatusAsync(interactive: false);
    }

    private async Task<GitHubReleaseCatalog> GetReleaseCatalogAsync()
    {
        var releases = new List<GitHubReleaseInfo>();
        var updaterReleases = new List<GitHubUpdaterInfo>();
        using JsonDocument helperDocument = await GetGitHubJsonAsync(HelperReleasesApiUrl);
        foreach (JsonElement releaseElement in helperDocument.RootElement.EnumerateArray())
            AddReleaseToCatalog(releaseElement, releases, updaterReleases);

        try
        {
            // GitHub의 목록 API가 새 릴리스를 늦게 반영하는 경우가 있어 latest 응답을 별도로 합친다.
            using JsonDocument latestDocument = await GetGitHubJsonAsync(HelperLatestReleaseApiUrl);
            AddReleaseToCatalog(latestDocument.RootElement, releases, updaterReleases);
        }
        catch (HttpRequestException)
        {
            // latest 조회만 실패한 경우에도 전체 목록에서 받은 이전 버전은 계속 사용할 수 있게 한다.
        }

        using JsonDocument updaterDocument = await GetGitHubJsonAsync(UpdaterReleasesApiUrl);
        foreach (JsonElement releaseElement in updaterDocument.RootElement.EnumerateArray())
            AddReleaseToCatalog(releaseElement, releases, updaterReleases);

        try
        {
            using JsonDocument latestDocument = await GetGitHubJsonAsync(UpdaterLatestReleaseApiUrl);
            AddReleaseToCatalog(latestDocument.RootElement, releases, updaterReleases);
        }
        catch (HttpRequestException)
        {
            // 전용 업데이터 저장소의 latest 조회 실패는 전체 목록을 막지 않는다.
        }

        // GitHub의 생성 순서와 무관하게 의미 버전이 높은 릴리스를 항상 목록 맨 위에 둔다.
        List<GitHubReleaseInfo> sortedReleases = releases
            .OrderByDescending(release => release.SortVersion)
            .ThenByDescending(release => release.PublishedAtUtc)
            .ToList();
        // 시험판은 기본 최신 버전/자동 갱신 판단에서 완전히 제외하고, 테스트 채널을 해제한 경우에만 메뉴에 별도로 제공한다.
        List<GitHubReleaseInfo> stableReleases = sortedReleases
            .Where(release => !release.Prerelease)
            .ToList();
        List<GitHubReleaseInfo> testReleases = sortedReleases
            .Where(release => release.Prerelease)
            .ToList();
        GitHubUpdaterInfo? latestUpdater = updaterReleases
            .OrderByDescending(updater => updater.Version)
            .FirstOrDefault();
        return new GitHubReleaseCatalog(stableReleases, testReleases, latestUpdater);
    }

    private static async Task<JsonDocument> GetGitHubJsonAsync(string apiUrl)
    {
        lock (GitHubApiCacheLock)
        {
            if (GitHubApiCache.TryGetValue(apiUrl, out GitHubApiCacheEntry? cached)
                && cached.ExpiresAtUtc > DateTimeOffset.UtcNow)
            {
                return JsonDocument.Parse(cached.Json);
            }
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, apiUrl);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        using HttpResponseMessage response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

        if (response.StatusCode == HttpStatusCode.Forbidden
            && response.Headers.TryGetValues("X-RateLimit-Remaining", out IEnumerable<string>? remainingValues)
            && remainingValues.FirstOrDefault() == "0")
        {
            string retryMessage = "잠시 뒤 다시 시도하세요.";
            if (response.Headers.TryGetValues("X-RateLimit-Reset", out IEnumerable<string>? resetValues)
                && long.TryParse(resetValues.FirstOrDefault(), out long resetUnixTime))
            {
                DateTimeOffset resetAt = DateTimeOffset.FromUnixTimeSeconds(resetUnixTime).ToLocalTime();
                retryMessage = $"{resetAt:HH:mm} 이후 다시 시도하세요.";
            }

            throw new InvalidOperationException($"GitHub 공개 API 요청 한도에 도달했습니다. {retryMessage}");
        }

        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync();
        lock (GitHubApiCacheLock)
        {
            GitHubApiCache[apiUrl] = new GitHubApiCacheEntry(
                json,
                DateTimeOffset.UtcNow.Add(GitHubApiCacheLifetime));
        }

        return JsonDocument.Parse(json);
    }

    private static void AddReleaseToCatalog(
        JsonElement releaseElement,
        List<GitHubReleaseInfo> releases,
        List<GitHubUpdaterInfo> updaterReleases)
    {
        if (releaseElement.GetProperty("draft").GetBoolean()) return;
        string tag = releaseElement.GetProperty("tag_name").GetString() ?? "알 수 없는 버전";
        JsonElement? packageAsset = null;
        foreach (JsonElement asset in releaseElement.GetProperty("assets").EnumerateArray())
        {
            string assetName = asset.GetProperty("name").GetString() ?? "";
            if (assetName.Equals(PackageFileName, StringComparison.OrdinalIgnoreCase)) packageAsset = asset;
            if (assetName.Equals(UpdaterAssetFileName, StringComparison.OrdinalIgnoreCase)
                && TryParseUpdaterReleaseVersion(tag, out Version? updaterVersion))
            {
                long updaterAssetId = asset.GetProperty("id").GetInt64();
                if (updaterReleases.All(release => release.AssetId != updaterAssetId))
                    updaterReleases.Add(new GitHubUpdaterInfo(
                        releaseElement.GetProperty("id").GetInt64(),
                        updaterVersion!,
                        updaterAssetId,
                        asset.GetProperty("size").GetInt64(),
                        asset.GetProperty("browser_download_url").GetString() ?? ""));
            }
        }

        if (packageAsset == null) return;
        long releaseId = releaseElement.GetProperty("id").GetInt64();
        if (releases.Any(release => release.ReleaseId == releaseId)) return;

        string name = releaseElement.GetProperty("name").GetString() ?? tag;
        DateTime published = releaseElement.TryGetProperty("published_at", out JsonElement publishedElement)
            && publishedElement.ValueKind == JsonValueKind.String
            && publishedElement.TryGetDateTime(out DateTime parsedPublished)
                ? parsedPublished.ToUniversalTime()
                : DateTime.MinValue;
        JsonElement assetElement = packageAsset.Value;
        releases.Add(new GitHubReleaseInfo(
            releaseId,
            tag,
            name,
            published,
            releaseElement.GetProperty("prerelease").GetBoolean(),
            assetElement.GetProperty("id").GetInt64(),
            assetElement.GetProperty("size").GetInt64(),
            assetElement.GetProperty("browser_download_url").GetString() ?? "",
            ParseReleaseVersion(tag)));
    }

    private static bool TryParseUpdaterReleaseVersion(string tagName, out Version? version)
    {
        version = null;
        if (!tagName.StartsWith(UpdaterReleaseTagPrefix, StringComparison.OrdinalIgnoreCase)) return false;

        string value = tagName[UpdaterReleaseTagPrefix.Length..];
        return Version.TryParse(value, out version);
    }

    private static bool IsUpdaterUpdateAvailable(GitHubUpdaterInfo release) =>
        NormalizeVersion(ParseReleaseVersion(Application.ProductVersion)) < NormalizeVersion(release.Version);

    private static Version NormalizeVersion(Version version) => new(
        version.Major,
        Math.Max(version.Minor, 0),
        Math.Max(version.Build, 0),
        Math.Max(version.Revision, 0));

    private async Task ApplyUpdaterUpdateManuallyAsync()
    {
        if (_busy) return;

        try
        {
            // 버튼을 누른 시점에 한 번 더 조회해, 오래 열린 업데이터가 예전 릴리스를 받지 않게 한다.
            GitHubReleaseCatalog catalog = await GetReleaseCatalogAsync();
            _latestUpdaterRelease = catalog.LatestUpdater;
        }
        catch (Exception ex)
        {
            SetStatus($"업데이터 버전 확인 실패: {ex.Message}");
            MessageBox.Show(this, ex.Message, "업데이터 버전 확인 오류", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        GitHubUpdaterInfo? release = _latestUpdaterRelease;
        if (release == null || !IsUpdaterUpdateAvailable(release))
        {
            SetStatus("업데이터가 최신 상태입니다.");
            MessageBox.Show(this, "설치된 업데이터가 최신 버전입니다.", "업데이터 갱신", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        DialogResult confirmation = MessageBox.Show(
            this,
            $"업데이터를 {Application.ProductVersion}에서 {release.Version}으로 업데이트할까요?\n\n업데이트 후 업데이터와 헬퍼가 다시 시작됩니다.",
            "업데이터 갱신",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);
        if (confirmation != DialogResult.Yes) return;

        await TryApplyUpdaterSelfUpdateAsync();
    }

    private async Task<bool> TryApplyUpdaterSelfUpdateAsync()
    {
        GitHubUpdaterInfo? release = _latestUpdaterRelease;
        if (release == null || !IsUpdaterUpdateAvailable(release)) return false;
        if (!await _updateGate.WaitAsync(0)) return false;

        string workRoot = Path.Combine(Path.GetTempPath(), "HD2HelperUpdaterSelf", Guid.NewGuid().ToString("N"));
        string downloadedUpdater = Path.Combine(workRoot, UpdaterAssetFileName);
        bool bootstrapStarted = false;
        try
        {
            SetBusy(true);
            Directory.CreateDirectory(workRoot);
            SetProgress(2);
            SetStatus($"업데이터 {release.Version} 다운로드 준비 중...");
            await DownloadFileWithProgressAsync(downloadedUpdater, release.AssetSize, release.AssetDownloadUrl, "업데이터", 3, 82);

            SetProgress(86);
            SetStatus("새 업데이터 파일을 검증하는 중...");
            Version downloadedVersion = ParseReleaseVersion(GetFileVersion(downloadedUpdater));
            if (NormalizeVersion(downloadedVersion) != NormalizeVersion(release.Version))
                throw new InvalidDataException($"업데이터 버전이 일치하지 않습니다. 파일 {downloadedVersion}, 릴리스 {release.Version}");

            string currentUpdaterPath = Path.GetFullPath(Application.ExecutablePath);
            string installedUpdaterPath = Path.GetFullPath(Path.Combine(_installDirectory, UpdaterFileName));
            var bootstrapInfo = new ProcessStartInfo
            {
                FileName = downloadedUpdater,
                UseShellExecute = true
            };
            bootstrapInfo.ArgumentList.Add("--self-update-bootstrap");
            bootstrapInfo.ArgumentList.Add($"--parent-pid={Environment.ProcessId}");
            bootstrapInfo.ArgumentList.Add($"--launch-target={installedUpdaterPath}");
            bootstrapInfo.ArgumentList.Add($"--replace-target={installedUpdaterPath}");
            if (!string.Equals(currentUpdaterPath, installedUpdaterPath, StringComparison.OrdinalIgnoreCase))
                bootstrapInfo.ArgumentList.Add($"--replace-target={currentUpdaterPath}");

            SetProgress(94);
            SetStatus($"업데이터 {Application.ProductVersion} → {release.Version} 교체 준비 완료...");
            _ = Process.Start(bootstrapInfo) ?? throw new InvalidOperationException("새 업데이터 교체 프로세스를 시작하지 못했습니다.");
            bootstrapStarted = true;

            UpdaterState state = LoadState();
            state.LatestUpdaterVersion = release.Version.ToString();
            SaveState(state);
            SetProgress(100);
            SetStatus("업데이터를 교체하고 다시 시작합니다...");
            await Task.Delay(300);
            // 자동 재시작은 사용자의 닫기 요청이 아니므로 업데이트 중 종료 방지 상태를 먼저 해제한다.
            SetBusy(false);
            Close();
            return true;
        }
        catch (Exception ex)
        {
            SetProgress(0);
            SetStatus($"업데이터 자체 업데이트 실패: {ex.Message}");
            MessageBox.Show(this, ex.Message, "업데이터 업데이트 오류", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        finally
        {
            if (!bootstrapStarted)
            {
                try { Directory.Delete(workRoot, true); } catch { }
            }
            SetBusy(false);
            _updateGate.Release();
        }
    }

    private bool IsNewReleaseAvailable(GitHubReleaseInfo latest, UpdaterState state)
    {
        if (!File.Exists(HelperPath)) return true;
        if (state.InstalledReleaseId > 0)
            return state.InstalledReleaseId != latest.ReleaseId || state.InstalledAssetId != latest.AssetId;

        return ParseReleaseVersion(GetHelperVersion()) < latest.SortVersion;
    }

    private bool IsReleaseInstalled(GitHubReleaseInfo release, UpdaterState state)
    {
        if (state.InstalledReleaseId > 0)
            return state.InstalledReleaseId == release.ReleaseId && state.InstalledAssetId == release.AssetId;
        return ParseReleaseVersion(GetHelperVersion()) == release.SortVersion;
    }

    private static Version ParseReleaseVersion(string value)
    {
        string normalized = value.Trim().TrimStart('v', 'V');
        int suffix = normalized.IndexOfAny(['-', '+']);
        if (suffix >= 0) normalized = normalized[..suffix];
        return Version.TryParse(normalized, out Version? version) ? version : new Version(0, 0);
    }

    // ProductVersion can include a build hash after '+', but the header only needs the user-facing semantic version.
    private static string GetUpdaterDisplayVersion()
    {
        Version version = ParseReleaseVersion(Application.ProductVersion);
        return version.Build >= 0 ? version.ToString(3) : version.ToString(2);
    }

    private async Task DownloadAndApplyUpdateAsync(GitHubReleaseInfo release)
    {
        string workRoot = Path.Combine(Path.GetTempPath(), "HD2HelperUpdater", Guid.NewGuid().ToString("N"));
        string packagePath = Path.Combine(workRoot, PackageFileName);
        string stageDirectory = Path.Combine(workRoot, "stage");
        string backupDirectory = Path.Combine(workRoot, "backup");
        Directory.CreateDirectory(stageDirectory);
        Directory.CreateDirectory(backupDirectory);

        try
        {
            SetProgress(2);
            await DownloadFileWithProgressAsync(packagePath, release.AssetSize, release.AssetDownloadUrl, "헬퍼 패키지", 3, 68);
            SetProgress(72);
            SetStatus("헬퍼 패키지 무결성을 검증하는 중...");
            string packageHash;
            await using (var packageStream = File.OpenRead(packagePath))
                packageHash = Convert.ToHexString(await SHA256.HashDataAsync(packageStream));
            SetProgress(77);
            SetStatus($"헬퍼 패키지 압축 해제 중... SHA-256 {packageHash[..12]}");
            await Task.Run(() => ExtractPackageSafely(packagePath, stageDirectory));

            SetProgress(84);
            SetStatus("헬퍼 패키지 구성 확인 중...");
            ValidateStagedPackage(stageDirectory);
            SetProgress(88);
            await StopHelpersIfExecutableChangesAsync(stageDirectory);
            SetProgress(91);
            SetStatus("새 헬퍼 파일을 적용하는 중...");
            await Task.Run(() => ApplyStagedPackage(stageDirectory, backupDirectory));
            SetProgress(96);
            SetStatus("이전 버전을 보관하고 설치 정보를 기록하는 중...");
            PreservePreviousPackage();
            File.Copy(packagePath, Path.Combine(_installDirectory, PackageFileName), true);

            UpdaterState state = LoadState();
            state.InstalledVersion = GetFileVersion(Path.Combine(stageDirectory, HelperFileName));
            state.PackageSha256 = packageHash;
            state.InstalledPackageSha256 = packageHash;
            state.InstalledReleaseId = release.ReleaseId;
            state.InstalledAssetId = release.AssetId;
            state.InstalledTag = release.TagName;
            GitHubReleaseInfo? latest = _lastReleases.FirstOrDefault();
            if (latest != null)
            {
                state.LatestReleaseId = latest.ReleaseId;
                state.LatestAssetId = latest.AssetId;
            }
            SaveState(state);
            SetProgress(98);
        }
        finally
        {
            try { Directory.Delete(workRoot, true); } catch { }
        }
    }

    private async Task ApplyLocalPackageAsync(string sourcePackagePath)
    {
        if (!File.Exists(sourcePackagePath))
            throw new FileNotFoundException("선택한 이전 버전 패키지를 찾지 못했습니다.", sourcePackagePath);

        string workRoot = Path.Combine(Path.GetTempPath(), "HD2HelperUpdater", Guid.NewGuid().ToString("N"));
        string packagePath = Path.Combine(workRoot, PackageFileName);
        string stageDirectory = Path.Combine(workRoot, "stage");
        string backupDirectory = Path.Combine(workRoot, "backup");
        Directory.CreateDirectory(stageDirectory);
        Directory.CreateDirectory(backupDirectory);

        try
        {
            SetProgress(5);
            SetStatus("선택한 로컬 패키지를 준비하는 중...");
            // 현재 패키지를 백업하면서 이전버전 폴더가 정리되어도 선택한 ZIP이 사라지지 않도록 임시 위치에 먼저 복사한다.
            File.Copy(sourcePackagePath, packagePath, true);
            SetProgress(20);
            SetStatus("선택한 로컬 패키지 무결성을 검증하는 중...");
            string packageHash;
            await using (var packageStream = File.OpenRead(packagePath))
                packageHash = Convert.ToHexString(await SHA256.HashDataAsync(packageStream));
            SetProgress(35);
            SetStatus($"선택한 로컬 패키지 압축 해제 중... SHA-256 {packageHash[..12]}");
            await Task.Run(() => ExtractPackageSafely(packagePath, stageDirectory));

            SetProgress(60);
            SetStatus("선택한 로컬 패키지 구성 확인 중...");
            ValidateStagedPackage(stageDirectory);
            SetProgress(70);
            await StopHelpersIfExecutableChangesAsync(stageDirectory);
            SetProgress(78);
            SetStatus("선택한 헬퍼 버전을 적용하는 중...");
            await Task.Run(() => ApplyStagedPackage(stageDirectory, backupDirectory));
            SetProgress(92);
            SetStatus("이전 버전과 설치 정보를 정리하는 중...");
            PreservePreviousPackage();
            File.Copy(packagePath, Path.Combine(_installDirectory, PackageFileName), true);

            UpdaterState state = LoadState();
            // 구형 상태 파일의 PackageSha256는 마지막 온라인 최신 ZIP의 해시이므로 다운그레이드 전에 최신 기준값으로 승격한다.
            if (string.IsNullOrWhiteSpace(state.LatestPackageSha256) && !string.IsNullOrWhiteSpace(state.PackageSha256))
                state.LatestPackageSha256 = state.PackageSha256;
            state.InstalledVersion = GetFileVersion(Path.Combine(stageDirectory, HelperFileName));
            state.PackageSha256 = packageHash;
            state.InstalledPackageSha256 = packageHash;
            state.InstalledReleaseId = 0;
            state.InstalledAssetId = 0;
            state.InstalledTag = state.InstalledVersion;
            SaveState(state);
            SetProgress(98);
        }
        finally
        {
            try { Directory.Delete(workRoot, true); } catch { }
        }
    }

    private async Task StopHelpersIfExecutableChangesAsync(string stageDirectory)
    {
        string stagedHelperPath = Path.Combine(stageDirectory, HelperFileName);

        // 실행 중인 EXE를 바꾸는 경우에만 Windows 파일 잠금 때문에 종료가 필요하다.
        // 아이콘, 데이터베이스, 사운드처럼 실행 파일과 무관한 변경은 헬퍼를 그대로 유지한다.
        if (!File.Exists(HelperPath) || !File.Exists(stagedHelperPath) || FilesAreIdentical(HelperPath, stagedHelperPath))
        {
            SetStatus("실행 중인 헬퍼를 유지한 채 업데이트 파일을 적용합니다...");
            return;
        }

        SetStatus("실행 파일 교체를 위해 헬퍼를 종료하는 중...");
        await StopAllLocalHelpersAsync();
    }

    private static bool FilesAreIdentical(string firstPath, string secondPath)
    {
        var first = new FileInfo(firstPath);
        var second = new FileInfo(secondPath);
        if (first.Length != second.Length) return false;

        const int bufferSize = 1024 * 128;
        using FileStream firstStream = File.OpenRead(firstPath);
        using FileStream secondStream = File.OpenRead(secondPath);
        byte[] firstBuffer = new byte[bufferSize];
        byte[] secondBuffer = new byte[bufferSize];

        while (true)
        {
            int firstRead = firstStream.Read(firstBuffer, 0, firstBuffer.Length);
            int secondRead = secondStream.Read(secondBuffer, 0, secondBuffer.Length);
            if (firstRead != secondRead) return false;
            if (firstRead == 0) return true;
            if (!firstBuffer.AsSpan(0, firstRead).SequenceEqual(secondBuffer.AsSpan(0, secondRead))) return false;
        }
    }

    private async Task DownloadFileWithProgressAsync(
        string destination,
        long expectedLength,
        string downloadUrl,
        string displayName,
        int startProgress,
        int endProgress)
    {
        SetStatus($"{displayName} 다운로드 중...");
        if (string.IsNullOrWhiteSpace(downloadUrl))
            throw new InvalidDataException("GitHub 릴리스의 다운로드 주소가 비어 있습니다.");
        using HttpResponseMessage response = await Http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        if (response.Content.Headers.ContentType?.MediaType?.Contains("text/html", StringComparison.OrdinalIgnoreCase) == true)
            throw new InvalidDataException("GitHub가 업데이트 파일 대신 HTML 페이지를 반환했습니다.");

        long total = response.Content.Headers.ContentLength ?? expectedLength;
        await using Stream source = await response.Content.ReadAsStreamAsync();
        await using var target = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 128, true);
        byte[] buffer = new byte[1024 * 128];
        long received = 0;
        long lastUiUpdate = 0;

        while (true)
        {
            int read = await source.ReadAsync(buffer);
            if (read == 0) break;
            await target.WriteAsync(buffer.AsMemory(0, read));
            received += read;

            if (received - lastUiUpdate >= 1024 * 512 || received == total)
            {
                lastUiUpdate = received;
                int percent = total > 0 ? (int)Math.Clamp(received * 100 / total, 0, 100) : 0;
                int overallProgress = startProgress + (endProgress - startProgress) * percent / 100;
                SetProgress(overallProgress);
                SetStatus($"{displayName} 다운로드 중... {percent}%  •  전체 {overallProgress}%");
            }
        }

        if (expectedLength > 0 && received != expectedLength)
            throw new InvalidDataException($"다운로드 크기가 일치하지 않습니다. 예상 {expectedLength:N0}, 실제 {received:N0}");
    }

    private static void ExtractPackageSafely(string packagePath, string stageDirectory)
    {
        string stageRoot = Path.GetFullPath(stageDirectory) + Path.DirectorySeparatorChar;
        using ZipArchive archive = ZipFile.OpenRead(packagePath);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string relativePath = entry.FullName.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
            if (string.IsNullOrWhiteSpace(relativePath)) continue;

            string destination = Path.GetFullPath(Path.Combine(stageDirectory, relativePath));
            if (!destination.StartsWith(stageRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"허용되지 않은 압축 경로입니다: {entry.FullName}");

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(destination);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, true);
        }
    }

    private static void ValidateStagedPackage(string stageDirectory)
    {
        string helper = Path.Combine(stageDirectory, HelperFileName);
        string database = Path.Combine(stageDirectory, "database.json");
        if (!File.Exists(helper) || new FileInfo(helper).Length < 1024 * 1024)
            throw new InvalidDataException("패키지에서 정상적인 HD2 Helper.exe를 찾지 못했습니다.");
        if (!File.Exists(database))
            throw new InvalidDataException("패키지에서 database.json을 찾지 못했습니다.");
    }

    private void ApplyStagedPackage(string stageDirectory, string backupDirectory)
    {
        var applied = new List<AppliedFile>();
        try
        {
            foreach (string sourcePath in Directory.EnumerateFiles(stageDirectory, "*", SearchOption.AllDirectories))
            {
                string relativePath = Path.GetRelativePath(stageDirectory, sourcePath);
                if (ShouldProtectUpdaterPath(relativePath)) continue;

                string destinationPath = Path.GetFullPath(Path.Combine(_installDirectory, relativePath));
                EnsureInsideInstallDirectory(destinationPath);
                string backupPath = Path.Combine(backupDirectory, relativePath);
                bool existed = File.Exists(destinationPath);

                if (existed)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
                    File.Copy(destinationPath, backupPath, true);
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
                string temporaryPath = destinationPath + ".update-new";
                File.Copy(sourcePath, temporaryPath, true);
                File.Move(temporaryPath, destinationPath, true);
                applied.Add(new AppliedFile(relativePath, existed));
            }

            ApplyDeletionList(stageDirectory, backupDirectory, applied);
        }
        catch
        {
            // 일부 파일만 바뀐 상태로 남지 않도록 적용 역순으로 기존 파일을 복구한다.
            foreach (AppliedFile file in applied.AsEnumerable().Reverse())
            {
                string destinationPath = Path.Combine(_installDirectory, file.RelativePath);
                string backupPath = Path.Combine(backupDirectory, file.RelativePath);
                try
                {
                    if (file.ExistedBefore && File.Exists(backupPath)) File.Copy(backupPath, destinationPath, true);
                    else if (!file.ExistedBefore && File.Exists(destinationPath)) File.Delete(destinationPath);
                }
                catch { }
            }
            throw;
        }
    }

    private void ApplyDeletionList(string stageDirectory, string backupDirectory, List<AppliedFile> applied)
    {
        string deleteListPath = Path.Combine(stageDirectory, DeleteListFileName);
        if (!File.Exists(deleteListPath)) return;

        foreach (string rawLine in File.ReadAllLines(deleteListPath))
        {
            string relativePath = rawLine.Trim();
            if (relativePath.Length == 0 || relativePath.StartsWith('#') || ShouldProtectUpdaterPath(relativePath)) continue;

            string destinationPath = Path.GetFullPath(Path.Combine(_installDirectory, relativePath));
            EnsureInsideInstallDirectory(destinationPath);
            if (!File.Exists(destinationPath)) continue;
            if (File.Exists(Path.Combine(stageDirectory, relativePath))) continue;

            string backupPath = Path.Combine(backupDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
            if (!File.Exists(backupPath)) File.Copy(destinationPath, backupPath, true);
            File.Delete(destinationPath);
            applied.Add(new AppliedFile(relativePath, true));
        }
    }

    private bool ShouldProtectUpdaterPath(string relativePath)
    {
        string normalized = relativePath.Replace('/', '\\');
        return normalized.Equals(UpdaterFileName, StringComparison.OrdinalIgnoreCase)
            || normalized.Equals(StateFileName, StringComparison.OrdinalIgnoreCase)
            || normalized.Equals(SettingsFileName, StringComparison.OrdinalIgnoreCase)
            || normalized.Equals(DeleteListFileName, StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("이전버전\\", StringComparison.OrdinalIgnoreCase);
    }

    private void EnsureInsideInstallDirectory(string path)
    {
        string root = Path.GetFullPath(_installDirectory) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"업데이트 대상이 헬퍼 폴더를 벗어났습니다: {path}");
    }

    private void PreservePreviousPackage()
    {
        string currentPackage = Path.Combine(_installDirectory, PackageFileName);
        if (!File.Exists(currentPackage)) return;

        string previousDirectory = Path.Combine(_installDirectory, "이전버전");
        Directory.CreateDirectory(previousDirectory);
        string backupName = $"HD2.Helper_{DateTime.Now:yyyyMMdd-HHmmss}.zip";
        File.Copy(currentPackage, Path.Combine(previousDirectory, backupName), false);

        // 기존 배포 규칙과 동일하게 이전 ZIP은 최신 20개까지만 유지한다.
        foreach (FileInfo oldFile in new DirectoryInfo(previousDirectory).GetFiles("*.zip")
                     .OrderByDescending(file => file.LastWriteTimeUtc).Skip(20))
            oldFile.Delete();
    }

    private async Task LaunchHelperEmbeddedAsync()
    {
        if (!File.Exists(HelperPath)) throw new FileNotFoundException("HD2 Helper.exe를 찾을 수 없습니다.", HelperPath);
        if (_helperProcess is { HasExited: false }) return;

        SetStatus("헬퍼를 실행하는 중...");
        _stoppingHelper = false;
        _hostPanel.CreateControl();
        _helperProcess = Process.Start(new ProcessStartInfo
        {
            FileName = HelperPath,
            WorkingDirectory = _installDirectory,
            // 부모 패널 핸들을 넘겨 헬퍼가 자기 창을 직접 자식 창으로 전환하게 한다.
            Arguments = $"--started-by-updater --embed-parent={_hostPanel.Handle.ToInt64()}",
            UseShellExecute = true
        }) ?? throw new InvalidOperationException("헬퍼 프로세스를 시작하지 못했습니다.");

        _helperProcess.EnableRaisingEvents = true;
        _helperProcess.Exited += (_, _) =>
        {
            if (_closing || IsDisposed) return;
            BeginInvoke(new Action(() =>
            {
                _embeddedHelperWindow = IntPtr.Zero;
                if (!_stoppingHelper) SetStatus("헬퍼가 종료되었습니다. 재시작 버튼으로 다시 실행할 수 있습니다.");
            }));
        };

        IntPtr helperWindow = await WaitForEmbeddedHelperWindowAsync(_helperProcess, TimeSpan.FromSeconds(25));
        RegisterEmbeddedHelperWindow(helperWindow);
        SetStatus($"헬퍼 실행 중 • {GetHelperVersion()}");
    }

    private async Task<IntPtr> WaitForEmbeddedHelperWindowAsync(Process process, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (process.HasExited) throw new InvalidOperationException("헬퍼가 창을 표시하기 전에 종료되었습니다.");
            IntPtr embeddedWindow = FindDirectChildWindow(_hostPanel.Handle, process.Id);
            if (embeddedWindow != IntPtr.Zero) return embeddedWindow;
            await Task.Delay(100);
        }
        throw new TimeoutException("업데이터 안에 배치된 헬퍼 창을 찾는 시간이 초과되었습니다.");
    }

    private static IntPtr FindDirectChildWindow(IntPtr parentWindow, int processId)
    {
        IntPtr foundWindow = IntPtr.Zero;
        EnumChildWindows(parentWindow, (window, _) =>
        {
            GetWindowThreadProcessId(window, out uint ownerProcessId);
            if (ownerProcessId != (uint)processId || GetParent(window) != parentWindow) return true;
            foundWindow = window;
            return false;
        }, IntPtr.Zero);
        return foundWindow;
    }

    private void RegisterEmbeddedHelperWindow(IntPtr helperWindow)
    {
        if (GetParent(helperWindow) != _hostPanel.Handle)
            throw new InvalidOperationException("헬퍼 창의 부모가 업데이터 표시 영역과 일치하지 않습니다.");
        if (!GetWindowRect(helperWindow, out RECT embeddedRect))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "헬퍼 창 크기를 읽지 못했습니다.");

        int width = Math.Max(InitialHelperWidth, embeddedRect.Right - embeddedRect.Left);
        int height = Math.Max(InitialHelperHeight, embeddedRect.Bottom - embeddedRect.Top);

        _embeddedHelperWindow = helperWindow;
        _lastEmbeddedSize = new Size(width, height);
        ResizeShellForHelper(_lastEmbeddedSize);
    }

    private void SynchronizeEmbeddedWindowSize()
    {
        if (_embeddedHelperWindow == IntPtr.Zero || !IsWindow(_embeddedHelperWindow)) return;
        if (!GetWindowRect(_embeddedHelperWindow, out RECT rect)) return;

        var size = new Size(rect.Right - rect.Left, rect.Bottom - rect.Top);
        if (size.Width < 400 || size.Height < 300 || size == _lastEmbeddedSize) return;
        _lastEmbeddedSize = size;
        ResizeShellForHelper(size);
    }

    private void ResizeShellForHelper(Size helperSize)
    {
        Rectangle workingArea = Screen.FromControl(this).WorkingArea;
        int maxHelperWidth = Math.Max(760, workingArea.Width);
        int maxHelperHeight = Math.Max(430, workingArea.Height - HeaderHeight - FooterHeight);
        Size nextClient = new(
            Math.Clamp(helperSize.Width, 760, maxHelperWidth),
            Math.Clamp(helperSize.Height, 430, maxHelperHeight) + HeaderHeight + FooterHeight
        );
        if (ClientSize != nextClient) ClientSize = nextClient;
    }

    private async Task StopAllLocalHelpersAsync()
    {
        _stoppingHelper = true;
        var targets = new List<Process>();
        if (_helperProcess is { HasExited: false }) targets.Add(_helperProcess);

        foreach (Process process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(HelperFileName)))
        {
            if (targets.Any(existing => existing.Id == process.Id)) continue;
            try
            {
                if (string.Equals(process.MainModule?.FileName, HelperPath, StringComparison.OrdinalIgnoreCase)) targets.Add(process);
                else process.Dispose();
            }
            catch { process.Dispose(); }
        }

        foreach (Process process in targets) { try { process.CloseMainWindow(); } catch { } }
        foreach (Process process in targets)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
                await process.WaitForExitAsync(timeout.Token);
            }
            catch { try { process.Kill(true); process.WaitForExit(3000); } catch { } }
        }

        _embeddedHelperWindow = IntPtr.Zero;
        _helperProcess = null;
    }

    private void StopHelpersSynchronously()
    {
        _stoppingHelper = true;
        foreach (Process process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(HelperFileName)))
        {
            try
            {
                if (!string.Equals(process.MainModule?.FileName, HelperPath, StringComparison.OrdinalIgnoreCase)) continue;
                process.CloseMainWindow();
                if (!process.WaitForExit(2000)) process.Kill(true);
            }
            catch { }
            finally { process.Dispose(); }
        }
    }

    private UpdaterState LoadState()
    {
        try
        {
            if (!File.Exists(StatePath)) return new UpdaterState();
            return JsonSerializer.Deserialize<UpdaterState>(File.ReadAllText(StatePath)) ?? new UpdaterState();
        }
        catch { return new UpdaterState(); }
    }

    private void SaveState(UpdaterState state)
    {
        Directory.CreateDirectory(UpdaterDataDirectory);
        string temporaryPath = StatePath + ".tmp";
        string json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(temporaryPath, json);
        File.Move(temporaryPath, StatePath, true);
    }

    private string GetHelperVersion() => GetFileVersion(HelperPath);
    private static string GetFileVersion(string path) => File.Exists(path) ? FileVersionInfo.GetVersionInfo(path).FileVersion ?? "알 수 없음" : "없음";

    private void UpdateHeaderHelperVersionLabel()
    {
        if (InvokeRequired) { BeginInvoke(new Action(UpdateHeaderHelperVersionLabel)); return; }

        _helperVersionLabel.Text = $"HELPER {GetHelperVersion()}";
        int titleWidth = TextRenderer.MeasureText(_titleLabel.Text, _titleLabel.Font).Width;
        _helperVersionLabel.Location = new Point(_titleLabel.Left + titleWidth + 6, 14);
        _helperVersionLabel.BringToFront();
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _checkButton.Enabled = !busy;
        _historyButton.Enabled = !busy;
        _installPathButton.Enabled = !busy;
    }

    private void SetNewVersionAvailable(bool available)
    {
        if (InvokeRequired) { BeginInvoke(new Action(() => SetNewVersionAvailable(available))); return; }

        // 새 헬퍼 버전은 별도 라벨 대신 버전 선택 버튼 자체를 초록색으로 밝혀 알린다.
        _checkButton.Text = available ? "버전 선택  NEW" : "버전 선택";
        _checkButton.ForeColor = available ? Color.FromArgb(76, 220, 112) : Color.WhiteSmoke;
        _checkButton.BackColor = available ? Color.FromArgb(31, 57, 39) : Color.FromArgb(32, 32, 32);
        _checkButton.FlatAppearance.BorderColor = available
            ? Color.FromArgb(76, 220, 112)
            : Color.FromArgb(100, 100, 100);
    }

    private void SetUpdateHistoryAvailable(bool available)
    {
        if (InvokeRequired) { BeginInvoke(new Action(() => SetUpdateHistoryAvailable(available))); return; }

        _historyButton.Text = available ? "업데이트 내역 NEW" : "업데이트 내역";
        _historyButton.ForeColor = available ? Color.FromArgb(76, 220, 112) : Color.White;
    }

    // The updater uses its own release stream, so its update indication is kept separate from helper package updates.
    private void SetUpdaterUpdateAvailable(bool available)
    {
        if (InvokeRequired) { BeginInvoke(new Action(() => SetUpdaterUpdateAvailable(available))); return; }

        _updaterUpdateButton.Text = available ? "업데이터 갱신 NEW" : "업데이터 갱신";
        _updaterUpdateButton.ForeColor = available ? Color.FromArgb(76, 220, 112) : Color.White;
    }

    private void SetStatus(string text)
    {
        if (InvokeRequired) { BeginInvoke(new Action(() => SetStatus(text))); return; }
        _statusLabel.Text = text;
    }

    private void SetProgress(int percent)
    {
        if (InvokeRequired) { BeginInvoke(new Action(() => SetProgress(percent))); return; }
        _progressFill.Width = (int)(_progressTrack.ClientSize.Width * Math.Clamp(percent, 0, 100) / 100d);
    }

    private void DragWindow(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        ReleaseCapture();
        SendMessage(Handle, WM_NCLBUTTONDOWN, HTCAPTION, 0);
    }

    private void OnUpdaterClosing(object? sender, FormClosingEventArgs e)
    {
        if (_busy && e.CloseReason == CloseReason.UserClosing)
        {
            // 다운로드나 파일 교체 도중 종료되어 설치 폴더가 절반만 갱신되는 상황을 막는다.
            e.Cancel = true;
            SetStatus("업데이트 작업이 끝난 뒤 종료할 수 있습니다.");
            return;
        }

        _closing = true;
        _periodicCheckTimer.Stop();
        _embeddedLayoutTimer.Stop();
        StopHelpersSynchronously();
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("HD2-Helper-Updater/1.3");
        return client;
    }

    private static bool IsHelldiversActive()
    {
        IntPtr foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero) return false;
        var className = new StringBuilder(256);
        return GetClassName(foreground, className, className.Capacity) > 0 && className.ToString() == "stingray_window";
    }

    private sealed record GitHubReleaseInfo(
        long ReleaseId,
        string TagName,
        string DisplayName,
        DateTime PublishedAtUtc,
        bool Prerelease,
        long AssetId,
        long AssetSize,
        string AssetDownloadUrl,
        Version SortVersion);
    private sealed record GitHubUpdaterInfo(
        long ReleaseId,
        Version Version,
        long AssetId,
        long AssetSize,
        string AssetDownloadUrl);
    private sealed record GitHubReleaseCatalog(
        List<GitHubReleaseInfo> StableHelperReleases,
        List<GitHubReleaseInfo> TestHelperReleases,
        GitHubUpdaterInfo? LatestUpdater);
    private sealed record GitHubApiCacheEntry(string Json, DateTimeOffset ExpiresAtUtc);
    private sealed record VersionChoice(
        string DisplayText,
        string? PackagePath,
        GitHubReleaseInfo? Remote,
        bool IsLatest,
        bool IsInstalled);
    private sealed record UpdateHistoryItem(
        long ReleaseId,
        string TagName,
        string Title,
        DateTime PublishedAtUtc,
        bool IsUpdater,
        string Body);
    private sealed record AppliedFile(string RelativePath, bool ExistedBefore);
    private sealed class UpdaterSettings
    {
        public string InstallDirectory { get; set; } = "";
        // 인증 코드는 저장하지 않고 테스트 채널 해제 여부만 남긴다.
        public bool TestChannelUnlocked { get; set; }
    }

    private sealed class UpdaterState
    {
        public string RemoteFingerprint { get; set; } = "";
        public DateTime RemoteLastModifiedUtc { get; set; }
        public string InstalledVersion { get; set; } = "";
        public string PackageSha256 { get; set; } = "";
        public string LatestPackageSha256 { get; set; } = "";
        public string InstalledPackageSha256 { get; set; } = "";
        public long LatestReleaseId { get; set; }
        public long LatestAssetId { get; set; }
        public long InstalledReleaseId { get; set; }
        public long InstalledAssetId { get; set; }
        public string InstalledTag { get; set; } = "";
        public string InstalledUpdaterVersion { get; set; } = "";
        public string LatestUpdaterVersion { get; set; } = "";
        public long LastReadUpdateReleaseId { get; set; }
    }

    private sealed class DarkMenuColorTable : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Color.FromArgb(30, 30, 30);
        public override Color MenuItemSelected => Color.FromArgb(54, 54, 54);
        public override Color MenuItemBorder => Color.FromArgb(90, 90, 90);
        public override Color ImageMarginGradientBegin => Color.FromArgb(30, 30, 30);
        public override Color ImageMarginGradientMiddle => Color.FromArgb(30, 30, 30);
        public override Color ImageMarginGradientEnd => Color.FromArgb(30, 30, 30);
    }

    private const int WM_NCLBUTTONDOWN = 0x00A1;
    private const int HTCAPTION = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetParent(IntPtr hWnd);
    private delegate bool EnumChildWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr parent, EnumChildWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int message, int wParam, int lParam);
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder className, int maxCount);
}
