using System.Media;
using System.Security.Cryptography;
using System.Text;
using Glossa.Core.Config;
using Windows.Media.SpeechSynthesis;
using Windows.Storage.Streams;

namespace Glossa.App.Speech;

/// <summary>
/// Word pronunciation with the Windows speech synthesizer (offline voices): the voice chosen for the language in
/// the settings, at the chosen rate and volume. WAV files are cached per language and reused for Anki audio.
/// </summary>
public sealed class SpeechService(string cacheDir, Func<SpeechSettings> settings)
{
    private SoundPlayer? _player;

    private static readonly Dictionary<string, string> Tags = new()
    {
        ["en"] = "en", ["ru"] = "ru", ["ja"] = "ja", ["zh"] = "zh-CN", ["zh-Hant"] = "zh-TW", ["ko"] = "ko",
    };

    public string CacheDir => cacheDir;

    /// <summary>Installed Windows voices for the language; Chinese falls back to any Chinese voice.</summary>
    public static IReadOnlyList<VoiceInformation> VoicesFor(string language)
    {
        var tag = Tags.GetValueOrDefault(language, language);
        var voices = SpeechSynthesizer.AllVoices.Where(v => v.Language.StartsWith(tag, StringComparison.OrdinalIgnoreCase)).ToList();
        if (voices.Count == 0 && language.StartsWith("zh", StringComparison.Ordinal))
            voices = SpeechSynthesizer.AllVoices.Where(v => v.Language.StartsWith("zh", StringComparison.OrdinalIgnoreCase)).ToList();
        return voices;
    }

    /// <summary>The voice chosen for the language, or its first installed one; null when there is none.</summary>
    public VoiceInformation? VoiceFor(string language)
    {
        var voices = VoicesFor(language);
        return settings().Voices.TryGetValue(language, out var id) && voices.FirstOrDefault(v => v.Id == id) is { } chosen
            ? chosen
            : voices.FirstOrDefault();
    }

    /// <summary>WAV bytes for the text, from cache or freshly synthesized; null when no voice is installed.</summary>
    public async Task<byte[]?> WavAsync(string text, string language)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var voice = VoiceFor(language);
        if (voice is null) return null;
        var s = settings();
        var rate = Math.Clamp(s.Rate, 0.5, 2.0);
        var volume = Math.Clamp(s.Volume, 0.0, 1.0);

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{voice.Id}|{rate:0.00}|{volume:0.00}|{text}")))[..24]
            .ToLowerInvariant();
        var path = Path.Combine(cacheDir, language, hash + ".wav");
        if (File.Exists(path)) return await File.ReadAllBytesAsync(path);

        using var synth = new SpeechSynthesizer { Voice = voice };
        synth.Options.SpeakingRate = rate;
        synth.Options.AudioVolume = volume;
        using var stream = await synth.SynthesizeTextToStreamAsync(text);
        var bytes = new byte[stream.Size];
        using (var reader = new DataReader(stream.GetInputStreamAt(0)))
        {
            await reader.LoadAsync((uint)stream.Size);
            reader.ReadBytes(bytes);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, bytes);
        return bytes;
    }

    /// <summary>Plays the text; returns false when there is no voice for the language.</summary>
    public async Task<bool> SpeakAsync(string text, string language)
    {
        var wav = await WavAsync(text, language);
        if (wav is null) return false;
        _player?.Stop();
        _player = new SoundPlayer(new MemoryStream(wav));
        _player.Play();
        return true;
    }

    /// <summary>Size of the cached recordings in bytes.</summary>
    public long CacheBytes() =>
        Directory.Exists(cacheDir) ? new DirectoryInfo(cacheDir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) : 0;

    /// <summary>Deletes the cached recordings; they are made again when needed.</summary>
    public void ClearCache()
    {
        if (!Directory.Exists(cacheDir)) return;
        foreach (var dir in Directory.EnumerateDirectories(cacheDir)) Directory.Delete(dir, recursive: true);
        foreach (var file in Directory.EnumerateFiles(cacheDir)) File.Delete(file);
    }
}
