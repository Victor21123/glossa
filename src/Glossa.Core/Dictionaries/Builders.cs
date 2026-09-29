using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml;
using Glossa.Core.Text;

namespace Glossa.Core.Dictionaries;

/// <summary>
/// JMdict (EDRDG, CC BY-SA 4.0): Japanese with Russian glosses where they exist (about a third of entries) and
/// English glosses for all. Part-of-speech and usage codes are shown as short Russian labels.
/// </summary>
public static class JmdictBuilder
{
    private static readonly Dictionary<string, string> Labels = new(StringComparer.Ordinal)
    {
        ["n"] = "сущ.", ["pn"] = "мест.", ["adj-i"] = "прил.", ["adj-ix"] = "прил.", ["adj-na"] = "na-прил.",
        ["adj-no"] = "no-прил.", ["adj-pn"] = "предн.", ["adj-t"] = "taru-прил.", ["adj-f"] = "атриб.",
        ["adv"] = "нареч.", ["adv-to"] = "нареч. (to)", ["conj"] = "союз", ["int"] = "межд.", ["prt"] = "частица",
        ["suf"] = "суфф.", ["pref"] = "преф.", ["ctr"] = "счётн.", ["exp"] = "выраж.", ["aux-v"] = "всп. гл.",
        ["aux-adj"] = "всп. прил.", ["aux"] = "всп.", ["v1"] = "гл. ichidan", ["v1-s"] = "гл. ichidan",
        ["vs"] = "гл. suru", ["vs-i"] = "гл. suru", ["vs-s"] = "гл. suru", ["vk"] = "гл. kuru", ["vz"] = "гл. zuru",
        ["vi"] = "неперех.", ["vt"] = "перех.", ["num"] = "числ.", ["cop"] = "связка", ["n-suf"] = "сущ. (суфф.)",
        ["n-pref"] = "сущ. (преф.)", ["n-adv"] = "сущ.-нареч.", ["n-t"] = "сущ. (время)",
        ["uk"] = "обычно каной", ["arch"] = "арх.", ["obs"] = "устар.", ["col"] = "разг.", ["hon"] = "почт.",
        ["hum"] = "скромн.", ["pol"] = "вежл.", ["sl"] = "сленг", ["abbr"] = "сокр.",
        ["on-mim"] = "звукоподр.", ["poet"] = "поэт.", ["derog"] = "пренебр.", ["fam"] = "фам.", ["rare"] = "редк.",
        ["male"] = "муж. речь", ["fem"] = "жен. речь", ["joc"] = "шутл.", ["id"] = "идиома", ["yoji"] = "ёдзидзюкуго",
        ["proverb"] = "посл.", ["form"] = "книжн.", ["chn"] = "детск.", ["ksb"] = "кансай", ["dated"] = "устар.",
        ["vs-c"] = "гл. su (арх.)", ["adj-ku"] = "ku-прил. (арх.)", ["adj-nari"] = "nari-прил. (арх.)",
        ["adj-shiku"] = "shiku-прил. (арх.)", ["adj-kari"] = "kari-прил. (арх.)", ["vr"] = "гл. ri (арх.)",
        // Register: JMdict dropped its "X" (rude or X-rated) tag; crude words carry vulg, sens or the slang tags.
        ["vulg"] = "вульг.", ["sens"] = "деликатн.", ["net-sl"] = "интернет-сленг", ["m-sl"] = "манга-сленг",
        ["euph"] = "эвфем.", ["hist"] = "ист.", ["unc"] = "неясн.", ["litf"] = "лит.", ["rk"] = "редк. кандзи",
        ["gikun"] = "гикун", ["obsc"] = "малоупотр.",
        // Names and things (JMdict proper-noun senses).
        ["person"] = "имя", ["surname"] = "фамилия", ["given"] = "имя", ["place"] = "место", ["station"] = "станция",
        ["organization"] = "организация", ["company"] = "компания", ["product"] = "продукт", ["work"] = "произведение",
        ["serv"] = "сервис", ["char"] = "персонаж", ["fict"] = "вымышл.", ["dei"] = "божество", ["ev"] = "событие",
        ["myth"] = "миф.", ["creat"] = "существо", ["group"] = "группа", ["obj"] = "объект", ["quote"] = "цитата",
        ["relig"] = "религ.", ["doc"] = "документ", ["leg"] = "легенда", ["ship"] = "корабль", ["oth"] = "прочее",
    };

