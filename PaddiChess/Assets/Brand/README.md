# Paddi象棋视觉资源

这套资源使用朱砂红、暖木色和淡墨山水，服务于棋盘与棋子阅读。

| 资源 | 用途 |
| --- | --- |
| `Logo.png` | 应用品牌标志、标题栏标志、平台图标母版 |
| `BoardTexture.png` | 棋盘底材；低透明度叠加，棋盘线与文字由程序绘制 |
| `WorkspaceBackdrop.png` | 工作区的淡墨背景，白色内容卡片保持清晰 |

三张 PNG 保留图像生成工具输出的原始像素。完整生成提示词与工具信息见 [generation-prompts.json](generation-prompts.json)。

应用通过 `BrandAssets` 一次加载、共享解码后的位图；棋子动画不会重复打开或解码图片。河界的“楚 河”“汉 界”和中央小字“Paddi象棋”保持为程序文字，随窗口缩放仍然清晰。

## 平台图标

在 macOS 执行 `bash PaddiChess/Packaging/build-icons.sh`，使用系统 `sips` 和 `iconutil` 从同一张 `Logo.png` 制作尺寸变体与 `.icns`；`MakeIcon.swift` 将尺寸变体封装成 Windows `.ico`，不重新绘制标志。

`Packaging/Paddi.v1.png`、`Packaging/Paddi.v1.icns` 和 `Packaging/MakeIcon.v1.swift` 保留此前的图标版本。
