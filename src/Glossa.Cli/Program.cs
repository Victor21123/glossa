using System.Diagnostics;
using System.Text;
using Glossa.Core.Config;
using Glossa.Core.Dictionaries;

// Dictionary maintenance and checks without the UI:
//   glossa-cli list
//   glossa-cli install <catalog id> [--keep]
//   glossa-cli import <file>
//   glossa-cli lookup <lang> <term> [more candidates…]
//   glossa-cli level <lang> <term> [reading]
//   glossa-cli runtime [cuda|vulkan]
Console.OutputEncoding = Encoding.UTF8;
if (args.Length == 0)
{
    Console.WriteLine("usage: list | install <id> [--keep] | import <file> | lookup <lang> <term…> | level <lang> <term> [reading]");
    return 1;
}

var sw = Stopwatch.StartNew();
var progress = new ConsoleProgress();
switch (args[0])
{
    case "list":
    {
        using var dicts = Open();
        foreach (var p in dicts.Installed)
            Console.WriteLine($"{p.Id,-16} {p.SourceLanguage}→{p.TargetLanguage}  {p.Entries,9:N0} entries  {p.SizeBytes >> 20,5} MB  {p.Title}");
        foreach (var item in DictionaryCatalog.Items)
            Console.WriteLine($"catalog {item.Id,-16} {(DictionaryCatalog.IsInstalled(item, DataPaths.Packs, DataPaths.Levels) ? "installed" : "-")}  {item.Title}");
        break;
    }
    case "install":
    {
        var item = DictionaryCatalog.Items.FirstOrDefault(i => i.Id == args[1]) ?? throw new ArgumentException("unknown id " + args[1]);
        using var http = new HttpClient { Timeout = TimeSpan.FromHours(2) };
        using var direct = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromHours(2) };
        var ctx = new CatalogContext(http, direct, DataPaths.Sources, DataPaths.Packs, DataPaths.Work, DataPaths.Levels,
            DataPaths.Cedict, progress, CancellationToken.None);
        await DictionaryCatalog.InstallAsync(item, ctx, keepSources: args.Contains("--keep"));
        Console.WriteLine();
        Console.WriteLine($"installed {item.Id} in {sw.Elapsed.TotalSeconds:F0} s");
        break;
    }
    case "runtime":
    {
        // The official llama.cpp build, downloaded and unpacked exactly as «Движок» → «Скачать» does.
        var entry = Glossa.Core.Llm.RuntimeCatalog.For(args.Length > 1 ? args[1] : "cuda") ?? throw new ArgumentException("cuda or vulkan");
        using var http = new HttpClient { Timeout = TimeSpan.FromHours(2) };
        using var direct = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromHours(2) };
        var shown = new Progress<Glossa.Core.Llm.ModelProgress>(p =>
            Console.Write($"\r{(p.Verifying ? "checking/unpacking" : "downloading")} {p.Done >> 20} / {p.Total >> 20} MB   "));
        var server = await new Glossa.Core.Llm.RuntimeInstaller(new Glossa.Core.Llm.ModelDownloader(http, direct))
            .InstallAsync(entry, DataPaths.Runtime, shown, CancellationToken.None);
        Console.WriteLine();
        Console.WriteLine($"{entry.Release} {entry.Id}: {server} in {sw.Elapsed.TotalSeconds:F0} s");
        break;
    }
    case "import":
    {
        var info = DictionaryImport.Import(args[1], DataPaths.Packs, DataPaths.Work, null, progress, CancellationToken.None);
        Console.WriteLine();
        Console.WriteLine($"imported {info.Id}: {info.Title} {info.SourceLanguage}→{info.TargetLanguage}, {info.Entries:N0} entries, {info.SizeBytes >> 20} MB in {sw.Elapsed.TotalSeconds:F0} s");
        break;
    }
    case "lookup":
    {
        using var dicts = Open();
        var t0 = sw.Elapsed;
        var sections = dicts.Lookup(args[1], args.Skip(2).ToList(), perPack: 3);
        var ms = (sw.Elapsed - t0).TotalMilliseconds;
        foreach (var s in sections)
        {
            Console.WriteLine($"== {s.Pack.Title}");
            foreach (var e in s.Entries)
            {
                Console.WriteLine($"  {e.Headword}  {e.Reading}  (rank {e.Rank})");
                foreach (var line in DictMarkup.Parse(e.Body).Take(14))
                    Console.WriteLine("    " + new string(' ', line.Indent * 2) + string.Concat(line.Spans.Select(Render)));
            }
        }
        Console.WriteLine($"-- {sections.Count} sections in {ms:F1} ms; hint: {DictionaryService.Hint(sections, "ru", 200)}");
        break;
    }
    case "level":
    {
        using var levels = new LevelService(DataPaths.Levels);
        Console.WriteLine(levels.LevelOf(args[1], args[2], args.Length > 3 ? args[3] : null) ?? "(none)");
        break;
    }
    case "ocr":
    {
        using var bench = new Bench();
        var page = await bench.OcrAsync(args[1]);
        foreach (var line in page.Lines)
            Console.WriteLine($"[{line.Box.Left:F0},{line.Box.Top:F0} {line.Box.Width:F0}x{line.Box.Height:F0}] {line.Text}");
        Console.WriteLine($"-- {page.Lines.Count} lines in {page.Elapsed.TotalMilliseconds:F0} ms");
        break;
    }
    case "ocr-at":
    {
        // ocr-at <image> <x> <y> [ja|zh|en]: one lookup at a point of a saved frame, lines and context as the app sees them
        using var bench = new Bench();
        await OcrEval.AtAsync(args[1], double.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture),
            double.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture), args.Length > 4 ? args[4] : null, bench.Words);
        break;
    }
    case "ocr-eval":
    {
        // ocr-eval <cases.json> [base|v5|v6|v6m] [--vision <api root>]: the word under the point on hard frames
        using var bench = new Bench();
        await OcrEval.RunAsync(args[1], OcrEval.Options.Parse(args, 2), bench.Words);
        break;
    }
    case "ocr-lines":
    {
        // ocr-lines <cases.json> [base|v5|v6|v6m] [--vision <api root>]: pieces of game screens, each true line against what was read
        using var bench = new Bench();
        await OcrEval.LinesAsync(args[1], OcrEval.Options.Parse(args, 2), bench.Words);
        break;
    }
    case "ocr-found":
    {
        // ocr-found <eval_cases.json> [base|v5|v6]: whether each word is read anywhere on its frame, and how many would be read again
        using var bench = new Bench();
        await OcrEval.FoundAsync(args[1], OcrEval.Options.Parse(args, 2), bench.Words);
        break;
    }
    case "dictcheck":
    {
        // dictcheck <cases.json> <results.json>: the "как в словаре" mark for translations of a finished eval run
        using var bench = new Bench();
        await bench.DictionaryCheckAsync(args[1], args[2]);
        break;
    }
    case "shot":
    case "plan":
    {
        // shot <image> <word> | plan <lang> "<line of text>" <word>
        using var bench = new Bench();
        var plan = await bench.PlanAsync(args[0] == "shot"
            ? new EvalCase { Image = args[1], Word = args[2] }
            : new EvalCase { Lang = args[1], Context = args[2], Word = args[3] });
        if (plan is null) { Console.WriteLine("word not found"); return 1; }
        Console.WriteLine($"hit '{plan.Hit.Word}' [{plan.Language}] form={plan.Seed.DictionaryForm} reading={plan.Seed.Reading} level={plan.Seed.Level}");
        Console.WriteLine($"context: {plan.Hit.Context}");
        Console.WriteLine($"candidates: {string.Join(" | ", plan.Candidates)}");
        foreach (var s in plan.Sections)
            Console.WriteLine($"{s.Pack.Title}: {string.Join(" | ", s.Entries.Select(e => e.Headword))}");
        Console.WriteLine($"hint: {plan.Hint}");
        break;
    }
    case "eval":
    {
        // eval <cases.json> <out.json> --card <url>[|model] [--tr <url>[|model]] [--dict-hint off]
        using var bench = new Bench { HintDictionary = Opt(args, "--dict-hint") != "off" };
        await bench.EvalAsync(args[1], args[2], Opt(args, "--card"), Opt(args, "--tr"));
        break;
    }
    default:
        Console.WriteLine("unknown command");
        return 1;
}
return 0;

