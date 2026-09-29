"""Imports a Glossa .apkg into a throwaway collection with the official Anki engine, twice.

The second import must update notes in place (same GUID), never duplicate them.
Usage: .venv\\Scripts\\python.exe tools\\validate_apkg.py path\\to\\file.apkg
"""
import sys
import tempfile
from pathlib import Path

from anki.collection import Collection, ImportAnkiPackageRequest, ImportAnkiPackageOptions


def import_once(col, apkg):
    req = ImportAnkiPackageRequest(
        package_path=str(apkg),
        options=ImportAnkiPackageOptions(with_scheduling=False, with_deck_configs=False, merge_notetypes=True),
    )
    out = col.import_anki_package(req)
    log = out.log
    return len(log.new), len(log.updated), len(log.duplicate), len(log.conflicting)


def main():
    apkg = Path(sys.argv[1]).resolve()
    with tempfile.TemporaryDirectory() as tmp:
        col = Collection(str(Path(tmp) / "collection.anki2"))
        print("import 1 (new, updated, duplicate, conflicting):", import_once(col, apkg))
        print("import 2 (new, updated, duplicate, conflicting):", import_once(col, apkg))
        print("notes:", col.note_count(), "cards:", col.card_count())
        print("decks:", sorted(d.name for d in col.decks.all_names_and_ids()))
        for nid in col.find_notes(""):
            note = col.get_note(nid)
            nt = note.note_type()["name"]
            print(f"- [{nt}] {note['Headword']} | {note['Translation']} | image={bool(note['Image'])} | cards={len(note.cards())}")
            card = note.cards()[0]
            q = card.question()
            print("  question html length:", len(q), "| contains <img:", "<img" in q)
        media = list(Path(col.media.dir()).iterdir())
        print("media files:", [m.name for m in media])
        col.close()


if __name__ == "__main__":
    main()
