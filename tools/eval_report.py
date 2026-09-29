"""Builds the model comparison page from glossa-cli eval runs and a grades file.

Usage: .venv\\Scripts\\python.exe tools\\eval_report.py OUT.html CONFIG.json
CONFIG.json: {"runs": [{"name", "title", "role", "vram_gb", "note"}], "grades": {case: {run: "good|ok|bad"}},
              "recommendation": "html", "intro": "html"}
"""
import html
import json
import sys

DIR = "D:/GlossaData/test/eval_results"


def pct(values, p):
    v = sorted(x for x in values if x is not None and x > 0)
    return v[min(len(v) - 1, int(len(v) * p))] if v else None


def main():
    out, cfg_path = sys.argv[1], sys.argv[2]
    cfg = json.load(open(cfg_path, encoding="utf-8"))
    runs = cfg["runs"]
    results = {r["name"]: {x["Id"]: x for x in json.load(open(f"{DIR}/{r['name']}.json", encoding="utf-8"))} for r in runs}
    grades = cfg.get("grades", {})
    case_ids = list(results[runs[0]["name"]].keys())

    def group(cid):
        return "screen" if cid.startswith("s-") else cid.split("-")[1]

    summary = []
    for r in runs:
        rs = results[r["name"]].values()
        score = {"all": [0, 0]}
        for cid in case_ids:
            g = grades.get(cid, {}).get(r["name"])
            if g is None:
                continue
            pts = {"good": 2, "ok": 1, "bad": 0}[g]
            for key in ("all", group(cid)):
                score.setdefault(key, [0, 0])
                score[key][0] += pts
                score[key][1] += 2
        summary.append({
            **r,
            "card50": pct([x.get("cardMs") for x in rs], .5), "card90": pct([x.get("cardMs") for x in rs], .9),
            "cardMax": pct([x.get("cardMs") for x in rs], 1.0), "first": pct([x.get("firstMs") for x in rs], .5),
            "tr50": pct([x.get("trMs") for x in rs], .5),
            "score": {k: round(100 * a / b) if b else None for k, (a, b) in score.items()},
        })

    cases = []
    for cid in case_ids:
        base = results[runs[0]["name"]][cid]
        cases.append({
            "id": cid, "group": group(cid), "lang": base.get("lang"), "word": base.get("word"),
            "context": base.get("context"), "expect": base.get("Expect"),
            "answers": [{
                "run": r["name"],
                "grade": grades.get(cid, {}).get(r["name"]),
                **{k: results[r["name"]].get(cid, {}).get(k) for k in
                   ("translation", "register", "usage", "pos", "contextTranslation", "translator", "cardMs", "trMs", "form", "reading", "level")},
            } for r in runs],
        })

    data = json.dumps({"summary": summary, "cases": cases}, ensure_ascii=False)
    page = TEMPLATE.replace("__DATA__", data.replace("</", "<\\/")) \
        .replace("__INTRO__", cfg.get("intro", "")).replace("__RECOMMENDATION__", cfg.get("recommendation", ""))
    open(out, "w", encoding="utf-8").write(page)
    print(out, len(page) // 1024, "KB")


TEMPLATE = r"""<title>Выбор ИИ-модели Glossa</title>
<link rel="stylesheet" href="https://fonts.googleapis.com/css2?family=IBM+Plex+Sans:wght@400;500;600&family=IBM+Plex+Mono:wght@400;500&family=Noto+Sans+JP:wght@400;500&family=Noto+Sans+SC:wght@400;500&display=swap">
<style>
:root {
  --bg: #f3f4f6; --surface: #ffffff; --ink: #1b1e23; --muted: #5d6470; --line: #dde1e6;
  --accent: #a86a12; --accent-soft: #f6ead6;
  --good: #1f7a53; --good-soft: #dcf1e6; --ok: #8a6a00; --ok-soft: #f7efcf; --bad: #b3261e; --bad-soft: #f9e0de;
  --strong: #b3261e; --tag: #2f6f66;
  font-family: "IBM Plex Sans", "Noto Sans JP", "Noto Sans SC", system-ui, sans-serif;
}
@media (prefers-color-scheme: dark) {
  :root:not([data-theme="light"]) {
    color-scheme: dark;
    --bg: #15171a; --surface: #1e2125; --ink: #e8e9eb; --muted: #9aa0a8; --line: #30343a;
    --accent: #e9c46a; --accent-soft: #3a3120;
    --good: #5fcf98; --good-soft: #173528; --ok: #e3c35a; --ok-soft: #3a3217; --bad: #f28b82; --bad-soft: #3d1f1d;
    --strong: #e0594f; --tag: #3f8f84;
  }
}
:root[data-theme="dark"] {
  color-scheme: dark;
  --bg: #15171a; --surface: #1e2125; --ink: #e8e9eb; --muted: #9aa0a8; --line: #30343a;
  --accent: #e9c46a; --accent-soft: #3a3120;
  --good: #5fcf98; --good-soft: #173528; --ok: #e3c35a; --ok-soft: #3a3217; --bad: #f28b82; --bad-soft: #3d1f1d;
  --strong: #e0594f; --tag: #3f8f84;
}
body { background: var(--bg); color: var(--ink); font-size: 15px; line-height: 1.5; }
.wrap { max-width: 1280px; margin: 0 auto; padding-inline: 16px; padding-block: 24px 64px; display: grid; gap: 28px; }
h1 { font-size: 26px; font-weight: 600; margin: 0; text-wrap: balance; }
h2 { font-size: 18px; font-weight: 600; margin: 0 0 10px; text-wrap: balance; }
p { margin: 0; max-width: 72ch; }
.muted { color: var(--muted); }
.mono { font-family: "IBM Plex Mono", ui-monospace, monospace; font-variant-numeric: tabular-nums; }
.intro { display: grid; gap: 8px; }
.table-scroll { overflow-x: auto; background: var(--surface); border: 1px solid var(--line); border-radius: 8px; }
table { border-collapse: collapse; width: 100%; min-width: 860px; }
th, td { text-align: left; padding: 9px 12px; border-bottom: 1px solid var(--line); vertical-align: top; }
th { font-size: 12px; font-weight: 500; letter-spacing: .04em; text-transform: uppercase; color: var(--muted); }
tr:last-child td { border-bottom: 0; }
td.num { font-family: "IBM Plex Mono", monospace; font-variant-numeric: tabular-nums; white-space: nowrap; }
.bar { display: flex; align-items: center; gap: 8px; }
.bar i { display: block; height: 6px; border-radius: 3px; background: var(--accent); }
.rec { background: var(--accent-soft); border-radius: 8px; padding: 16px 18px; display: grid; gap: 8px; }
.filters { display: flex; flex-wrap: wrap; gap: 8px; align-items: center; position: sticky; top: env(safe-area-inset-top, 0px); background: var(--bg); padding-block: 8px; z-index: 2; }
.chip { border: 1px solid var(--line); background: var(--surface); color: var(--ink); border-radius: 999px; padding: 5px 12px; font: inherit; font-size: 13px; cursor: pointer; }
.chip[aria-pressed="true"] { background: var(--ink); color: var(--bg); border-color: var(--ink); }
.chip:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; }
.sep { width: 1px; height: 22px; background: var(--line); }
.cases { display: grid; gap: 14px; }
.case { background: var(--surface); border: 1px solid var(--line); border-radius: 8px; padding: 14px 16px; display: grid; gap: 10px; }
.case-head { display: flex; flex-wrap: wrap; gap: 6px 14px; align-items: baseline; }
.word { font-size: 20px; font-weight: 600; }
.lang { font-size: 11px; letter-spacing: .08em; text-transform: uppercase; color: var(--muted); }
.ctx { color: var(--muted); }
.expect { font-size: 13px; }
.expect b { font-weight: 500; color: var(--accent); }
.answers { display: grid; grid-template-columns: repeat(auto-fit, minmax(230px, 1fr)); gap: 10px; }
.ans { border-radius: 6px; padding: 10px 12px; display: grid; gap: 4px; align-content: start; background: var(--bg); border-left: 3px solid var(--line); }
.ans.good { border-left-color: var(--good); } .ans.ok { border-left-color: var(--ok); } .ans.bad { border-left-color: var(--bad); }
.ans-head { display: flex; justify-content: space-between; gap: 8px; font-size: 12px; color: var(--muted); }
.grade { font-weight: 600; font-size: 12px; padding: 0 6px; border-radius: 4px; }
.grade.good { color: var(--good); background: var(--good-soft); } .grade.ok { color: var(--ok); background: var(--ok-soft); } .grade.bad { color: var(--bad); background: var(--bad-soft); }
.tr { font-weight: 600; }
.reg { font-size: 11px; color: #fff; background: var(--tag); border-radius: 4px; padding: 0 5px; margin-left: 4px; font-weight: 500; }
.reg.strong { background: var(--strong); }
.usage { font-size: 13px; color: var(--muted); font-style: italic; }
.line { font-size: 13px; }
.line span { color: var(--muted); font-size: 11px; text-transform: uppercase; letter-spacing: .05em; margin-right: 4px; }
@media (max-width: 600px) { .word { font-size: 18px; } h1 { font-size: 22px; } }
@media (prefers-reduced-motion: no-preference) { .chip { transition: background .15s, color .15s; } }
</style>

<div class="wrap">
  <header class="intro">
    <h1>Какая модель лучше передаёт грубость, сленг и 18+</h1>
    __INTRO__
  </header>

  <section>
    <h2>Сводка</h2>
    <div class="table-scroll"><table id="summary"></table></div>
    <p class="muted" style="margin-top:8px;font-size:13px">Оценка — доля баллов: ✓ = 2 (смысл и сила переданы), ~ = 1 (смысл есть, но смягчено или неточно), ✗ = 0 (ошибка). Время — полная ИИ-карточка; «первое поле» — когда в карточке появляется перевод слова.</p>
  </section>

  <section class="rec">__RECOMMENDATION__</section>

  <section>
    <h2>Примеры бок о бок</h2>
    <div class="filters" id="filters"></div>
    <div class="cases" id="cases"></div>
  </section>
</div>

<script>
const DATA = __DATA__;
const GROUPS = [["all","Все"],["screen","Скриншоты"],["en","Английский"],["ja","Японский"],["zh","Китайский"]];
const STRONG = new Set(["vulgar","sexual"]);
const REG_RU = {informal:"разг.", slang:"сленг", rude:"грубо", vulgar:"мат", sexual:"18+"};
const MARK = {good:"✓", ok:"~", bad:"✗"};
const state = { group: "all", hidden: new Set(), onlyBad: false };
try { const s = JSON.parse(localStorage.getItem("glossa-eval") || "{}"); if (s.group) state.group = s.group; (s.hidden||[]).forEach(h => state.hidden.add(h)); } catch (e) {}
const esc = s => (s ?? "").toString().replace(/[&<>"]/g, c => ({"&":"&amp;","<":"&lt;",">":"&gt;",'"':"&quot;"}[c]));
const sec = ms => ms == null || ms < 0 ? "—" : (ms / 1000).toFixed(1) + " с";

function summary() {
  const cols = [["title","Конфигурация"],["role","Роль"],["vram","VRAM"],["card","Карточка: медиана / p90 / макс"],["first","Первое поле"],["tr","Переводчик"],["score","Оценка"],["en","EN"],["ja","JA"],["zh","ZH"],["screen","Скрин."]];
  let h = "<thead><tr>" + cols.map(c => `<th>${c[1]}</th>`).join("") + "</tr></thead><tbody>";
  for (const r of DATA.summary) {
    const s = r.score || {};
    h += `<tr><td><b>${esc(r.title)}</b><div class="muted" style="font-size:13px">${esc(r.note || "")}</div></td>
      <td>${esc(r.role)}</td><td class="num">${r.vram_gb ?? "—"} ГБ</td>
      <td class="num">${sec(r.card50)} / ${sec(r.card90)} / ${sec(r.cardMax)}</td><td class="num">${sec(r.first)}</td>
      <td class="num">${sec(r.tr50)}</td>
      <td><div class="bar"><i style="width:${(s.all ?? 0) * 0.8}px"></i><span class="mono">${s.all ?? "—"}%</span></div></td>
      <td class="num">${s.en ?? "—"}</td><td class="num">${s.ja ?? "—"}</td><td class="num">${s.zh ?? "—"}</td><td class="num">${s.screen ?? "—"}</td></tr>`;
  }
  document.getElementById("summary").innerHTML = h + "</tbody>";
}

function filters() {
  const f = document.getElementById("filters");
  let h = GROUPS.map(([k, v]) => `<button class="chip" id="g-${k}" aria-pressed="${state.group === k}" data-group="${k}">${v}</button>`).join("");
  h += `<span class="sep"></span>`;
  h += DATA.summary.map(r => `<button class="chip" id="m-${r.name}" aria-pressed="${!state.hidden.has(r.name)}" data-model="${r.name}">${esc(r.short || r.title)}</button>`).join("");
  f.innerHTML = h;
  f.onclick = e => {
    const b = e.target.closest("button"); if (!b) return;
    if (b.dataset.group) state.group = b.dataset.group;
    if (b.dataset.model) state.hidden.has(b.dataset.model) ? state.hidden.delete(b.dataset.model) : state.hidden.add(b.dataset.model);
    try { localStorage.setItem("glossa-eval", JSON.stringify({group: state.group, hidden: [...state.hidden]})); } catch (e) {}
    filters(); cases();
  };
}

function cases() {
  const titles = Object.fromEntries(DATA.summary.map(r => [r.name, r.short || r.title]));
  const list = DATA.cases.filter(c => state.group === "all" || c.group === state.group);
  document.getElementById("cases").innerHTML = list.map(c => `
    <article class="case">
      <div class="case-head"><span class="word">${esc(c.word)}</span><span class="lang">${esc(c.lang)} · ${c.group === "screen" ? "скриншот" : "текст"}</span></div>
      <div class="ctx">${esc(c.context)}</div>
      <div class="expect"><b>Ожидается:</b> ${esc(c.expect)}</div>
      <div class="answers">${c.answers.filter(a => !state.hidden.has(a.run)).map(a => `
        <div class="ans ${a.grade || ""}">
          <div class="ans-head"><span>${esc(titles[a.run])}</span><span>${a.grade ? `<span class="grade ${a.grade}">${MARK[a.grade]}</span> ` : ""}<span class="mono">${sec(a.cardMs)}</span></span></div>
          <div><span class="tr">${esc(a.translation || "—")}</span>${a.register && REG_RU[a.register] ? `<span class="reg ${STRONG.has(a.register) ? "strong" : ""}">${REG_RU[a.register]}</span>` : ""}</div>
          ${a.usage ? `<div class="usage">контекст сцены: ${esc(a.usage)}</div>` : ""}
          ${a.contextTranslation ? `<div class="line"><span>карточка</span>${esc(a.contextTranslation)}</div>` : ""}
          ${a.translator && a.translator !== a.contextTranslation ? `<div class="line"><span>переводчик</span>${esc(a.translator)}</div>` : ""}
        </div>`).join("")}
      </div>
    </article>`).join("");
}
summary(); filters(); cases();
</script>
"""

if __name__ == "__main__":
    main()
