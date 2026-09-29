using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Glossa.Core.Config;
using Glossa.Core.Dictionaries;
using Glossa.Core.Ocr;
using Glossa.Core.Text;

namespace Glossa.Tests.Dictionaries;

public sealed class DictionaryTests : IDisposable
{
    private readonly TestFolders _temp = new();

    public void Dispose() => _temp.Dispose();

    private string Temp() => _temp.New();

    [Fact]
    public void Markup_parses_styles_indents_and_escapes()
    {
        var lines = DictMarkup.Parse("[p]сущ.[/p] [t]дом[/t]\n\t[ex]a \\[big\\] house[/ex]");
        Assert.Equal(2, lines.Count);
        Assert.Equal(SpanStyle.Label, lines[0].Spans[0].Style);
        Assert.Equal("дом", lines[0].Spans[2].Text);
        Assert.Equal(SpanStyle.Accent, lines[0].Spans[2].Style);
        Assert.Equal(1, lines[1].Indent);
        Assert.Equal("a [big] house", lines[1].Text);
        Assert.Equal("сущ. дом; a [big] house", DictMarkup.ToPlain("[p]сущ.[/p] [t]дом[/t];\n\t[ex]a \\[big\\] house[/ex]"));
    }

    [Fact]
    public void Html_becomes_lines_with_styles()
    {
        var markup = HtmlMarkup.Convert("<div><b>go</b> <span class=\"p\">v.</span><br>to <i>move</i> &amp; leave<ul><li>one</li><li>two</li></ul></div><script>x()</script>");
        var lines = DictMarkup.Parse(markup);
        Assert.Equal("go v.", lines[0].Text);
        Assert.Equal(SpanStyle.Bold, lines[0].Spans[0].Style);
        Assert.Equal(SpanStyle.Label, lines[0].Spans[2].Style);
        Assert.Equal("to move & leave", lines[1].Text);
        Assert.Equal("• one", lines[2].Text);
        Assert.Equal(1, lines[2].Indent);
        Assert.DoesNotContain(lines, l => l.Text.Contains("x()"));
    }

    [Fact]
    public void Style_spanning_a_line_break_is_reopened()
    {
        var lines = DictMarkup.Parse(HtmlMarkup.Convert("<i>one<br>two</i>"));
        Assert.All(lines, l => Assert.Equal(SpanStyle.Italic, l.Spans[0].Style));
    }

    [Fact]
    public void Dsl_headword_optional_and_unsorted_parts()
    {
        var (display, variants) = DslImporter.ParseHeadword("abandon(ed){[i] tr.[/i]}");
        Assert.Equal("abandon(ed) tr.", display);
        Assert.Equal(["abandon", "abandoned"], variants.Order().ToList());
    }

    [Fact]
    public void Dsl_import_reads_utf16_cards_tags_and_pinyin()
    {
        var dir = Temp();
        var dsl = Path.Combine(dir, "test.dsl");
        File.WriteAllText(dsl, """
            #NAME "Test"
            #INDEX_LANGUAGE "Chinese"
            #CONTENTS_LANGUAGE "Russian"

            学习
            學習
             xuéxí
             [m1]учиться, [p]гл.[/p] изучать[/m][m2][ex]学习计划 учебный план[/ex][/m]
             [m1][s]sound.wav[/s]см. <<学>>[/m]

            大
             dà
             [m1]большой ~[/m]
            """, Encoding.Unicode);

        var info = DictionaryImport.Import(dsl, dir, dir);
        Assert.Equal("zh", info.SourceLanguage);
        Assert.Equal("ru", info.TargetLanguage);
        Assert.Equal(2, info.Entries);

        using var pack = new DictionaryPack(info.Path);
        var e = Assert.Single(pack.Find(DictKeys.Normalize("學習")));
        Assert.Equal("学习", e.Headword);
        Assert.Equal("xuéxí", e.Reading);
        var lines = DictMarkup.Parse(e.Body);
        Assert.Equal("учиться, гл. изучать", lines[0].Text);
        Assert.Equal(1, lines[1].Indent > 0 ? 1 : 0);
        Assert.Equal(SpanStyle.Example, lines[1].Spans[0].Style);
        Assert.Equal("см. 学", lines[2].Text);
        Assert.Equal(SpanStyle.Ref, lines[2].Spans[1].Style);
        Assert.Equal("большой 大", DictMarkup.Parse(Assert.Single(pack.Find("大")).Body)[0].Text);
    }

