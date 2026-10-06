#!/usr/bin/env python3
"""Mark ditto's UTF-8 entry names explicitly without recompressing signed files."""
from pathlib import Path
import struct
import sys
import zipfile


def normalize(path: Path) -> None:
    data = bytearray(path.read_bytes())
    with zipfile.ZipFile(path) as archive:
        cursor = archive.start_dir
        for entry in archive.infolist():
            if data[cursor:cursor + 4] != b"PK\x01\x02":
                raise ValueError("Invalid central directory")
            name_size, extra_size, comment_size = struct.unpack_from("<HHH", data, cursor + 28)
            name = data[cursor + 46:cursor + 46 + name_size]
            name.decode("utf-8", errors="strict")
            local = entry.header_offset
            if data[local:local + 4] != b"PK\x03\x04":
                raise ValueError("Invalid local header")
            local_name_size = struct.unpack_from("<H", data, local + 26)[0]
            if data[local + 30:local + 30 + local_name_size] != name:
                raise ValueError("Local and central entry names differ")
            for flag_offset in (cursor + 8, local + 6):
                flags = struct.unpack_from("<H", data, flag_offset)[0]
                struct.pack_into("<H", data, flag_offset, flags | 0x800)
            cursor += 46 + name_size + extra_size + comment_size
    path.write_bytes(data)
    with zipfile.ZipFile(path) as archive:
        if archive.testzip() is not None:
            raise ValueError("Archive integrity check failed")
        if not all(entry.flag_bits & 0x800 for entry in archive.infolist()):
            raise ValueError("An entry is missing the UTF-8 flag")


if __name__ == "__main__":
    if len(sys.argv) != 2:
        raise SystemExit("Usage: normalize-zip-utf8.py ARCHIVE.zip")
    normalize(Path(sys.argv[1]))
