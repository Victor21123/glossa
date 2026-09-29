using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Glossa.App.Diagnostics;
using Glossa.App.Lookup;
using Glossa.App.ViewModels;

namespace Glossa.App.Views.Settings;

public partial class LoadSection : UserControl
{
    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");
    private readonly AppServices _services;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private LoadSample? _previous;

    public LoadSection(AppServices services, SettingsViewModel model)
    {
        InitializeComponent();
        _services = services;
        DataContext = model;
        ShowLast();
        ShowNow();
        _timer.Tick += (_, _) => ShowNow();
        Loaded += (_, _) =>
        {
            _services.LookupReported += OnReported;
            _timer.Start();
            ShowLast();
        };
        Unloaded += (_, _) =>
        {
            _services.LookupReported -= OnReported;
            _timer.Stop();
        };
    }

    private void OnReported(LookupReport report) => Dispatcher.Invoke(ShowLast);

    /// <summary>The last lookup stage by stage, as bars on one time line from the key press.</summary>
    private void ShowLast()
    {
        Waterfall.Children.Clear();
        if (_services.RecentLookups.FirstOrDefault(r => r.Stages is not null) is not { Stages: { } s } report)
        {
            LastTotal.Text = "—";
            LastNote.Text = "поисков пока не было";
            return;
        }
        var total = s.AiMs ?? s.CardMs;
        LastTotal.Text = Seconds(total);
        // "слово «reconsider» · 3,0 с" → the word only: the time is already the big number.
        var what = report.Result.Split(" · ")[0];
        LastNote.Text = report.Model is { } m ? $"{what} · {m}" : what;
        var rows = new List<(string Name, long From, long To)>
        {
            ("Снимок экрана", 0, s.CaptureMs),
            ("Распознавание", s.CaptureMs, s.OcrMs),
            ("Справочники", s.OcrMs, s.DictionariesMs),
            ("Карточка на экране", 0, s.CardMs),
        };
        if (s.FirstFieldMs is { } first) rows.Add(("ИИ: перевод", s.CardMs, first));
        if (s.AiMs is { } ai) rows.Add(("ИИ: вся карточка", s.CardMs, ai));
        foreach (var (name, from, to) in rows) Waterfall.Children.Add(Row(name, from, to, Math.Max(total, 1)));
    }

    private static FrameworkElement Row(string name, long from, long to, long total)
    {
        var grid = new Grid { Height = 30 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
        var label = new TextBlock { Text = name, FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
        label.SetResourceReference(TextBlock.ForegroundProperty, "InkSoft");
        grid.Children.Add(label);

        // The track, and the stage's span on it drawn to scale (a sliver at least, so short stages stay visible).
        var track = new Grid { Height = 10, VerticalAlignment = VerticalAlignment.Center };
        track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(from, GridUnitType.Star) });
        track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(to - from, total / 120.0), GridUnitType.Star) });
        track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0, total - to), GridUnitType.Star) });
        var back = new Border { CornerRadius = new CornerRadius(3) };
        back.SetResourceReference(Border.BackgroundProperty, "Band");
        Grid.SetColumnSpan(back, 3);
        track.Children.Add(back);
        var bar = new Border { CornerRadius = new CornerRadius(3) };
        bar.SetResourceReference(Border.BackgroundProperty, "Ink");
        Grid.SetColumn(bar, 1);
        track.Children.Add(bar);
        Grid.SetColumn(track, 1);
        grid.Children.Add(track);

        var time = new TextBlock { Text = Duration(to - from), FontSize = 13, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(time, 2);
        grid.Children.Add(time);
        return grid;
    }

    /// <summary>Memory of Glossa and llama-server, processor share since the last tick, free video memory.</summary>
    private void ShowNow()
    {
        if (_services.SampleLoad?.Invoke() is not { } now) return;
        RamText.Text = Megabytes(now.GlossaMb + now.ServerMb);
        CpuText.Text = _previous is { } before ? string.Format(Russian, "{0:0.#}%", now.CpuPercentSince(before)) : "…";
        VramText.Text = now.VramFreeMb >= 0 ? string.Format(Russian, "{0:0.0} ГБ свободно", now.VramFreeMb / 1024.0) : "нет данных";
        _previous = now;
        var loaded = _services.Ai.Current?.Dictionary is { Endpoint.IsLocal: true };
        NowNote.Text = loaded
            ? $"Модель загружена: Glossa {Megabytes(now.GlossaMb)}, llama-server {Megabytes(now.ServerMb)} памяти. Выгрузится без поисков по таймеру или когда игре не хватит видеопамяти."
            : $"Модель выгружена. Glossa держит {Megabytes(now.GlossaMb)} памяти; видеопамять ИИ не занимает.";
    }

    private static string Megabytes(double mb) =>
        mb >= 1024 ? string.Format(Russian, "{0:0.0} ГБ", mb / 1024) : string.Format(Russian, "{0:0} МБ", mb);

    private static string Seconds(long ms) => string.Format(Russian, "{0:0.0} с", ms / 1000.0);

    private static string Duration(long ms) => ms >= 1000 ? Seconds(ms) : $"{ms} мс";
}