    [Fact]
    public void Warodai_header_gives_kanji_and_kana_keys()
    {
        var dir = Temp();
        var txt = Path.Combine(dir, "warodai.txt");
        File.WriteAllText(txt, "*** license ***\n\nきもち【気持･気持ち】(кимоти)〔008-97-31〕\n1) чувство;\n気持ちがいい <i>приятно</i>;\n得/エ/る <a href=\"#1\">ср.</a>\n\nあいち【愛知】(Айти) [геогр.]〔008-48-24〕\nАйти\n", Encoding.Unicode);

        var info = DictionaryImport.Import(txt, dir, dir);
        using var pack = new DictionaryPack(info.Path);
        var byKanji = Assert.Single(pack.Find("気持ち"));
        Assert.Equal("きもち【気持・気持ち】", byKanji.Headword);
        Assert.Equal(0, byKanji.Rank);
        Assert.Equal(1, Assert.Single(pack.Find("きもち")).Rank);
        Assert.Contains("得(エ)る", DictMarkup.ToPlain(byKanji.Body));
        var place = Assert.Single(pack.Find("愛知"));
        Assert.Equal(SpanStyle.Label, DictMarkup.Parse(place.Body)[0].Spans[0].Style);
    }

    [Fact]
    public void StarDict_import_with_synonyms()
    {
        var dir = Temp();
        var body1 = "a sweet fruit\nred or green"u8.ToArray();
        var body2 = "<b>big</b> cat"u8.ToArray();
        using (var dict = new MemoryStream())
        using (var idx = new MemoryStream())
        {
            void Entry(string word, byte[] body)
            {
                idx.Write(Encoding.UTF8.GetBytes(word));
                idx.WriteByte(0);
                Span<byte> n = stackalloc byte[4];
                BinaryPrimitives.WriteUInt32BigEndian(n, (uint)dict.Length);
                idx.Write(n);
                BinaryPrimitives.WriteUInt32BigEndian(n, (uint)body.Length);
                idx.Write(n);
                dict.Write(body);
            }
            Entry("apple", body1);
            Entry("tiger", body2);
            File.WriteAllBytes(Path.Combine(dir, "t.idx"), idx.ToArray());
            using var gz = new GZipStream(File.Create(Path.Combine(dir, "t.dict.dz")), CompressionLevel.Fastest);
            gz.Write(dict.ToArray());
        }
        var syn = new List<byte>(Encoding.UTF8.GetBytes("apples")) { 0, 0, 0, 0, 0 };
        File.WriteAllBytes(Path.Combine(dir, "t.syn"), syn.ToArray());
        // First entry is plain text ("m"), the second HTML: typed fields, no sametypesequence.
        File.WriteAllText(Path.Combine(dir, "t.ifo"), "StarDict's dict ifo file\nversion=2.4.2\nwordcount=2\nbookname=Fruits\nsametypesequence=m\n");

        var info = DictionaryImport.Import(Path.Combine(dir, "t.ifo"), dir, dir);
        Assert.Equal("en", info.SourceLanguage);
        using var pack = new DictionaryPack(info.Path);
        var apple = Assert.Single(pack.Find("apples"));
        Assert.Equal("apple", apple.Headword);
        Assert.Equal(1, apple.Rank);
        Assert.Equal(["a sweet fruit", "red or green"], DictMarkup.Parse(apple.Body).Select(l => l.Text).ToList());
        Assert.Equal("<b>big</b> cat", DictMarkup.ToPlain(Assert.Single(pack.Find("tiger")).Body));
    }

