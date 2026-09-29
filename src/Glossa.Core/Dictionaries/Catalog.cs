using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;

namespace Glossa.Core.Dictionaries;

/// <summary>A dictionary Glossa knows how to download and build.</summary>
public sealed record CatalogItem(
    string Id,
    string Title,
    string Description,
    string Language,
    string License,
    int DownloadMb,
    Func<CatalogContext, Task> InstallAsync)
{
    /// <summary>The level lists are not a pack: they go to levels.db.</summary>
    public bool IsLevels => Id == DictionaryCatalog.LevelsId;
}

/// <summary>Paths, HTTP and progress for one installation.</summary>
public sealed class CatalogContext(
    HttpClient http,
    HttpClient direct,
    string sourcesDir,
    string packsDir,
    string workDir,
    string levelsPath,
    string cedictPath,
    IProgress<ImportProgress>? progress,
    CancellationToken ct)
{
    public string SourcesDir { get; } = sourcesDir;
    public string PacksDir { get; } = packsDir;
    public string WorkDir { get; } = workDir;
    public string LevelsPath { get; } = levelsPath;
    public string CedictPath { get; } = cedictPath;
    public CancellationToken Ct { get; } = ct;

    /// <summary>Files downloaded by this installation, removed after a successful build.</summary>
    public List<string> Downloaded { get; } = [];

    public string PackPath(string id) => Path.Combine(PacksDir, id + DictionaryPack.Extension);

    public void Report(string stage, double fraction) => progress?.Report(new ImportProgress(stage, fraction));

    public IProgress<double> Stage(string stage) => new Reporter(f => Report(stage, f));

    /// <summary>
    /// Downloads to the sources folder unless the file is already there. Tries the system proxy first and then
    /// a direct connection: a proxy that cannot reach a site should not block the download.
    /// </summary>
    /// <param name="gzip">Ask for gzip transfer and keep the compressed bytes (kaikki.org: 3.3 GB → 0.5 GB).</param>
    public async Task<string> DownloadAsync(string url, string fileName, bool gzip = false)
    {
        Directory.CreateDirectory(SourcesDir);
        var path = Path.Combine(SourcesDir, fileName);
        if (File.Exists(path) && new FileInfo(path).Length > 0) return path;
        try
        {
            await DownloadWith(http, url, path, gzip, fileName).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            await DownloadWith(direct, url, path, gzip, fileName).ConfigureAwait(false);
        }
        Downloaded.Add(path);
        return path;
    }

    public async Task<string> GetStringAsync(string url)
    {
        try
        {
            return await http.GetStringAsync(url, Ct).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            return await direct.GetStringAsync(url, Ct).ConfigureAwait(false);
        }
    }

    private async Task DownloadWith(HttpClient client, string url, string path, bool gzip, string name)
    {
        var part = path + ".part";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Glossa", "0.3"));
        if (gzip) request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, Ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? -1;
        await using (var src = await response.Content.ReadAsStreamAsync(Ct).ConfigureAwait(false))
        await using (var dst = File.Create(part))
        {
            var buffer = new byte[1 << 20];
            long done = 0;
            int read;
            while ((read = await src.ReadAsync(buffer, Ct).ConfigureAwait(false)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read), Ct).ConfigureAwait(false);
                done += read;
                Report($"Загрузка {name}: {done >> 20} МБ" + (total > 0 ? $" из {total >> 20}" : ""), total > 0 ? (double)done / total : 0);
            }
        }
        File.Move(part, path, overwrite: true);
    }

    private sealed class Reporter(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}

public static class DictionaryCatalog
{
    public const string LevelsId = "levels";

