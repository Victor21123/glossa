"""Side-by-side view of glossa-cli eval runs.

Usage: .venv\\Scripts\\python.exe tools\\compare_eval.py OUT.txt RUN1 RUN2 ...
Runs are names of D:/GlossaData/test/eval_results/<name>.json files.
"""
import json
import sys

DIR = "D:/GlossaData/test/eval_results"


def main():
    out, names = sys.argv[1], sys.argv[2:]
    runs = {n: {r["Id"]: r for r in json.load(open(f"{DIR}/{n}.json", encoding="utf-8"))} for n in names}
    lines = []
    for cid, first in runs[names[0]].items():
        lines.append(f"### {cid} [{first.get('lang')}] «{first.get('word')}» — {first.get('Expect')}")
        lines.append(f"    ctx: {first.get('context', '')[:120]}")
        for n in names:
            r = runs[n].get(cid, {})
            lines.append(f"  {n[:22]:22} {r.get('cardMs', '?'):>5}ms | {r.get('translation')} [{r.get('register')}] "
                         f"({r.get('pos')}) контекст сцены: {r.get('usage')} | ctx: {r.get('contextTranslation')} | tr: {r.get('translator')}")
    lines.append("")
    for n in names:
        rs = list(runs[n].values())
        card = sorted(r["cardMs"] for r in rs if r.get("cardMs", -1) > 0)
        first = sorted(r["firstMs"] for r in rs if r.get("firstMs", -1) > 0)
        tr = sorted(r["trMs"] for r in rs if r.get("trMs", -1) > 0)
        def q(v, p): return v[min(len(v) - 1, int(len(v) * p))] if v else -1
        lines.append(f"{n}: card median {q(card, .5)} ms, p90 {q(card, .9)} ms, max {card[-1] if card else -1} ms; "
                     f"first field {q(first, .5)} ms; translator median {q(tr, .5)} ms, p90 {q(tr, .9)} ms; "
                     f"errors {sum(1 for r in rs if r.get('error'))}")
    open(out, "w", encoding="utf-8").write("\n".join(lines))


if __name__ == "__main__":
    main()