    /// <summary>Russian label for a JMdict code; verb classes by family (v5r, v5k-s… → «гл. godan»).</summary>
    private static string Label(string code) =>
        Labels.TryGetValue(code, out var label) ? label
        : code.StartsWith("v5", StringComparison.Ordinal) ? "гл. godan"
        : code.StartsWith("v4", StringComparison.Ordinal) ? "гл. yodan (арх.)"
        : code.StartsWith("v2", StringComparison.Ordinal) ? "гл. nidan (арх.)"
        : code;

    private sealed class Sense
    {
        public List<string> Pos { get; } = [];
        public List<string> Misc { get; } = [];
        public List<string> English { get; } = [];
        public List<string> Russian { get; } = [];
        public string? Info { get; set; }
    }

    public static PackInfo Build(string jmdictGz, string packPath, IProgress<double>? progress, CancellationToken ct)
    {
        var meta = new PackMeta("jmdict", "JMdict", "ja", "ru", Priority: 20,
            Description: "Японско-английский словарь EDRDG, русские значения — где они есть",
            License: "CC BY-SA 4.0, EDRDG", Origin: "http://ftp.edrdg.org/pub/Nihongo/JMdict.gz");
        using var writer = new DictionaryPackWriter(packPath, meta);
        using var file = File.OpenRead(jmdictGz);
        using var gz = new GZipStream(file, CompressionMode.Decompress);
        // XmlTextReader keeps JMdict's entities (&n; &v5k;) as references, so the short codes can be read back.
        // No resolver: the DTD is inline, and nothing external may be fetched (the file comes over plain http).
        using var xml = new XmlTextReader(gz)
        {
            DtdProcessing = DtdProcessing.Parse,
            EntityHandling = EntityHandling.ExpandCharEntities,
            XmlResolver = null,
        };

        var kanji = new List<string>();
        var readings = new List<string>();
        var senses = new List<Sense>();
        Sense? sense = null;
        List<string>? lastPos = null;
        string? element = null;
        string? glossLang = null;
        var n = 0;

        while (xml.Read())
        {
            switch (xml.NodeType)
            {
                case XmlNodeType.Element:
                    element = xml.Name;
                    switch (element)
                    {
                        case "entry":
                            kanji.Clear(); readings.Clear(); senses.Clear(); lastPos = null;
                            break;
                        case "sense":
                            sense = new Sense();
                            senses.Add(sense);
                            break;
                        case "gloss":
                            glossLang = xml.GetAttribute("xml:lang") ?? "eng";
                            break;
                    }
                    break;

                case XmlNodeType.EntityReference when sense is not null:
                    if (element == "pos") sense.Pos.Add(xml.Name);
                    else if (element == "misc") sense.Misc.Add(xml.Name);
                    break;

                case XmlNodeType.Text:
                    var text = xml.Value;
                    switch (element)
                    {
                        case "keb": kanji.Add(text); break;
                        case "reb": readings.Add(text); break;
                        case "s_inf" when sense is not null: sense.Info = text; break;
                        case "gloss" when sense is not null:
                            if (glossLang == "eng") sense.English.Add(text);
                            else if (glossLang == "rus") sense.Russian.Add(text);
                            break;
                    }
                    break;

                case XmlNodeType.EndElement:
                    if (xml.Name == "sense" && sense is not null)
                    {
                        // A sense without <pos> inherits the previous one (JMdict convention).
                        if (sense.Pos.Count == 0 && lastPos is not null && sense.English.Count > 0) sense.Pos.AddRange(lastPos);
                        else if (sense.Pos.Count > 0) lastPos = [.. sense.Pos];
                        sense = null;
                    }
                    else if (xml.Name == "entry")
                    {
                        AddEntry(writer, kanji, readings, senses);
                        if (++n % 10000 == 0)
                        {
                            ct.ThrowIfCancellationRequested();
                            progress?.Report((double)file.Position / file.Length);
                        }
                    }
                    element = null;
                    break;
            }
        }
        return writer.Complete();
    }