    [Fact]
    public void Yomitan_rows_of_one_entry_are_merged()
    {
        var dir = Temp();
        var zipPath = Path.Combine(dir, "yomi.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            Write(zip, "index.json", """{"title":"Mini","revision":"1","format":3,"sourceLanguage":"ja","targetLanguage":"en"}""");
            Write(zip, "term_bank_1.json", """
                [["猫","ねこ","n","",0,["cat"],1,""],
                 ["猫","ねこ","n","",0,[{"type":"structured-content","content":[{"tag":"span","content":"feline"},{"tag":"div","data":{"content":"example-sentence"},"content":"猫が好き"}]}],1,""],
                 ["犬","いぬ","n","",0,["dog"],2,""]]
                """);
        }
        var info = DictionaryImport.Import(zipPath, dir, dir);
        Assert.Equal(2, info.Entries);
        using var pack = new DictionaryPack(info.Path);
        var cat = Assert.Single(pack.Find("ねこ"));
        Assert.Equal("猫", cat.Headword);
        var lines = DictMarkup.Parse(cat.Body);
        Assert.StartsWith("1. n cat", lines[0].Text);
        Assert.Contains(lines, l => l.Spans.Any(s => s.Style.HasFlag(SpanStyle.Example) && s.Text.Contains("猫が好き")));

        static void Write(ZipArchive zip, string name, string text)
        {
            using var w = new StreamWriter(zip.CreateEntry(name).Open());
            w.Write(text);
        }
    }

    [Theory]
    [InlineData("", "cdf26213a150dc3ecb610f18f6b38b46")]
    [InlineData("abc", "c14a12199c66e4ba84636b0f69144c77")]
    [InlineData("message digest", "9e327b3d6e523062afc1132d7df9d1b8")]
    [InlineData("12345678901234567890123456789012345678901234567890123456789012345678901234567890", "3f45ef194732c2dbb2c4a2c769795fa3")]
    public void Ripemd128_matches_reference_vectors(string input, string expected) =>
        Assert.Equal(expected, Convert.ToHexString(Ripemd128.Hash(Encoding.ASCII.GetBytes(input))).ToLowerInvariant());

    [Fact]
    public void Keys_fold_case_katakana_yo_and_stress()
    {
        Assert.Equal("こーひー", DictKeys.Normalize("コーヒー"));
        Assert.Equal("don't", DictKeys.Normalize("Don’t"));
        Assert.Equal("еж", DictKeys.Normalize("ё\u0301ж"));
        Assert.Equal("give up", DictKeys.Normalize("  give   up "));
    }

    [Fact]
    public void Translation_is_checked_against_dictionary_equivalents()
    {
        var wiki = new PackInfo("wiktionary-en", "W", "en", "ru", 1, 10, null, null, null, "");
        var bastard = new DictEntry(1, "bastard", null,
            "[p]сущ.[/p]\n1. [c](dated)[/c] A person born out of wedlock.\n\t[t]ублюдок, байстрюк, бастард[/t]\n2. A mongrel.", 0);
        IReadOnlyList<DictSection> en = [new DictSection(wiki, [bastard])];

        Assert.True(DictionaryCheck.Of("Ублюдки", en, "ru")!.Matches);            // another form
        var miss = DictionaryCheck.Of("скотина", en, "ru")!;
        Assert.False(miss.Matches);
        Assert.Equal(["ублюдок", "байстрюк", "бастард"], miss.Equivalents);
        Assert.Null(DictionaryCheck.Of("скотина", en, "en"));                   // no dictionary into English

        var bkrs = new PackInfo("bkrs", "B", "zh", "ru", 1, 10, null, null, null, "");
        var body = "\t[p]вульг.[/p]\n\t1) обалденный, впечатляющий; молодец\n\t\t[ex]老公太牛屄了 во муж даёт[/ex]\n\t2) [p]воскл.[/p] круто, заебись";
        IReadOnlyList<DictSection> zh = [new DictSection(bkrs, [new DictEntry(2, "牛屄", "niúbī", body, 0)])];
        Assert.True(DictionaryCheck.Of("охренеть, заебись", zh, "ru")!.Matches);
        Assert.Equal(["обалденный", "впечатляющий", "молодец"], DictionaryCheck.Of("охренеть", zh, "ru")!.Equivalents);

        var warodai = new PackInfo("warodai", "Wa", "ja", "ru", 1, 10, null, null, null, "");
        var mamoru = "1) защищать, охранять;\n身を守る защищаться;\n2) соблюдать; выполнить [i](закон)[/i];";
        IReadOnlyList<DictSection> ja = [new DictSection(warodai, [new DictEntry(3, "まもる", null, mamoru, 0)])];
        Assert.Equal(["защищать", "охранять", "соблюдать"], DictionaryCheck.Of("защитили", ja, "ru")!.Equivalents);
        Assert.False(DictionaryCheck.Of("Черт возьми, да!", ja, "ru")!.Matches);
    }

    [Fact]
    public void Reference_only_lines_show_their_target()
    {
        var dir = Temp();
        using (var w = new DictionaryPackWriter(Path.Combine(dir, "zh.gdict"), new PackMeta("zh", "ZH", "zh", "ru", 10)))
        {
            w.Add("傻逼", "shǎbī", "\t[p]вм.[/p] [ref]傻屄[/ref]", [new DictKey("傻逼")]);
            w.Add("傻屄", "shǎbī", "\t[p]груб.[/p] мудак; долбоёб", [new DictKey("傻屄")]);
            w.Add("卧槽", "wòcáo", "\t1) лежать в кормушке\n\t2) [p]вм.[/p] [ref]我肏[/ref]", [new DictKey("卧槽")]);
            w.Add("我肏", "wǒcào", "[p]груб.[/p] ебать! пиздец!", [new DictKey("我肏")]);
            w.Add("牛", "niú", "бык; см. также [ref]牛屄[/ref] в значении «круто»", [new DictKey("牛")]);
            w.Complete();
        }
        using var service = new DictionaryService(dir);
        service.Reload([], []);

        var shabi = service.Lookup("zh", ["傻逼"])[0].Entries[0];
        Assert.Equal("\t[p]вм.[/p] [ref]傻屄[/ref]\n\t\t\t[p]груб.[/p] мудак; долбоёб", shabi.Body);
        Assert.Contains("мудак", DictionaryService.Hint(service.Lookup("zh", ["傻逼"]), "ru"));

        var wocao = service.Lookup("zh", ["卧槽"])[0].Entries[0].Body;
        Assert.EndsWith("2) [p]вм.[/p] [ref]我肏[/ref]\n\t\t[p]груб.[/p] ебать! пиздец!", wocao);

        // A reference inside real text stays a link.
        Assert.Equal("бык; см. также [ref]牛屄[/ref] в значении «круто»", service.Lookup("zh", ["牛"])[0].Entries[0].Body);
    }

    [Fact]
    public void Service_searches_packs_in_order_and_resolves_links()
    {
        var dir = Temp();
        using (var w = new DictionaryPackWriter(Path.Combine(dir, "a.gdict"), new PackMeta("a", "A", "en", "ru", 20)))
        {
            w.Add("go", "/ɡəʊ/", "[t]идти[/t]", [new DictKey("go"), new DictKey("went", 2)]);
            w.Add("look after", null, "[t]заботиться[/t]", [new DictKey("look after"), new DictKey("looked after", 2)]);
            w.AddLink("goes", "go");
            w.Complete();
        }
        using (var w = new DictionaryPackWriter(Path.Combine(dir, "b.gdict"), new PackMeta("b", "B", "en", "en", 10)))
        {
            w.Add("go", null, "to move", [new DictKey("go")]);
            w.Complete();
        }

        using var service = new DictionaryService(dir);
        Assert.Empty(service.Reload([], []));
        Assert.Equal(["b", "a"], service.Installed.Select(p => p.Id).ToList()); // by priority

        var sections = service.Lookup("en", ["looked after the", "looked after", "looked"]);
        Assert.Equal("look after", Assert.Single(sections).Entries[0].Headword);
        Assert.Equal("go", service.Lookup("en", ["goes"])[^1].Entries[0].Headword);
        Assert.Equal("go", service.Lookup("en", ["went"])[0].Entries[0].Headword);
        Assert.True(service.HasKey("en", "Look After"));
        Assert.False(service.HasKey("ja", "go"));

        service.Reload(["a", "b"], ["b"]);
        var only = Assert.Single(service.Lookup("en", ["go"]));
        Assert.Equal("a", only.Pack.Id);
        Assert.Equal("go (/ɡəʊ/): идти", DictionaryService.Hint(service.Lookup("en", ["go"]), "ru"));
    }

    [Fact]
    public void Levels_from_lists()
    {
        var dir = Temp();
        File.WriteAllText(Path.Combine(dir, "jlpt_n5.csv"), "expression,reading,meaning,tags,guid\n会う,あう,\"to meet, to see\",JLPT,x\n");
        File.WriteAllText(Path.Combine(dir, "jlpt_n4.csv"), "expression,reading,meaning,tags,guid\n");
        File.WriteAllText(Path.Combine(dir, "jlpt_n3.csv"), "expression,reading,meaning,tags,guid\n");
        File.WriteAllText(Path.Combine(dir, "jlpt_n2.csv"), "expression,reading,meaning,tags,guid\n薄暗い,うすぐらい,dim,JLPT,x\n");
        File.WriteAllText(Path.Combine(dir, "jlpt_n1.csv"), "expression,reading,meaning,tags,guid\n会う,あう,to meet,JLPT,x\n");
        File.WriteAllText(Path.Combine(dir, LevelsBuilder.HskFile), """[{"s":"学习","l":["n1","o1"],"f":[{"t":"學習"}]},{"s":"阿姨","l":["t3","n4","o3"],"f":[]},{"s":"呵护","l":["n7"],"f":[]}]""");
        File.WriteAllText(Path.Combine(dir, LevelsBuilder.CefrFile), "headword,pos,CEFR\nabandon,verb,B1\na.m./A.M./am/AM,adverb,A1\n");
        File.WriteAllText(Path.Combine(dir, LevelsBuilder.CefrC1C2File), "headword,pos,CEFR,notes\ncloak,noun,C1,\n");

        var db = Path.Combine(dir, "levels.db");
        LevelsBuilder.Build(dir, db, CancellationToken.None);
        using var levels = new LevelService(db);
        Assert.Equal("JLPT N5", levels.LevelOf("ja", "会う"));
        Assert.Equal("JLPT N2", levels.LevelOf("ja", "薄暗い", "うすぐらい"));
        Assert.Equal("HSK 1", levels.LevelOf("zh", "學習"));
        Assert.Equal("HSK 4", levels.LevelOf("zh", "阿姨"));
        Assert.Equal("HSK 7-9", levels.LevelOf("zh", "呵护"));
        Assert.Equal("B1", levels.LevelOf("en", "Abandon"));
        Assert.Equal("A1", levels.LevelOf("en", "am"));
        Assert.Equal("C1", levels.LevelOf("en", "cloak"));
        Assert.Null(levels.LevelOf("en", "zebra"));
    }

    [Fact]
    public void Levels_built_with_an_en_dash_get_a_hyphen()
    {
        var dir = Temp();
        var db = Path.Combine(dir, "levels.db");
        using (var old = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={db};Pooling=False"))
        {
            old.Open();
            using var cmd = old.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE levels(lang TEXT NOT NULL, term TEXT NOT NULL, reading TEXT, level TEXT NOT NULL, rank INTEGER NOT NULL);
                INSERT INTO levels VALUES ('zh', '呵护', NULL, 'HSK 7' || char(8211) || '9', 7), ('zh', '阿姨', NULL, 'HSK 4', 4);
                """;
            cmd.ExecuteNonQuery();
        }

        using var levels = new LevelService(db);
        Assert.Equal("HSK 7-9", levels.LevelOf("zh", "呵护"));
        Assert.Equal("HSK 4", levels.LevelOf("zh", "阿姨"));
    }

