using Glossa.Core.Config;

namespace Glossa.Core.Llm;

/// <summary>When a loaded local model should leave memory (Настройки → ИИ и модели, → Нагрузка на ПК).</summary>
public static class ModelLifetime
{
    /// <summary>Free video memory below this while a model is loaded means a game needs the room…</summary>
    public const int YieldBelowMb = 400;

    /// <summary>…if at least this much was taken by others since the model settled (the model itself fills the
    /// memory up to about its reserve, sometimes further: 100 MB free right after a load is normal).</summary>
    public const int TakenByOthersMb = 300;

    /// <summary>The load itself fills video memory up to the reserve; it is judged only after this.</summary>
    public static readonly TimeSpan Settle = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Why a loaded, not busy local model should go now, or null: no lookups for the idle time (the shorter game
    /// time while the game of the last lookup is in front), or a game that took video memory after the model settled
    /// and is now short of it. <paramref name="freeVramMb"/> and <paramref name="freeAfterLoadMb"/> are -1 when unknown.
    /// </summary>
    public static string? UnloadReason(AppSettings s, bool inGame, TimeSpan idle, TimeSpan sinceLoad, int freeVramMb, int freeAfterLoadMb)
    {
        var game = inGame && s.Performance.GameIdleUnloadMinutes > 0;
        var minutes = game ? s.Performance.GameIdleUnloadMinutes : s.LocalAi.IdleUnloadMinutes;
        if (minutes > 0 && idle >= TimeSpan.FromMinutes(minutes))
            return $"idle {minutes} min{(game ? " in a game" : "")}";
        if (s.Performance.YieldVram && sinceLoad >= Settle && freeVramMb is >= 0 and < YieldBelowMb
            && freeAfterLoadMb >= 0 && freeAfterLoadMb - freeVramMb >= TakenByOthersMb)
            return $"free video memory {freeAfterLoadMb} → {freeVramMb} MB, a game needs it";
        return null;
    }
}
