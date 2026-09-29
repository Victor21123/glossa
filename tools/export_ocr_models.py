"""Copy PP-OCR ONNX models fetched by Python rapidocr into Glossa's data folder.

RapidOcrNet needs a plain dictionary file next to each recognizer, while the
rapidocr ONNX files keep their character list in model metadata ("character").
This script copies the models and writes those dictionaries.

Usage: .venv\\Scripts\\python.exe tools\\export_ocr_models.py [D:\\GlossaData]
"""
import shutil
import sys
from pathlib import Path

import onnxruntime as ort

import rapidocr

SRC = Path(rapidocr.__file__).parent / "models"
DST = Path(sys.argv[1] if len(sys.argv) > 1 else r"D:\GlossaData") / "models" / "ocr"

# (source file, target file, dictionary file or None)
MODELS = [
    ("ch_PP-OCRv5_det_mobile.onnx", "v5/ch_PP-OCRv5_mobile_det.onnx", None),
    ("ch_PP-OCRv5_rec_mobile.onnx", "v5/ch_PP-OCRv5_rec_mobile.onnx", "v5/ppocrv5_ch_dict.txt"),
    ("eslav_PP-OCRv5_rec_mobile.onnx", "v5/eslav_PP-OCRv5_rec_mobile.onnx", "v5/ppocrv5_eslav_dict.txt"),
    ("PP-OCRv6_det_small.onnx", "v6/PP-OCRv6_det_small.onnx", None),
    ("PP-OCRv6_rec_small.onnx", "v6/PP-OCRv6_rec_small.onnx", "v6/ppocrv6_small_dict.txt"),
]


def export_dict(model: Path, target: Path) -> int:
    meta = ort.InferenceSession(str(model), providers=["CPUExecutionProvider"]).get_modelmeta()
    chars = meta.custom_metadata_map["character"].splitlines()
    target.write_text("\n".join(chars) + "\n", encoding="utf-8")
    return len(chars)


def main() -> None:
    for src_name, dst_name, dict_name in MODELS:
        src = SRC / src_name
        if not src.exists():
            print(f"skip {src_name}: not downloaded yet")
            continue
        dst = DST / dst_name
        dst.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(src, dst)
        line = f"{dst_name}  {dst.stat().st_size / 1e6:.1f} MB"
        if dict_name:
            line += f"  + {dict_name} ({export_dict(src, DST / dict_name)} chars)"
        print(line)


if __name__ == "__main__":
    main()
