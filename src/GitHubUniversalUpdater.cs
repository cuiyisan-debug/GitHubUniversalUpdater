using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
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
        private const string ModeArchive = "解压覆盖";
        private const string ModeInstaller = "静默安装";

        private readonly string baseDir;
        private readonly string configPath;
        private readonly string workDir;
        private readonly string logPath;
        private readonly Dictionary<int, ReleaseInfo> checkedReleases = new Dictionary<int, ReleaseInfo>();

        private DataGridView grid;
        private TextBox logBox;
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
            baseDir = AppDomain.CurrentDomain.BaseDirectory;
            configPath = Path.Combine(baseDir, "apps.json");
            workDir = Path.Combine(baseDir, ".work");
            logPath = Path.Combine(baseDir, "update.log");
            Directory.CreateDirectory(workDir);
            InitializeUi();
            LoadConfig();
        }

        private void InitializeUi()
        {
            Text = "GitHub 通用一键更新器 v1.1.0";
            Width = 1180;
            Height = 720;
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(980, 580);

            var top = new FlowLayoutPanel();
            top.Dock = DockStyle.Top;
            top.Height = 42;
            top.Padding = new Padding(8, 8, 8, 4);

            addButton = MakeButton("添加");
            removeButton = MakeButton("删除");
            checkButton = MakeButton("检查更新");
            updateButton = MakeButton("一键更新");
            saveButton = MakeButton("保存配置");
            useIdmCheckBox = new CheckBox { Text = "使用 IDM", AutoSize = true, Margin = new Padding(14, 6, 4, 0) };
            idmPathBox = new TextBox { Width = 260, Margin = new Padding(4, 3, 4, 0) };
            browseIdmButton = MakeButton("选择 IDM");

            top.Controls.AddRange(new Control[] { addButton, removeButton, checkButton, updateButton, saveButton, useIdmCheckBox, idmPathBox, browseIdmButton });
            Controls.Add(top);

            grid = new DataGridView();
            grid.Dock = DockStyle.Fill;
            grid.AllowUserToAddRows = false;
            grid.RowHeadersVisible = false;
            grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            grid.MultiSelect = true;
            grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None;
            grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;

            AddTextColumn("Name", "软件名称", 130);
            AddTextColumn("InstallDir", "安装目录/主程序", 210);
            AddTextColumn("GitHubUrl", "GitHub 仓库", 240);
            AddTextColumn("LastInstalledTag", "已安装版本", 90);
            AddTextColumn("PreferredAssetRegex", "资产筛选(可选)", 145);
            var modeColumn = new DataGridViewComboBoxColumn();
            modeColumn.Name = "UpdateMode";
            modeColumn.HeaderText = "更新方式";
            modeColumn.Width = 90;
            modeColumn.Items.AddRange(ModeArchive, ModeInstaller);
            grid.Columns.Add(modeColumn);
            AddTextColumn("SilentInstallArgs", "静默参数", 130);
            AddTextColumn("LatestTag", "最新版本", 90);
            AddTextColumn("Status", "状态", 145);
            Controls.Add(grid);

            logBox = new TextBox();
            logBox.Dock = DockStyle.Bottom;
            logBox.Height = 170;
            logBox.Multiline = true;
            logBox.ScrollBars = ScrollBars.Vertical;
            logBox.ReadOnly = true;
            Controls.Add(logBox);

            addButton.Click += delegate { AddBlankRow(); };
            removeButton.Click += delegate { RemoveSelectedRows(); };
            saveButton.Click += delegate { SaveConfig(); };
            browseIdmButton.Click += delegate { BrowseIdmPath(); };
            checkButton.Click += delegate { RunInBackground(RunCheck); };
            updateButton.Click += delegate { RunInBackground(RunUpdate); };
            FormClosing += delegate { SaveConfig(); };
        }

        private Button MakeButton(string text)
        {
            return new Button { Text = text, AutoSize = true, Height = 27, Margin = new Padding(4, 0, 4, 0) };
        }

        private void AddTextColumn(string name, string header, int width)
        {
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = name, HeaderText = header, Width = width });
        }

        private void LoadConfig()
        {
            AppConfig config = null;
            if (File.Exists(configPath))
            {
                try
                {
                    config = new JavaScriptSerializer().Deserialize<AppConfig>(File.ReadAllText(configPath, Encoding.UTF8));
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
            var json = new JavaScriptSerializer().Serialize(config);
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
            AddRow(new AppEntry { LastInstalledTag = "未安装", UpdateMode = ModeArchive });
        }

        private void AddRow(AppEntry app)
        {
            var mode = NormalizeMode(app.UpdateMode);
            grid.Rows.Add(
                app.Name ?? "",
                app.InstallDir ?? "",
                app.GitHubUrl ?? "",
                string.IsNullOrWhiteSpace(app.LastInstalledTag) ? "未安装" : app.LastInstalledTag,
                app.PreferredAssetRegex ?? "",
                mode,
                app.SilentInstallArgs ?? "",
                "",
                "");
        }

        private void RemoveSelectedRows()
        {
            foreach (DataGridViewRow row in grid.SelectedRows)
            {
                if (!row.IsNewRow)
                    grid.Rows.Remove(row);
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
                var local = DetectLocalVersion(app);
                var status = IsNewer(local, release.Tag) ? "发现更新" : "已是最新";
                if (IsInstallerMode(app))
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

                var local = DetectLocalVersion(app);
                if (!IsNewer(local, release.Tag))
                {
                    SetStatus(rowIndex, local, release.Tag, "跳过：已是最新");
                    return;
                }

                var asset = SelectAsset(release, app.PreferredAssetRegex, IsInstallerMode(app));
                Log(app.Name + " 选择资产：" + asset.Name);
                var assetPath = DownloadAsset(app, release, asset, rowIndex);

                if (IsInstallerMode(app))
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
                SetStatus(rowIndex, app.LastInstalledTag, "", "失败：" + ex.Message);
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
            catch
            {
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
            request.UserAgent = "GitHubUniversalUpdater/1.1";
            request.Accept = accept;
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
            var releaseHtml = HttpGet("https://github.com/" + owner + "/" + repo + "/releases/tag/" + Uri.EscapeDataString(tag), "text/html");
            var release = new ReleaseInfo { Tag = tag, Source = "GitHub Web" };
            foreach (Match m in Regex.Matches(releaseHtml, @"href=""([^""]*/releases/download/[^""]+)""[^>]*>([^<]+)</a>", RegexOptions.IgnoreCase))
            {
                var href = WebUtility.HtmlDecode(m.Groups[1].Value);
                var name = WebUtility.HtmlDecode(m.Groups[2].Value).Trim();
                if (href.StartsWith("/"))
                    href = "https://github.com" + href;
                release.Assets.Add(new ReleaseAsset { Name = name, Url = href });
            }
            if (release.Assets.Count == 0)
                throw new InvalidOperationException("最新 Release 没有可下载资产");
            return release;
        }

        private ReleaseAsset SelectAsset(ReleaseInfo release, string preferredRegex, bool installerMode)
        {
            IEnumerable<ReleaseAsset> assets = release.Assets;
            if (!string.IsNullOrWhiteSpace(preferredRegex))
            {
                var regex = new Regex(preferredRegex, RegexOptions.IgnoreCase);
                var preferred = assets.Where(a => regex.IsMatch(a.Name)).ToList();
                if (preferred.Count > 0)
                    assets = preferred;
            }
            var allowed = installerMode
                ? new[] { ".exe", ".msi", ".msix", ".msixbundle" }
                : new[] { ".zip", ".7z", ".rar" };
            var filtered = assets.Where(a => allowed.Contains(Path.GetExtension(a.Name).ToLowerInvariant())).ToList();
            if (filtered.Count == 0)
                filtered = assets.ToList();
            return filtered.Select(a => new { Asset = a, Score = ScoreAsset(a.Name, installerMode) })
                .OrderByDescending(x => x.Score)
                .ThenByDescending(x => x.Asset.Size)
                .First().Asset;
        }

        private int ScoreAsset(string fileName, bool installerMode)
        {
            var n = fileName.ToLowerInvariant();
            var score = 0;
            if (ContainsAny(n, "win", "windows")) score += 40;
            if (ContainsAny(n, "x64", "amd64", "x86_64")) score += Environment.Is64BitOperatingSystem ? 30 : -20;
            if (ContainsAny(n, "arm64", "aarch64", "mac", "darwin", "linux")) score -= 60;
            if (installerMode && ContainsAny(n, "setup", "installer", "install")) score += 20;
            if (!installerMode && ContainsAny(n, "portable")) score += 10;
            if (n.EndsWith(".msi")) score += 16;
            if (n.EndsWith(".exe")) score += 12;
            if (n.EndsWith(".zip")) score += 10;
            return score;
        }

        private bool ContainsAny(string text, params string[] parts)
        {
            return parts.Any(p => text.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private string DetectLocalVersion(AppEntry app)
        {
            if (IsInstallerMode(app))
                return IsUsableSavedVersion(app.LastInstalledTag) ? app.LastInstalledTag : "未安装";
            var root = GetInstallRoot(app.InstallDir);
            var marker = Path.Combine(root, ".github-universal-updater-version");
            if (File.Exists(marker))
                return File.ReadAllText(marker, Encoding.UTF8).Trim();
            if (IsUsableSavedVersion(app.LastInstalledTag))
                return app.LastInstalledTag;
            return "未安装";
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
                DownloadWithIdm(GetIdmPathFromUi(), url, dir, fileName, target, app.Name, rowIndex);
            }
            else
            {
                DownloadWithWebClient(url, target, app.Name, rowIndex);
            }
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
            while (!File.Exists(targetPath) && DateTime.Now - start < TimeSpan.FromMinutes(30))
            {
                Thread.Sleep(1000);
                SetStatus(rowIndex, "", "", "等待 IDM 下载...");
            }
            if (!File.Exists(targetPath))
                throw new IOException("IDM 下载超时");
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

        private string DefaultSilentArgs(string installerPath)
        {
            var ext = Path.GetExtension(installerPath).ToLowerInvariant();
            if (ext == ".msi") return "/qn /norestart";
            if (ext == ".msix" || ext == ".msixbundle") return "";
            return "/S";
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
            return ModeArchive;
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
