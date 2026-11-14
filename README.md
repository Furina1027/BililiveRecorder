<div style="text-align:center">
<img width="20%" src=".github/assets/logo.svg">

# mikufans录播姬

</div>

> 本仓库是 [BililiveRecorder/BililiveRecorder](https://github.com/BililiveRecorder/BililiveRecorder) 的本地分支（基于上游 v2.20.0 / `a2c5b39`），当前版本 **v3.0.0**。

## 相对上游的改动（v3.0.0）

- **全项目统一 net10.0**：WPF 从 .NET Framework 4.7.2 老式工程迁移为 SDK 风格 `net10.0-windows`，与 Cli/Web 同走 `dotnet` 工具链；移除 net472 条件编译与全部 polyfill 包
- **新增：录制文件夹容量限制**（房间级/全局，GB 口径）——文件夹总量超限后自动从最早的录像开始删除，弹幕与封面永不删除，正在录制的文件不受影响；每次新建录制文件时在后台线程检查，不阻塞录制管线
- **修复一批**：弹幕握手失败后的假连接状态、房间删除后 timer 泄漏、录制事件 NRE 竞态、封面 URL 带参数保存失败、DNS 过滤空数组越界、直播流重定向无限循环、弹幕 XML 时间戳在逗号小数区域损坏、Web BasicAuth 时序侧信道、CLI IPv6 绑定、单实例 Mutex 遗弃处理等
- **移除**：公告页与检查更新（Squirrel 更新循环）、除简体中文外的全部语言资源、Sentry 上报保留但不再弹更新
- **单实例**：.NET Remoting IPC（已从现代 .NET 移除）重写为 Mutex + NamedPipe
- 分段与容量限制的单位统一为 **GB**（GiB 口径，与资源管理器显示一致）

## 构建

需要 [.NET SDK 10](https://dotnet.microsoft.com/download/dotnet/10.0)（global.json 已固定 10.0.100/latestMajor）与完整 git 历史（版本号由 GitVersion 生成）。

```powershell
dotnet publish BililiveRecorder.WPF/BililiveRecorder.WPF.csproj -c Release -o <输出目录>
```

一条命令同时构建 Core/Flv/ToolBox/Web 并产出 WPF 桌面版。版本号规则：打 `vX.Y.Z` tag 即为该版本，之后每个提交自动递增 patch（Mainline 模式，见 `GitVersion.yml`）。

## 部署与配置

- 发布产物为绿色版文件夹，直接运行 `BililiveRecorder.WPF.exe`
- 配置与房间列表保存在**工作目录**的 `config.json`（程序根的 `path.json` 记录工作目录指针），备份工作目录 = 备份一切
- 录像默认输出到工作目录下按主播分的子文件夹，受容量限制循环管理

## 功能

- 主播开播后自动开始录制（弹幕长连接 `PREPARING` 信号，秒级响应）
- 同时录制多个直播间，自动修复 mikufans 直播服务器导致的各种问题
- 工具箱模式，用于修复旧版录播姬或其他软件录的视频文件<sup>1</sup>
- 纯 C# 实现，无 ffmpeg 等 native 依赖<sup>2</sup>

<sup>1</sup>：仅限未经处理的直接从直播服务器下载的原始 FLV 文件；FFmpeg 录制或处理过的文件无法修复。  
<sup>2</sup>：桌面版内含 mini 版 FFmpeg，用于工具箱的转封装功能。

## 上游资源

- 上游仓库：[BililiveRecorder/BililiveRecorder](https://github.com/BililiveRecorder/BililiveRecorder)（dev 分支）
- 安装使用教程（上游）：[rec.danmuji.org](https://rec.danmuji.org)
- 许可证见 [LICENSE](LICENSE)
