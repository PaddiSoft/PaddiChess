# Offline Xiangqi board recognition

Model: Chinese Chess Recognition, Swin Transformer V2 multi-region classifier,
`nano_v3-0319.onnx`. Each of 90 intersections predicts empty, unknown or one of
14 side/piece identities. The model is used unchanged; no client skin is trained
or selected at runtime.

- Author/project: https://github.com/TheOne1006/chinese-chess-recognition
- Published model and inference demo: https://huggingface.co/spaces/yolo12138/Chinese_Chess_Recognition
- Pinned revision: `4bcaf91777effa645040ee76d008d45c2ffc03f3`
- Download: https://huggingface.co/spaces/yolo12138/Chinese_Chess_Recognition/resolve/4bcaf91777effa645040ee76d008d45c2ffc03f3/onnx/layout_recognition/nano_v3-0319.onnx
- Local filename: `xiangqi-nano-v3.onnx`
- Size: 31,101,356 bytes
- SHA-256: `da66ba9809f15127f8ae729b1755e42ee61c100c4f9979ce0ef13602ac471298`

The upstream Hugging Face Space declares `license: mit` in its README metadata;
the original README is retained as `UPSTREAM-README.md`. `LICENSE.txt` contains
the MIT terms and attribution. The client implements the documented preprocessing
in C#: orient the four grid corners with black at the top, warp to 450×500,
centre crop to 400×450, resize to 280×315, and normalize RGB channels with means
123.675/116.28/103.53 and standard deviations 58.395/57.12/57.375.

Inference is local through ONNX Runtime CPU. No screenshot upload, Python
installation, service credential or runtime model download is required.
Uncertain identities, unknown classes, excess pieces and invalid squares are
not automatically accepted. Chinese OCR supplements only unresolved cells.
