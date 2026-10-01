using System.Globalization;
using Glossa.Core.Config;

namespace Glossa.Core.Updates;

/// <summary>The words of the update check, in the style of STYLE.md (plain keyboard characters, a hyphen for a dash).</summary>
public static class UpdateTexts
{
    /// <summary>The tray notice's title: "Вышла Glossa 0.1.0".</summary>
    public static string BalloonTitle(string version) => $"Вышла Glossa {version}";

    /// <summary>The tray notice's text: what a click does.</summary>
    public const string BalloonText = "Щёлкни, чтобы открыть страницу на GitHub";

    /// <summary>The tray menu's item while an update waits.</summary>
    public static string TrayItem(string version) => $"Доступна {version} - открыть";

    /// <summary>The status line for a fresh answer.</summary>
    public static string For(UpdateOutcome outcome) => outcome.Status switch
    {
        UpdateStatus.UpToDate => "Установлена последняя версия",
        UpdateStatus.Available => $"Доступна {outcome.Release!.Version}",
        _ => "Не удалось проверить: " + outcome.Failure switch
        {
            UpdateFailure.Offline => "нет сети",
            UpdateFailure.Refused => "GitHub отказал (слишком много запросов), попробуй позже",
            UpdateFailure.NoRelease => "на GitHub пока нет выпусков",
            UpdateFailure.TooLarge => "ответ GitHub слишком большой",
            _ => "GitHub ответил не так, как ожидалось",
        },
    };

    /// <summary>The status line before any check in this run: from what the settings remember.</summary>
    public static string Summary(UpdateSettings updates, AppVersion current)
    {
        if (UpdateCheck.Pending(updates, current) is { } pending) return $"Доступна {pending.Version}";
        return updates.LastCheckUtc is { } last
            ? $"Установлена последняя версия (проверено {last.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)})"
            : "Ещё не проверялось";
    }
}
