# 内置开局库

- 名称：新编刮骨刀，2026-11-18。
- 来源：项目作者提供的 `新编刮骨刀-20261118-1446.xqb`。
- 保留 2,688,227 条库招的 key、move、score、win、draw、lost、valid 和 id。
- 发布数据只含 `book`、`information` 和 key 索引。memo 置空；不发布原库的训练任务、worker、claim、局面池及运行记录。
- `bundled.json` 记录原件、发布数据库和压缩文件的 SHA-256。原件保留在开发者本机，未直接纳入 Git。
- `.xqb.gz` 为标准 gzip 压缩的 SQLite 数据库。客户端首次启动在后台验证并解压到本机缓存，以后复用；查询只读、使用索引，不整库载入内存。
- 数据由项目作者提供随软件分发，不将第三方棋库数据自动声明为客户端的 AGPL 代码。

格式说明：[fisherfan/xqbook](https://github.com/fisherfan/xqbook)。客户端按公开格式实现局面编码和镜像转换，没有复制第三方引擎源码。SQLite 使用系统库：macOS 的 libsqlite3、Windows 10+ 的 winsqlite3、Linux 的 libsqlite3.so.0。

重新整理原库可运行 `python3 scripts/prepare-opening-book.py 原库.xqb`。该脚本不修改原件；重新生成后须更新 runtime-assets.json 中的哈希及发布审计中已审核数据的哈希。
