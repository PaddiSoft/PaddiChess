# Opening-book format references and notices

PaddiChess's managed opening-book readers implement public file formats. No external
book editor, modified SQLite DLL or additional engine is loaded or executed.
The application source, including modifications and compacted tables, is in
`PaddiChess/Engine/` in https://github.com/PaddiSoft/PaddiChess.

## Binghe OBK

The fixed Zobrist constants in `BingheBookKeys.cs` are taken from
[public-Xiangqi, ZobristUtils.java](https://github.com/sojourners/public-Xiangqi/blob/3f2bfa8373d171509987aaa91f9b6f38efa998f5/src/main/java/com/sojourners/chess/util/ZobristUtils.java),
copyright its contributors, GPL-3.0 (license included as `GPL-3.0.txt`). PaddiChess
retains the 90 playable squares per piece, discarding unused mailbox squares.
The same project's ObkReader.java documents the unsigned index layout. PaddiChess
implements its own bounded reader, additionally handling ordinary SQLite indexes.
Record/page/overflow layout follows https://www.sqlite.org/fileformat2.html.

## jqxq BIN

`JqxqBookCodec.cs` contains compacted/reordered fixed hash constants from jqxq,
provided by [liujh168/openbook, jqxq_zobrist.h](https://github.com/liujh168/openbook/blob/33e02fc9cccb4e6c4f45029d707d3074090f9107/jqxq_zobrist.h).
Copyright jqxq and openbook contributors; GPL-3.0-or-later, license included.
Its `convert_obk.py` and README specify the 16-byte, little-endian key/move/count/learn
layout. This is not the international-chess Polyglot format.

## ElephantEye BIN/DAT

Format and RC4-zero key sequence reference:
[ElephantEye](https://github.com/xqbase/eleeye/tree/61c2f8b45c1acf8ec99326746ca0f8ab9bc8305f),
`eleeye/book.h`, `eleeye/pregen.h`, `eleeye/pregen.cpp`, `base/rc4prng.h`.
Designed by Morning Yellow; Copyright (C) 2004–2011 www.xqbase.com.
LGPL-2.1-or-later; license included as `LGPL-2.1.txt`.
PaddiChess reimplements the documented binary search, key generation and coordinate
conversion in C#. No upstream binary or complete third-party opening database is bundled.

The bundled XQB library has separate provenance in `Assets/OpeningBooks/README.md`.
