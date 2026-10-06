# 构建、测试与打包

[文档目录](README.md) · [工程架构](architecture.md) · [贡献指南](../CONTRIBUTING.md)

## 工具与平台

- `.NET 10 SDK`：仓库 [`global.json`](../global.json) 以 `10.0.100` 为基准，允许兼容的后续 feature band，不使用预览版。
- macOS：Xcode Command Line Tools，提供 `xcrun`、Swift、Clang 和 SDK；构建还使用 Python 3、Bash。
- Linux x64：Clang、可用的 LTO 链接环境、Python 3、Bash，以及 Skia／ONNX 所需运行库。
- Windows：.NET 10 SDK、PowerShell 5 或更新版本，以及 Zig 0.13.0。原生规则组件由构建自动生成；统一 Bash 验证入口仍用于 macOS／Linux。

首次 macOS 构建会编译 Swift 桥接和当前 RID 的 C++ 规则程序。构建产物不要提交源码仓库。

## 获取源码与资源

```bash
git clone https://github.com/PaddiSoft/PaddiChess.git
cd PaddiChess
```

固定版本的三平台引擎、NNUE、OCR 和测试资源直接随 Git 仓库提供，无须 Git LFS 或额外资源下载步骤。正常克隆后可运行下述验证；若手动复制了部分源码或过滤了二进制文件，请先恢复仓库中的完整资源。

| 资源 | 预期路径 | 来源说明 |
| --- | --- | --- |
| 三平台执棋引擎 | `Pikafish.2026-09-25/Pikafish-{平台}-universal*` | [引擎来源与 SHA-256](../PaddiChess/Packaging/Engine-README.md) |
| NNUE | `Pikafish.2026-09-25/pikafish.nnue` | 与该发行包配套，保留原 NNUE 许可 |
| OCR 模型 | `PaddiChess/Assets/Ocr/ch_PP-OCRv5_rec_mobile.onnx` | [来源与 SHA-256](../PaddiChess/Assets/Ocr/SOURCE.md) |
| 测试样本 | `PaddiChess.Tests/Fixtures/` | 保留随样本提交的来源说明 |
| 规则源码 | `PaddiChess/Native/PikafishRules/upstream/` | [0906 规则源码说明](../PaddiChess/Native/PikafishRules/SOURCE.md) |

发布按目标 RID 选择对应引擎；完整验证同时检查三平台发行资源，因此仅保留宿主的那个文件仍无法通过资源检查。资源版本应与清单匹配；不能随意用“最新”模型或权重替代。

应用代码许可不覆盖全部资源：NNUE 按其随附协议使用，包含未经允许不得商用的限制。0925 主引擎随原始发行包分发，上游项目与源码入口见资源表中的来源说明；尚未独立核验该二进制与具体上游提交的对应关系。0906 规则组件源码 ZIP 仅对应独立的 `PaddiRules`，不能作为 0925 主引擎的对应源码。

## 完整验证入口

在 macOS 或 Linux x64 的仓库根目录运行：

```bash
bash scripts/verify.sh
```

脚本依次执行：

1. 检查 `dotnet`、Python、Clang，以及宿主所需工具。
2. 检查引擎、NNUE、OCR 和关键测试图大小；核验 OCR 哈希，拒绝缺失文件与占位文本。
3. `dotnet restore PaddiChess.slnx --locked-mode`。
4. Release 构建，使用 `-warnaserror`。
5. 在该构建结果上运行完整测试项目，保存 TRX。

输出位于 `artifacts/verification/<运行标识>/`，包括 SDK 信息、还原／构建／测试日志和 `verification.trx`。布局图位于 `artifacts/ui/`。该入口不发送真实鼠标输入、不为真实游戏发起对局，也不创建发行包、不使用开发者证书签名或公证；macOS 原生组件构建会执行本地 ad hoc 签名。

通过数量、跳过数量和运行平台应一起记录。Windows 专用测试在非 Windows 上跳过属于范围说明；跨编译不等于实机通过。当前分支的最新结果以完整验证日志和对应 Release 为准，文档不固定一个会随新增测试失效的总数。

## 运行与定向测试

完整验证后运行：

```bash
dotnet run --project PaddiChess/PaddiChess.csproj --configuration Release --no-restore
```

开发中的定向回归示例：

```bash
dotnet test PaddiChess.Tests/PaddiChess.Tests.csproj \
  --configuration Release --filter FullyQualifiedName~ExternalHistory
```

定向结果用于快速定位，不替代发布前完整 gate。UI 测试使用 Avalonia Headless；截图测试需要确保实际渲染帧产生，不能以空截图文件检查代替布局验证。

测试可通过 `PADDI_SETTINGS_PATH` 隔离偏好和历史目录，避免污染个人配置。网络协议测试优先使用模拟处理器；CI 不应依赖个人 API Key。

## 锁文件

