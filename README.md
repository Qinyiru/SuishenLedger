# 随身赊账本

当前版本：1.3.2。把 `dist` 文件夹完整复制到U盘，双击 `SuishenLedger.exe` 或带版本号的新版 EXE 即可使用。首次打开会创建密码和 `Data/ledger.dat` 加密账本；更新软件时必须保留整个 `Data` 文件夹。

## 已实现

- 12号字体界面，客户、期初欠款、商品、销售和还款记录
- 一张销售清单录入多个商品，支持检索、自动或手动金额、撤回和加密草稿恢复
- “上传到账单”会把整张销售清单写入该客户账单，修改和删除无需填写理由
- 正式上传销售清单时，自动把首次录入的商品登记到商品库
- 指定客户或全部客户查账，支持日期/客户排序、修改、删除和操作留痕
- 销售清单与查账单导出标准 `.xlsx` 表格，无需安装 Excel
- A4 横向打印/PDF，12号表格字，自动列宽、换行、分页并重复表头；查账导出的表格和 PDF 备注栏留白
- 加密账本、手动加密备份与恢复；恢复前自动备份当前账本
- GitHub Releases 检查更新、下载进度、超时、SHA-256、自检、升级前备份和失败回退

## 构建和打包

在 PowerShell 中运行：

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

生成给别人试用的干净压缩包（不包含本机 `Data` 账本）：

```powershell
powershell -ExecutionPolicy Bypass -File .\package.ps1
```

压缩包会生成在 `dist\packages`，首次运行时软件会在U盘旁边创建新的 `Data` 文件夹。

## 发布更新

在“设置”填写 GitHub 仓库 `owner/repository`。GitHub Release 标签使用 `v1.3.2` 形式，并上传两个附件：

- `suishen-ledger.exe`
- `suishen-ledger.exe.sha256`，内容为 EXE 的小写 SHA-256

更新只替换 EXE，不覆盖 `Data`。发布新版本前应递增 `Program.cs` 中的 `AssemblyVersion`。

项目已包含 `.github\workflows\release.yml`：把代码推送到 GitHub 后，推送 `v*` 标签即可自动构建并发布试用压缩包。GitHub Actions 使用仓库自带的 `GITHUB_TOKEN`，不需要把个人密码写进软件。

## 数据说明

账本只保存在U盘本地，软件不会上传业务数据。请定期把 `.szbbackup` 备份到另一块存储设备。导出的 `.xlsx` 和打印/PDF 文件不受账本密码保护。