    [Fact]
    public void English_phrase_candidates_surround_the_word()
    {
        var context = "She looked after the cat, then left.";
        var hit = new WordHit("looked", default, context, context, 4, Script.Latin);
        Assert.Equal(["looked after the cat", "She looked after the", "looked after the", "She looked after", "looked after", "She looked", "looked"],
            EnglishPhrases.Candidates(hit));
        var last = new WordHit("left", default, context, context, context.IndexOf("left", StringComparison.Ordinal), Script.Latin);
        Assert.Equal(["then left", "left"], EnglishPhrases.Candidates(last));
    }

    [Fact]
    public void English_idiom_is_found_from_any_word_with_a_placeholder_pronoun()
    {
        var context = "Go fuck yourself, you piece of shit.";
        var hit = new WordHit("fuck", default, context, context, 3, Script.Latin);
        Assert.Equal(["Go fuck yourself", "Go fuck oneself", "fuck yourself", "fuck oneself", "Go fuck", "fuck"],
            EnglishPhrases.Candidates(hit));

        static IReadOnlyList<string> At(string line, string word) =>
            EnglishPhrases.Candidates(new WordHit(word, default, line, line, line.IndexOf(word, StringComparison.Ordinal), Script.Latin));
        Assert.Contains("blew someone's mind", At("That movie blew my mind.", "mind"));
        Assert.Contains("Mind one's own business", At("Mind your own business, kid.", "business"));
        Assert.Contains("piss off", At("Don't piss him off again.", "off"));
    }