[`Directory.Build.props`](../Directory.Build.props) 启用 nullable、分析器、确定性构建与 NuGet 锁文件。普通构建使用 `packages.lock.json`；跨平台发布使用相应 `packages.<RID>.lock.json`。

有意升级依赖时，检查项目引用与锁文件差异，再完整验证。例如：

```bash
dotnet restore PaddiChess.slnx --force-evaluate
for rid in osx-arm64 osx-x64 win-x64 linux-x64; do
  dotnet restore PaddiChess/PaddiChess.csproj \
    -p:RuntimeIdentifier="$rid" --force-evaluate
done
dotnet restore PaddiChess.slnx --locked-mode
```

显式 `RuntimeIdentifier` 让项目引用在属性求值时选择对应锁文件。常规验证和打包不应自动改写依赖锁。

## 原生规则构建

原生规则由 `Native/PikafishRules/build.sh` 构建：

```bash
bash PaddiChess/Native/PikafishRules/build.sh osx-arm64
```

支持 `osx-arm64`、`osx-x64`、`win-x64`、`linux-x64`。macOS 上跨编译 Windows／Linux 需要 Zig，可用 `PADDI_ZIG` 指向已安装的可执行文件：

```bash
PADDI_ZIG="$(command -v zig)" \
  bash PaddiChess/Native/PikafishRules/build.sh win-x64
```

Windows 宿主的 MSBuild 会调用 `Native/PikafishRules/build.ps1`，使用与 Bash 脚本相同的 C++ 源文件和编译参数生成 `PaddiRules.exe` 及对应源码 ZIP。先安装 Zig 0.13.0，并将 `zig.exe` 加入 PATH，或显式指定：

```powershell
$env:PADDI_ZIG = 'C:\tools\zig\zig.exe'
dotnet restore 'PaddiChess.slnx' --locked-mode
dotnet build 'PaddiChess/PaddiChess.csproj' -c Release --no-restore
dotnet test 'PaddiChess.Tests/PaddiChess.Tests.csproj' -c Release
```

此入口已完成代码审查；Windows 二进制经过 macOS 交叉构建，PowerShell 构建流程仍需要 Windows 本机验收。

## 打包

macOS 上的本地包示例：

```bash
PADDI_SIGN_IDENTITY=- bash PaddiChess/Packaging/package.sh arm
PADDI_ZIG="$(command -v zig)" bash PaddiChess/Packaging/package.sh windows
```

其他目标为 `intel`、`linux`、`all`；输出默认在 `dist/`，可用 `PADDI_DIST_DIR` 改变。打包脚本发布自带运行时的应用，保留引擎、模型、规则组件、许可与源码 ZIP。

`PADDI_SIGN_IDENTITY=-` 明确选择 ad hoc 签名。正式签名可通过运行环境提供自己的有效身份，不能把私人证书、指纹、私钥或钥匙串导出物提交仓库。签名、公证与平台验收是独立步骤；成功 `dotnet publish` 不表示完成这些步骤。

Windows ZIP 需要保持 UTF-8 文件名及完整目录。发布前应核对可执行文件架构、ZIP 完整性、内置资源和许可，不向 Windows 包混入 macOS 程序、个人设置或棋谱。

## CI 与验证范围

[GitHub Actions 工作流](../.github/workflows/verify.yml) 在 macOS runner 使用相同验证入口，按锁文件缓存依赖，并保存日志、TRX 和布局图。仓库提供工作流不等于某个提交已经 CI 通过；应查看对应提交的实际运行。

| 验证 | 能说明 | 不能替代 |
| --- | --- | --- |
| 单元／协议／状态机测试 | 夹具覆盖的规则、取消、历史与错误行为 | 任意真实游戏兼容性 |
| Headless UI 与截图 | 指定尺寸的布局、选择与复盘行为 | 所有系统字体／DPI 实机表现 |
| 固定样本识别回归 | 给定主题／动画／棋局的识别结果 | 全部主题与真实捕获时序 |
| 原生窗口实验 | 该系统与测试窗口的真实输入／捕获 | 第三方客户端接受后台输入 |
| 跨编译与包校验 | 产物架构、资源、归档一致性 | 目标系统实际运行与对局 |

新增平台 CI 前，应补齐该平台原生依赖与实际运行测试，避免把跳过关键路径的绿灯当作支持证明。

## 发布前检查

```bash
python3 scripts/audit-publication.py
python3 scripts/audit-publication.py --release dist/Paddi象棋-Windows-x64.zip
```

检查仅打印相对路径与问题类别，不回显凭据。Release 使用嵌入调试符号及源路径映射，避免将开发机主目录写入程序集。第三方包的许可正文和 notices 位于 `Packaging/ThirdParty`，随发布复制；更新依赖后运行 `python3 scripts/collect-dependency-licenses.py` 并审核来源变化。API 默认地址和模型为空，打包不读取本机用户设置。
