# 安装与更新

[文档目录](README.md) · [下一步：快速上手](usage.md)

## 选择发布包

从 [PaddiSoft/PaddiChess Releases](https://github.com/PaddiSoft/PaddiChess/releases) 获取正式附件。GitHub 自动生成的 `Source code.zip` 是源码，不是可直接运行的桌面发布包。

| 系统 | 包名 | 启动入口 |
| --- | --- | --- |
| macOS Apple Silicon | `PaddiChess-macOS-AppleSilicon.zip` | `Paddi象棋.app` |
| Windows x64 | `PaddiChess-Windows-x64.zip` | `Paddi象棋.exe` |
| macOS Intel | 若该版本提供 `Paddi象棋-macOS-Intel.zip` | `Paddi象棋-Intel.app` |
| Linux x64 | 若该版本提供 `Paddi象棋-Linux-x64.tar.gz` | `Paddi象棋` |

发布包自带 .NET 运行时、本机引擎、NNUE、原生规则组件、象棋识别模型和 OCR 模型。完整解压，保留目录结构；不要只复制 `.exe` 或应用包内主程序。CPU 架构应与发布目标匹配。Linux 外部接管尚未实现；Intel 与 Linux 是否提供本次预构建附件，以 Release 为准。

Windows 版要求 Windows 10 1903（18362）或更新系统。包内 `onnxruntime.dll`、`DirectML.dll` 与 `Microsoft.Windows.AI.MachineLearning.dll` 是一套匹配的识别运行库，请勿混用旧版 DLL。GPU 自动检测，无需安装 CUDA；NPU 的厂商提供程序要求 Windows 11 24H2 或更高，缺少或不兼容时仍可用 GPU / CPU。

交叉编译结果不等于完整系统兼容性矩阵。Windows 的 GDI、DPI 和输入路径需要在 Windows 实际验证；macOS 原生桥接构建目标也不代表所有旧系统均已实测。

## macOS

1. 完整解压 ZIP，把 `.app` 放到固定位置，例如“应用程序”。
2. 打开应用。若系统要求确认来源，检查下载仓库和该版本签名说明后，使用 macOS 提供的“打开”流程。
3. 先确认本地棋盘与引擎正常。只有外部接管需要录屏和输入权限。
4. 进入“外部接管”，按页面提示在系统“隐私与安全性”中授权屏幕录制与辅助功能。不同系统版本的录屏权限项目名称可能略有不同。
5. 返回应用，点击“我已授权，重新检测”；必要时按系统提示重启应用。

常规权限检查只查询状态，主动请求授权由对应按钮触发。应用不会修改系统 TCC 数据库，也不要求关闭系统安全保护。

Release 会标明签名与公证状态。本地构建可使用 ad hoc 签名，这不等于 Apple 公证。应用路径、签名身份或系统设置变化后，可能需要重新授权；先核对正确的应用副本，避免同时运行多个旧版本。

macOS 14 及以上的捕获路径使用常驻 ScreenCaptureKit 会话；较早系统使用兼容截图路径。截图可用性仍受目标窗口是否渲染、遮挡方式、镜像软件与系统行为影响，不承诺任意后台或跨桌面场景都可用。

## Windows

1. 将 ZIP 完整解压到可写目录。
2. 双击 `Paddi象棋.exe`，不需要另装 .NET。
3. 保留 `Engine`、`Native`、`Assets`、DLL 和其他运行时文件。
4. 接管前保持目标窗口可用，核对显示缩放与棋盘定位。切换显示器、DPI 或窗口大小后重新核对。

捕获与输入受目标绘制方式、进程权限和桌面状态影响。跨编译和 ZIP 校验不能证明实际游戏已接收点击。本地棋盘正常、外部黑屏或无响应时，按[排查指南](troubleshooting.md)检查兼容性。

## 更新、备份与卸载

- 更新前暂停接管并关闭应用，再替换整个应用或完整发布目录。
- 偏好和自动历史位于用户数据目录，不在发布包中；替换程序通常不会删除这些数据。
- 手动导出的棋谱在你选择的位置，建议与自动历史一起备份。
- 旧 `PikaDesk` 目录会在正常设置加载时复制缺失文件到新 `Paddi象棋` 目录，不覆盖新文件、不删除旧目录。
- 删除程序目录只移除程序。要移除设置、棋谱和学习样式，还需处理对应数据目录；位置和内容见[数据与隐私](privacy-and-data.md)。

应用启动不要求配置模型 API。本地引擎对弈与本地识别可独立使用；主动使用模型功能时才需要服务地址、Key 和模型权限。

## 第三方许可文件

Windows 便携包的依赖许可位于 `Licenses`；macOS 应用内位于 `Contents/Resources/Licenses`。引擎、NNUE 与原生规则组件另外保留其来源、许可和源码说明。
