# GitHub 通用一键更新器

双击 `GitHubUniversalUpdater.exe` 运行。

功能：

- 支持多组软件安装目录，也可直接填写主程序 `.exe` 路径。
- 每组软件填写对应 GitHub 仓库地址。
- 自动通过 GitHub Releases 网页检查最新版本，不依赖 GitHub API。
- 自动选择 `zip`、`7z`、`rar` 压缩包资产，优先选择 Windows 相关文件。
- 自动读取当前系统架构并按资产名打分，降低 Windows x64 机器误选 ARM64/macOS/Linux 包的概率。
- 下载时自动在 GitHub 资产链接前添加 `https://gh-proxy.org/` 加速。
- 支持多线程分段下载；服务器不支持分段时自动回退单线程。
- 可选调用本地 Internet Download Manager 下载；会自动识别常见 IDM 安装路径，也可手动选择 `IDMan.exe`。
- 状态栏实时显示下载进度和平均下载速度。
- 解压后覆盖安装目录。
- 更新结束后自动清理下载压缩包和解压临时目录。
- 配置保存在同目录 `apps.json`。

注意：

- 写入 `Program Files` 通常需要管理员权限，exe 已配置为管理员运行。
- `rar` 和 `7z` 解压依赖系统 `tar.exe` 或 7-Zip。
- 本工具不备份安装目录，避免占用大量磁盘空间。
- 如果某个仓库有多个下载资产，可在“资产筛选(可选)”列填写正则，例如 `(?i)windows.*\.rar$`。
