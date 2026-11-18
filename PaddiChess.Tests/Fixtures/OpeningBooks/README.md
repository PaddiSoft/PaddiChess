# Opening-book regression fixtures

`sample.xqb` is the existing synthetic XQB fixture.
The OBK/BIN/DAT fixtures contain artificial records, not a user's library or games.

- `formats.json`: independent OBK/jqxq reference keys for red/black, an asymmetric
  opening and a position whose OBK key has an IEEE NaN bit pattern. ElephantEye's
  initial key `052656D5` was also checked against upstream `BOOK/BOOK.DAT`.
- `binghe.obk`: unsigned-order OBK index, 512-byte pages, multiple B-tree levels,
  raw REAL high-bit keys, an overflow memo and disabled/illegal high-score moves.
- `binghe-sqlite.obk`: identical test entries ordered as a standard SQLite index;
  excludes NaN entries.
- `binghe-nan-index.obk`: numeric-order index where NaN keys became NULL during
  reindex, but original raw keys survive in the table; exercises exact-bit recovery.
- `eleeye.dat`: 8-byte ElephantEye records, sorted by 32-bit lock.
- `jqxq.bin`: 16-byte jqxq records, sorted by key/move.

Regenerate with Python 3 and the independent reference checkout:

```sh
git clone https://github.com/liujh168/openbook /tmp/paddi-openbook-reference
git -C /tmp/paddi-openbook-reference checkout 33e02fc9cccb4e6c4f45029d707d3074090f9107
python3 PaddiChess.Tests/Fixtures/OpeningBooks/generate.py /tmp/paddi-openbook-reference
```

Reference code and hash constants are GPL-3.0-or-later. The fixed table provenance
and bundled license texts are in `PaddiChess/Packaging/ThirdParty/OpeningBooks/`.
`generate.py` uses those independent references, not the C# implementation under test.