    public static IReadOnlyList<CatalogItem> Items { get; } =
    [
        new("warodai", "Warodai - большой японско-русский словарь",
            "Около 100 тысяч статей с примерами, основной словарь японский -> русский.",
            "ja", "CC BY-NC-ND 3.0, warodai.ru", 6, async ctx =>
            {
                var zip = await ctx.DownloadAsync("https://warodai.ru/download/warodai_txt.zip", "warodai_txt.zip");
                var dir = Path.Combine(ctx.WorkDir, "warodai");
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
                ZipFile.ExtractToDirectory(zip, dir);
                var txt = Directory.EnumerateFiles(dir, "*.txt", SearchOption.AllDirectories).First();
                await Task.Run(() => DictionaryImport.Import(txt, ctx.PacksDir, ctx.WorkDir,
                    new PackMeta("warodai", "Warodai", "ja", "ru", 10, "Большой японско-русский словарь", "CC BY-NC-ND 3.0, warodai.ru", "https://warodai.ru"),
                    new Progress(ctx, "Сборка Warodai"), ctx.Ct), ctx.Ct);
                Directory.Delete(dir, true);
            }),

        new("jmdict", "JMdict - японско-английский (+ русский)",
            "Около 220 тысяч слов; у трети есть русские значения. Пометы частей речи.",
            "ja", "CC BY-SA 4.0, EDRDG", 22, async ctx =>
            {
                var gz = await ctx.DownloadAsync("http://ftp.edrdg.org/pub/Nihongo/JMdict.gz", "JMdict.gz");
                await Task.Run(() => JmdictBuilder.Build(gz, ctx.PackPath("jmdict"), ctx.Stage("Сборка JMdict"), ctx.Ct), ctx.Ct);
            }),

        new("bkrs", "БКРС - большой китайско-русский словарь",
            "Более 2 миллионов статей, обновляется ежедневно. Пиньинь, примеры.",
            "zh", "Свободное использование, bkrs.info", 85, async ctx =>
            {
                var page = await ctx.GetStringAsync("https://bkrs.info/p47");
                var m = Regex.Match(page, @"downloads/daily/(dabkrs_\d+\.gz)");
                if (!m.Success) throw new InvalidDataException("На bkrs.info не найдена ссылка на свежую базу.");
                var gz = await ctx.DownloadAsync("https://bkrs.info/" + m.Value, m.Groups[1].Value);
                await Task.Run(() => DictionaryImport.Import(gz, ctx.PacksDir, ctx.WorkDir,
                    new PackMeta("bkrs", "БКРС", "zh", "ru", 10, "Большой китайско-русский словарь", "bkrs.info", "https://bkrs.info"),
                    new Progress(ctx, "Сборка БКРС"), ctx.Ct), ctx.Ct);
            }),

        new("cedict", "CC-CEDICT - китайско-английский",
            "Около 120 тысяч слов, упрощённые и традиционные иероглифы.",
            "zh", "CC BY-SA 4.0, MDBG", 4, async ctx =>
            {
                if (!File.Exists(ctx.CedictPath))
                {
                    var gz = await ctx.DownloadAsync("https://www.mdbg.net/chinese/export/cedict/cedict_1_0_ts_utf-8_mdbg.txt.gz", "cedict.txt.gz");
                    Directory.CreateDirectory(Path.GetDirectoryName(ctx.CedictPath)!);
                    await using var src = new GZipStream(File.OpenRead(gz), CompressionMode.Decompress);
                    await using var dst = File.Create(ctx.CedictPath);
                    await src.CopyToAsync(dst, ctx.Ct);
                }
                await Task.Run(() => CedictBuilder.Build(ctx.CedictPath, ctx.PackPath("cedict"), ctx.Stage("Сборка CC-CEDICT"), ctx.Ct), ctx.Ct);
            }),

        new("wiktionary-en", "Wiktionary - английский",
            "Толкования на английском, транскрипция, русские переводы по значениям; все формы слов и фразовые глаголы.",
            "en", "CC BY-SA 4.0, Wiktionary / kaikki.org", 500, async ctx =>
            {
                var gz = await ctx.DownloadAsync("https://kaikki.org/dictionary/English/kaikki.org-dictionary-English.jsonl",
                    "kaikki-English.jsonl.gz", gzip: true);
                await Task.Run(() => WiktionaryBuilder.Build(gz, ctx.PackPath("wiktionary-en"), ctx.Stage("Сборка Wiktionary"), ctx.Ct), ctx.Ct);
            }),

        new("wiktionary-ja", "Wiktionary - японский (сленг, 18+)",
            "Значения на английском: разговорное, интернет-сленг, мат и 18+, включая цензурные написания (ま○こ).",
            "ja", "CC BY-SA 4.0, Wiktionary / kaikki.org", 48, async ctx =>
            {
                var gz = await ctx.DownloadAsync("https://kaikki.org/dictionary/Japanese/kaikki.org-dictionary-Japanese.jsonl",
                    "kaikki-Japanese.jsonl.gz", gzip: true);
                await Task.Run(() => WiktionaryBuilder.Build(gz, ctx.PackPath("wiktionary-ja"), ctx.Stage("Сборка Wiktionary (японский)"), ctx.Ct, "ja"), ctx.Ct);
            }),

        new("wiktionary-zh", "Wiktionary - китайский (сленг, 18+)",
            "Значения на английском: сленг, интернет-сленг и мат (傻屄, 舔狗, 屌丝), пиньинь.",
            "zh", "CC BY-SA 4.0, Wiktionary / kaikki.org", 155, async ctx =>
            {
                var gz = await ctx.DownloadAsync("https://kaikki.org/dictionary/Chinese/kaikki.org-dictionary-Chinese.jsonl",
                    "kaikki-Chinese.jsonl.gz", gzip: true);
                await Task.Run(() => WiktionaryBuilder.Build(gz, ctx.PackPath("wiktionary-zh"), ctx.Stage("Сборка Wiktionary (китайский)"), ctx.Ct, "zh"), ctx.Ct);
            }),

        new(LevelsId, "Уровни слов: CEFR, JLPT, HSK",
            "Уровень слова из официальных и открытых списков вместо догадки ИИ.",
            "", "CEFR-J / Octanove, J. Waller (CC BY), complete-hsk-vocabulary (MIT)", 4, async ctx =>
            {
                const string raw = "https://raw.githubusercontent.com/";
                for (var n = 1; n <= 5; n++)
                    await ctx.DownloadAsync($"{raw}jamsinclair/open-anki-jlpt-decks/main/src/n{n}.csv", $"jlpt_n{n}.csv");
                await ctx.DownloadAsync($"{raw}drkameleon/complete-hsk-vocabulary/main/complete.min.json", LevelsBuilder.HskFile);
                await ctx.DownloadAsync($"{raw}openlanguageprofiles/olp-en-cefrj/master/cefrj-vocabulary-profile-1.5.csv", LevelsBuilder.CefrFile);
                await ctx.DownloadAsync($"{raw}openlanguageprofiles/olp-en-cefrj/master/octanove-vocabulary-profile-c1c2-1.0.csv", LevelsBuilder.CefrC1C2File);
                ctx.Report("Сборка списков", 0.5);
                await Task.Run(() => LevelsBuilder.Build(ctx.SourcesDir, ctx.LevelsPath, ctx.Ct), ctx.Ct);
            }),
    ];

    public static bool IsInstalled(CatalogItem item, string packsDir, string levelsPath) =>
        item.IsLevels ? File.Exists(levelsPath) : File.Exists(Path.Combine(packsDir, item.Id + DictionaryPack.Extension));

    /// <summary>Runs one item; downloaded sources are deleted afterwards unless <paramref name="keepSources"/>.</summary>
    public static async Task InstallAsync(CatalogItem item, CatalogContext ctx, bool keepSources)
    {
        Directory.CreateDirectory(ctx.PacksDir);
        Directory.CreateDirectory(ctx.WorkDir);
        await item.InstallAsync(ctx).ConfigureAwait(false);
        ctx.Report("Готово", 1);
        if (keepSources) return;
        foreach (var f in ctx.Downloaded)
        {
            try { File.Delete(f); } catch (IOException) { }
        }
    }

    private sealed class Progress(CatalogContext ctx, string stage) : IProgress<ImportProgress>
    {
        public void Report(ImportProgress value) => ctx.Report(stage, value.Fraction);
    }
}