    private static void AddEntry(DictionaryPackWriter writer, List<string> kanji, List<string> readings, List<Sense> senses)
    {
        if (readings.Count == 0) return;
        var english = senses.Where(s => s.English.Count > 0).ToList();
        var russian = senses.Where(s => s.Russian.Count > 0).ToList();
        if (english.Count == 0 && russian.Count == 0) return;

        var body = new StringBuilder();
        for (var i = 0; i < russian.Count; i++)
        {
            if (body.Length > 0) body.Append('\n');
            if (russian.Count > 1) body.Append(i + 1).Append(") ");
            body.Append("[t]").Append(DictMarkup.Escape(string.Join("; ", russian[i].Russian))).Append("[/t]");
        }
        for (var i = 0; i < english.Count; i++)
        {
            var s = english[i];
            if (body.Length > 0) body.Append('\n');
            if (english.Count > 1) body.Append(i + 1).Append(". ");
            var labels = s.Pos.Concat(s.Misc).Select(Label).Distinct().ToList();
            if (labels.Count > 0) body.Append("[p]").Append(DictMarkup.Escape(string.Join(", ", labels))).Append("[/p] ");
            // Russian text above makes the English secondary; without it, English is the translation.
            var gloss = DictMarkup.Escape(string.Join("; ", s.English));
            body.Append(russian.Count > 0 ? gloss : $"[t]{gloss}[/t]");
            if (s.Info is not null) body.Append(" [c](").Append(DictMarkup.Escape(s.Info)).Append(")[/c]");
        }

        var usuallyKana = senses.Count > 0 && senses[0].Misc.Contains("uk");
        var keys = new List<DictKey>();
        foreach (var k in kanji) keys.Add(new DictKey(k, 0));
        foreach (var r in readings) keys.Add(new DictKey(r, kanji.Count == 0 || usuallyKana ? 0 : 1));
        var headword = kanji.Count > 0 ? kanji[0] : readings[0];
        writer.Add(headword, kanji.Count > 0 ? readings[0] : null, body.ToString(), keys);
    }
}

/// <summary>
/// English Wiktionary as extracted by kaikki.org (wiktextract, CC BY-SA): English definitions, IPA, Russian
/// translations per sense, and every inflected form as a search key ("went" → go, "looked after" → look after).
/// </summary>
public static class WiktionaryBuilder
{
    private static readonly HashSet<string> SkippedPos = new(StringComparer.Ordinal) { "name", "character", "symbol", "romanization" };

    private static readonly HashSet<string> NoiseTags = new(StringComparer.Ordinal)
    {
        "form-of", "alt-of", "table-tags", "inflection-template", "class", "no-table-tags", "canonical",
        "morpheme", "error-unknown-tag",
    };

    private static readonly Dictionary<string, string> PosNames = new(StringComparer.Ordinal)
    {
        ["noun"] = "сущ.", ["verb"] = "гл.", ["adj"] = "прил.", ["adv"] = "нареч.", ["pron"] = "мест.",
        ["prep"] = "предл.", ["conj"] = "союз", ["intj"] = "межд.", ["det"] = "опред.", ["num"] = "числ.",
        ["particle"] = "частица", ["phrase"] = "фраза", ["prep_phrase"] = "предл. оборот", ["proverb"] = "посл.",
        ["prefix"] = "преф.", ["suffix"] = "суфф.", ["abbrev"] = "сокр.", ["contraction"] = "стяж.", ["article"] = "арт.",
    };

