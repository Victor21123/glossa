using System.Text.Json;
using Glossa.Core.Logging;

namespace Glossa.Core.Companions;

/// <summary>
/// The companions there are: in the pack, folders <c>&lt;rarity&gt;/&lt;id&gt;</c> with <c>&lt;id&gt;_base.png</c>, the other
/// frames and <c>&lt;id&gt;.json</c> (name, game, gender, phrases). A new character is a new folder, no code change. The
/// program reads only the encrypted pack (<see cref="LoadPack"/>): no folder beside the exe or in the data folder is
/// looked at, so art cannot be swapped by copying files. A broken character is left out with a line in the log, never
/// an exception. Sizes come from the PNG headers; nothing is decoded here.
/// </summary>
public sealed class CompanionCatalog
{
    private readonly Dictionary<string, Companion> _byId;

    private CompanionCatalog(IReadOnlyList<Companion> all)
    {
        All = all;
        _byId = all.ToDictionary(c => c.Id, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Every companion, the rarest first, then by id.</summary>
    public IReadOnlyList<Companion> All { get; }

    public Companion? Find(string id) => _byId.GetValueOrDefault(id);

    public IReadOnlyList<Companion> Of(Rarity rarity) => All.Where(c => c.Rarity == rarity).ToList();

    /// <summary>The tallest sprite of all: one scale for every companion keeps a child shorter than an adult.</summary>
    public int TallestHeight => All.Count == 0 ? 0 : All.Max(c => c.Height);

    /// <summary>No companions: a build without the secrets, or no pack beside the exe.</summary>
    public static CompanionCatalog Empty { get; } = new([]);

    /// <summary>
    /// The program's catalog: <c>companions.pack</c> opened with the art key. Empty (and a line in the log) when the
    /// key is missing, the pack is missing, or it was changed - a changed byte fails the AES-GCM tag.
    /// </summary>
    public static CompanionCatalog LoadPack(string packPath, byte[]? artKey, ILog? log = null)
    {
        if (artKey is null)
        {
            log?.Info("Companions: this build has no secrets (built from the public sources) - no companions");
            return Empty;
        }
        byte[] pack;
        try
        {
            pack = File.ReadAllBytes(packPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log?.Warn($"Companions: {packPath} unreadable ({ex.Message})");
            return Empty;
        }
        if (CompanionPack.Open(pack, artKey) is not { } files)
        {
            log?.Warn($"Companions: {packPath} does not open with this build's key (changed or from another build)");
            return Empty;
        }
        return Load(files, log);
    }

    /// <summary>Catalog folders as they are on disk (the art tools, tests); the program reads only the pack.</summary>
    public static CompanionCatalog Load(IEnumerable<string> roots, ILog? log = null)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        // A later root replaces a character of the same id as a whole.
        foreach (var root in roots)
        {
            var more = CompanionPack.ReadFolder(root);
            var ids = more.Keys.Select(IdOf).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var stale in files.Keys.Where(k => IdOf(k) is { } id && ids.Contains(id)).ToList())
                files.Remove(stale);
            foreach (var (path, bytes) in more)
                files[path] = bytes;
        }
        return Load(files, log);
    }

    private static string? IdOf(string path) => path.Split('/') is [_, var id, _] ? id : null;

    /// <summary>The catalog of a pack's files (path in the catalog -> bytes).</summary>
    public static CompanionCatalog Load(IReadOnlyDictionary<string, byte[]> files, ILog? log = null)
    {
        var found = new List<Companion>();
        var folders = files.Keys.Select(k => k.Split('/')).Where(p => p.Length == 3)
            .Select(p => (Rarity: p[0], Id: p[1])).Distinct().OrderBy(f => f.Rarity, StringComparer.Ordinal)
            .ThenBy(f => f.Id, StringComparer.Ordinal);
        foreach (var (rarityFolder, id) in folders)
        {
            if (Rarities.FromFolder(rarityFolder) is not { } rarity)
                continue;
            if (Read(files, $"{rarityFolder}/{id}", id, rarity, log) is { } companion
                && found.TrueForAll(c => !c.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
                found.Add(companion);
        }
        return new CompanionCatalog(found.OrderBy(c => c.Rarity).ThenBy(c => c.Id, StringComparer.Ordinal).ToList());
    }

    private static Companion? Read(IReadOnlyDictionary<string, byte[]> files, string dir, string id, Rarity rarity, ILog? log)
    {
        if (!files.TryGetValue($"{dir}/{id}_{CompanionFrames.Base}.png", out var basePng)
            || !TryReadPngSize(basePng, out var width, out var height))
        {
            log?.Warn($"Companion {id} left out: no readable {id}_{CompanionFrames.Base}.png in {dir}");
            return null;
        }
        string name, game, gender;
        var phrases = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var zones = new List<CompanionZone>();
        try
        {
            if (!files.TryGetValue($"{dir}/{id}.json", out var json))
                throw new InvalidOperationException($"no {id}.json");
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            name = Text(root, "name") ?? id;
            game = Text(root, "game") ?? "";
            gender = Text(root, "gender") ?? "";
            // The speech bubble's lines: moment -> lines; anything else in there is skipped.
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("phrases", out var said)
                && said.ValueKind == JsonValueKind.Object)
            {
                foreach (var moment in said.EnumerateObject().Where(m => m.Value.ValueKind == JsonValueKind.Array))
                    phrases[moment.Name] = moment.Value.EnumerateArray()
                        .Where(l => l.ValueKind == JsonValueKind.String).Select(l => l.GetString()!.Trim())
                        .Where(l => l.Length > 0).ToList();
            }
            // The click zones: name -> [[x, y, w, h], ...] in the base frame's pixels, in the manifest's order.
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("zones", out var places)
                && places.ValueKind == JsonValueKind.Object)
            {
                foreach (var place in places.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.Array))
                {
                    var boxes = place.Value.EnumerateArray()
                        .Where(b => b.ValueKind == JsonValueKind.Array && b.GetArrayLength() == 4
                                    && b.EnumerateArray().All(v => v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out _)))
                        .Select(b => b.EnumerateArray().Select(v => v.GetInt32()).ToArray())
                        .Where(v => v[2] > 0 && v[3] > 0)
                        .Select(v => (v[0], v[1], v[2], v[3])).ToList();
                    if (boxes.Count > 0)
                        zones.Add(new CompanionZone(place.Name, boxes));
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
        {
            log?.Warn($"Companion {id} left out: {id}.json unreadable ({ex.Message})");
            return null;
        }
        var frames = new Dictionary<string, byte[]>(StringComparer.Ordinal) { [CompanionFrames.Base] = basePng };
        foreach (var frame in CompanionFrames.All.Where(f => f != CompanionFrames.Base))
        {
            if (!files.TryGetValue($"{dir}/{id}_{frame}.png", out var png))
                continue;
            // Frames that take turns in one place (base, blink, the breaths, the moods) must be the base's size. The
            // sleep is a pose of its own, on its own pixel grid (252 against 255 px for seven of the first eleven),
            // shown alone and set on the floor.
            if (TryReadPngSize(png, out var w, out var h) && (frame == CompanionFrames.Sleep || (w, h) == (width, height)))
                frames[frame] = png;
            else
                log?.Warn($"Companion {id}: {id}_{frame}.png is not {width}x{height}, the base frame shows instead");
        }
        return new Companion(id, rarity, name, game, gender, width, height, frames, phrases, zones);
    }

    private static string? Text(JsonElement root, string key) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    /// <summary>A PNG's width and height from its IHDR chunk (bytes 16-23, big-endian).</summary>
    public static bool TryReadPngSize(ReadOnlySpan<byte> png, out int width, out int height)
    {
        width = height = 0;
        if (png.Length < 24)
            return false;
        ReadOnlySpan<byte> signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        if (!png[..8].SequenceEqual(signature) || png[12] != 'I' || png[13] != 'H' || png[14] != 'D' || png[15] != 'R')
            return false;
        width = (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19];
        height = (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23];
        return width > 0 && height > 0;
    }
}