static string? Opt(string[] a, string name) => Array.IndexOf(a, name) is var i and >= 0 && i + 1 < a.Length ? a[i + 1] : null;

static DictionaryService Open()
{
    var d = new DictionaryService(DataPaths.Packs);
    foreach (var e in d.Reload([], [])) Console.WriteLine("! " + e);
    return d;
}

// Styles as plain-text hints: *bold*, _italic_, <label>, «accent», {example}, ~muted~, →ref.
static string Render(MarkupSpan s)
{
    var t = s.Text;
    if (s.Style.HasFlag(SpanStyle.Label)) t = $"<{t}>";
    if (s.Style.HasFlag(SpanStyle.Accent)) t = $"«{t}»";
    if (s.Style.HasFlag(SpanStyle.Example)) t = $"{{{t}}}";
    if (s.Style.HasFlag(SpanStyle.Ref)) t = "→" + t;
    if (s.Style.HasFlag(SpanStyle.Bold)) t = $"*{t}*";
    return t;
}

sealed class ConsoleProgress : IProgress<ImportProgress>
{
    private string _last = "";
    private DateTime _at;

    public void Report(ImportProgress value)
    {
        var line = $"{value.Stage} {value.Fraction:P0}";
        if (line == _last || (DateTime.UtcNow - _at).TotalMilliseconds < 500 && value.Fraction < 1) return;
        _last = line;
        _at = DateTime.UtcNow;
        Console.Write("\r" + line.PadRight(70));
    }
}