    /// <param name="language">en, ja or zh: the kaikki.org extract of that language from English Wiktionary.</param>
    public static PackInfo Build(string kaikkiJsonlGz, string packPath, IProgress<double>? progress, CancellationToken ct, string language = "en")
    {
        var meta = language switch
        {
            "ja" => new PackMeta("wiktionary-ja", "Wiktionary (японский)", "ja", "en", Priority: 30,
                Description: "Значения на английском, в том числе сленг, интернет-сленг и 18+ (с цензурными написаниями вроде ま○こ)",
                License: "CC BY-SA 4.0, Wiktionary / kaikki.org", Origin: "https://kaikki.org/dictionary/Japanese/"),
            "zh" => new PackMeta("wiktionary-zh", "Wiktionary (китайский)", "zh", "en", Priority: 30,
                Description: "Значения на английском, в том числе сленг, интернет-сленг и мат, пиньинь",
                License: "CC BY-SA 4.0, Wiktionary / kaikki.org", Origin: "https://kaikki.org/dictionary/Chinese/"),
            _ => new PackMeta("wiktionary-en", "Wiktionary (английский)", "en", "ru", Priority: 10,
                Description: "Толкования на английском, транскрипция, переводы на русский по значениям, все словоформы",
                License: "CC BY-SA 4.0, Wiktionary / kaikki.org", Origin: "https://kaikki.org/dictionary/English/"),
        };
        using var writer = new DictionaryPackWriter(packPath, meta);
        using var file = File.OpenRead(kaikkiJsonlGz);
        using var gz = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new StreamReader(gz, Encoding.UTF8, false, 1 << 20);

        var word = "";
        var group = new List<JsonDocument>();
        var n = 0;
        while (reader.ReadLine() is { } line)
        {
            if (++n % 50000 == 0)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report((double)file.Position / file.Length);
            }
            var doc = JsonDocument.Parse(line);
            var w = doc.RootElement.TryGetProperty("word", out var wp) ? wp.GetString() ?? "" : "";
            if (w != word)
            {
                Flush(writer, word, group, language);
                word = w;
            }
            group.Add(doc);
        }
        Flush(writer, word, group, language);
        return writer.Complete();
    }

    /// <summary>All parts of speech of one word become one article.</summary>
    private static void Flush(DictionaryPackWriter writer, string word, List<JsonDocument> group, string language)
    {
        try
        {
            if (word.Length == 0 || group.Count == 0) return;
            var body = new StringBuilder();
            var keys = new List<DictKey>();
            string? ipa = null;
            string? lemmaOf = null;
            var hasContent = false;
            var parts = 0;
            var datedOnly = 0;

            foreach (var doc in group)
            {
                var e = doc.RootElement;
                var pos = e.TryGetProperty("pos", out var p) ? p.GetString() ?? "" : "";
                // Letters are noise in English; a kanji or hanzi entry is a real lookup target.
                if (pos == "soft-redirect" || SkippedPos.Contains(pos) && !(pos == "character" && language != "en")) continue;
                ipa ??= language switch { "ja" => Kana(e) ?? Ipa(e), "zh" => Pinyin(e), _ => Ipa(e) };

                var senses = e.TryGetProperty("senses", out var ss) ? ss.EnumerateArray().ToList() : [];
                var real = senses.Where(s => !s.TryGetProperty("form_of", out _) && !s.TryGetProperty("alt_of", out _) && HasGloss(s)).ToList();
                if (real.Count == 0)
                {
                    // Only "past tense of go": point this spelling at the lemma instead of adding an article.
                    lemmaOf ??= senses.Select(FormOf).FirstOrDefault(x => x is not null);
                    continue;
                }

                if (body.Length > 0) body.Append('\n');
                body.Append("[p]").Append(DictMarkup.Escape(PosNames.GetValueOrDefault(pos, pos))).Append("[/p]");
                // Translations Wiktionary could not tie to a sense: shown only when no sense has its own.
                var unassigned = real.Any(s => Russian(s).Count > 0) ? [] : Russian(e);
                if (unassigned.Count > 0) body.Append(" [t]").Append(DictMarkup.Escape(string.Join(", ", unassigned.Take(6)))).Append("[/t]");
                if (real.All(IsDated)) datedOnly++;

                var i = 0;
                foreach (var s in real.Take(12))
                {
                    i++;
                    var glosses = s.GetProperty("glosses").EnumerateArray().Select(g => g.GetString() ?? "").ToList();
                    var gloss = glosses[^1];
                    var tags = s.TryGetProperty("tags", out var t)
                        ? t.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0 && !NoiseTags.Contains(x)).Take(3).ToList()
                        : [];
                    body.Append('\n').Append(i).Append(". ");
                    if (tags.Count > 0) body.Append("[c](").Append(DictMarkup.Escape(string.Join(", ", tags))).Append(")[/c] ");
                    body.Append(DictMarkup.Escape(gloss));
                    var ru = Russian(s);
                    if (ru.Count > 0) body.Append("\n\t[t]").Append(DictMarkup.Escape(string.Join(", ", ru.Take(6)))).Append("[/t]");
                    if (Example(s) is { } ex) body.Append("\n\t[ex]").Append(DictMarkup.Escape(ex)).Append("[/ex]");
                }
                hasContent = true;
                parts++;

                if (e.TryGetProperty("forms", out var forms))
                {
                    foreach (var f in forms.EnumerateArray())
                    {
                        var form = f.TryGetProperty("form", out var fv) ? fv.GetString() : null;
                        if (string.IsNullOrWhiteSpace(form) || form == word || form.Length > 60) continue;
                        if (language != "en" && form.Contains(' ')) continue; // "食べる transitive ichidan"
                        if (f.TryGetProperty("tags", out var ft) && ft.EnumerateArray().Any(x => x.GetString() is "table-tags" or "inflection-template" or "romanization"))
                            continue;
                        keys.Add(new DictKey(form, 2));
                    }
                }
            }

            if (hasContent)
            {
                // An article that is only obsolete senses ("went", n.) ranks below the lemma it is a form of.
                keys.Insert(0, new DictKey(word, datedOnly == parts ? 3 : 0));
                if (language == "ja" && ipa is not null && ipa != word) keys.Add(new DictKey(ipa, 1));
                writer.Add(word, ipa, body.ToString(), keys);
            }
            else if (lemmaOf is not null && lemmaOf != word)
            {
                writer.AddLink(word, lemmaOf);
            }
        }
        finally
        {
            foreach (var d in group) d.Dispose();
            group.Clear();
        }
    }

    private static bool IsDated(JsonElement s) =>
        s.TryGetProperty("tags", out var t) && t.EnumerateArray().Any(x => x.GetString() is "obsolete" or "archaic" or "dated");

    private static bool HasGloss(JsonElement s) =>
        s.TryGetProperty("glosses", out var g) && g.ValueKind == JsonValueKind.Array && g.GetArrayLength() > 0;

    private static string? FormOf(JsonElement s)
    {
        foreach (var name in new[] { "form_of", "alt_of" })
        {
            if (s.TryGetProperty(name, out var f) && f.ValueKind == JsonValueKind.Array && f.GetArrayLength() > 0
                && f[0].TryGetProperty("word", out var w))
                return w.GetString();
        }
        return null;
    }

    private static List<string> Russian(JsonElement e)
    {
        if (!e.TryGetProperty("translations", out var tr) || tr.ValueKind != JsonValueKind.Array) return [];
        return tr.EnumerateArray()
            .Where(t => t.TryGetProperty("code", out var c) && c.GetString() == "ru" && t.TryGetProperty("word", out _))
            .Select(t => t.GetProperty("word").GetString()!.Replace("́", "").Trim())
            .Where(w => w.Length > 0)
            .Distinct()
            .ToList();
    }

    /// <summary>Japanese reading in kana ("sounds": [{"other": "たべる"}]).</summary>
    private static string? Kana(JsonElement e)
    {
        if (!e.TryGetProperty("sounds", out var sounds)) return null;
        foreach (var s in sounds.EnumerateArray())
            if (s.TryGetProperty("other", out var o) && o.GetString() is { Length: > 0 } kana && kana.All(c => Scripts.Of(c) == Script.Kana)) return kana;
        return null;
    }

    /// <summary>Mandarin pinyin ("sounds": [{"zh_pron": "shǎbī", "tags": ["Mandarin", "Pinyin"]}]).</summary>
    private static string? Pinyin(JsonElement e)
    {
        if (!e.TryGetProperty("sounds", out var sounds)) return null;
        foreach (var s in sounds.EnumerateArray())
        {
            if (s.TryGetProperty("zh_pron", out var z) && s.TryGetProperty("tags", out var tags)
                && tags.EnumerateArray().Any(t => t.GetString() == "Mandarin") && tags.EnumerateArray().Any(t => t.GetString() == "Pinyin"))
                return z.GetString();
        }
        return null;
    }

    private static string? Ipa(JsonElement e)
    {
        if (!e.TryGetProperty("sounds", out var sounds)) return null;
        string? any = null;
        foreach (var s in sounds.EnumerateArray())
        {
            if (!s.TryGetProperty("ipa", out var ipa)) continue;
            var value = ipa.GetString();
            any ??= value;
            // General American or UK RP first; regional accents only as a fallback.
            if (s.TryGetProperty("tags", out var tags) && tags.EnumerateArray().Any(t => t.GetString() is "General-American" or "US" or "Received-Pronunciation" or "UK"))
                return value;
        }
        return any;
    }

    private static string? Example(JsonElement s)
    {
        if (!s.TryGetProperty("examples", out var exs)) return null;
        foreach (var ex in exs.EnumerateArray())
        {
            // Usage examples are short; dated literary quotations are not worth pop-up space.
            if (ex.TryGetProperty("type", out var type) && type.GetString() == "quotation") continue;
            if (ex.TryGetProperty("text", out var text) && text.GetString() is { Length: > 0 and < 200 } t) return t;
        }
        return null;
    }
}

