namespace SunsetCurse.Core
{
    /// <summary>
    /// Difficulty tier the player picked on the menu. Multiplayer forces this to <see cref="Hard"/>.
    /// </summary>
    public enum Difficulty { Easy, Medium, Hard }

    /// <summary>
    /// Single-player vs. multiplayer choice carried from the menu into the gameplay scene.
    /// </summary>
    public enum GameMode { SinglePlayer, Multiplayer }

    /// <summary>
    /// Static cross-scene preference store. The MainMenu writes <see cref="Selected"/> + <see cref="Mode"/>
    /// when the player picks; the gameplay scene reads them to configure the monster, the lobby, etc.
    /// Static fields survive scene loads (no DontDestroyOnLoad / PlayerPrefs needed).
    ///
    /// The per-difficulty *Mult* methods are the dial we use to scale the monster. Easy = 1.0 across the
    /// board (the values you tuned in the Inspector are the EASY baseline); Medium/Hard scale up.
    /// </summary>
    public static class DifficultyPreference
    {
        public static Difficulty Selected { get; set; } = Difficulty.Easy;
        public static GameMode Mode { get; set; } = GameMode.SinglePlayer;

        /// <summary>How many day/night cycles this run lasts (2, 4, or 7). Drives GameClock.totalDays
        /// AND the number of ritual potions needed (one per night). Set from the menu; on the host
        /// this is authoritative and EscapeTracker replicates the potion count to clients.</summary>
        public static int TotalNights { get; set; } = 7;

        // Multipliers applied to MonsterAI's Inspector base values at game start.
        // Easy = no change; Medium = clearly tougher; Hard = oppressive.
        public static float RunSpeedMult(Difficulty d)        => d == Difficulty.Hard ? 1.50f : d == Difficulty.Medium ? 1.25f : 1.0f;
        public static float StalkSpeedMult(Difficulty d)      => d == Difficulty.Hard ? 1.30f : d == Difficulty.Medium ? 1.15f : 1.0f;
        public static float DetectRadiusMult(Difficulty d)    => d == Difficulty.Hard ? 1.60f : d == Difficulty.Medium ? 1.30f : 1.0f;
        public static float CloseScentMult(Difficulty d)      => d == Difficulty.Hard ? 1.75f : d == Difficulty.Medium ? 1.35f : 1.0f;
        public static float KillRangeMult(Difficulty d)       => d == Difficulty.Hard ? 1.40f : d == Difficulty.Medium ? 1.20f : 1.0f;
        public static float WanderBiasMult(Difficulty d)      => d == Difficulty.Hard ? 1.50f : d == Difficulty.Medium ? 1.25f : 1.0f;
        public static float SearchDurationMult(Difficulty d)  => d == Difficulty.Hard ? 1.50f : d == Difficulty.Medium ? 1.25f : 1.0f;
        // Bonus scent bursts ADDED on top of the base scentChasesPerNight.
        public static int   ScentCountBonus(Difficulty d)     => d == Difficulty.Hard ? 5    : d == Difficulty.Medium ? 2    : 0;

        public static string Label(Difficulty d) => d switch
        {
            Difficulty.Easy => "EASY",
            Difficulty.Medium => "MEDIUM",
            Difficulty.Hard => "HARD",
            _ => "?"
        };
    }
}
