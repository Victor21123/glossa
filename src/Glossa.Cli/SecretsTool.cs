using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Glossa.Core.Companions;

/// <summary>
/// The author's companion secrets, kept outside the repository (GLOSSA_SECRETS, else ..\GlossaSecrets beside the
/// clone, as Directory.Build.props finds it):
///   glossa-cli secrets init        three random keys (art, seal, fate) in keys.json, once; CompanionSecrets.g.cs
///   glossa-cli secrets word        the admin menu's secret word, typed twice unseen; only its PBKDF2 hash is kept
///   glossa-cli companions pack [catalog]   assets\companions -> companions.pack, encrypted with the art key
/// The keys are never replaced once made: a new seal key would turn every companion into a "forgery".
/// </summary>
internal static class SecretsTool
{
    public static int Run(string[] args)
    {
        var dir = Folder();
        switch (args.ElementAtOrDefault(1))
        {
            case "init":
                Directory.CreateDirectory(dir);
                var keys = Keys(dir);
                foreach (var name in new[] { "art", "seal", "fate" })
                    if (keys[name] is null)
                        keys[name] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
                Save(dir, keys);
                Generate(dir, keys);
                Console.WriteLine($"secrets in {dir}: keys.json, CompanionSecrets.g.cs");
                return 0;
            case "word":
                return Word(dir);
            default:
                Console.WriteLine("usage: secrets init | secrets word");
                return 1;
        }
    }

    /// <summary>The secret word: typed twice, never shown, never written - only a salted PBKDF2 hash goes to keys.json.</summary>
    private static int Word(string dir)
    {
        if (Console.IsInputRedirected)
        {
            Console.WriteLine("the word is typed in a console window, not piped");
            return 1;
        }
        var keys = Keys(dir);
        if (keys["art"] is null)
        {
            Console.WriteLine("run \"secrets init\" first");
            return 1;
        }
        var word = ReadHidden("secret word (6+ characters; a longer one is harder to guess): ");
        if (word.Trim().Length < 6)
        {
            Console.WriteLine("too short: 6 characters at least");
            return 1;
        }
        if (ReadHidden("again: ") != word)
        {
            Console.WriteLine("the two do not match, nothing changed");
            return 1;
        }
        var salt = RandomNumberGenerator.GetBytes(16);
        keys["wordSalt"] = Convert.ToBase64String(salt);
        keys["wordHash"] = Convert.ToBase64String(AdminWord.Derive(word, salt));
        Save(dir, keys);
        Generate(dir, keys);
        Console.WriteLine("the word is set; rebuild Glossa (tools\\publish.ps1) for it to work");
        return 0;
    }

    private static string ReadHidden(string prompt)
    {
        Console.Write(prompt);
        var text = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
                break;
            if (key.Key == ConsoleKey.Backspace)
            {
                if (text.Length > 0) text.Length--;
                continue;
            }
            if (!char.IsControl(key.KeyChar))
                text.Append(key.KeyChar);
        }
        Console.WriteLine();
        return text.ToString();
    }

    /// <summary>The author's catalog folder packed and encrypted with the art key, into the secrets folder.</summary>
    public static int Pack(string[] args)
    {
        if (args.ElementAtOrDefault(1) != "pack")
        {
            Console.WriteLine("usage: companions pack [catalog folder]");
            return 1;
        }
        var dir = Folder();
        var catalog = args.ElementAtOrDefault(2) ?? Path.Combine(RepoRoot() ?? ".", "assets", "companions");
        if (Keys(dir)["art"]?.GetValue<string>() is not { } art)
        {
            Console.WriteLine("no keys: run \"secrets init\" first");
            return 1;
        }
        var files = CompanionPack.ReadFolder(catalog);
        var companions = CompanionCatalog.Load(files).All;
        if (companions.Count == 0)
        {
            Console.WriteLine($"no companions in {catalog}");
            return 1;
        }
        var pack = CompanionPack.Seal(files, Convert.FromBase64String(art));
        File.WriteAllBytes(Path.Combine(dir, "companions.pack"), pack);
        Console.WriteLine($"{companions.Count} companions, {files.Count} files -> {Path.Combine(dir, "companions.pack")} ({pack.Length >> 10} KB)");
        return 0;
    }

    private static string Folder() =>
        Environment.GetEnvironmentVariable("GLOSSA_SECRETS") is { Length: > 0 } env ? env
            : Path.GetFullPath(Path.Combine(RepoRoot() ?? throw new InvalidOperationException(
                "run from the Glossa clone or set GLOSSA_SECRETS"), "..", "GlossaSecrets"));

    private static string? RepoRoot()
    {
        for (var d = new DirectoryInfo(Environment.CurrentDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "Glossa.sln")))
                return d.FullName;
        return null;
    }

    private static JsonObject Keys(string dir)
    {
        var path = Path.Combine(dir, "keys.json");
        return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path))!.AsObject() : [];
    }

    private static void Save(string dir, JsonObject keys) =>
        File.WriteAllText(Path.Combine(dir, "keys.json"), keys.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

    /// <summary>
    /// CompanionSecrets.g.cs: the keys masked (CompanionSecrets.Unmask), compiled into Glossa.Core only where this
    /// folder is. Never commit it.
    /// </summary>
    private static void Generate(string dir, JsonObject keys)
    {
        string Line(string field, string name) =>
            keys[name]?.GetValue<string>() is { } b64
                ? $"        {field} = Unmask({Bytes(Mask(Convert.FromBase64String(b64), out var salt))}, {Bytes(salt)});"
                : $"        // {name}: not set";
        var code = $$"""
            // <auto-generated> glossa-cli secrets: the release's companion keys. Private - never commit, never share. </auto-generated>
            #nullable enable
            namespace Glossa.Core.Companions;

            public static partial class CompanionSecrets
            {
                static partial void Load(ref byte[]? art, ref byte[]? seal, ref byte[]? fate, ref byte[]? wordSalt, ref byte[]? wordHash)
                {
            {{Line("art", "art")}}
            {{Line("seal", "seal")}}
            {{Line("fate", "fate")}}
            {{Line("wordSalt", "wordSalt")}}
            {{Line("wordHash", "wordHash")}}
                }
            }

            """;
        File.WriteAllText(Path.Combine(dir, "CompanionSecrets.g.cs"), code);
    }

    private static byte[] Mask(byte[] plain, out byte[] salt)
    {
        salt = RandomNumberGenerator.GetBytes(16);
        return CompanionSecrets.Mask(plain, salt);
    }

    private static string Bytes(byte[] b) => "[" + string.Join(", ", b.Select(x => "0x" + x.ToString("X2"))) + "]";
}