/// <summary>CC-CEDICT (MDBG, CC BY-SA 4.0) as a pack: Chinese → English, pinyin with tone marks.</summary>
public static class CedictBuilder
{
    public static PackInfo Build(string cedictPath, string packPath, IProgress<double>? progress, CancellationToken ct)
    {
        var meta = new PackMeta("cedict", "CC-CEDICT", "zh", "en", Priority: 20,
            Description: "Китайско-английский словарь, упрощённые и традиционные иероглифы",
            License: "CC BY-SA 4.0, MDBG", Origin: "https://www.mdbg.net/chinese/dictionary?page=cedict");
        var groups = new Dictionary<string, List<CedictEntry>>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var line in File.ReadLines(cedictPath, Encoding.UTF8))
        {
            if (line.Length == 0 || line[0] == '#') continue;
            if (ChineseDictionary.Parse(line) is not { } e) continue;
            if (!groups.TryGetValue(e.Simplified, out var list))
            {
                groups[e.Simplified] = list = [];
                order.Add(e.Simplified);
            }
            list.Add(e);
        }

        using var writer = new DictionaryPackWriter(packPath, meta);
        var n = 0;
        foreach (var simplified in order)
        {
            if (++n % 20000 == 0)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report((double)n / order.Count);
            }
            var entries = groups[simplified].OrderBy(e => e.IsProperName).ToList();
            var body = new StringBuilder();
            foreach (var e in entries)
            {
                if (body.Length > 0) body.Append('\n');
                if (entries.Count > 1) body.Append("[c]").Append(DictMarkup.Escape(ChineseDictionary.ToToneMarks(e.Pinyin))).Append("[/c] ");
                body.Append(DictMarkup.Escape(string.Join("; ", e.Glosses)));
                if (e.Traditional != e.Simplified) body.Append(" [c](").Append(DictMarkup.Escape(e.Traditional)).Append(")[/c]");
            }
            var keys = new List<DictKey> { new(simplified, 0) };
            keys.AddRange(entries.Where(e => e.Traditional != simplified).Select(e => new DictKey(e.Traditional, 0)));
            writer.Add(simplified, ChineseDictionary.ToToneMarks(entries[0].Pinyin), body.ToString(), keys);
        }
        return writer.Complete();
    }
}
