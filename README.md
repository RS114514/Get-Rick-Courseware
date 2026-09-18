# 获取 Rick 课件（Get-Rick-Courseware）

[![Windows](https://img.shields.io/badge/platform-Windows-blue)](https://github.com/RS114514/Get-Rick-courseware)
[![macOS](https://img.shields.io/badge/platform-macOS-lightgrey)](https://github.com/RS114514/Get-Rick-courseware)
[![language](https://img.shields.io/badge/language-C%23-178600)](https://github.com/RS114514/Get-Rick-courseware)
[![license](https://img.shields.io/badge/license-MIT-green)](https://github.com/RS114514/Get-Rick-courseware/blob/main/LICENSE)

一个轻量、高效的跨平台 U 盘课件自动备份小工具。插入 U 盘后自动将课件备份归档到指定文件夹或校园 NAS 网络盘。
- **Windows 版本**：基于 C# WinForms 开发，单文件仅约 680KB，无外部运行库依赖，深度适配学校教学机、4K 触控一体机与校园网络环境。
- **macOS 版本**：基于 C# Avalonia UI 框架开发，提供原生一致的视觉界面。

---

## 核心特性

- **4K 一体机高分屏动态自适应** — 动态感知屏幕 DPI 缩放比率（完美适配 200%~300% 4K 教学一体机），窗口、功能控件与字体成比例等比自适应拉伸，彻底杜绝文字截断与界面排版错乱。
- **开机与后台彻底静默常驻** — 启动阶段与开机自启全程静默常驻系统托盘，零弹窗、零打扰。仅在双击托盘图标或托盘菜单选择“显示主窗口”时展示界面。
- **春晖 NAS 网络映射盘与 UNC 解析** — 引入 Win32 `WNetGetConnection` API，精准识别网络映射盘（如 `Z:\课件`）与底层真实 UNC 路径；即使开机阶段网络未就绪，已配置的路径亦完整保留，绝不清空。
- **离线本地暂存与网络恢复自动同步** — 春晖 NAS 离线或网络断开时，插入 U 盘自动将课件安全暂存至本地缓存目录，并弹出 Windows 10/11 系统原生横幅通知：
  > **“云上春晖未连接，课件已暂存本地，等待网络恢复自动同步”**  
  网络恢复后，后台自动将本地暂存的课件平滑迁移至春晖 NAS 目标目录，内置防递归、防自删等安全防护机制。
- **极速免安装与广泛兼容** — 采用 AnyCPU 独立绿色单文件设计，完美兼容 Windows 7 / 10 / 11 各类硬件设备。
- **U 盘智能检测与精准屏蔽** — 插入 U 盘秒级响应；支持一键屏蔽特定 U 盘，通过硬件卷序列号与卷标唯一识别，避免重复拷贝个人工作盘。
- **规整归档与文件占用保护** — 每次备份自动生成规范目录：`日期_时间_盘符/卷标_U盘名称`；对被进程独占的文件安全跳过并记录，保证队列稳定。
- **macOS 专属优化** — 精准过滤外接移动硬盘与虚拟磁盘镜像（DMG），自动跳过 `.DS_Store`、`.Trashes` 等系统冗余文件。
- **运行日志持久化** — 日志自动按日期保存至 `logs/` 目录下，便于排查与追溯。

---

## 使用须知

- 本工具**仅用于备份您本人拥有的课件与 U 盘文件**。
- **请勿**在未经同意的情况下拷贝他人 U 盘里的文件。
- 备份文件请妥善保管，遵守学校与机构的数据安全管理规范。

---

## 环境要求

### Windows 平台
- 支持 Windows 7 / 8 / 10 / 11（x86 / x64 / ARM64）
- 免安装绿色版：直接双击 `Rick.exe` 即可运行，无需预装庞大的运行库。

### macOS 平台
- macOS 11.0 及以上（支持 Apple Silicon M 系列芯片）
- 免安装版：解压后直接双击运行 `获取Rick课件.app`。

---

## 使用方法

### 下载运行
前往 [Releases](https://github.com/RS114514/Get-Rick-courseware/releases) 页面获取最新版本：
* **Windows 用户**：下载 `Rick.exe`（或 `获取Rick课件.exe`），双击启动即可直接使用。
* **macOS 用户**：下载 `Rick-macOS.zip`，解压得到 `获取Rick课件.app`，拖入应用程序文件夹使用。

### 基本操作
1. 打开程序，点击 **「浏览」** 选择课件保存文件夹（支持本地磁盘路径或春晖 NAS 映射网络盘 `Z:\...`）。
2. 点击 **「启动监控」**（开机自启模式下会自动在后台静默开启）。
3. 插入 U 盘后，程序自动静默进行复制，并在主界面底部显示进度条与日志。
4. 若网络盘离线，程序会自动暂存并在右下角弹出 Win10 通知，网络恢复后自动完成同步迁移。
5. 勾选 **「开机自动启动」** 可随系统登录常驻后台。
6. 在“当前U盘”下拉列表中选中设备并点击 **「屏蔽此U盘」**，可将其加入黑名单不再自动备份。

---

## 本地编译与构建

### 💻 构建 Windows 官方版本
在仓库根目录下执行构建脚本：
```bash
# macOS / Linux (需安装 mono-devel)
chmod +x build-all.sh
./build-all.sh
```
编译成功后，产物将生成在 `./publish-win/Rick.exe`（同时生成别名 `./publish-win/获取Rick课件.exe`）。

在 Windows 环境下亦可直接双击根目录下的 **`build-win.bat`** 进行构建。

### 🍏 构建 macOS 版本
在 macOS 终端进入 `mac/` 目录：
```bash
cd mac
chmod +x build-app.sh
./build-app.sh
```
生成产物位于 `./mac/获取Rick课件.app`。

---

## 运行自动化测试

项目内置了针对 DPI 缩放、离线网络暂存、后台同步及路径有效性的完整自动化测试套件：
```bash
# 运行综合测试套件 (13项用例)
csc -target:exe -main:RickCourseware.Tests.ComprehensiveTestRunner \
    -out:test.exe -r:System.Windows.Forms.dll -r:System.Drawing.dll -r:System.dll -r:System.Core.dll -r:System.Management.dll -r:System.Data.dll \
    -d:WINDOWS rick.cs USBMonitor.cs Settings.cs RickCourseware.Tests/ComprehensiveTestRunner.cs
mono test.exe && rm -f test.exe
```

---

## 开源协议

基于 [MIT 协议](LICENSE) 开源。
