"""Collects game screenshots with on-screen text for OCR and translation tests.

Finds games through the public Steam store API, downloads their store screenshots (the English, Japanese and
Simplified Chinese store pages can carry different ones), runs OCR and keeps the screenshots with enough text
in the wanted script. Output: D:/GlossaData/test/screens_raw/<game>/NN.jpg and index.json with OCR stats.

Usage: .venv\\Scripts\\python.exe tools\\collect_screens.py
"""
import json
import re
import subprocess
import sys
import time
import urllib.parse
import urllib.request
from pathlib import Path

from rapidocr import RapidOCR

OUT = Path("D:/GlossaData/test/screens_raw")

# (search term, language of the on-screen text, adult/mature flag for the report)
GAMES = [
    ("Being a DIK", "en", True),
    ("HuniePop 2: Double Date", "en", True),
    ("Summer's Gone", "en", True),
    ("Disco Elysium", "en", False),
    ("Cyberpunk 2077", "en", True),
    ("Baldur's Gate 3", "en", True),
    ("Hades", "en", False),
    ("Grand Theft Auto V", "en", True),
    ("Yakuza 0", "ja", True),
    ("Like a Dragon: Infinite Wealth", "ja", True),
    ("Sakura no Uta", "ja", False),
    ("The House in Fata Morgana", "ja", False),
    ("Muv-Luv", "ja", False),
    ("ChaoS;Child", "ja", False),
    ("Danganronpa: Trigger Happy Havoc", "ja", False),
    ("AI: THE SOMNIUM FILES", "ja", True),
    ("Persona 5 Royal", "ja", True),
    ("STEINS;GATE", "ja", False),
    ("Ace Attorney Investigations Collection", "ja", False),
    ("Octopath Traveler II", "ja", False),
    ("DRAGON QUEST XI S", "ja", False),
    ("Tokyo Xanadu eX+", "ja", False),
    ("Sengoku Rance", "ja", True),
    ("Evenicle", "ja", True),
    ("Love Is All Around", "zh", True),
    ("Chinese Parents", "zh", False),
    ("The Scroll Of Taiwu", "zh", False),
    ("Tale of Immortal", "zh", False),
    ("The Invisible Guardian", "zh", False),
    ("Gujian 3", "zh", False),
]

STORE_LANG = {"en": "english", "ja": "japanese", "zh": "schinese"}
UA = {"User-Agent": "Mozilla/5.0 (Glossa test collector)"}


PROXY = "socks5h://127.0.0.1:10808"  # the machine's SOCKS proxy; some hosts fail TLS without it


def fetch(url, attempts=3):
    """Direct first; on a dropped or refused TLS connection, through the SOCKS proxy (Windows curl)."""
    for attempt in range(attempts):
        try:
            req = urllib.request.Request(url, headers=UA)
            with urllib.request.urlopen(req, timeout=30) as r:
                return r.read()
        except OSError:
            try:
                return subprocess.run(["curl", "-sSL", "--fail", "--max-time", "60", "--proxy", PROXY, "-A", UA["User-Agent"], url],
                                      check=True, capture_output=True).stdout
            except subprocess.CalledProcessError:
                if attempt == attempts - 1:
                    raise
                time.sleep(10 * (attempt + 1))


def get_json(url):
    return json.loads(fetch(url))


def app_id(term):
    q = urllib.parse.quote(term)
    items = get_json(f"https://store.steampowered.com/api/storesearch/?term={q}&l=english&cc=US").get("items", [])
    return (items[0]["id"], items[0]["name"]) if items else (None, None)


def screenshots(appid, lang):
    urls = []
    for l in dict.fromkeys(["english", STORE_LANG[lang]]):
        data = get_json(f"https://store.steampowered.com/api/appdetails?appids={appid}&l={l}").get(str(appid), {})
        if not data.get("success"):
            continue
        for s in data["data"].get("screenshots", []):
            if s["path_full"] not in urls:
                urls.append(s["path_full"])
        time.sleep(1.5)  # the store API rate-limits bursts
    return urls


def script_counts(text):
    return {
        "kana": len(re.findall(r"[\u3040-\u30ff]", text)),
        "han": len(re.findall(r"[\u4e00-\u9fff]", text)),
        "latin": len(re.findall(r"[A-Za-z]", text)),
    }


def wanted(lang, c):
    if lang == "ja":
        return c["kana"] >= 15
    if lang == "zh":
        return c["han"] >= 20 and c["kana"] == 0
    return c["latin"] >= 60


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    ocr = RapidOCR(params={"Global.log_level": "error"})
    index_path = OUT / "index.json"
    index = json.loads(index_path.read_text(encoding="utf-8")) if index_path.exists() else []
    only = set(sys.argv[1:])  # optional: search terms to (re)collect
    games = [g for g in GAMES if not only or g[0] in only]
    for term, lang, adult in games:
        try:
            appid, name = app_id(term)
            if appid is None:
                print(f"[{term}] not found"); continue
            urls = screenshots(appid, lang)
        except Exception as e:  # noqa: BLE001 - keep collecting other games
            print(f"[{term}] store error: {e}"); continue
        folder = OUT / re.sub(r"[^A-Za-z0-9]+", "_", name).strip("_")
        folder.mkdir(exist_ok=True)
        kept = 0
        for i, url in enumerate(urls[:14]):
            path = folder / f"{i:02d}.jpg"
            try:
                if not path.exists():
                    path.write_bytes(fetch(url))
                res = ocr(str(path))
                text = "".join(res.txts or [])
                c = script_counts(text)
                ok = wanted(lang, c)
                if ok:
                    kept += 1
                    index = [x for x in index if x["file"] != str(path)]
                    index.append({"game": name, "appid": appid, "lang": lang, "adult": adult, "file": str(path),
                                  "counts": c, "text": text[:300]})
                else:
                    path.unlink()
            except Exception as e:  # noqa: BLE001
                print(f"  {url}: {e}")
        print(f"[{name}] {lang} appid={appid}: {len(urls)} screenshots, kept {kept}")
        index_path.write_text(json.dumps(index, ensure_ascii=False, indent=1), encoding="utf-8")
        time.sleep(3)


if __name__ == "__main__":
    sys.exit(main())
