using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Enchantments;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Enchantments;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Runs.History;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Runs;
using MegaCrit.Sts2.Core.ValueProps;
using MultiEnchantmentMod.Api;
using MultiEnchantmentMod.Api.Internal;

namespace MultiEnchantmentMod;

internal static partial class MultiEnchantmentSupport
{
    internal const string SavePropertyName = nameof(MultiEnchantmentSaveCarrier.MultiEnchantmentData);
    internal const string OrderSavePropertyName = nameof(MultiEnchantmentSaveCarrier.MultiEnchantmentOrderData);
    private const float ExtraSlotYOffset = 44f;
    private const string EnchantVfxViewportBadgePrefix = "MultiEnchantVfxViewportBadge";
    private const string EnchantVfxStaticBadgePrefix = "MultiEnchantVfxStaticBadge";
    private const string ExtraEnchantmentTabPrefix = "MultiEnchantmentTab";
    private const string EnchantVfxSparklesBasePositionMeta = "_multi_enchant_sparkles_base_position";
    private const string EnchantVfxOverrideRestorePositionMeta = "_multi_enchant_vfx_override_restore_position";
    private const string EnchantVfxOverrideRestoreSizeMeta = "_multi_enchant_vfx_override_restore_size";
    private const string EnchantVfxOverrideRestoreActiveMeta = "_multi_enchant_vfx_override_restore_active";

    private static readonly ConditionalWeakTable<CardModel, CardEnchantmentState> CardStates = new();
    private static readonly ConditionalWeakTable<NCard, CardUiState> CardUiStates = new();
    private static readonly ConditionalWeakTable<Node, EnchantmentVfxSnapshotState> PendingEnchantVfxSnapshots = new();

    /// <summary>
    /// Reentrancy guard for <see cref="ApplyDynamicVarEnchantments"/>. Tracks (card, varKey) pairs
    /// that are currently being evaluated. If a contribution callback recursively queries the same
    /// card+varKey (e.g. enchantment A reads enchantment B's damage contribution, which itself reads A),
    /// we short-circuit and return the base value to prevent stack overflow.
    /// </summary>
    [ThreadStatic]
    private static HashSet<(CardModel Card, string VarKey)>? _activeDynamicVarKeys;

