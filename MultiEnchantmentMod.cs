using System;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Enchantments;
using MegaCrit.Sts2.Core.Models.Monsters;

namespace MultiEnchantmentMod;

[ModInitializer(nameof(Initialize))]
public partial class MultiEnchantmentMod : Node
{
    private const string ModId = "MultiEnchantmentMod";
    private static bool _loggedThievingHopperReflectionFallback;

    public static MegaCrit.Sts2.Core.Logging.Logger Logger { get; } =
        new(ModId, MegaCrit.Sts2.Core.Logging.LogType.Generic);

    public static void Initialize()
    {
        MultiEnchantmentSupport.Initialize();

        // Register every built-in MegaCrit enchantment via the v2 fluent builder before any
        // Resolve* call can hit. Replaces the hardcoded switch tables that used to live in
        // MultiEnchantmentStackSupport.GetBuiltInDefinition / GetBuiltInKeywordSourceAmount.
        Api.Internal.BuiltInRegistrations.RegisterAll();

        // Pre-scan our own assembly so any v2 attributes / EnchantmentDefinition<T> classes the
        // mod itself ships (none yet — the built-in matrix is registered above) get picked up
        // before the first Resolve* call. Third-party mods are expected to call
        // MultiEnchantmentApi.ScanCallingAssembly() from their own [ModInitializer]; the lazy
        // first-Resolve fallback in MultiEnchantmentStackApi covers anyone who forgets.
        Api.MultiEnchantmentApi.ScanAssembly(Assembly.GetExecutingAssembly());

        new Harmony(ModId).PatchAll(Assembly.GetExecutingAssembly());
        PatchThievingHopperPriorities();
    }

    private static void PatchThievingHopperPriorities()
    {
        // Base-game source: ThievingHopper._stealPriorities.
        // We only widen the Imbued check so multi-enchanted cards are prioritized consistently.
        FieldInfo? field = AccessTools.Field(typeof(ThievingHopper), "_stealPriorities");
        if (field == null)
        {
            LogThievingHopperReflectionFallback("Field _stealPriorities was not found.");
            return;
        }

        object? value;
        try
        {
            value = field.GetValue(null);
        }
        catch (Exception ex)
        {
            LogThievingHopperReflectionFallback($"Reading _stealPriorities threw: {ex.GetBaseException().Message}");
            return;
        }

        if (value is not Func<CardModel, bool>[] priorities)
        {
            LogThievingHopperReflectionFallback("Field _stealPriorities did not contain the expected delegate array.");
            return;
        }

        if (priorities.Length < 4)
        {
            LogThievingHopperReflectionFallback($"Field _stealPriorities had length {priorities.Length}, expected at least 4.");
            return;
        }

        priorities[0] = static card => !MultiEnchantmentSupport.HasEnchantment<Imbued>(card) &&
                                       card.Rarity == CardRarity.Uncommon;
        priorities[1] = static card => !MultiEnchantmentSupport.HasEnchantment<Imbued>(card) &&
                                       (card.Rarity == CardRarity.Common ||
                                        card.Rarity == CardRarity.Rare ||
                                        card.Rarity == CardRarity.Event);
        priorities[2] = static card => !MultiEnchantmentSupport.HasEnchantment<Imbued>(card) &&
                                       (card.Rarity == CardRarity.Basic ||
                                        card.Rarity == CardRarity.Quest);
        priorities[3] = static card => card.Rarity == CardRarity.Ancient ||
                                       MultiEnchantmentSupport.HasEnchantment<Imbued>(card);
    }

    private static void LogThievingHopperReflectionFallback(string reason)
    {
        if (_loggedThievingHopperReflectionFallback)
        {
            return;
        }

        _loggedThievingHopperReflectionFallback = true;
        Logger.Warn(
            "[MultiEnchantmentMod] Failed to patch ThievingHopper steal priorities via reflection. Falling back to the base-game implementation, which may ignore additional Imbued enchantments. Reason: " +
            reason);
    }
}
