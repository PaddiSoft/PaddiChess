# Offline Chinese character recognition

Model: PaddleOCR PP-OCRv5 mobile Chinese recognizer, ONNX conversion distributed by RapidAI/RapidOCR v3.9.2.

Source manifest: https://github.com/RapidAI/RapidOCR/blob/main/python/rapidocr/default_models.yaml
Download: https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.9.2/onnx/PP-OCRv5/rec/ch_PP-OCRv5_rec_mobile.onnx
SHA-256: 5825fc7ebf84ae7a412be049820b4d86d77620f204a041697b0494669b1742c5

PaddleOCR and RapidOCR are Apache-2.0 licensed; included license texts retain their notices. The ONNX model includes its character dictionary. Preprocessing uses the upstream BGR / CHW / [-1, 1] input convention and CTC decoding, adapted to repeated individual chess glyphs. No screenshot is uploaded and no runtime model download is performed.

The model identifies text, not a particular board skin. It supplements uncertain cells after the dedicated board classifier; the editor can also request a full OCR pass. If the board model is unavailable, it serves as the full recognition fallback. Live play uses per-cell image differences and legal transitions to avoid running OCR on every frame. Unknown fonts, severe occlusion, unsupported symbols or ambiguous colours remain unconfirmed.
