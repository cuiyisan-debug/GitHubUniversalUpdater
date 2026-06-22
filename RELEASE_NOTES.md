# v1.1.0

- 新增“更新方式”列：支持“解压覆盖”和“静默安装”两种模式。
- 新增安装包后台静默安装：可从 GitHub Release 下载 `.exe`、`.msi`、`.msix`、`.msixbundle` 安装包并后台运行。
- 新增“静默参数”列：可为不同安装器填写 `/S`、`/silent`、`/verysilent`、`/qn /norestart` 等参数。
- 静默安装模式不读取本地软件版本，只按 GitHub Release tag 与 `LastInstalledTag` 判断是否已安装过。
- 更新 README、使用说明和示例配置，增加安装包模式示例。
