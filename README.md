# Paddi象棋 · PaddiChess

> [!IMPORTANT]
> **Paddi象棋永久免费。作者不销售付费版，谨防冒充官方的收费版本。**
>
> 如果你觉得好用，可以 **[❤️ 给作者一点小小的赞助心意](https://paddisoft.com/creators/paddi#creator-support-title)**。
>
> 赞助完全自愿，不影响功能使用。

基于 **.NET 10 + Avalonia** 的桌面象棋客户端，把本机 UCI 引擎、大模型对弈、棋谱复盘和外部棋盘接管放在同一个工作台中。

[发行版本](https://github.com/PaddiSoft/PaddiChess/releases) · [使用文档](docs/README.md) · [构建与测试](docs/development.md) · [问题反馈](https://github.com/PaddiSoft/PaddiChess/issues) · [参与贡献](CONTRIBUTING.md)

## 作者测试：JJ 象棋 145 连胜

作者使用 Paddi象棋在 JJ 象棋实战接管测试中取得 **145 连胜、100% 胜率，登上棋圣**。以下截图为作者本次测试的战绩与接管界面；测试结果不保证其他版本、设置、对手或运行环境下的胜率。

<img src="docs/images/jj-145-win-streak.png" alt="作者测试的 JJ 象棋 145 连胜、棋圣 1 战绩与 Paddi象棋接管界面" width="1200">

## 可以做什么

- **本地对弈与分析**：红黑双方分别选择人类、引擎或模型，查看评分、候选路线、将杀与终局结果。
- **多引擎插件**：管理本机 UCI 象棋引擎版本，独立设置 NNUE 和引擎声明的规则选项。默认随包 Pikafish 2026-09-25。
- **多服务、多模型**：连接兼容的模型 API，分别设置双方模型和思考等级，保留服务实际返回的可见思考与说明。
- **外部棋盘接管**：定位目标窗口，在本机识别棋子并持续同步棋谱；点击开始后自动落子，支持暂停、重新同步和下一局。
- **识别自动加速**：Windows 使用 DirectML 覆盖 NVIDIA、AMD、Intel 显卡；macOS 使用 CoreML。兼容的已安装 NPU 提供程序也参与检测，加载或推理失败自动回退 CPU。
- **标准开局与图形校对**：按当前朝向生成标准 32 子，打开摆棋窗口对照原图确认，适合从标准局面开始或修正识别。
- **可追查的接管记录**：自动保存每次会话的棋谱、识别候选、决策、输入及确认事件；在事件棋盘中切换程序局面、变化前与图像识别结果。
- **棋谱与自定义局面**：FEN 导入、图形摆棋、逐手复盘、注释，以及 `.paddi.json` 棋谱导入导出。

## 下载与安装

从 [GitHub Releases](https://github.com/PaddiSoft/PaddiChess/releases) 下载与你的系统匹配的包，完整解压后运行。发布包自带 .NET 运行时、引擎、配套 NNUE 和离线象棋识别与 OCR 资源，无须另外安装 .NET。

| 平台 | 发布包／使用方式 | 当前范围 |
| --- | --- | --- |
| macOS Apple Silicon | `PaddiChess-macOS-AppleSilicon.zip` → `Paddi象棋.app` | 本地对弈、分析、外部接管 |
| Windows x64 | `PaddiChess-Windows-x64.zip` → `Paddi象棋.exe` | 本地对弈、分析；外部输入和捕获兼容性需在目标机器验证 |
| macOS Intel | 仓库提供 `osx-x64` 构建目标 | 成品包以该次 Release 附件为准 |
| Linux x64 | 仓库提供 `linux-x64` 构建目标 | 本地对弈客户端；尚未实现外部窗口接管 |

保留包内 `Engine`、`Native`、`Assets` 和运行时文件。macOS 外部接管需要屏幕录制与辅助功能权限；安装、权限及签名说明见[安装指南](docs/installation.md)。

## 第一次使用

**本地对弈**：打开应用，默认双方为玩家。选择红黑双方的执棋方式后开始走棋；需要引擎分析时打开分析模式。棋谱可逐手回看，也可导入 FEN 或新建自定义棋谱。

**外部接管**：打开“外部接管”，完成权限检查，选择目标窗口并点击 **① 连接并同步**。核对棋子、朝向、行棋方和接管方，再点击 **② 开始接管**。连接阶段持续观察和预计算，但不发送落子。**停止 / Esc** 暂停输出；“重新同步”修复局面；“接管记录”打开本机会话历史。

**标准开局**：点击接管控制区的“标准开局”，暂停输出并打开图形校对窗口。程序生成标准 32 子、红方先行；对照截图修正后采用局面，再决定何时开始接管。此按钮不会代替目标应用开始一盘新棋，也不会把当前外部棋盘强行当作标准开局。

完整操作见[快速上手](docs/usage.md)与[外部接管说明](docs/external-takeover.md)。

## 如何避免在错误局面上落子

同步使用合法着法、棋子身份与稳定落点共同确认。发送输入前，程序独立检查最新画面的棋子，并核对决策对应的局面、轮次、已确认历史和执棋配置。识别冲突时暂停落子并继续观察；尚未确认的部分输入不会盲目重复发送。

接管历史保留最终棋谱和当时采用的 FEN、识别 FEN、候选与发送事件。事件棋盘能直观看到程序当时认成了什么棋子。**单张截图的棋子分布不能独立证明轮到谁，也不能恢复接入前的走棋历史。** 图像识别预览明确提示行棋方以确认记录为准。

这些核验覆盖已知回归场景，不能保证任意字体、特效、模糊画面或第三方输入方式都兼容。33 ms 是观察目标周期，不是端到端落子时间。详见[已知限制与排查](docs/troubleshooting.md)。

## 引擎、模型与数据

引擎搜索在本机运行。插件规则按 UCI 握手声明展示；模型候选由独立 `PaddiRules` 原生组件校验。该组件使用 Pikafish 2026-09-06 规则源码，**不会自动继承其他引擎插件的规则设置**。目标游戏仍负责外部对局的最终裁定。

棋盘识别在本机完成，默认使用随包象棋专用模型，中文 OCR 仅补查不确定位置。无需按皮肤安装识别包，详见[识别流程与实测范围](docs/recognition.md)。启用模型 API 后，会把棋局文本与候选发送至你配置的服务；API Key 只保留在本次运行内存，不写入设置、棋谱或接管历史。模型权限、费用、思考等级支持和服务端数据处理由对应服务决定。详见[引擎与模型](docs/engines-and-models.md)、[数据与隐私](docs/privacy-and-data.md)。

## 从源码运行

需要 `global.json` 指定的 .NET 10 SDK。固定版本的三平台引擎、NNUE、象棋识别模型、OCR 和测试资源随 Git 仓库提供，正常克隆后无须另行下载。macOS 还需要 Xcode Command Line Tools；完整环境与资源清单见[开发指南](docs/development.md)。

```bash
git clone https://github.com/PaddiSoft/PaddiChess.git
cd PaddiChess
bash scripts/verify.sh
dotnet run --project PaddiChess/PaddiChess.csproj --configuration Release --no-restore
```

`verify.sh` 执行资源检查、锁定依赖还原、Release 构建和完整测试，并保存日志及 TRX。通过数量、平台跳过项和发布包验收以对应 Release 的实际记录为准；交叉编译成功不等于目标平台实机对局通过。

工程分为 `Core / Engine / External / Services / Sessions / UI`，业务程序集不引用 Avalonia。架构、线程与取消约定见[工程架构](docs/architecture.md)。当前发布未启用 NativeAOT；本机 Pikafish 搜索使用 CPU，设置与测速见[性能说明](docs/performance.md)。

## 文档导航

| 文档 | 内容 |
| --- | --- |
| [安装指南](docs/installation.md) | macOS / Windows 安装、授权、更新 |
| [快速上手](docs/usage.md) | 工作区、本地对弈、分析、摆棋和复盘 |
| [外部接管](docs/external-takeover.md) | 连接、标准开局、确认机制、记录、平台输入 |
| [引擎与模型](docs/engines-and-models.md) | UCI 插件、原生规则、多服务与思考等级 |
| [棋盘识别](docs/recognition.md) | 专用模型、补识、置信度、测试与限制 |
| [性能说明](docs/performance.md) | 线程、Hash、时间、采样、CPU 与 AOT |
| [数据与隐私](docs/privacy-and-data.md) | 本机文件、临时截图、API 数据、分享记录 |
| [工程架构](docs/architecture.md) | 项目边界、数据流、状态与生命周期 |
| [开发指南](docs/development.md) | 资源、构建、测试、锁文件、打包 |
| [排查指南](docs/troubleshooting.md) | 识别、输入、权限、模型和引擎常见问题 |

## 许可与致谢

Paddi象棋应用代码采用 [AGPL-3.0](LICENSE)。第三方引擎、权重、OCR 模型和其他组件保留各自许可与来源说明；项目许可证不替代这些许可。随包 NNUE 有独立使用限制，包括未经允许不得用于商业用途，不能按本项目 AGPL 许可理解其使用范围。

- Pikafish：[上游项目与源码入口](https://github.com/official-pikafish/Pikafish)、[随包引擎与 NNUE 来源](PaddiChess/Packaging/Engine-README.md)、[NNUE 原许可](Pikafish.2026-09-25/NNUE权重协议（使用视为同意本协议）_NNUE%20License.txt)。内置版本来自项目中的 `Pikafish.2026-09-25` 原始发行包，保留其许可和作者文件；该二进制与上游具体源码提交的对应关系尚未独立核验。
- PaddiRules：[0906 原生规则组件来源](PaddiChess/Native/PikafishRules/SOURCE.md)，GPL 源码随包提供；这份源码仅对应规则组件，不是 0925 主引擎的完整对应源码。
- Chinese Chess Recognition：[专用模型来源、校验值与 MIT 声明](PaddiChess/Assets/Recognition/SOURCE.md)。
- PaddleOCR / RapidOCR：[离线模型来源](PaddiChess/Assets/Ocr/SOURCE.md)与随包 Apache-2.0 声明。
- Avalonia、SkiaSharp、ONNX Runtime 及其他依赖：版本由项目文件与锁文件固定。
- [品牌资源](PaddiChess/Assets/Brand/README.md)与[识别参考资源](PaddiChess/Assets/BoardSkins/README.md)另有来源说明。JJ 象棋截图用于展示作者的测试记录，不表示第三方运营方参与或背书。

缺陷和建议请提交 [Issue](https://github.com/PaddiSoft/PaddiChess/issues)。安全问题先阅读 [SECURITY.md](SECURITY.md)，不要在公开 Issue 中粘贴 Key 或含凭据的日志。
