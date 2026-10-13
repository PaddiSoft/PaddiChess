# 工程改进与性能验证

[文档目录](README.md) · [本轮检查](audit-2026-10-13.md) · [开发指南](development.md)

## 识别进程与恢复

正常客户端将棋子分类和中文 OCR 放入一个常驻子进程，连续识别复用模型与补识缓存。传输使用原始 BGRA，避免为每帧编码 PNG。控制消息最多 4 MiB、像素最多 256 MiB，损坏消息不能触发无界分配。

首次准备只等待 CPU 模型可用，加速器继续后台编译；准备好后在请求之间切换。单次通信上限 15 秒，接管的上层核验有更短的取消预算。原生推理不响应取消时终止子进程，下次使用 CPU 重启；不会因此重复发送落子。排队请求被取消不会误杀另一个正在执行的识别。界面显示实际后端，故障后以 CPU 为准。

`NNUE` 仍属于执棋引擎进程；此改动只隔离图像识别模型，不把皮卡鱼搜索改成 GPU 运算。

## 接管时延记录

每次会话暂停、结束或报错时，把以下统计写入接管记录，可在“接管记录”的事件列表查看。每阶段只保留最近 2048 个样本，样本总数单独累计；单位为毫秒。

| 阶段 | 含义 |
| --- | --- |
| `capture` | 一次捕获调用 |
| `tracking` | 当前画面的轻量观察匹配 |
| `full-recognition` | 核验触发的完整棋子识别 |
| `verification` | 一次落子前核验检查；可能只是启动后台补识 |
| `search` | 一次引擎或模型决策计算 |
| `ready-to-input` | 结果就绪至提交原生输入，含其间核验等待；连接后未点击“开始”的人工等待不计入 |
| `native-input` | 原生发送过程完成，尚不表示游戏已接受 |

观察“计算结束后迟迟不落子”时，重点比较 `ready-to-input` 和 `native-input`。不能把一次很快返回的 `verification` 当成整个核验等待；完整等待反映在前者。统计事件只有数字和阶段名，不自动保存截图或上传到服务器。

## 同一组图像跨设备回放

完成构建后，使用测试输出中的清单及配套资源：

```bash
PADDI_RECOGNITION_DEVICE=cpu dotnet PaddiChess/bin/Release/net10.0/Paddi象棋.dll \
  --recognition-benchmark PaddiChess.Tests/bin/Release/net10.0/Fixtures/recognition-cases.json \
  artifacts/recognition-cpu.json

PADDI_RECOGNITION_DEVICE=auto dotnet PaddiChess/bin/Release/net10.0/Paddi象棋.dll \
  --recognition-benchmark PaddiChess.Tests/bin/Release/net10.0/Fixtures/recognition-cases.json \
  artifacts/recognition-auto.json
```

Windows PowerShell 先设置 `$env:PADDI_RECOGNITION_DEVICE='auto'`，再执行同样的 `dotnet ...` 参数；带 RID 构建时使用实际的输出子目录。发布程序也接受这两个路径参数，无需进入 UI。

清单是 JSON 数组，每项为 `Id`、`Path`、`Grid`（左、上、右、下四个交点坐标）、`Flipped`（红上黑下）、`Fen`。图像路径相对清单目录，仓库夹具另外支持相对输出根目录。可以按时间顺序加入自己有权使用的连续截图；模型不会针对清单中的皮肤增加规则。

报告包含系统／架构、模式、实际后端、准备耗时、进程启动／重启次数，每帧三次识别的耗时、正确性和不确定格数，以及首次／重复帧 P50／P95／P99。`auto` 不等于 GPU 已启用，必须核对 `Devices`。首次运行期间可能仍是 CPU，不能据此宣布某个 GPU 更快。

这是离线识别回放，**不包含实时捕获、鼠标发送、第三方游戏确认**。在空闲设备上使用相同清单、缩放和预热条件对比；不同任务同时运行的数字不能用于纯硬件速度排名。NVIDIA／AMD／Intel／NPU 的可用性需要在对应设备运行报告，CI 的 CPU 通过不构成 GPU 兼容证明。

## 历史分页与清理

“接管记录”按 500 个会话和 128 行事件分页，默认打开选中会话的最新事件。上一页／下一页／最新页直接显示。事件索引按字节偏移读取，旧日志首次使用时生成索引；不完整的末行保留给下一次刷新，避免解析半条记录。

记录默认保留全部。选择 7／30／90 天以前，点击“检查占用”查看数量和体积，再点“确认清理”。正在记录的会话持有独占锁，会被跳过；只删除本程序认识的历史文件。删除无法撤销，不会后台自动清除排错证据。

## 规则能力

引擎插件的 `Repetition Rule` 等 UCI 选项决定该引擎自己的搜索裁定。标准 UCI 没有统一的“给出全部符合长将／长捉规则的候选着法”命令，因此 `RuleCapabilities` 将“规则可配置”和“候选可查询”分别声明。

大模型的候选仍由内置 `PaddiRules` 组件提供，其来源是随包的 Pikafish 2026-09-06 固定规则源码，界面明确显示此来源；执棋插件默认是 2026-09-25。这个接口允许替换规则提供者，但不会把任意 UCI 插件冒充成可查询候选的规则服务。

## Linux 支持边界

X11 后端在独立进程中使用 XComposite 获取窗口内容；被其他窗口覆盖时仍可读取合成图像。窗口不能最小化，显示模式要求 24／32 位 TrueColor。

输入支持 XTest 系统鼠标的两次点击或拖动，会聚焦并抬升目标窗口；不提供 Linux 后台窗口事件模式。发送前验证窗口身份、位置和坐标，序列中检查 Esc，并在结束时释放按键。Wayland 环境不启用此后端；需要选择 X11 登录会话。

Ubuntu／Debian 运行库：`libx11-6`、`libxcomposite1`、`libxtst6`。CI 使用 Xvfb 自建窗口验证遮挡捕获与点击坐标，不能代表某个第三方游戏的输入接受策略。
