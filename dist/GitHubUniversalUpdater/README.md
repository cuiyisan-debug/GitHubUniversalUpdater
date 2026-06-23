# GitHub 通用一键更新器

双击 `GitHubUniversalUpdater.exe` 运行。

## 功能

- 支持多组软件安装目录，也可直接填写主程序 `.exe` 路径。
- 每行提供“选择”按钮，可手动选择安装目录或主程序 `.exe`。
- 每组软件填写对应 GitHub 仓库地址。
- 自动通过 GitHub Releases 检查最新版本。
- 自动选择 `zip`、`7z`、`rar` 压缩包资产，优先选择 Windows 相关文件。
- 自动识别 `.exe`、`.msi`、`.msix`、`.msixbundle` 安装包资产，识别后后台静默安装。
- 自动读取当前系统架构并按资产名打分，降低 Windows x64 机器误选 ARM64/macOS/Linux 包的概率。
- 支持“解压覆盖”模式：下载压缩包、解压后覆盖安装目录。
- 静默安装不读取本地软件版本，只按 GitHub Release tag 和 `LastInstalledTag` 判断是否已经安装过。
- 安装包静默参数为空时，`.msi` 默认使用 `/qn /norestart`，`.exe` 默认使用 `/S`。
- 支持资产筛选正则，例如 `(?i)(setup|installer).*\.(exe|msi)$`。
- 下载时自动在 GitHub 资产链接前添加 `https://gh-proxy.org/` 加速。
- 支持多线程分段下载；服务器不支持分段时自动回退单线程。
- 可选调用本地 Internet Download Manager 下载；IDM 下载未产出文件时自动回退内置多线程下载。
- 状态栏实时显示下载进度。
- 更新结束后自动清理下载文件和临时目录。
- 配置保存在同目录 `apps.json`。

## 注意

- 写入 `Program Files` 通常需要管理员权限，exe 已配置为管理员运行。
- 不同安装包的静默参数不完全相同，如需指定参数，可在 `apps.json` 中配置 `SilentInstallArgs`。
- `rar` 和 `7z` 解压依赖系统 `tar.exe` 或 7-Zip。
- 本工具不备份安装目录，避免占用大量磁盘空间。
