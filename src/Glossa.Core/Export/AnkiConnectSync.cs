using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Glossa.Core.Library;

namespace Glossa.Core.Export;

public sealed class AnkiConnectException(string message) : Exception(message);

public sealed record AnkiSyncResult(int Added, int Updated, int Tagged);

/// <summary>
/// Pushes the library into a running Anki through the AnkiConnect add-on (API version 6). Notes are matched by
/// the permanent GlossaId field. Nothing is ever deleted in Anki: removed words get the glossa::removed tag.
/// </summary>
public sealed class AnkiConnectSync(HttpClient http, string url = "http://127.0.0.1:8765")
{
    public async Task<bool> IsAvailableAsync(CancellationToken ct)
    {
        try
        {
            return await InvokeAsync("version", null, ct).ConfigureAwait(false) is JsonValue v && v.GetValue<int>() >= 6;
        }
        catch (Exception e) when (e is HttpRequestException or AnkiConnectException or TaskCanceledException)
        {
            return false;
        }
    }

    public async Task<AnkiSyncResult> SyncAsync(string dataRoot, IReadOnlyList<SavedWord> words, IReadOnlyList<SavedWord> removed,
        AnkiExportOptions options, IProgress<string>? progress, CancellationToken ct)
    {
        await EnsureModelAsync(ct).ConfigureAwait(false);
        foreach (var deck in words.Select(w => AnkiNoteType.DeckFor(w.Language)).Distinct())
            await InvokeAsync("createDeck", new JsonObject { ["deck"] = deck }, ct).ConfigureAwait(false);

        int added = 0, updated = 0, tagged = 0, i = 0;
        foreach (var w in words)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"Anki: {++i} / {words.Count}");

            var image = options.IncludeImages ? AnkiNoteType.CardImage(dataRoot, w) : null;
            var audio = options.Audio?.Invoke(w);
            if (image is not null) await StoreMediaAsync(AnkiNoteType.ImageFileName(w), image, ct).ConfigureAwait(false);
            if (audio is not null) await StoreMediaAsync(AnkiNoteType.AudioFileName(w), audio, ct).ConfigureAwait(false);

            var values = AnkiNoteType.FieldValues(w, image is not null, audio is not null, options.ReverseCards);
            var fields = new JsonObject();
            for (var f = 0; f < AnkiNoteType.Fields.Length; f++) fields[AnkiNoteType.Fields[f]] = values[f];

            var existing = await FindAsync(w.Id, ct).ConfigureAwait(false);
            if (existing.Count > 0)
            {
                await InvokeAsync("updateNoteFields", new JsonObject
                {
                    ["note"] = new JsonObject { ["id"] = existing[0], ["fields"] = fields },
                }, ct).ConfigureAwait(false);
                updated++;
            }
            else
            {
                await InvokeAsync("addNote", new JsonObject
                {
                    ["note"] = new JsonObject
                    {
                        ["deckName"] = AnkiNoteType.DeckFor(w.Language),
                        ["modelName"] = AnkiNoteType.Name,
                        ["fields"] = fields,
                        ["tags"] = new JsonArray("glossa", w.Language),
                        ["options"] = new JsonObject { ["allowDuplicate"] = true },
                    },
                }, ct).ConfigureAwait(false);
                added++;
            }
        }

        foreach (var r in removed)
        {
            var ids = await FindAsync(r.Id, ct).ConfigureAwait(false);
            if (ids.Count == 0) continue;
            var arr = new JsonArray();
            foreach (var id in ids) arr.Add(id);
            await InvokeAsync("addTags", new JsonObject { ["notes"] = arr, ["tags"] = AnkiNoteType.RemovedTag }, ct).ConfigureAwait(false);
            tagged++;
        }
        return new AnkiSyncResult(added, updated, tagged);
    }

    private async Task EnsureModelAsync(CancellationToken ct)
    {
        var names = await InvokeAsync("modelNames", null, ct).ConfigureAwait(false) as JsonArray;
        if (names?.Any(n => n?.GetValue<string>() == AnkiNoteType.Name) == true) return;

        var templates = new JsonArray();
        foreach (var t in AnkiNoteType.Templates)
            templates.Add(new JsonObject { ["Name"] = t.Name, ["Front"] = t.Front, ["Back"] = t.Back });
        var fields = new JsonArray();
        foreach (var f in AnkiNoteType.Fields) fields.Add(f);
        await InvokeAsync("createModel", new JsonObject
        {
            ["modelName"] = AnkiNoteType.Name,
            ["inOrderFields"] = fields,
            ["css"] = AnkiNoteType.Css,
            ["isCloze"] = false,
            ["cardTemplates"] = templates,
        }, ct).ConfigureAwait(false);
    }

    private async Task<List<long>> FindAsync(string glossaId, CancellationToken ct)
    {
        var res = await InvokeAsync("findNotes", new JsonObject
        {
            ["query"] = $"\"note:{AnkiNoteType.Name}\" \"GlossaId:{glossaId}\"",
        }, ct).ConfigureAwait(false) as JsonArray;
        return res?.Select(n => n!.GetValue<long>()).ToList() ?? [];
    }

    private Task StoreMediaAsync(string name, byte[] data, CancellationToken ct) =>
        InvokeAsync("storeMediaFile", new JsonObject { ["filename"] = name, ["data"] = Convert.ToBase64String(data) }, ct);

    internal async Task<JsonNode?> InvokeAsync(string action, JsonObject? parameters, CancellationToken ct)
    {
        var body = new JsonObject { ["action"] = action, ["version"] = 6 };
        if (parameters is not null) body["params"] = parameters;
        using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var resp = await http.PostAsync(url, content, ct).ConfigureAwait(false);
        var json = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false))
                   ?? throw new AnkiConnectException("Пустой ответ AnkiConnect");
        if (json["error"] is JsonNode err && err.GetValueKind() != JsonValueKind.Null)
            throw new AnkiConnectException($"AnkiConnect ({action}): {err}");
        return json["result"];
    }
}
