namespace PerkTree.Mod.Effects;

/// <summary>Which effect ids have a combat handler, and a fresh instance per player.</summary>
internal static class HandlerRegistry
{
    // One line per handler. Adding an effect = write the class, add it here.
    private static readonly Dictionary<string, Func<EffectHandler>> Factories = Build(
        () => new StartThorns(),
        () => new StartBlock(),
        () => new StartVigor(),
        () => new StartHeal(),
        () => new TurnPingDamage(),
        () => new EnergyEveryNTurns(),
        () => new EndTurnNoBlock(),
        () => new EnergyPerAttacks(),
        () => new AttackStrength(),
        () => new AttackDexterity(),
        () => new AttackBlock(),
        () => new SkillDamageAll(),
        () => new DrawOnFirstHit(),
        () => new SkillDrawChance(),
        // combat start
        () => new StartStrength(), () => new StartDexterity(), () => new StartRegen(),
        () => new StartArtifact(), () => new StartBuffer(), () => new StartBlur(),
        () => new StartVulnerableEnemies(), () => new GoldStrength(), () => new BlockPerRelic(),
        // turns, draws, first-time effects
        () => new EnduringStrength(), () => new TurnVigor(), () => new FirstTurnDraw(), () => new LowHpDraw(),
        () => new SecondWindBlock(), () => new FirstHpLossStrength(), () => new PowerFirstDraw(),
        () => new PowerEnergy(), () => new HpLostVigor(), () => new ExecuteBonus(),
        // damage dealt
        () => new KillGold(), () => new KillBlock(), () => new HitEnergyChance(),
        // potions
        () => new PotionBlock(), () => new PotionStrength(), () => new PotionDexterity(),
        () => new PotionVulnerable(), () => new PotionWeak(), () => new PotionBuffer(), () => new PotionAcid(), () => new PotionFind(), () => new PotionRefund(), () => new PotionPoison(), () => new DamagedBlockChance(), () => new TurnForgeChance(), () => new CombatForgeChance(), () => new ForgeBlockChance(), () => new BladeAttackForge(),
        () => new FirstTurnStarsChance(), () => new FirstTurnEnergyChance(), () => new TurnFocusChance(), () => new TurnExhaustChoice(), () => new TurnDiscardChoice(), () => new FreeAttackReturn(),
        () => new StartChannelLightning(), () => new StartChannelFrost(), () => new StartChannelDark(), () => new StartChannelPlasma(), () => new StartChannelGlass(),
        // Vigor and gain amplification
        () => new VigorStrengthPct(), () => new VigorBlockPct(), () => new VigorEnergyPct(), () => new VigorRegenPct(),
        () => new VigorVulnerablePct(), () => new VigorWeakPct(), () => new VigorUnusedToBlock(),
        () => new VigorUnusedStrengthPct(), () => new VigorGainAmplify(), () => new GainAmplify(), () => new VigorProcDouble(),
        // gambles that debuff, and moving debuffs
        () => new SymmetricVulnerable(), () => new SymmetricWeak(), () => new RandomDebuff(), () => new SpreadDebuffsOnKill(),
        // card piles
        () => new ExhaustBlock(), () => new DiscardDraw(), () => new CurseDrawStat(), () => new RetainRandom(), () => new CurseCountRamp(),
        // power applications
        () => new VulnerableExtraChance(), () => new PoisonExtraChance(), () => new PoisonApplyBlock(), () => new PoisonApplyDamage(), () => new DoomExtra(), () => new FirstFocusBonus(), () => new LossInvert(),
        // cost keystones
        () => new EnergyForHp(), () => new EnergyForMaxHp(), () => new ReplayGrant(), () => new EnemyStrip(), () => new GoldArmor(),
        // character mechanics: Shivs, Orbs, Osty, Stars and Forge
        () => new ShivDuplicateChance(), () => new ShivReturnChance(),
        () => new OrbChannelChance(), () => new OrbChannelFocus(), () => new OrbChannelBlock(), () => new FocusWhenFull(), () => new StartChannelOrb(),
        () => new SummonBlock(), () => new SummonEachTurn(), () => new TurnSummonFromHpLost(), () => new TurnBlockFromSummonHp(),
        () => new StarEachTurn(), () => new ForgeStar(), () => new ForgeDrawChance(), () => new StarRefundChance(), () => new StarGainChance(),
        // added after the audit
        () => new TurnHeal(), () => new FocusGainChance(), () => new PlayCopyChance(), () => new CombatGoldReward(),
        // run from Harmony patches (EffectPatches.cs)
        () => new ExhaustDodge(), () => new EtherealSave(), () => new EnergyDischarge(), () => new DoomDefer());

    // Effects that work through Harmony patches, or that are only read by other handlers, rather than having a combat
    // handler of their own. Listed so a committed one isn't reported as "no handler".
    private static readonly HashSet<string> PatchHandled = new(StringComparer.Ordinal)
    {
        "vulnerable_resist", "weak_resist", "frail_resist",
        // Passive: no code of their own, other handlers read their totals (EffectHandler.Roll, PotionAmount, BoostedStartPower).
        "proc_boost", "proc_double", "proc_reroll", "potion_boost", "potion_double", "start_power_boost",
        "potion_slot", // Potion+: PerkHostModel.ReconcilePotionSlots reads its total on room entry
        "max_hp",      // Healthy: PerkHostModel.ReconcileMaxHp does the same for Max HP
    };

    /// <summary>
    /// Effects whose number is just read, never rolled: modifiers the game asks about many times (hand size, damage, resists, loss
    /// invert), the luck/boost perks that change other perks, and effects that are only "do I own this" switches. A chance
    /// below 100% on one of these has no effect, and the loader says so. Keep in step with the handlers: tools/scan-mod.mjs
    /// reports every other handled effect as able to roll.
    /// </summary>
    public static readonly IReadOnlySet<string> GuaranteedOnly = new HashSet<string>(StringComparer.Ordinal)
    {
        "vulnerable_resist", "weak_resist", "frail_resist",
        "proc_boost", "proc_double", "proc_reroll", "potion_boost", "potion_double", "start_power_boost",
        "low_hp_draw", "execute_bonus", "loss_invert",
        "ethereal_save", "spread_debuffs_on_kill", "doom_defer",
        "potion_slot", // a permanent slot count, not an event
        "max_hp",      // a permanent stat, not an event
        "gold_armor", // its discount is worked out inside the damage calculation, which also runs for previews and so cannot roll
    };

    public static EffectHandler? Create(string effectId)
        => Factories.TryGetValue(effectId, out var make) ? make() : null;

    public static bool IsPatchHandled(string effectId) => PatchHandled.Contains(effectId);

    public static IEnumerable<string> HandledIds => Factories.Keys;

    private static Dictionary<string, Func<EffectHandler>> Build(params Func<EffectHandler>[] makers)
    {
        var map = new Dictionary<string, Func<EffectHandler>>(StringComparer.Ordinal);
        foreach (var make in makers) map.Add(make().Id, make); // a duplicate id throws at startup, which is what we want
        return map;
    }
}