    private static readonly FieldInfo? CardEnchantmentChangedField =
        AccessTools.Field(typeof(CardModel), nameof(CardModel.EnchantmentChanged));
    private static readonly PropertyInfo? CardCurrentTargetProperty =
        AccessTools.Property(typeof(CardModel), nameof(CardModel.CurrentTarget));
    private static readonly FieldInfo? CardTemporaryStarCostsField =
        AccessTools.Field(typeof(CardModel), "_temporaryStarCosts");
    private static readonly FieldInfo? CardPlayedField =
        AccessTools.Field(typeof(CardModel), nameof(CardModel.Played));
    private static readonly FieldInfo? CardStarCostChangedField =
        AccessTools.Field(typeof(CardModel), nameof(CardModel.StarCostChanged));
    private static readonly FieldInfo? NCardForceUnpoweredPreviewField =
        AccessTools.Field(typeof(NCard), "_forceUnpoweredPreview");
    private static readonly FieldInfo? NCardPreviewTargetField =
        AccessTools.Field(typeof(NCard), "_previewTarget");
    private static readonly FieldInfo? NCardDefaultEnchantmentPositionField =
        AccessTools.Field(typeof(NCard), "_defaultEnchantmentPosition");
    private static readonly FieldInfo? NCardEnchantmentTabField =
        AccessTools.Field(typeof(NCard), "_enchantmentTab");
    private static readonly PropertyInfo? EnchantedValueProperty =
        typeof(DynamicVar).GetProperty(
            nameof(DynamicVar.EnchantedValue),
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
    private static readonly MethodInfo? CardModelOnPlayMethod =
        AccessTools.Method(typeof(CardModel), "OnPlay");
    private static readonly MethodInfo? CardModelGetResultPileTypeMethod =
        AccessTools.Method(typeof(CardModel), "GetResultPileTypeForCardPlay");
    private static readonly MethodInfo? CardModelPlayPowerCardFlyVfxMethod =
        AccessTools.Method(typeof(CardModel), "PlayPowerCardFlyVfx");
    private static readonly MethodInfo? CardModelClearEnchantmentInternalMethod =
        AccessTools.Method(typeof(CardModel), "ClearEnchantmentInternal");
    private static readonly FieldInfo? SavedPropertiesNetIdMapField =
        AccessTools.Field(typeof(SavedPropertiesTypeCache), "_netIdToPropertyNameMap");
    private static readonly PropertyInfo? SavedPropertiesNetIdBitSizeProperty =
        AccessTools.Property(typeof(SavedPropertiesTypeCache), nameof(SavedPropertiesTypeCache.NetIdBitSize));

    private static readonly StringName ShaderH = new("h");
    private static readonly StringName ShaderS = new("s");
    private static readonly StringName ShaderV = new("v");

    public static void Initialize()
    {
        ValidateReflectionTargets();
        SavedPropertiesTypeCache.InjectTypeIntoCache(typeof(MultiEnchantmentSaveCarrier));
        RefreshSavedPropertiesNetIdBitSize();
    }

    private static void ValidateReflectionTargets()
    {
        List<string> missing = new();
        AddMissing(missing, CardEnchantmentChangedField, "CardModel.EnchantmentChanged");
        AddMissing(missing, CardCurrentTargetProperty, "CardModel.CurrentTarget");
        AddMissing(missing, CardTemporaryStarCostsField, "CardModel._temporaryStarCosts");
        AddMissing(missing, CardPlayedField, "CardModel.Played");
        AddMissing(missing, CardStarCostChangedField, "CardModel.StarCostChanged");
        AddMissing(missing, NCardForceUnpoweredPreviewField, "NCard._forceUnpoweredPreview");
        AddMissing(missing, NCardPreviewTargetField, "NCard._previewTarget");
        AddMissing(missing, NCardDefaultEnchantmentPositionField, "NCard._defaultEnchantmentPosition");
        AddMissing(missing, NCardEnchantmentTabField, "NCard._enchantmentTab");
        AddMissing(missing, EnchantedValueProperty, "DynamicVar.EnchantedValue");
        AddMissing(missing, CardModelOnPlayMethod, "CardModel.OnPlay");
        AddMissing(missing, CardModelGetResultPileTypeMethod, "CardModel.GetResultPileTypeForCardPlay");
        AddMissing(missing, CardModelPlayPowerCardFlyVfxMethod, "CardModel.PlayPowerCardFlyVfx");
        AddMissing(missing, CardModelClearEnchantmentInternalMethod, "CardModel.ClearEnchantmentInternal");
        AddMissing(missing, SavedPropertiesNetIdMapField, "SavedPropertiesTypeCache._netIdToPropertyNameMap");
        AddMissing(missing, SavedPropertiesNetIdBitSizeProperty, "SavedPropertiesTypeCache.NetIdBitSize");

        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                "MultiEnchantmentMod could not resolve required game reflection targets: " +
                string.Join(", ", missing));
        }
    }

    private static void AddMissing(List<string> missing, MemberInfo? member, string name)
    {
        if (member == null)
        {
            missing.Add(name);
        }
    }

    internal static void RefreshSavedPropertiesNetIdBitSize()
    {
        if (SavedPropertiesNetIdMapField?.GetValue(null) is not List<string> propertyNames ||
            SavedPropertiesNetIdBitSizeProperty == null)
        {
            throw new InvalidOperationException("Failed to access SavedPropertiesTypeCache network ID metadata.");
        }

        int count = Math.Max(1, propertyNames.Count);
        int bitSize = Mathf.CeilToInt(Math.Log2(count));
        SavedPropertiesNetIdBitSizeProperty.SetValue(null, bitSize);
    }

    public static IEnumerable<EnchantmentModel> GetEnchantments(CardModel? card)
    {
        if (card == null)
        {
            yield break;
        }

        if (card.Enchantment != null)
        {
            yield return card.Enchantment;
        }

        foreach (EnchantmentModel enchantment in GetAdditionalEnchantments(card))
        {
            yield return enchantment;
        }
    }

    internal static IEnumerable<EnchantmentVisualState> GetOrderedVisualStates(CardModel? card)
    {
        foreach (OrderedVisualEntry entry in GetOrderedVisualEntries(card))
        {
            yield return entry.VisualState;
        }
    }

    internal static bool TryGetFirstVisualState(CardModel? card, [NotNullWhen(true)] out EnchantmentVisualState? visualState)
    {
        visualState = GetOrderedVisualStates(card).FirstOrDefault();
        return visualState != null;
    }

    public static IReadOnlyList<EnchantmentModel> GetAdditionalEnchantments(CardModel? card)
    {
        if (card == null)
        {
            return Array.Empty<EnchantmentModel>();
        }

        return CardStates.TryGetValue(card, out CardEnchantmentState? state)
            ? state.ExtraEnchantments
            : Array.Empty<EnchantmentModel>();
    }

    public static bool HasEnchantment<T>(CardModel? card) where T : EnchantmentModel
    {
        return GetEnchantments(card).Any(static enchantment => enchantment is T);
    }

    public static bool ShouldOfferCloneRestSiteOption(Player player)
    {
        return player.Deck.Cards.Any(HasEnchantment<Clone>);
    }

    public static EnchantmentModel? GetEnchantment(CardModel? card, Type enchantmentType)
    {
        return GetEnchantments(card).FirstOrDefault(enchantment => enchantment.GetType() == enchantmentType);
    }

    public static bool ShouldGlowGold(CardModel card)
    {
        return GetAdditionalEnchantments(card).Any(static enchantment => enchantment.ShouldGlowGold);
    }

    public static bool ShouldGlowRed(CardModel card)
    {
        return GetAdditionalEnchantments(card).Any(static enchantment => enchantment.ShouldGlowRed);
    }

    public static bool ShouldStartAtBottomOfDrawPile(CardModel card)
    {
        return GetEnchantments(card).Any(static enchantment => enchantment.ShouldStartAtBottomOfDrawPile);
    }

    public static bool HasAnyEnchantments(CardModel? card)
    {
        return card?.Enchantment != null || GetAdditionalEnchantments(card).Count > 0;
    }

    /// <summary>
    /// Returns true when the mod's per-card multi-enchantment logic can produce a different
    /// result than vanilla. Used by Harmony prefixes/postfixes to short-circuit back to the
    /// vanilla implementation when the mod has nothing to contribute, so that other mods'
    /// patches on the same method can take effect normally.
    /// </summary>
    public static bool RequiresMultiEnchantmentLogic(CardModel? card)
    {
        if (card == null)
        {
            return false;
        }

        if (GetAdditionalEnchantments(card).Count > 0)
        {
            return true;
        }

        // Primary enchantment with multi-slice merged stack metadata needs the per-slice path.
        EnchantmentModel? primary = card.Enchantment;
        if (primary != null && RequiresSinglePrimaryMultiEnchantmentLogic(card, primary))
        {
            return true;
        }

        // Single-enchantment fast path opt-out: if any enchantment on the card registered a
        // ModifyDynamicVar contribution, the UpdateCardPreview prefix must run so the new
        // pipeline gets to fold contributions on top of vanilla's value. Without this check, a
        // card carrying only a SamplePlusFive (which contributes to "damage"/"block") would
        // bypass our prefix entirely and the contribution would never fire.
        foreach (EnchantmentModel enchantment in GetEnchantments(card))
        {
            if (EnchantmentRegistry.HasAnyDynamicVarContributions(enchantment.GetType()))
            {
                return true;
            }
        }

        return false;
    }

    public static bool RequiresOnPlayWrapperMultiEnchantmentLogic(CardModel? card)
    {
        if (card == null)
        {
            return false;
        }

        if (GetAdditionalEnchantments(card).Count > 0)
        {
            return true;
        }

        EnchantmentModel? primary = card.Enchantment;
        return primary != null && RequiresSinglePrimaryMultiEnchantmentLogic(card, primary);
    }

    private static bool RequiresSinglePrimaryMultiEnchantmentLogic(CardModel card, EnchantmentModel primary)
    {
        if (MultiEnchantmentStackSupport.TryGetMergedStackAmounts(primary, out int[] slices) &&
            slices.Length > 1)
        {
            return true;
        }

        if (EnchantmentRegistry.HasLifecycleHandlers(primary.GetType()))
        {
            return true;
        }

        return MultiEnchantmentStackApi.GetHookExecutionCount(primary, EnchantmentHookKind.OnPlay) != 1;
    }

    internal static void TriggerEnchantmentChanged(CardModel card)
    {
        foreach (NCard cardNode in CardUiStates.Select(static entry => entry.Key).Where(node => node.Model == card).ToList())
        {
            if (CardUiStates.TryGetValue(cardNode, out CardUiState? state))
            {
                state.LastVisualStateFingerprint = null;
                state.LastSyncCardModel = null;
            }
        }

        if (CardEnchantmentChangedField?.GetValue(card) is Action action)
        {
            action();
        }
    }

    /// <summary>
    /// Refreshes all derived state (DynamicVars, keywords, UI signals) for
    /// <paramref name="enchantment"/>'s owning card after its <see cref="EnchantmentModel.Props"/>
    /// have been mutated by user code. Called from
    /// <see cref="MultiEnchantmentApi.NotifyPropsChanged"/>.
    /// </summary>
    internal static void RefreshDerivedStateFor(EnchantmentModel enchantment)
    {
        CardModel? card = enchantment.Card;
        if (card == null)
        {
            return;
        }

        MultiEnchantmentScopeSupport.RefreshActiveStatuses(card);
        card.DynamicVars.RecalculateForUpgradeOrEnchant();
        MultiEnchantmentStackSupport.RefreshDerivedState(card);
        TriggerEnchantmentChanged(card);
    }

    private static void RememberLastAppliedEnchantment(CardModel card, EnchantmentModel enchantment)
    {
        CardStates.GetOrCreateValue(card).LastAppliedEnchantment = enchantment;
    }

    public static Task OnPlayForMultiEnchantmentPatch(CardModel card, PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        if (CardModelOnPlayMethod?.Invoke(card, new object[] { choiceContext, cardPlay }) is Task task)
        {
            return task;
        }

        // Fallback when OnPlay is unavailable (renamed/removed in newer game versions):
        // the card's internal OnPlay logic has been moved elsewhere in the game; skip safely.
        return Task.CompletedTask;
    }

    public static PileType GetResultPileTypeForMultiEnchantmentPatch(CardModel card)
    {
        // Dispatch to vanilla CardModel.GetResultPileTypeForCardPlay via reflection so subclass
        // overrides (e.g., ParticleWall returning to Hand) AND the ExhaustOnNextPlay side effect
        // both match base-game behavior exactly.
        if (CardModelGetResultPileTypeMethod?.Invoke(card, null) is PileType pileType)
        {
            return pileType;
        }

        // Fallback when GetResultPileTypeForCardPlay is unavailable (renamed/removed in newer
        // game versions): replicate the base-game branches as best we can. NOTE: this path
        // cannot clear ExhaustOnNextPlay because the field is not reachable from here.
        if (card.IsDupe || card.Type == CardType.Power)
            return PileType.None;
        if (card.Keywords.Contains(CardKeyword.Exhaust))
            return PileType.Exhaust;
        return PileType.Discard;
    }

    public static Task PlayPowerCardFlyVfxForMultiEnchantmentPatch(CardModel card)
    {
        if (CardModelPlayPowerCardFlyVfxMethod?.Invoke(card, null) is Task task)
        {
            return task;
        }

        // Fallback when PlayPowerCardFlyVfx is unavailable (renamed/removed in newer game versions):
        // the VFX is cosmetic; skip safely.
        return Task.CompletedTask;
    }

    public static void InvokeStarCostChangedForMultiEnchantmentPatch(CardModel card)
    {
        if (CardStarCostChangedField?.GetValue(card) is Action action)
        {
            action();
        }
    }

    public static void InvokePlayedForMultiEnchantmentPatch(CardModel card)
    {
        if (CardPlayedField?.GetValue(card) is Action action)
        {
            action();
        }
    }

    public static void SetCurrentTargetForMultiEnchantmentPatch(CardModel card, Creature? target)
    {
        if (CardCurrentTargetProperty == null)
        {
            return;
        }

        card.AssertMutable();
        CardCurrentTargetProperty.SetValue(card, target);
    }

    private static bool ClearTemporaryStarCostsOnPlay(CardModel card)
    {
        if (CardTemporaryStarCostsField?.GetValue(card) is not System.Collections.IList temporaryStarCosts)
        {
            return false;
        }

        List<object> toRemove = new();
        foreach (object item in temporaryStarCosts)
        {
            PropertyInfo? clearsWhenPlayedProperty = item.GetType().GetProperty("ClearsWhenCardIsPlayed");
            if (clearsWhenPlayedProperty?.GetValue(item) is bool clears && clears)
            {
                toRemove.Add(item);
            }
        }

        foreach (object item in toRemove)
        {
            temporaryStarCosts.Remove(item);
        }

        return toRemove.Count > 0;
    }

}
