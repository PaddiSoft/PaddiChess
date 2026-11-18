#!/usr/bin/env python3
"""Export only playing data from an author-provided XQB, leaving the original untouched."""
import gzip
import hashlib
import json
from pathlib import Path
import sqlite3
import sys
import tempfile

root = Path(__file__).resolve().parents[1]
source = Path(sys.argv[1]).resolve()
assets = root / 'PaddiChess/Assets/OpeningBooks'
assets.mkdir(parents=True, exist_ok=True)
with tempfile.TemporaryDirectory() as temporary:
    database = Path(temporary) / 'book.xqb'
    with sqlite3.connect(database) as db:
        db.execute('ATTACH DATABASE ? AS original', (source.as_uri() + '?mode=ro',))
        db.executescript('''CREATE TABLE book (id INTEGER PRIMARY KEY, key BLOB, move INTEGER, score INTEGER, win INTEGER, draw INTEGER, lost INTEGER, valid INTEGER, memo TEXT);
INSERT INTO book SELECT id,key,move,score,win,draw,lost,valid,NULL FROM original.book;
CREATE INDEX idxkey ON book(key);
CREATE TABLE information(name TEXT,value TEXT);
INSERT INTO information VALUES ('version','1'),('type','xiangqi');''')
        entries = db.execute('SELECT count(*) FROM book').fetchone()[0]
        assert db.execute('PRAGMA quick_check').fetchone()[0] == 'ok'
        assert db.execute("SELECT count(*) FROM book WHERE typeof(key)!='blob' OR typeof(move)!='integer' OR typeof(score)!='integer' OR typeof(win)!='integer' OR typeof(draw)!='integer' OR typeof(lost)!='integer' OR typeof(valid)!='integer'").fetchone()[0] == 0
    compressed = assets / 'guagudao-20261118.xqb.gz'
    with compressed.open('wb') as out, gzip.GzipFile(filename='', mode='wb', fileobj=out, mtime=0, compresslevel=9) as archive:
        archive.write(database.read_bytes())
    sha = lambda path: hashlib.sha256(path.read_bytes()).hexdigest()
    manifest = dict(name='新编刮骨刀 · 2026-11-18', sourceFile=source.name, sourceSha256=sha(source),
                    file=compressed.name, sha256=sha(database), compressedSha256=sha(compressed), entries=entries, size=database.stat().st_size)
    (assets / 'bundled.json').write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + '\n')
    print(json.dumps(manifest, ensure_ascii=False))
