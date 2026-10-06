# Pikafish rules adapter

The `upstream` directory contains unmodified source from the official
`Pikafish-2026-09-06` tag:
https://github.com/official-pikafish/Pikafish/tree/Pikafish-2026-09-06

`main.cpp`, `standalone_support.cpp`, `windows_threads.h` and `build.sh` are
Paddi's GPL-3.0-or-later protocol/build adapter. The support files supply the
diagnostic square formatter and Windows C++ thread declarations; an attempted
search transposition-table lookup aborts (all adapter moves pass a null table).
The native executable uses Pikafish's own `MoveList<LEGAL>`, `Position::do_move`
and `Position::rule_judge`; no rule logic is reimplemented in the adapter.
It does not link or load an NNUE network, run evaluation, or search variations.

Each tab-delimited request contains `rules`, the starting FEN and every observed
UCI move. Each JSON response contains basic legal moves partitioned into
`allowed` and `excluded`. A move is excluded only when the native rule judge
definitively awards the following position to the opponent of the mover.
Draws and poor strategic/tactical choices are not excluded. The helper is a
rule validator, not a recommendation list or a full game adjudicator.

Rules are pinned to this source version. An unrelated engine plugin may have a
different ruleset. The helper does not pretend to switch a plugin's rules or
recover history preceding a screenshot import.

Source and the GPL licence are included in distribution packages so the helper
can be rebuilt. See `upstream/Copying.txt` and `upstream/AUTHORS`.