    [Fact]
    public void Chinese_phrase_extends_forward_by_segments()
    {
        if (!File.Exists(DataPaths.Cedict)) return;
        var zh = new ChineseDictionary(DataPaths.Cedict);
        const string text = "别担心，这件事包在我身上。";
        var phrases = new HashSet<string> { "别担心", "包在我身上" };

        Assert.Equal((0, 3), zh.MatchPhrase(text, 0, phrases.Contains));
        Assert.Equal((1, 2), zh.MatchPhrase(text, 1, phrases.Contains)); // 担心: punctuation ends the run
        Assert.Equal((7, 5), zh.MatchPhrase(text, 7, phrases.Contains));
        Assert.Equal("bié dānxīn", zh.PhrasePinyinOf("别担心"));
    }

    [Fact]
    public void Character_breakdown_keeps_only_characters_of_multi_character_words()
    {
        var kana = new[] { new Glossa.Core.Lookup.CardComponent("な", null, "x"), new Glossa.Core.Lookup.CardComponent("何", null, "что") };
        Assert.Empty(Glossa.Core.Lookup.CardService.UsefulComponents(kana, "何でも"));
        var dim = new[]
        {
            new Glossa.Core.Lookup.CardComponent("薄", null, "тонкий"), new Glossa.Core.Lookup.CardComponent("暗い", null, "тёмный"),
            new Glossa.Core.Lookup.CardComponent("い", null, "?"), new Glossa.Core.Lookup.CardComponent("薄", null, "тонкий"),
        };
        Assert.Equal(["薄", "暗い"], Glossa.Core.Lookup.CardService.UsefulComponents(dim, "薄暗い").Select(c => c.Part).ToList());
    }

    [Fact]
    public void Japanese_set_phrase_is_found_by_dictionary_form()
    {
        const string dicDir = @"D:\GlossaData\dict\unidic-lite";
        if (!Directory.Exists(dicDir)) return;
        using var ja = new JapaneseAnalyzer(dicDir);
        var known = new HashSet<string> { "気をつける", "食べる" };

        var phrase = ja.WordAt("よく気をつけたね", 2, known.Contains);
        Assert.NotNull(phrase);
        Assert.Equal("気をつける", phrase.DictionaryForm);
        Assert.Equal("きをつける", phrase.Reading);
        Assert.Equal(2, phrase.Start);
        Assert.Equal("気をつけ".Length, phrase.Length);

        var verb = ja.WordAt("食べられなかった", 0, known.Contains);
        Assert.Equal("食べる", verb!.DictionaryForm);
        Assert.Equal(2, verb.Length);
    }
}
