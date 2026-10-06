# PlayXiangqi regression artwork

`playxiangqi-opening.png` is an offline rendering of the public site's actual
LXGW WenKai TC glyph outlines, disc geometry and colour palette, downloaded
2026-10-05 from https://playxiangqi.com/js/xiangqi-glyphs.js and renderer/CSS.
It is not a screenshot of a live game; no live moves were sent.
Reproduction script and original reference sources are under
`artifacts/recognition/playxiangqi/`, with the font's SIL Open Font License.
The reproduction preserves the rare red rook glyph 俥 that the bundled OCR
model's character dictionary does not contain, and the river text that was
misread as a sixth black pawn in the user's Windows report.

`playxiangqi-transparent.png` reuses these same glyph outlines with the disc
and face omitted, to test conservative handling of flat/transparent themes.
`playxiangqi-sparse.png` uses the original discs and a four-piece endgame, to
check that occupancy evidence does not depend on opening piece counts.
