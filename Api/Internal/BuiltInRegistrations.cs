using System;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models.Enchantments;

namespace MultiEnchantmentMod.Api.Internal;

/// <summary>
/// Registers every built-in MegaCrit enchantment with the v2 stacking registry. Called from
/// <c>MultiEnchantmentMod.Initialize()</c> before the assembly scanner runs so the mod's own
/// matrix becomes ground truth even when no third-party mod is loaded.
/// </summary>
/// <remarks>
/// Built-in enchantments are vanilla MegaCrit types, so we can't decorate them with attributes.
/// Use the fluent <see cref="MultiEnchantmentApi.Register(Type)"/> builder for each one.
/// Special cases (Instinct's per-merge energy delta, Glam/Spiral's <c>Times</c> resync,
/// Goopy/SoulsPower/Steady/RoyallyApproved/TezcatarasEmber's keyword sources) get explicit
/// per-type registrations; everything else stays definition-only.
/// </remarks>
internal static class BuiltInRegistrations
{
    private static readonly Type[] MergeAmountSharedTypes =
    {
        typeof(Adroit),
        typeof(Clone),
        typeof(Glam),
        typeof(Imbued),
        typeof(Instinct),
        typeof(Momentum),
        typeof(Nimble),
        typeof(Sharp),
        typeof(Slither),
        typeof(SlumberingEssence),
        typeof(SoulsPower),
        typeof(Sown),
        typeof(Spiral),
        typeof(Swift),
        typeof(Vigorous),
    };

    private static readonly Type[] ExistenceStackPresenceTypes =
    {
        typeof(PerfectFit),
        typeof(RoyallyApproved),
        typeof(Steady),
        typeof(TezcatarasEmber),
    };

    private static bool _alreadyRegistered;

    /// <summary>
    /// Idempotent: subsequent calls are no-ops, since every registration would be a duplicate.
    /// </summary>
    public static void RegisterAll()
    {
        if (_alreadyRegistered)
        {
            return;
        }

        _alreadyRegistered = true;

        // === Definition + Status only (no special hooks) =====================================

        foreach (Type mergeType in MergeAmountSharedTypes)
        {
            // Special cases get fluent OnMergedDelta / OnMergedRefresh overrides applied below
            // before Commit, so skip the plain definition-only registration for those types.
            if (mergeType == typeof(Instinct) ||
                mergeType == typeof(Glam) ||
                mergeType == typeof(Spiral) ||
                mergeType == typeof(SoulsPower))
            {
                continue;
            }

            MultiEnchantmentApi.Register(mergeType)
                .Stack(StackBehavior.MergeAmount, StatusAggregation.SharedAcrossStack)
                .Commit();
        }

        foreach (Type existenceType in ExistenceStackPresenceTypes)
        {
            // Same: skip types with custom keyword tracking; they're registered below in one
            // builder chain.
            if (existenceType == typeof(Steady) ||
                existenceType == typeof(RoyallyApproved) ||
                existenceType == typeof(TezcatarasEmber))
            {
                continue;
            }

            MultiEnchantmentApi.Register(existenceType)
                .Stack(StackBehavior.ExistenceStack, StatusAggregation.AnyInstanceCountsAsOne)
                .Commit();
        }

        // === Special cases ===================================================================

        // Instinct's merge delta lowers the card's energy cost once per added amount. v1 lived
        // in MultiEnchantmentStackSupport.ApplyMergedAmountDelta as a hardcoded `is Instinct`
        // branch; v2 expresses it as an explicit OnMergedDelta.
        MultiEnchantmentApi.Register<Instinct>()
            .Stack(StackBehavior.MergeAmount, StatusAggregation.SharedAcrossStack)
            .OnMergedDelta((Instinct e, int added) =>
            {
                for (int i = 0; i < added; i++)
                {
                    e.Card.EnergyCost.UpgradeBy(-1);
                }
            })
            .Commit();

        // Glam / Spiral keep their {Times} dynamic var in sync with merged Amount via two pieces:
        //   1. OnMergedRefresh: re-run RecalculateValues and seed DynamicVars["Times"].BaseValue =
        //      Amount, so vanilla code paths that read BaseValue directly stay correct.
        //   2. ModifyDynamicVar("Times", c => c) trip-wire registration: the fold is a no-op, but
        //      it registers "Times" as a contributed key. That flips HasContributionsFor("Times")
        //      to true, which is what causes the base DynamicVar.UpdateCardPreview postfix to fire
        //      and copy BaseValue → PreviewValue through the (no-op) pipeline. Without this
        //      trip-wire a lone-Glam card's {Times} wouldn't propagate to PreviewValue on every
        //      refresh. Any future cross-mod contributor that targets "Times" composes after this
        //      no-op naturally.
        MultiEnchantmentApi.Register<Glam>()
            .Stack(StackBehavior.MergeAmount, StatusAggregation.SharedAcrossStack)
            .OnMergedRefresh((Glam e) =>
            {
                e.RecalculateValues();
                e.DynamicVars["Times"].BaseValue = e.Amount;
                e.Card?.DynamicVars.RecalculateForUpgradeOrEnchant();
            })
            .ModifyDynamicVar("Times", (snapshot, current) => current)
            .Commit();

        MultiEnchantmentApi.Register<Spiral>()
            .Stack(StackBehavior.MergeAmount, StatusAggregation.SharedAcrossStack)
            .OnMergedRefresh((Spiral e) =>
            {
                e.RecalculateValues();
                e.DynamicVars["Times"].BaseValue = e.Amount;
                e.Card?.DynamicVars.RecalculateForUpgradeOrEnchant();
            })
            .ModifyDynamicVar("Times", (snapshot, current) => current)
            .Commit();

        // === Keyword sources =================================================================
        // Each registration below mirrors a case in the deleted v1
        // GetBuiltInKeywordSourceAmount switch.

        MultiEnchantmentApi.Register<Goopy>()
            .Stack(StackBehavior.DuplicateInstance, StatusAggregation.PerInstanceOwned)
            .TrackKeyword(CardKeyword.Exhaust, snapshot => snapshot.ActiveInstanceCount)
            .Commit();

        MultiEnchantmentApi.Register<SoulsPower>()
            .Stack(StackBehavior.MergeAmount, StatusAggregation.SharedAcrossStack)
            .TrackKeyword(CardKeyword.Exhaust, snapshot => -snapshot.ActiveTotalAmount)
            .Commit();

        MultiEnchantmentApi.Register<Steady>()
            .Stack(StackBehavior.ExistenceStack, StatusAggregation.AnyInstanceCountsAsOne)
            .TrackKeyword(CardKeyword.Retain, snapshot => snapshot.ActiveInstanceCount > 0 ? 1 : 0)
            .Commit();

        MultiEnchantmentApi.Register<RoyallyApproved>()
            .Stack(StackBehavior.ExistenceStack, StatusAggregation.AnyInstanceCountsAsOne)
            .TrackKeyword(CardKeyword.Retain, snapshot => snapshot.ActiveInstanceCount > 0 ? 1 : 0)
            .TrackKeyword(CardKeyword.Innate, snapshot => snapshot.ActiveInstanceCount > 0 ? 1 : 0)
            .Commit();

        MultiEnchantmentApi.Register<TezcatarasEmber>()
            .Stack(StackBehavior.ExistenceStack, StatusAggregation.AnyInstanceCountsAsOne)
            .TrackKeyword(CardKeyword.Eternal, snapshot => snapshot.ActiveInstanceCount > 0 ? 1 : 0)
            .Commit();
    }
}
