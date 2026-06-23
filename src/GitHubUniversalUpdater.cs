using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace GitHubUniversalUpdater
{
    public class AppEntry
    {
        public string Name { get; set; }
        public string InstallDir { get; set; }
        public string GitHubUrl { get; set; }
        public string LastInstalledTag { get; set; }
        public string PreferredAssetRegex { get; set; }
        public string UpdateMode { get; set; }
        public string SilentInstallArgs { get; set; }
    }

    public class AppConfig
    {
        public List<AppEntry> Apps { get; set; }
        public bool UseIdm { get; set; }
        public string IdmPath { get; set; }
    }

    internal class ReleaseAsset
    {
        public string Name;
        public string Url;
        public long Size;
    }

    internal class ReleaseInfo
    {
        public string Tag;
        public string Source;
        public List<ReleaseAsset> Assets = new List<ReleaseAsset>();
    }

    internal class DownloadProbe
    {
        public long ContentLength;
        public bool SupportsRanges;
    }

    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            ServicePointManager.ServerCertificateValidationCallback = delegate { return true; };
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }

    public class MainForm : Form
    {
        private const string ModeAuto = "自动识别";
        private const string ModeArchive = "解压覆盖";
        private const string ModeInstaller = "静默安装";
        private const int SegmentCount = 8;

        private readonly string baseDir;
        private readonly string configPath;
        private readonly string workDir;
        private readonly string logPath;
        private readonly Dictionary<int, ReleaseInfo> checkedReleases = new Dictionary<int, ReleaseInfo>();
        private readonly Dictionary<int, string> downloadedAssets = new Dictionary<int, string>();

        private DataGridView grid;
        private TextBox logBox;
        private ContextMenuStrip rowMenu;
        private Button addButton;
        private Button removeButton;
        private Button checkButton;
        private Button updateButton;
        private Button saveButton;
        private CheckBox useIdmCheckBox;
        private TextBox idmPathBox;
        private Button browseIdmButton;

        public MainForm()
        {
            var currentAssembly = typeof(MainForm).Assembly;
            var entryAssembly = Assembly.GetEntryAssembly();
            var assembly = entryAssembly != null && entryAssembly.GetName().Name == currentAssembly.GetName().Name
                ? entryAssembly
                : currentAssembly;
            baseDir = Path.GetDirectoryName(assembly.Location);
            configPath = Path.Combine(baseDir, "apps.json");
            workDir = Path.Combine(baseDir, ".work");
            logPath = Path.Combine(baseDir, "update.log");
            Directory.CreateDirectory(workDir);
            InitializeUi();
            LoadConfig();
        }

        private void InitializeUi()
        {
            Text = "GitHub 通用一键更新器 v1.1";
            Width = 1545;
            Height = 720;
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(1180, 580);

            var layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Fill;
            layout.ColumnCount = 1;
            layout.RowCount = 3;
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 170));
            Controls.Add(layout);

            var top = new FlowLayoutPanel();
            top.Dock = DockStyle.Fill;
            top.Height = 42;
            top.Padding = new Padding(8, 8, 8, 4);

            addButton = MakeButton("新增软件");
            removeButton = MakeButton("删除选中");
            checkButton = MakeButton("检查更新");
            updateButton = MakeButton("一键更新");
            saveButton = MakeButton("保存配置");
            var proxyLabel = new Label { Text = "下载加速：https://gh-proxy.org/", AutoSize = true, Margin = new Padding(12, 6, 8, 0) };
            useIdmCheckBox = new CheckBox { Text = "使用", AutoSize = true, Margin = new Padding(14, 6, 4, 0) };
            idmPathBox = new TextBox { Width = 260, Margin = new Padding(4, 3, 4, 0) };
            browseIdmButton = MakeButton("选择IDM");

            top.Controls.AddRange(new Control[] { addButton, removeButton, saveButton, checkButton, updateButton, proxyLabel, useIdmCheckBox, idmPathBox, browseIdmButton });
            layout.Controls.Add(top, 0, 0);

            grid = new DataGridView();
            grid.Dock = DockStyle.Fill;
            grid.AllowUserToAddRows = false;
            grid.RowHeadersVisible = true;
            grid.RowHeadersWidth = 34;
            grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            grid.MultiSelect = true;
            grid.AllowUserToResizeColumns = true;
            grid.AllowUserToResizeRows = false;
            grid.BackgroundColor = Color.FromArgb(170, 170, 170);
            grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
            grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None;
            grid.ColumnHeadersVisible = true;
            grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            grid.ColumnHeadersHeight = 26;

            AddTextColumn("Name", "软件名称", 150);
            AddTextColumn("InstallDir", "安装目录或主程序exe", 285);
            grid.Columns.Add(new DataGridViewButtonColumn { Name = "BrowseInstall", HeaderText = "选择", Text = "选择", UseColumnTextForButtonValue = true, Width = 60, Resizable = DataGridViewTriState.True });
            AddTextColumn("GitHubUrl", "GitHub 地址", 300);
            AddTextColumn("LastInstalledTag", "本地版本", 90);
            AddTextColumn("LatestTag", "最新版本", 90);
            AddTextColumn("Status", "状态", 110);
            AddTextColumn("PreferredAssetRegex", "资产筛选(可选)", 190);
            var modeColumn = new DataGridViewComboBoxColumn();
            modeColumn.Name = "UpdateMode";
            modeColumn.HeaderText = "更新方式";
            modeColumn.Width = 85;
            modeColumn.Resizable = DataGridViewTriState.True;
            modeColumn.Items.AddRange(ModeAuto, ModeArchive, ModeInstaller);
            grid.Columns.Add(modeColumn);
            AddTextColumn("SilentInstallArgs", "静默参数", 105);
            layout.Controls.Add(grid, 0, 1);

            logBox = new TextBox();
            logBox.Dock = DockStyle.Bottom;
            logBox.Height = 170;
            logBox.Multiline = true;
            logBox.ScrollBars = ScrollBars.Vertical;
            logBox.ReadOnly = true;
            layout.Controls.Add(logBox, 0, 2);

            addButton.Click += delegate { AddBlankRow(); };
            removeButton.Click += delegate { RemoveSelectedRows(); };
            saveButton.Click += delegate { SaveConfig(); };
            browseIdmButton.Click += delegate { BrowseIdmPath(); };
            grid.CellContentClick += Grid_CellContentClick;
            grid.CellMouseDown += Grid_CellMouseDown;
            checkButton.Click += delegate { RunInBackground(RunCheck); };
            updateButton.Click += delegate { RunInBackground(RunUpdate); };
            FormClosing += delegate { SaveConfig(); };
            InitializeRowMenu();
        }

        private Button MakeButton(string text)
        {
            return new Button { Text = text, AutoSize = true, Height = 27, Margin = new Padding(4, 0, 4, 0) };
        }

        private void AddTextColumn(string name, string header, int width)
        {
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = name, HeaderText = header, Width = width, Resizable = DataGridViewTriState.True });
        }

        private void AddHiddenTextColumn(string name, string header)
        {
            var column = new DataGridViewTextBoxColumn { Name = name, HeaderText = header, Visible = false, Resizable = DataGridViewTriState.True };
            grid.Columns.Add(column);
        }

        private void LoadConfig()
        {
            AppConfig config = null;
            if (File.Exists(configPath))
            {
                try
                {
                    using (var stream = File.OpenRead(configPath))
                    {
                        config = (AppConfig)new DataContractJsonSerializer(typeof(AppConfig)).ReadObject(stream);
                    }
                }
                catch (Exception ex)
                {
                    Log("读取配置失败：" + ex.Message);
                }
            }
            if (config == null)
            {
                config = new AppConfig { Apps = new List<AppEntry>(), UseIdm = false, IdmPath = FindIdmPath() };
            }

            grid.Rows.Clear();
            checkedReleases.Clear();
            useIdmCheckBox.Checked = config.UseIdm;
            idmPathBox.Text = config.IdmPath ?? "";
            if (config.Apps != null)
            {
                foreach (var app in config.Apps)
                    AddRow(app);
            }
            if (grid.Rows.Count == 0)
                AddBlankRow();
        }

        private void SaveConfig()
        {
            if (InvokeRequired)
            {
                Invoke(new Action(SaveConfig));
                return;
            }
            var config = new AppConfig
            {
                Apps = GetAppsFromGrid(),
                UseIdm = useIdmCheckBox.Checked,
                IdmPath = idmPathBox.Text.Trim()
            };
            string json;
            using (var stream = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(AppConfig)).WriteObject(stream, config);
                json = Encoding.UTF8.GetString(stream.ToArray());
            }
            File.WriteAllText(configPath, PrettyJson(json), new UTF8Encoding(false));
            Log("配置已保存：" + configPath);
        }

        private string PrettyJson(string json)
        {
            var sb = new StringBuilder();
            var indent = 0;
            var quoted = false;
            for (int i = 0; i < json.Length; i++)
            {
                var ch = json[i];
                if (ch == '"' && (i == 0 || json[i - 1] != '\\')) quoted = !quoted;
                if (!quoted && (ch == '{' || ch == '['))
                {
                    sb.Append(ch).AppendLine();
                    sb.Append(new string(' ', ++indent * 2));
                }
                else if (!quoted && (ch == '}' || ch == ']'))
                {
                    sb.AppendLine();
                    sb.Append(new string(' ', --indent * 2)).Append(ch);
                }
                else if (!quoted && ch == ',')
                {
                    sb.Append(ch).AppendLine();
                    sb.Append(new string(' ', indent * 2));
                }
                else if (!quoted && ch == ':')
                {
                    sb.Append(": ");
                }
                else
                {
                    sb.Append(ch);
                }
            }
            return sb.ToString();
        }

        private List<AppEntry> GetAppsFromGrid()
        {
            var list = new List<AppEntry>();
            foreach (DataGridViewRow row in grid.Rows)
            {
                var app = GetAppFromRow(row);
                if (!string.IsNullOrWhiteSpace(app.Name) || !string.IsNullOrWhiteSpace(app.GitHubUrl))
                    list.Add(app);
            }
            return list;
        }

        private void AddBlankRow()
        {
            AddRow(new AppEntry { LastInstalledTag = "未安装", UpdateMode = ModeAuto });
        }

        private void AddRow(AppEntry app)
        {
            var mode = NormalizeMode(app.UpdateMode);
            grid.Rows.Add(
                app.Name ?? "",
                app.InstallDir ?? "",
                "选择",
                app.GitHubUrl ?? "",
                string.IsNullOrWhiteSpace(app.LastInstalledTag) ? "未安装" : app.LastInstalledTag,
                "",
                "未检查",
                app.PreferredAssetRegex ?? "",
                mode,
                app.SilentInstallArgs ?? "");
        }

        private void RemoveSelectedRows()
        {
            foreach (DataGridViewRow row in grid.SelectedRows)
            {
                if (!row.IsNewRow)
                    grid.Rows.Remove(row);
            }
        }

        private void Grid_CellContentClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0) return;
            if (grid.Columns[e.ColumnIndex].Name != "BrowseInstall") return;
            BrowseInstallPath(e.RowIndex);
        }

        private void InitializeRowMenu()
        {
            rowMenu = new ContextMenuStrip();
            rowMenu.Items.Add("手动安装已下载文件", null, delegate { ManualInstallSelectedAsset(); });
            rowMenu.Items.Add("打开下载文件位置", null, delegate { OpenSelectedAssetLocation(); });
            rowMenu.Items.Add("清理该软件下载缓存", null, delegate { ClearSelectedAssetCache(); });
        }

        private void Grid_CellMouseDown(object sender, DataGridViewCellMouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right || e.RowIndex < 0) return;
            grid.ClearSelection();
            grid.Rows[e.RowIndex].Selected = true;
            grid.CurrentCell = grid.Rows[e.RowIndex].Cells[Math.Max(0, e.ColumnIndex)];
            rowMenu.Show(Cursor.Position);
        }

        private void BrowseInstallPath(int rowIndex)
        {
            using (var chooser = new Form())
            using (var fileButton = new Button())
            using (var folderButton = new Button())
            using (var cancelButton = new Button())
            {
                chooser.Text = "选择安装目录或主程序";
                chooser.StartPosition = FormStartPosition.CenterParent;
                chooser.FormBorderStyle = FormBorderStyle.FixedDialog;
                chooser.MinimizeBox = false;
                chooser.MaximizeBox = false;
                chooser.ClientSize = new Size(360, 82);

                folderButton.Text = "选择安装目录";
                folderButton.SetBounds(16, 24, 105, 30);
                fileButton.Text = "选择主程序exe";
                fileButton.SetBounds(128, 24, 105, 30);
                cancelButton.Text = "取消";
                cancelButton.SetBounds(240, 24, 88, 30);

                folderButton.DialogResult = DialogResult.Yes;
                fileButton.DialogResult = DialogResult.OK;
                cancelButton.DialogResult = DialogResult.Cancel;
                chooser.Controls.AddRange(new Control[] { folderButton, fileButton, cancelButton });
                chooser.AcceptButton = folderButton;
                chooser.CancelButton = cancelButton;

                var result = chooser.ShowDialog(this);
                if (result == DialogResult.Yes)
                {
                    using (var dialog = new FolderBrowserDialog())
                    {
                        dialog.Description = "选择软件安装目录";
                        if (dialog.ShowDialog(this) == DialogResult.OK)
                            grid.Rows[rowIndex].Cells["InstallDir"].Value = dialog.SelectedPath;
                    }
                }
                else if (result == DialogResult.OK)
                {
                    using (var dialog = new OpenFileDialog())
                    {
                        dialog.Filter = "可执行文件|*.exe|所有文件|*.*";
                        dialog.Title = "选择主程序 exe";
                        if (dialog.ShowDialog(this) == DialogResult.OK)
                            grid.Rows[rowIndex].Cells["InstallDir"].Value = dialog.FileName;
                    }
                }
            }
        }

        private void BrowseIdmPath()
        {
            using (var dialog = new OpenFileDialog())
            {
                dialog.Filter = "IDMan.exe|IDMan.exe|可执行文件|*.exe|所有文件|*.*";
                if (dialog.ShowDialog(this) == DialogResult.OK)
                    idmPathBox.Text = dialog.FileName;
            }
        }

        private void RunInBackground(Action action)
        {
            ToggleButtons(false);
            ThreadPool.QueueUserWorkItem(delegate
            {
                try { action(); }
                catch (Exception ex) { Log("操作失败：" + ex); }
                finally { ToggleButtons(true); }
            });
        }

        private void RunCheck()
        {
            SaveConfig();
            checkedReleases.Clear();
            for (int i = 0; i < grid.Rows.Count; i++)
                CheckOne(i);
        }

        private void RunUpdate()
        {
            SaveConfig();
            for (int i = 0; i < grid.Rows.Count; i++)
                UpdateOne(i);
            CleanupEmptyWorkDirs();
        }

        private void CheckOne(int rowIndex)
        {
            var app = GetAppFromRow(rowIndex);
            if (string.IsNullOrWhiteSpace(app.Name) || string.IsNullOrWhiteSpace(app.GitHubUrl))
                return;
            try
            {
                SetStatus(rowIndex, app.LastInstalledTag, "", "检查中...");
                Log("检查 " + app.Name + "：" + app.GitHubUrl);
                var release = GetLatestRelease(app.GitHubUrl);
                checkedReleases[rowIndex] = release;
                var local = DetectLocalVersion(app, release);
                var status = IsNewer(local, release.Tag) ? "发现更新" : "已是最新";
                var mode = NormalizeMode(app.UpdateMode);
                var asset = SelectAsset(release, app.PreferredAssetRegex, mode);
                if (ShouldInstallAsInstaller(mode, asset))
                    status += "（安装包）";
                SetStatus(rowIndex, local, release.Tag, status);
            }
            catch (Exception ex)
            {
                SetStatus(rowIndex, app.LastInstalledTag, "", "失败：" + ex.Message);
                Log(app.Name + " 检查失败：" + ex.Message);
            }
        }

        private void UpdateOne(int rowIndex)
        {
            var app = GetAppFromRow(rowIndex);
            if (string.IsNullOrWhiteSpace(app.Name) || string.IsNullOrWhiteSpace(app.GitHubUrl))
                return;
            try
            {
                ReleaseInfo release;
                if (!checkedReleases.TryGetValue(rowIndex, out release))
                    release = GetLatestRelease(app.GitHubUrl);

                var local = DetectLocalVersion(app, release);
                if (!IsNewer(local, release.Tag))
                {
                    SetStatus(rowIndex, local, release.Tag, "跳过：已是最新");
                    return;
                }

                var mode = NormalizeMode(app.UpdateMode);
                var asset = SelectAsset(release, app.PreferredAssetRegex, mode);
                var installAsInstaller = ShouldInstallAsInstaller(mode, asset);
                Log(app.Name + " 选择资产：" + asset.Name);
                var assetPath = DownloadAsset(app, release, asset, rowIndex);
                downloadedAssets[rowIndex] = assetPath;

                if (installAsInstaller)
                {
                    SetStatus(rowIndex, local, release.Tag, "静默安装中...");
                    InstallSilently(app, assetPath);
                    CleanupUpdateFiles(assetPath, null);
                }
                else
                {
                    SetStatus(rowIndex, local, release.Tag, "解压覆盖中...");
                    var extractPath = ExtractAsset(app, assetPath);
                    var installRoot = GetInstallRoot(app.InstallDir);
                    var sourceRoot = FindInstallRoot(extractPath, installRoot);
                    CopyDirectory(sourceRoot, installRoot);
                    CleanupUpdateFiles(assetPath, extractPath);
                }

                WriteVersionMarker(GetInstallRoot(app.InstallDir), release.Tag);
                UpdateRowValue(rowIndex, "LastInstalledTag", release.Tag);
                SetStatus(rowIndex, release.Tag, release.Tag, "更新完成");
                Log(app.Name + " 更新完成：" + release.Tag);
            }
            catch (Exception ex)
            {
                var cachedAsset = FindCachedAsset(rowIndex);
                var suffix = !string.IsNullOrWhiteSpace(cachedAsset) && File.Exists(cachedAsset)
                    ? "（已保留下载文件，右键可手动安装）"
                    : "";
                SetStatus(rowIndex, app.LastInstalledTag, "", "失败：" + ex.Message + suffix);
                Log(app.Name + " 更新失败：" + ex);
            }
        }

        private ReleaseInfo GetLatestRelease(string repoUrl)
        {
            var parts = ParseRepo(repoUrl);
            var url = "https://api.github.com/repos/" + parts.Item1 + "/" + parts.Item2 + "/releases/latest";
            try
            {
                var json = HttpGet(url, "application/vnd.github+json");
                var dict = new JavaScriptSerializer().DeserializeObject(json) as Dictionary<string, object>;
                return ParseReleaseDict(dict, "GitHub API");
            }
            catch (Exception ex)
            {
                Log("GitHub API 查询失败，改用网页解析：" + ex.Message);
                return GetLatestReleaseFromWeb(parts.Item1, parts.Item2);
            }
        }

        private Tuple<string, string> ParseRepo(string repoUrl)
        {
            var match = Regex.Match(repoUrl ?? "", @"github\.com[/:]([^/\s]+)/([^/\s#?]+)", RegexOptions.IgnoreCase);
            if (!match.Success)
                throw new InvalidOperationException("不是有效的 GitHub 仓库地址");
            return Tuple.Create(match.Groups[1].Value, Regex.Replace(match.Groups[2].Value, @"\.git$", "", RegexOptions.IgnoreCase));
        }

        private ReleaseInfo ParseReleaseDict(Dictionary<string, object> dict, string source)
        {
            if (dict == null) throw new InvalidOperationException("Release 返回为空");
            var release = new ReleaseInfo { Tag = Convert.ToString(dict["tag_name"]), Source = source };
            object assetsObj;
            if (dict.TryGetValue("assets", out assetsObj))
            {
                foreach (var item in (object[])assetsObj)
                {
                    var a = item as Dictionary<string, object>;
                    if (a == null) continue;
                    release.Assets.Add(new ReleaseAsset
                    {
                        Name = Convert.ToString(a["name"]),
                        Url = Convert.ToString(a["browser_download_url"]),
                        Size = a.ContainsKey("size") ? Convert.ToInt64(a["size"]) : 0
                    });
                }
            }
            if (string.IsNullOrWhiteSpace(release.Tag))
                throw new InvalidOperationException("未找到最新版本 tag");
            if (release.Assets.Count == 0)
                throw new InvalidOperationException("最新 Release 没有可下载资产");
            return release;
        }

        private string HttpGet(string url, string accept)
        {
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) GitHubUniversalUpdater/1.1";
            request.Accept = accept;
            request.Headers[HttpRequestHeader.AcceptLanguage] = "zh-CN,zh;q=0.9,en;q=0.8";
            request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            request.Timeout = 30000;
            using (var response = (HttpWebResponse)request.GetResponse())
            using (var stream = response.GetResponseStream())
            using (var reader = new StreamReader(stream, Encoding.UTF8))
                return reader.ReadToEnd();
        }

        private ReleaseInfo GetLatestReleaseFromWeb(string owner, string repo)
        {
            var html = HttpGet("https://github.com/" + owner + "/" + repo + "/releases/latest", "text/html");
            var tagMatch = Regex.Match(html, @"/" + Regex.Escape(owner) + "/" + Regex.Escape(repo) + @"/releases/tag/([^""?#]+)", RegexOptions.IgnoreCase);
            if (!tagMatch.Success)
                throw new InvalidOperationException("无法从 Releases 页面解析最新版本");
            var tag = WebUtility.HtmlDecode(tagMatch.Groups[1].Value);
            var releaseHtml = HttpGet("https://github.com/" + owner + "/" + repo + "/releases/expanded_assets/" + Uri.EscapeDataString(tag), "text/html");
            var release = new ReleaseInfo { Tag = tag, Source = "GitHub Web" };
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in Regex.Matches(releaseHtml, @"href=""([^""]*/releases/download/[^""]+)""", RegexOptions.IgnoreCase))
            {
                var href = WebUtility.HtmlDecode(m.Groups[1].Value);
                var name = WebUtility.UrlDecode(href.Substring(href.LastIndexOf('/') + 1)).Trim();
                if (Regex.IsMatch(name, @"(?i)^Source code|source|src")) continue;
                if (href.StartsWith("/"))
                    href = "https://github.com" + href;
                if (!seen.Add(href)) continue;
                release.Assets.Add(new ReleaseAsset { Name = name, Url = href });
            }
            if (release.Assets.Count == 0)
                throw new InvalidOperationException("最新 Release 没有可下载资产");
            return release;
        }

        private ReleaseAsset SelectAsset(ReleaseInfo release, string preferredRegex, string updateMode)
        {
            var installerMode = updateMode == ModeInstaller;
            var archiveMode = updateMode == ModeArchive;
            IEnumerable<ReleaseAsset> assets = release.Assets
                .Where(a => !Regex.IsMatch(a.Name ?? "", @"(?i)^Source code|source[._ -]?code|src"))
                .ToList();
            if (!string.IsNullOrWhiteSpace(preferredRegex))
            {
                try
                {
                    var regex = new Regex(preferredRegex, RegexOptions.IgnoreCase);
                    var preferred = assets.Where(a => regex.IsMatch(a.Name)).ToList();
                    if (preferred.Count > 0)
                    {
                        Log("按资产筛选选择：" + preferred[0].Name);
                        assets = preferred;
                    }
                }
                catch (Exception ex)
                {
                    Log("资产筛选正则无效，改用自动选择：" + ex.Message);
                }
            }
            string[] allowed;
            if (installerMode)
                allowed = new[] { ".exe", ".msi", ".msix", ".msixbundle" };
            else if (archiveMode)
                allowed = new[] { ".zip", ".7z", ".rar" };
            else
                allowed = new[] { ".zip", ".7z", ".rar", ".exe", ".msi", ".msix", ".msixbundle" };
            var filtered = assets.Where(a => allowed.Contains(Path.GetExtension(a.Name).ToLowerInvariant())).ToList();
            if (filtered.Count == 0)
                filtered = assets.ToList();
            filtered = FilterCompatibleArchitecture(filtered);
            if (filtered.Count == 0)
                throw new InvalidOperationException("Release 没有找到可用下载资产");
            var best = filtered.Select(a => new { Asset = a, Score = ScoreAsset(a.Name, installerMode || IsInstallerAsset(a.Name)) })
                .OrderByDescending(x => x.Score)
                .ThenBy(x => x.Asset.Name.Length)
                .First();
            Log("系统识别：windows " + GetCurrentArch() + "；自动选择资产：" + best.Asset.Name + "；评分 " + best.Score);
            return best.Asset;
        }

        private bool IsInstallerAsset(string fileName)
        {
            return Regex.IsMatch(fileName ?? "", @"(?i)\.(exe|msi|msix|msixbundle)$");
        }

        private bool ShouldInstallAsInstaller(string updateMode, ReleaseAsset asset)
        {
            if (updateMode == ModeInstaller) return true;
            if (updateMode == ModeArchive) return false;
            return asset != null && IsInstallerAsset(asset.Name);
        }

        private int ScoreAsset(string fileName, bool installerMode)
        {
            var n = fileName.ToLowerInvariant();
            var score = 0;
            var arch = GetCurrentArch();
            var hasArm64 = IsArm64Asset(n);
            var hasX64 = IsX64Asset(n);
            var hasX86 = IsX86Asset(n);
            if (Regex.IsMatch(n, installerMode ? @"\.(exe|msi|msix|msixbundle)$" : @"\.(zip|7z|rar)$")) score += 20;
            if (ContainsAny(n, "source", "src")) score -= 200;
            if (ContainsAny(n, "windows", "win32", "win64", "win-", "win_", ".win")) score += 120;
            if (ContainsAny(n, "mac", "macos", "darwin", "osx", "linux", "ubuntu", "debian", "appimage", "rpm", "dmg")) score -= 500;
            if (arch == "x64")
            {
                if (hasX64) score += 140;
                if (ContainsAny(n, "win64") && !hasArm64) score += 60;
                if (hasArm64) score -= 1000;
                if (hasX86) score += 20;
            }
            else if (arch == "arm64")
            {
                if (hasArm64) score += 160;
                if (hasX64) score -= 80;
                if (hasX86) score -= 220;
            }
            else
            {
                if (hasX86) score += 130;
                if (hasX64 || hasArm64 || ContainsAny(n, "win64")) score -= 800;
            }
            if (installerMode && ContainsAny(n, "setup", "installer", "install")) score += 20;
            if (!installerMode && ContainsAny(n, "portable")) score += 10;
            if (!ContainsAny(n, "windows", "win", "mac", "linux", "arm", "x64", "x86", "amd64", "aarch64")) score += 5;
            if (n.EndsWith(".msi")) score += 16;
            if (n.EndsWith(".exe")) score += 12;
            if (n.EndsWith(".zip")) score += 10;
            return score;
        }

        private List<ReleaseAsset> FilterCompatibleArchitecture(List<ReleaseAsset> assets)
        {
            var compatible = assets.Where(a => IsCompatibleArchitecture(a.Name)).ToList();
            return compatible.Count > 0 ? compatible : assets;
        }

        private bool IsCompatibleArchitecture(string fileName)
        {
            var n = (fileName ?? "").ToLowerInvariant();
            var arch = GetCurrentArch();
            var hasArm64 = IsArm64Asset(n);
            var hasX64 = IsX64Asset(n);
            var hasX86 = IsX86Asset(n);

            if (arch == "x64")
                return !hasArm64;
            if (arch == "arm64")
                return !hasX86;
            return !hasArm64 && !hasX64 && !ContainsAny(n, "win64");
        }

        private bool IsArm64Asset(string fileName)
        {
            return ContainsAny(fileName, "arm64", "aarch64", "armv8");
        }

        private bool IsX64Asset(string fileName)
        {
            return ContainsAny(fileName, "x64", "x86_64", "amd64");
        }

        private bool IsX86Asset(string fileName)
        {
            return Regex.IsMatch(fileName ?? "", @"(^|[^a-z0-9])x86([^a-z0-9]|$)") || ContainsAny(fileName, "ia32", "win32", "x32");
        }

        private string GetCurrentArch()
        {
            var arch = "";
            try
            {
                arch = Environment.GetEnvironmentVariable("PROCESSOR_ARCHITEW6432");
                if (string.IsNullOrWhiteSpace(arch))
                    arch = Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE");
            }
            catch { }
            arch = (arch ?? "").ToLowerInvariant();
            if (arch.Contains("arm64") || arch.Contains("aarch64")) return "arm64";
            if (Environment.Is64BitOperatingSystem) return "x64";
            return "x86";
        }

        private bool ContainsAny(string text, params string[] parts)
        {
            return parts.Any(p => text.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private string DetectLocalVersion(AppEntry app)
        {
            return DetectLocalVersion(app, null);
        }

        private string DetectLocalVersion(AppEntry app, ReleaseInfo release)
        {
            var root = GetInstallRoot(app.InstallDir);
            var marker = Path.Combine(root, ".github-universal-updater-version");
            if (File.Exists(marker))
                return File.ReadAllText(marker, Encoding.UTF8).Trim();
            var exeVersion = DetectExecutableVersion(app, root, release);
            if (IsUsableSavedVersion(exeVersion))
                return exeVersion;
            if (IsUsableSavedVersion(app.LastInstalledTag))
                return app.LastInstalledTag;
            return "未安装";
        }

        private string DetectExecutableVersion(AppEntry app, string root, ReleaseInfo release)
        {
            var exePath = FindMainExecutable(app, root, release);
            if (string.IsNullOrWhiteSpace(exePath)) return "";
            try
            {
                var info = FileVersionInfo.GetVersionInfo(exePath);
                var version = FirstNonEmpty(info.FileVersion, info.ProductVersion);
                if (!string.IsNullOrWhiteSpace(version))
                    return NormalizeFileVersion(version);
            }
            catch { }
            return "";
        }

        private string FindMainExecutable(AppEntry app, string root, ReleaseInfo release)
        {
            if (!string.IsNullOrWhiteSpace(app.InstallDir) &&
                Path.GetExtension(app.InstallDir).Equals(".exe", StringComparison.OrdinalIgnoreCase) &&
                File.Exists(app.InstallDir))
                return app.InstallDir;
            var hints = BuildVersionHints(app, root, release);
            if (string.IsNullOrWhiteSpace(root))
                return "";

            if (Directory.Exists(root))
            {
                var localExe = SelectBestExecutable(Directory.GetFiles(root, "*.exe", SearchOption.TopDirectoryOnly), hints, 0);
                if (!string.IsNullOrWhiteSpace(localExe))
                    return localExe;
            }

            var siblingExe = FindSiblingExecutable(root, hints);
            if (!string.IsNullOrWhiteSpace(siblingExe))
                return siblingExe;
            return "";
        }

        private string SelectBestExecutable(IEnumerable<string> candidates, IEnumerable<string> hints, int minScore)
        {
            var exeFiles = candidates
                .Where(p => !Regex.IsMatch(Path.GetFileName(p), @"(?i)unins|uninstall|setup|install|update|crash|helper"))
                .ToList();
            if (exeFiles.Count == 0) return "";
            if (exeFiles.Count == 1 && minScore <= 0) return exeFiles[0];

            var best = exeFiles
                .Select(p => new { Path = p, Score = ScoreExecutableName(p, hints) })
                .OrderByDescending(x => x.Score)
                .ThenByDescending(x => new FileInfo(x.Path).Length)
                .First();
            return best.Score >= minScore ? best.Path : "";
        }

        private string FindSiblingExecutable(string root, IEnumerable<string> hints)
        {
            var parent = Directory.Exists(root)
                ? Directory.GetParent(root)
                : Directory.GetParent(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (parent == null || !Directory.Exists(parent.FullName)) return "";

            var candidates = new List<string>();
            foreach (var dir in Directory.GetDirectories(parent.FullName).Take(200))
            {
                var dirName = Path.GetFileName(dir);
                var dirScore = ScoreText(dirName, hints);
                if (dirScore < 20) continue;
                try
                {
                    candidates.AddRange(Directory.GetFiles(dir, "*.exe", SearchOption.TopDirectoryOnly));
                }
                catch { }
            }
            return SelectBestExecutable(candidates, hints, 30);
        }

        private List<string> BuildVersionHints(AppEntry app, string root, ReleaseInfo release)
        {
            var hints = new List<string>();
            hints.Add(Path.GetFileName(root));
            hints.Add(app.Name);
            var repo = ParseRepoName(app.GitHubUrl);
            if (!string.IsNullOrWhiteSpace(repo)) hints.Add(repo);
            if (release != null && release.Assets != null)
            {
                foreach (var asset in release.Assets)
                    hints.Add(Path.GetFileNameWithoutExtension(asset.Name));
            }
            return hints;
        }

        private int ScoreExecutableName(string exePath, IEnumerable<string> hints)
        {
            return ScoreText(Path.GetFileNameWithoutExtension(exePath), hints);
        }

        private int ScoreText(string text, IEnumerable<string> hints)
        {
            var name = (text ?? "").ToLowerInvariant();
            var score = 0;
            foreach (var hint in hints)
            {
                foreach (var token in SplitNameTokens(hint))
                {
                    if (token.Length < 2) continue;
                    if (name.Contains(token)) score += token.Length * 10;
                }
            }
            return score;
        }

        private IEnumerable<string> SplitNameTokens(string value)
        {
            return Regex.Split((value ?? "").ToLowerInvariant(), @"[^a-z0-9\u4e00-\u9fff]+")
                .Where(t => !string.IsNullOrWhiteSpace(t));
        }

        private string ParseRepoName(string repoUrl)
        {
            var match = Regex.Match(repoUrl ?? "", @"github\.com[/:][^/\s]+/([^/\s#?]+)", RegexOptions.IgnoreCase);
            return match.Success ? Regex.Replace(match.Groups[1].Value, @"\.git$", "", RegexOptions.IgnoreCase) : "";
        }

        private string FirstNonEmpty(params string[] values)
        {
            foreach (var value in values)
            {
                if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
            }
            return "";
        }

        private string NormalizeFileVersion(string version)
        {
            version = Regex.Replace(version ?? "", @"\s+", "").Trim();
            var match = Regex.Match(version, @"\d+(?:\.\d+){1,3}");
            if (!match.Success) return version;
            var parts = match.Value.Split('.').ToList();
            while (parts.Count > 2 && parts[parts.Count - 1] == "0")
                parts.RemoveAt(parts.Count - 1);
            return string.Join(".", parts.ToArray());
        }

        private bool IsUsableSavedVersion(string savedTag)
        {
            return !string.IsNullOrWhiteSpace(savedTag) && savedTag != "未安装";
        }

        private bool IsNewer(string local, string latest)
        {
            if (string.IsNullOrWhiteSpace(latest)) return false;
            if (!IsUsableSavedVersion(local)) return true;
            return !string.Equals(NormalizeTag(local), NormalizeTag(latest), StringComparison.OrdinalIgnoreCase);
        }

        private string NormalizeTag(string value)
        {
            return Regex.Replace(value ?? "", @"^[vV]", "").Trim();
        }

        private string DownloadAsset(AppEntry app, ReleaseInfo release, ReleaseAsset asset, int rowIndex)
        {
            var dir = Path.Combine(workDir, SafePath(app.Name), SafePath(release.Tag));
            Directory.CreateDirectory(dir);
            var fileName = SafePath(asset.Name);
            var target = Path.Combine(dir, fileName);
            var url = GetProxyUrl(asset.Url);
            if (ShouldUseIdm())
            {
                try
                {
                    DownloadWithIdm(GetIdmPathFromUi(), url, dir, fileName, target, app.Name, rowIndex);
                    return target;
                }
                catch (Exception ex)
                {
                    Log("IDM 下载失败，回退内置多线程下载：" + ex.Message);
                }
            }
            DownloadWithSegments(url, target, app.Name, rowIndex);
            return target;
        }

        private bool ShouldUseIdm()
        {
            var result = false;
            InvokeIfRequired(delegate { result = useIdmCheckBox.Checked && File.Exists(idmPathBox.Text.Trim()); });
            return result;
        }

        private string GetIdmPathFromUi()
        {
            var result = "";
            InvokeIfRequired(delegate { result = idmPathBox.Text.Trim(); });
            return result;
        }

        private string GetProxyUrl(string url)
        {
            if (url.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase) ||
                url.StartsWith("https://objects.githubusercontent.com/", StringComparison.OrdinalIgnoreCase))
                return "https://gh-proxy.org/" + url;
            return url;
        }

        private void DownloadWithIdm(string idmPath, string url, string dir, string fileName, string targetPath, string displayName, int rowIndex)
        {
            if (File.Exists(targetPath)) File.Delete(targetPath);
            RunProcess(idmPath, "/d \"" + url + "\" /p \"" + dir + "\" /f \"" + fileName + "\" /n /q");
            var start = DateTime.Now;
            while (!File.Exists(targetPath) && DateTime.Now - start < TimeSpan.FromSeconds(60))
            {
                Thread.Sleep(1000);
                SetStatus(rowIndex, "", "", "等待 IDM 下载...");
            }
            if (!File.Exists(targetPath))
                throw new IOException("IDM 下载超时");
        }

        private void DownloadWithSegments(string url, string path, string displayName, int rowIndex)
        {
            var probe = ProbeDownload(url);
            if (probe.ContentLength <= 0 || !probe.SupportsRanges)
            {
                Log("服务器不支持分段下载，使用单线程下载");
                DownloadWithWebClient(url, path, displayName, rowIndex);
                return;
            }

            if (File.Exists(path)) File.Delete(path);
            var partPaths = new List<string>();
            var errors = new List<Exception>();
            var received = new long[SegmentCount];
            var threads = new List<Thread>();
            var partSize = probe.ContentLength / SegmentCount;
            var startTime = DateTime.Now;

            for (int i = 0; i < SegmentCount; i++)
            {
                var index = i;
                var startByte = i * partSize;
                var endByte = (i == SegmentCount - 1) ? probe.ContentLength - 1 : startByte + partSize - 1;
                var partPath = path + ".part" + i;
                partPaths.Add(partPath);
                if (File.Exists(partPath)) File.Delete(partPath);
                var thread = new Thread(delegate()
                {
                    try
                    {
                        DownloadRange(url, partPath, startByte, endByte, delegate(long delta)
                        {
                            Interlocked.Add(ref received[index], delta);
                        });
                    }
                    catch (Exception ex)
                    {
                        lock (errors) errors.Add(ex);
                    }
                });
                thread.IsBackground = true;
                threads.Add(thread);
                thread.Start();
            }

            while (threads.Any(t => t.IsAlive))
            {
                var total = received.Sum();
                var seconds = Math.Max(1, (DateTime.Now - startTime).TotalSeconds);
                SetStatus(rowIndex, "", "", "下载中 " + (total * 100 / probe.ContentLength) + "% " + FormatBytes((long)(total / seconds)) + "/s");
                Thread.Sleep(500);
            }
            foreach (var thread in threads) thread.Join();
            if (errors.Count > 0)
                throw errors[0];

            using (var output = File.Create(path))
            {
                foreach (var part in partPaths)
                {
                    using (var input = File.OpenRead(part))
                        input.CopyTo(output);
                    File.Delete(part);
                }
            }
        }

        private DownloadProbe ProbeDownload(string url)
        {
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "HEAD";
            request.UserAgent = "GitHubUniversalUpdater/1.1";
            using (var response = (HttpWebResponse)request.GetResponse())
            {
                return new DownloadProbe
                {
                    ContentLength = response.ContentLength,
                    SupportsRanges = string.Equals(response.Headers["Accept-Ranges"], "bytes", StringComparison.OrdinalIgnoreCase)
                };
            }
        }

        private void DownloadRange(string url, string path, long startByte, long endByte, Action<long> onBytes)
        {
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.UserAgent = "GitHubUniversalUpdater/1.1";
            request.AddRange(startByte, endByte);
            using (var response = (HttpWebResponse)request.GetResponse())
            using (var input = response.GetResponseStream())
            using (var output = File.Create(path))
            {
                var buffer = new byte[1024 * 128];
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    output.Write(buffer, 0, read);
                    onBytes(read);
                }
            }
        }

        private void DownloadWithWebClient(string url, string path, string displayName, int rowIndex)
        {
            using (var client = new WebClient())
            {
                client.Headers[HttpRequestHeader.UserAgent] = "GitHubUniversalUpdater/1.1";
                client.DownloadProgressChanged += delegate(object sender, DownloadProgressChangedEventArgs e)
                {
                    SetStatus(rowIndex, "", "", "下载中 " + e.ProgressPercentage + "%");
                };
                var done = new AutoResetEvent(false);
                Exception error = null;
                client.DownloadFileCompleted += delegate(object sender, AsyncCompletedEventArgs e)
                {
                    error = e.Error;
                    done.Set();
                };
                client.DownloadFileAsync(new Uri(url), path);
                done.WaitOne();
                if (error != null) throw error;
            }
        }

        private string ExtractAsset(AppEntry app, string archivePath)
        {
            var extractPath = Path.Combine(Path.GetDirectoryName(archivePath), "_extract");
            if (Directory.Exists(extractPath)) Directory.Delete(extractPath, true);
            Directory.CreateDirectory(extractPath);
            var ext = Path.GetExtension(archivePath).ToLowerInvariant();
            if (ext == ".zip")
                ZipFile.ExtractToDirectory(archivePath, extractPath);
            else
                RunProcess("tar.exe", "-xf \"" + archivePath + "\" -C \"" + extractPath + "\"");
            return extractPath;
        }

        private void InstallSilently(AppEntry app, string installerPath)
        {
            var args = app.SilentInstallArgs;
            if (string.IsNullOrWhiteSpace(args))
                args = DefaultSilentArgs(installerPath);
            Log(app.Name + " 静默安装：" + Path.GetFileName(installerPath) + " " + args);
            var ext = Path.GetExtension(installerPath).ToLowerInvariant();
            if (ext == ".msi")
                RunProcess("msiexec.exe", "/i \"" + installerPath + "\" " + args);
            else
                RunProcess(installerPath, args);
        }

        private void ManualInstallSelectedAsset()
        {
            var rowIndex = GetSelectedRowIndex();
            if (rowIndex < 0) return;
            var assetPath = FindCachedAsset(rowIndex);
            if (string.IsNullOrWhiteSpace(assetPath) || !File.Exists(assetPath))
            {
                MessageBox.Show(this, "没有找到已下载的安装文件。请先重新执行更新下载。", "手动安装", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            try
            {
                var ext = Path.GetExtension(assetPath).ToLowerInvariant();
                if (ext == ".msi")
                    Process.Start(new ProcessStartInfo("msiexec.exe", "/i \"" + assetPath + "\"") { UseShellExecute = true });
                else
                    Process.Start(new ProcessStartInfo(assetPath) { UseShellExecute = true });
                SetStatus(rowIndex, "", "", "已打开手动安装界面");
                Log("手动安装：" + assetPath);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "打开安装文件失败：" + ex.Message, "手动安装", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void OpenSelectedAssetLocation()
        {
            var rowIndex = GetSelectedRowIndex();
            if (rowIndex < 0) return;
            var assetPath = FindCachedAsset(rowIndex);
            if (string.IsNullOrWhiteSpace(assetPath) || !File.Exists(assetPath))
            {
                MessageBox.Show(this, "没有找到已下载文件。", "打开文件位置", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + assetPath + "\"") { UseShellExecute = true });
        }

        private void ClearSelectedAssetCache()
        {
            var rowIndex = GetSelectedRowIndex();
            if (rowIndex < 0) return;
            var app = GetAppFromRow(grid.Rows[rowIndex]);
            var dir = Path.Combine(workDir, SafePath(app.Name));
            try
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, true);
                downloadedAssets.Remove(rowIndex);
                SetStatus(rowIndex, "", "", "已清理下载缓存");
                Log(app.Name + " 已清理下载缓存：" + dir);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "清理缓存失败：" + ex.Message, "清理缓存", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private int GetSelectedRowIndex()
        {
            if (grid.SelectedRows.Count > 0)
                return grid.SelectedRows[0].Index;
            if (grid.CurrentCell != null)
                return grid.CurrentCell.RowIndex;
            return -1;
        }

        private string FindCachedAsset(int rowIndex)
        {
            string assetPath;
            if (downloadedAssets.TryGetValue(rowIndex, out assetPath) && File.Exists(assetPath))
                return assetPath;

            if (rowIndex < 0 || rowIndex >= grid.Rows.Count) return "";
            var app = GetAppFromRow(grid.Rows[rowIndex]);
            var dir = Path.Combine(workDir, SafePath(app.Name));
            if (!Directory.Exists(dir)) return "";

            var files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
                .Where(p => !Path.GetFileName(p).StartsWith("_", StringComparison.OrdinalIgnoreCase))
                .Where(p => Regex.IsMatch(Path.GetExtension(p), @"(?i)^\.(exe|msi|msix|msixbundle|zip|7z|rar)$"))
                .OrderByDescending(p => File.GetLastWriteTime(p))
                .ToList();
            if (files.Count == 0) return "";
            downloadedAssets[rowIndex] = files[0];
            return files[0];
        }

        private string DefaultSilentArgs(string installerPath)
        {
            var ext = Path.GetExtension(installerPath).ToLowerInvariant();
            if (ext == ".msi") return "/qn /norestart";
            if (ext == ".msix" || ext == ".msixbundle") return "";
            return "/S";
        }

        private string FormatBytes(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB" };
            double value = bytes;
            int unit = 0;
            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }
            return value.ToString("0.##") + units[unit];
        }

        private void CleanupUpdateFiles(string assetPath, string extractPath)
        {
            try { if (File.Exists(assetPath)) File.Delete(assetPath); } catch { }
            try { if (!string.IsNullOrWhiteSpace(extractPath) && Directory.Exists(extractPath)) Directory.Delete(extractPath, true); } catch { }
        }

        private void CleanupEmptyWorkDirs()
        {
            try
            {
                foreach (var dir in Directory.GetDirectories(workDir, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
                    if (!Directory.EnumerateFileSystemEntries(dir).Any())
                        Directory.Delete(dir);
            }
            catch { }
        }

        private string FindInstallRoot(string extractPath, string installDir)
        {
            var dirs = Directory.GetDirectories(extractPath);
            var files = Directory.GetFiles(extractPath);
            if (files.Length == 0 && dirs.Length == 1)
                return dirs[0];
            return extractPath;
        }

        private void CopyDirectory(string source, string destination)
        {
            Directory.CreateDirectory(destination);
            foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(dir.Replace(source, destination));
            foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                var target = file.Replace(source, destination);
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Copy(file, target, true);
            }
        }

        private void WriteVersionMarker(string installDir, string tag)
        {
            try
            {
                Directory.CreateDirectory(installDir);
                File.WriteAllText(Path.Combine(installDir, ".github-universal-updater-version"), tag ?? "", new UTF8Encoding(false));
            }
            catch { }
        }

        private string GetInstallRoot(string installPath)
        {
            if (string.IsNullOrWhiteSpace(installPath)) return baseDir;
            if (Path.GetExtension(installPath).Equals(".exe", StringComparison.OrdinalIgnoreCase))
                return Path.GetDirectoryName(installPath);
            return installPath;
        }

        private string FindIdmPath()
        {
            foreach (var path in new[]
            {
                @"C:\Program Files (x86)\Internet Download Manager\IDMan.exe",
                @"C:\Program Files\Internet Download Manager\IDMan.exe"
            })
            {
                if (File.Exists(path)) return path;
            }
            return "";
        }

        private void RunProcess(string file, string args)
        {
            var info = new ProcessStartInfo(file, args);
            info.UseShellExecute = false;
            info.CreateNoWindow = true;
            info.WindowStyle = ProcessWindowStyle.Hidden;
            var p = Process.Start(info);
            p.WaitForExit();
            if (p.ExitCode != 0)
                throw new InvalidOperationException(Path.GetFileName(file) + " 退出码：" + p.ExitCode);
        }

        private AppEntry GetAppFromRow(int rowIndex)
        {
            AppEntry app = null;
            InvokeIfRequired(delegate { app = GetAppFromRow(grid.Rows[rowIndex]); });
            return app;
        }

        private AppEntry GetAppFromRow(DataGridViewRow row)
        {
            return new AppEntry
            {
                Name = CellText(row, "Name"),
                InstallDir = CellText(row, "InstallDir"),
                GitHubUrl = CellText(row, "GitHubUrl"),
                LastInstalledTag = CellText(row, "LastInstalledTag"),
                PreferredAssetRegex = CellText(row, "PreferredAssetRegex"),
                UpdateMode = NormalizeMode(CellText(row, "UpdateMode")),
                SilentInstallArgs = CellText(row, "SilentInstallArgs")
            };
        }

        private string NormalizeMode(string value)
        {
            if (string.Equals(value, "installer", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, ModeInstaller, StringComparison.OrdinalIgnoreCase))
                return ModeInstaller;
            if (string.Equals(value, "archive", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, ModeArchive, StringComparison.OrdinalIgnoreCase))
                return ModeArchive;
            return ModeAuto;
        }

        private bool IsInstallerMode(AppEntry app)
        {
            return NormalizeMode(app.UpdateMode) == ModeInstaller;
        }

        private string CellText(DataGridViewRow row, string column)
        {
            var value = row.Cells[column].Value;
            return value == null ? "" : value.ToString().Trim();
        }

        private void SetStatus(int rowIndex, string local, string latest, string status)
        {
            InvokeIfRequired(delegate
            {
                if (rowIndex < 0 || rowIndex >= grid.Rows.Count) return;
                if (!string.IsNullOrWhiteSpace(local)) grid.Rows[rowIndex].Cells["LastInstalledTag"].Value = local;
                if (!string.IsNullOrWhiteSpace(latest)) grid.Rows[rowIndex].Cells["LatestTag"].Value = latest;
                grid.Rows[rowIndex].Cells["Status"].Value = status;
            });
        }

        private void UpdateRowValue(int rowIndex, string column, string value)
        {
            InvokeIfRequired(delegate
            {
                if (rowIndex >= 0 && rowIndex < grid.Rows.Count)
                    grid.Rows[rowIndex].Cells[column].Value = value;
            });
        }

        private void ToggleButtons(bool enabled)
        {
            InvokeIfRequired(delegate
            {
                addButton.Enabled = enabled;
                removeButton.Enabled = enabled;
                checkButton.Enabled = enabled;
                updateButton.Enabled = enabled;
                saveButton.Enabled = enabled;
                browseIdmButton.Enabled = enabled;
            });
        }

        private void Log(string message)
        {
            var line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ") + message;
            try { File.AppendAllText(logPath, line + Environment.NewLine, Encoding.UTF8); } catch { }
            InvokeIfRequired(delegate
            {
                logBox.AppendText(line + Environment.NewLine);
            });
        }

        private void InvokeIfRequired(Action action)
        {
            if (IsHandleCreated && InvokeRequired)
                Invoke(action);
            else
                action();
        }

        private string SafePath(string text)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder();
            foreach (var ch in text ?? "")
                sb.Append(invalid.Contains(ch) ? '_' : ch);
            return string.IsNullOrWhiteSpace(sb.ToString()) ? "download" : sb.ToString();
        }
    }
}
