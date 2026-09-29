using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Glossa.App.ViewModels;
using Glossa.Core.Config;

namespace Glossa.App.Views.Settings;

public partial class AppSection : UserControl
{
    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");
    private readonly AppServices _services;
    private bool _counted;

    public AppSection(AppServices services, SettingsViewModel model)
    {
        InitializeComponent();
        _services = services;
        DataContext = model;
        DataFolder.Content = DataPaths.Root;
        var exe = Environment.ProcessPath ?? "";
        Build.Text = "Glossa · сборка " + File.GetLastWriteTime(exe).ToString("d MMMM yyyy", Russian);
        Location.Text = Path.GetDirectoryName(exe);
        Loaded += async (_, _) =>
        {
            if (_counted) return;
            _counted = true;
            var parts = await Task.Run(() => Measure(_services.Settings, exe));
            ShowDisk(parts);
        };
    }

    /// <summary>What Glossa keeps on disk, biggest first: the AI model(s), llama.cpp, dictionaries, language data, the library.</summary>
    private static List<DiskPart> Measure(AppSettings s, string exe)
    {
        var ai = s.LocalAi;
        string[] models = ai.SingleModel(ai.Profile) is { Length: > 0 } one ? [one] : [];
        var parts = new List<(string Name, long Bytes)>
        {
            ("Модель ИИ", models.Sum(Sizes.File)),
            ("llama.cpp", Sizes.Folder(Path.GetDirectoryName(ai.LlamaServerResolved()))),
            ("Справочники", Sizes.Folder(DataPaths.Packs) + Sizes.File(DataPaths.Levels) + Sizes.File(DataPaths.Cedict)),
            ("UniDic", Sizes.Folder(DataPaths.UniDic)),
            ("Распознавание", Sizes.Folder(DataPaths.OcrModels)),
            ("Программа", Sizes.Folder(Path.GetDirectoryName(exe))),
            ("Словарь, кадры, звук", Sizes.File(DataPaths.Library) + Sizes.File(DataPaths.Library + "-wal")
                                     + Sizes.Folder(DataPaths.Shots) + Sizes.Folder(DataPaths.Audio)),
        };
        double[] shades = [1, 0.72, 0.52, 0.38, 0.28, 0.2, 0.13];
        return parts.Where(p => p.Bytes > 0).OrderByDescending(p => p.Bytes)
            .Select((p, i) => new DiskPart(p.Name, p.Bytes, Sizes.Format(p.Bytes), shades[Math.Min(i, shades.Length - 1)]))
            .ToList();
    }

    private void ShowDisk(List<DiskPart> parts)
    {
        var total = parts.Sum(p => p.Bytes);
        DiskTotal.Text = Sizes.Format(total);
        DiskLegend.ItemsSource = parts;
        DiskBar.ColumnDefinitions.Clear();
        DiskBar.Children.Clear();
        for (var i = 0; i < parts.Count; i++)
        {
            // Tiny parts still get a sliver so every legend entry has a place in the bar.
            DiskBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(parts[i].Bytes, total / 200.0), GridUnitType.Star) });
            var piece = new Border { Opacity = parts[i].Opacity, Margin = new Thickness(i == 0 ? 0 : 1, 0, 0, 0) };
            piece.SetResourceReference(Border.BackgroundProperty, "Ink");
            Grid.SetColumn(piece, i);
            DiskBar.Children.Add(piece);
        }
        DiskBar.Clip = new RectangleGeometry(new Rect(0, 0, 4000, 14), 4, 4);
        DiskNote.Text = $"Модели ИИ лежат в {_services.Settings.LocalAi.ModelsFolderResolved()}, программа — в {Location.Text}, " +
                        $"остальное — в папке данных.";
    }

    private void OnOpenData(object sender, RoutedEventArgs e) => Process.Start("explorer.exe", DataPaths.Root);

    private void OnOpenLog(object sender, RoutedEventArgs e)
    {
        var latest = Directory.Exists(DataPaths.Logs)
            ? new DirectoryInfo(DataPaths.Logs).GetFiles("*.log").OrderByDescending(f => f.LastWriteTime).FirstOrDefault()
            : null;
        if (latest is not null) Process.Start("notepad.exe", latest.FullName);
        else Process.Start("explorer.exe", DataPaths.Root);
    }
}

public sealed record DiskPart(string Name, long Bytes, string Size, double Opacity);

/// <summary>Sizes on disk, in the units the mockups use: «13,4 ГБ», «608 МБ», «меньше 1 МБ».</summary>
public static class Sizes
{
    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");

    public static long File(string? path) => path is { Length: > 0 } && System.IO.File.Exists(path) ? new FileInfo(path).Length : 0;

    public static long Folder(string? path)
    {
        if (path is not { Length: > 0 } || !Directory.Exists(path)) return 0;
        try
        {
            return new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
        }
        catch (IOException)
        {
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            return 0;
        }
    }

    public static string Format(long bytes) => bytes switch
    {
        >= 1L << 30 => string.Format(Russian, "{0:0.0} ГБ", bytes / (double)(1L << 30)),
        >= 1L << 20 => string.Format(Russian, "{0:0} МБ", bytes / (double)(1L << 20)),
        _ => "меньше 1 МБ",
    };
}
