# v1.1.1

- 回到 v1.0.6 的完整更新器能力基线：GitHub API + 网页解析、资产筛选、系统架构资产打分、gh-proxy 加速、多线程分段下载、IDM 回退、临时文件清理。
- 在原能力基础上新增“更新方式”列：支持“解压覆盖”和“静默安装”两种模式。
- 新增安装包后台静默安装：可从 GitHub Release 下载 `.exe`、`.msi`、`.msix`、`.msixbundle` 安装包并后台运行。
- 新增“静默参数”列：可为不同安装器填写 `/S`、`/silent`、`/verysilent`、`/qn /norestart` 等参数。
- 静默安装模式不读取本地软件版本，只按 GitHub Release tag 与 `LastInstalledTag` 判断是否已安装过。
