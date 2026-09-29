"""Copy PP-OCR ONNX models fetched by Python rapidocr into Glossa's data folder.

RapidOcrNet needs a plain dictionary file next to each recognizer, while the
rapidocr ONNX files keep their character list in model metadata ("character").
This script copies the models and writes those dictionaries. The PP-OCRv6 medium
recognizer (the default reader since 2026-09-29) is not in rapidocr: it is
downloaded from Hugging Face, pinned to a revision and checked by SHA-256; it
reads with the same characters as v6 small, so it uses small's dictionary.

Usage: .venv\\Scripts\\python.exe tools\\export_ocr_models.py [D:\\GlossaData]
"""
import hashlib
import shutil
import sys
import urllib.request
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


# (repository, revision, file, target file, SHA-256)
DOWNLOADS = [
    ("PaddlePaddle/PP-OCRv6_medium_rec_onnx", "50c7eacafc52fa7bcf4194e8cd08e46f8558504b", "inference.onnx",
     "v6/PP-OCRv6_rec_medium.onnx", "9c09abf0957f7968c7586464b7397b84ad2387a0497a351af40e9acc71b673ba"),
]


def download(repo: str, revision: str, name: str, target: Path, sha256: str) -> str:
    if target.exists() and hashlib.sha256(target.read_bytes()).hexdigest() == sha256:
        return "have"
    target.parent.mkdir(parents=True, exist_ok=True)
    part = target.with_suffix(target.suffix + ".part")
    url = f"https://huggingface.co/{repo}/resolve/{revision}/{name}"
    with urllib.request.urlopen(url, timeout=120) as r, open(part, "wb") as f:
        shutil.copyfileobj(r, f, 1 << 20)
    if hashlib.sha256(part.read_bytes()).hexdigest() != sha256:
        part.unlink()
        raise SystemExit(f"checksum mismatch: {url}")
    part.replace(target)
    return "downloaded"


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
    for repo, revision, name, dst_name, sha256 in DOWNLOADS:
        dst = DST / dst_name
        print(f"{dst_name}  {download(repo, revision, name, dst, sha256)}  {dst.stat().st_size / 1e6:.1f} MB")


if __name__ == "__main__":
    main()
