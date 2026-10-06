# Local recognition references

- `jj-classic.png`: cropped board from the user's iPhone mirroring report.
- `web-default.skin.json`: normalized 24 × 24 RGB feature patches for recognition of the default-v1 piece/board theme on <https://xiangqiai.com/>. Extracted from the site's publicly served `skins/piece/default-v1/` and `skins/board/default-v1/` assets on 2026-10-04. Used only as a visual vocabulary, not as the client's board artwork. Sampled at two sizes; includes empty intersections.

No cloud recognition is used. Colour is additional evidence only when all templates demonstrate the same red/non-red convention. A high-error patch remains unresolved. The screenshot fixture containing calibration dots must never be accepted as a confidently recognized board.
