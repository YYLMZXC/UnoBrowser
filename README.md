# Uno浏览器

一个基于 **Uno Platform** 构建的跨平台浏览器应用，主打网页资源浏览、下载与管理。

- 中文名：**Uno浏览器**
- 包标识：`com.companyname.unobrowser.yylmzxc001`
- 主页：`https://www.bing.com`

---

## 功能特性

- **跨平台 WebView2 内核**：基于 Uno Platform 的 WebView2 封装，在各平台映射为原生浏览器
  - Windows / 桌面 Skia：Edge WebView2
  - Android：Android WebView
  - iOS：WKWebView
- **浏览器基础操作**：后退、前进、刷新、地址栏跳转、主页返回
- **UA 自适应**：可在「跟随系统 / Windows / 安卓 / iPhone / Linux / macOS」之间切换
- **下载管理**：拦截页面下载请求，支持下载列表查看、打开下载文件夹、清除下载历史
- **浏览历史**：记录访问历史，支持点击跳转与一键清除
- **设置面板**：浏览器设置、下载管理、历史记录三个标签页，覆盖在主界面之上（无遮罩）

---

## 技术栈

| 项目 | 说明 |
| --- | --- |
| 框架 | Uno Platform（Uno.Sdk）+ WinUI |
| 语言 | C# (.NET 10) |
| 架构 | MVVM（CommunityToolkit.Mvvm） |
| 序列化 | Newtonsoft.Json |
| 目标平台 | `net10.0-android`、`net10.0-ios`、`net10.0-desktop` |

---

## 目录结构

```
src/UnoBrowser.UnoApp/UnoBrowser.UnoApp/
├── App.xaml(.cs)            应用入口、单实例启动逻辑
├── UnoBrowser.UnoApp.csproj 项目文件（含 Android 签名配置）
├── Views/                   UI 页面
│   ├── MainPage.xaml(.cs)   主界面：工具栏 + 浏览器宿主
│   ├── SettingsPanel.xaml   设置/下载/历史面板
│   └── DownloadListPanel.xaml
├── ViewModels/             MVVM 视图模型
│   ├── MainViewModel.cs     主页导航、地址栏、当前标签
│   ├── SettingsViewModel.cs 设置 + 下载 + 历史整合
│   └── DownloadListViewModel.cs
├── Services/               服务层
│   ├── BrowserProvider.cs   WebView2 生命周期与事件管理
│   ├── SettingsService.cs   设置持久化（含旧版迁移）
│   ├── DownloadService.cs / DownloadHistoryService.cs
│   └── AppPaths.cs          跨平台数据目录
├── Models/                 数据模型（AppSettings、DownloadRecord 等）
├── Converters/             XAML 值转换器
├── Platforms/             各平台启动入口（Desktop/Android/iOS）
├── Assets/                图标与资源
├── key/                   Android 签名密钥（scassistant.keystore）
└── Properties/            launchSettings.json
```

---

## 构建与运行

### 前置要求

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- 对应平台工作负载：
  - 桌面：`dotnet workload install wasm-tools` 或相应桌面负载
  - Android：`dotnet workload install android`
  - iOS（仅 macOS）：`dotnet workload install ios`
- Windows 桌面运行时需安装 [WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/)

### 还原与构建

```bash
# 进入项目目录
cd src/UnoBrowser.UnoApp/UnoBrowser.UnoApp

# 还原依赖
dotnet restore

# 运行桌面版（win-x64）
dotnet run -f net10.0-desktop

# 构建 Android 包
dotnet build -f net10.0-android -c Release
```

> 说明：项目为多目标（Single Project），`dotnet restore` 时若 IDE 传入了错误的 `RuntimeIdentifiers`，csproj 已通过条件属性将 android/ios 固定为其平台 RID，桌面保持 `win-x64`。

---

## 配置说明

### Android 签名

签名密钥位于 `key/scassistant.keystore`（别名 `scassistant`）。相关配置在 `UnoBrowser.UnoApp.csproj`：

```xml
<AndroidKeyStore>true</AndroidKeyStore>
<AndroidSigningKeyStore>key\scassistant.keystore</AndroidSigningKeyStore>
<AndroidSigningKeyAlias>scassistant</AndroidSigningKeyAlias>
```

> 密钥与证书绑定，重命名会导致发布签名失败，请勿修改文件名。

### 数据目录

- **Android**：应用专属外部存储（不可用时回退内部存储）
- **桌面**：程序所在目录（便携式，数据随 exe）；程序目录不可写时回退 `%LocalAppData%/UnoBrowser`
- 旧版 `SCAssistant` 路径下的设置与下载历史会在首次运行（桌面）时自动迁移，避免升级后数据丢失

---

## 许可证

见仓库 LICENSE 文件（如有）。
