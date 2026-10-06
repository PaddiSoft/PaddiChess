# Pikafish 2026-09-25

本目录的引擎与 `pikafish.nnue` 来自项目根目录用户提供的
`Pikafish.2026-09-25` 发行包，引擎算法和配套权重未作修改。
macOS 打包会重签名引擎，文件字节与校验和因此可能不同于原包。
支持 macOS Apple Silicon / Intel、Windows x64 和 Linux x64；
universal 引擎自行选择 CPU 指令集。

内置引擎支持 `Repetition Rule`，默认 `AsianRule`，另可选择
`ChineseRule`、`SkyRule`、`ComputerRule`、`YitianRule`、`AllowChase`、
`NoJudgement`。其他规则选项包括 `Draw Rule`、`Sixty Move Rule`、
`Rule60MaxPly` 和 `Mate Threat Depth`。客户端按 UCI 握手结果显示选项。

原包随附文档保留为 `Introduction.txt`、`UpdateLog.txt`、`AUTHORS`
和 `NNUE-License.md`。`Copying.txt` 为从项目已有 Pikafish 源码复制的
标准 GPL-3.0 许可证文本。项目地址：https://github.com/official-pikafish/Pikafish

上述链接是原包介绍中给出的上游项目与源码入口。本项目尚未独立核验
该 0925 二进制与上游具体源码提交的对应关系，未修改引擎算法或权重，
也不将下述 0906 规则组件源码标作 0925 主引擎的对应源码。

独立的模型候选校验组件 `Native/PaddiRules` 仍由 Pikafish 2026-09-06
规则源码构建，不随本引擎的规则选项切换。

源发行包 SHA-256（macOS 应用重签名可能改变引擎二进制校验和）：

| 文件 | SHA-256 |
| --- | --- |
| Pikafish-MacOS-universal | dfa1aed00d416d9684163235bb127da41353833e61d7d5144a90a866e47e4ab9 |
| Pikafish-Windows-x86-64-universal.exe | 9824fff4e3c4a72afbc6cfdc611c140b32f79e3c5fd5fe4bf6d747833875b86d |
| Pikafish-Linux-x86-64-universal | e3364787ca970c0b110f72a1325bcc6f88949a86511a216366432999b9188a22 |
| pikafish.nnue | 7d13d73569a9b571ba0eb20cf1596247bc2a42738967e61afef6482b231e900e |
