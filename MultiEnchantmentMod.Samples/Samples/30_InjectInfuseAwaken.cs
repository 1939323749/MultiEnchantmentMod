using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MultiEnchantmentMod.Api;

namespace MultiEnchantmentMod.Samples;

// Tier C — downstream "inject / infuse / awaken / ignite / re-inject" verbs.
//
// Goal: show how a downstream enchantment-themed card set wires its core verbs onto the public
// API. Each verb is a one-liner; this sample is the reference for which API to call.
//
// NOTE: "inject / infuse / awaken" are a *downstream* pack's vocabulary — they are simply names for
// three scopes, and the API does not model them. A real pack would wrap its own verb layer (and, if
// it wants per-verb reactions, its own event) on top of EnchantAsync. The mod only ships the
// generic EnchantAsync + AfterCardEnchanted (Card / AppliedEnchantment / ScopeOverride / CascadeDepth).
//
//   注入 Inject  — enchant valid for THIS TURN          -> Enchant/EnchantAsync(..., UntilTurnEnds)
//   附灵 Infuse  — enchant valid for THIS COMBAT         -> Enchant(..., UntilCombatEnds)
//   觉醒 Awaken  — enchant valid for THIS RUN (forever)  -> Enchant(..., Permanent)
//   激发 Ignite  — "when enchanted, play this card"      -> AfterCardEnchanted + EnchantAsync
//   再次注入     — re-inject the enchantment last applied THIS TURN onto another card
//                                                        -> GetMostRecentlyAppliedEnchantmentThisTurn + CopyEnchantment
//   旧神咏唱     — "whenever you enchant, also enchant a random hand card" (cascade)
//                                                        -> AfterCardEnchanted + CascadeDepth guard
//   灵纹转写     — move all enchantments between cards    -> GetSiblings + MoveEnchantment
//   条件判定     — "if this card has any enchantment"     -> HasAnyEnchantment / GetEnchantmentCount
//
// The sample issues no real game commands so it stays safe in every test environment. Replace the
// marked comments with CardCmd/CardSelectCmd calls in a real downstream mod.

public sealed class SampleInjectedSpark : EnchantmentModel
{
    public override bool ShowAmount => true;
    public override bool HasExtraCardText => true;
}

public static class SampleInjectInfuseAwaken
{
    // Cards flagged with the 激发 (Ignite) keyword: "when this card is enchanted, play it".
    // A real mod would derive this from a card keyword instead of a side table; a
    // ConditionalWeakTable keeps the flag from leaking card instances.
    private static readonly ConditionalWeakTable<CardModel, object> IgniteCards = new();
    private static IDisposable? _igniteSubscription;
    private static IDisposable? _echoSubscription;

    /// <summary>注入 — inject an enchantment that expires at the end of the current turn.</summary>
    public static EnchantmentModel? Inject(CardModel card) =>
        MultiEnchantmentApi.Enchant(card, new SampleInjectedSpark(), 1, EnchantmentScope.UntilTurnEnds);

    /// <summary>附灵 — infuse an enchantment that lasts for the rest of this combat.</summary>
    public static EnchantmentModel? Infuse(CardModel card) =>
        MultiEnchantmentApi.Enchant(card, new SampleInjectedSpark(), 1, EnchantmentScope.UntilCombatEnds);

    /// <summary>觉醒 — awaken: a permanent (run-long) enchantment.</summary>
    public static EnchantmentModel? Awaken(CardModel card) =>
        MultiEnchantmentApi.Enchant(card, new SampleInjectedSpark(), 1, EnchantmentScope.Permanent);

    /// <summary>
    /// Inject through the async path so the card-level <see cref="MultiEnchantmentApi.AfterCardEnchanted"/>
    /// notification fires. This is the path 激发 (Ignite) needs: the sync <c>Enchant</c> overload
    /// would NOT raise that notification (and could not safely auto-play).
    /// </summary>
    public static Task<EnchantmentModel?> InjectAndMaybeIgnite(PlayerChoiceContext choiceContext, CardModel card) =>
        MultiEnchantmentApi.EnchantAsync(
            choiceContext,
            card,
            new SampleInjectedSpark(),
            1,
            EnchantmentScope.UntilTurnEnds);

    /// <summary>
    /// 再次注入 — copy the enchantment most recently injected onto <paramref name="source"/> THIS TURN
    /// onto <paramref name="target"/>. Returns <c>null</c> when nothing was injected this turn, so the
    /// card text "再次注入到1张手牌" naturally no-ops on an empty turn. <c>CopyEnchantment</c> clones the
    /// live instance (preserving Amount/Props) and resets the copy's runtime scope counters.
    /// </summary>
    public static EnchantmentModel? ReinjectLastIntoHandCard(CardModel source, CardModel target)
    {
        EnchantmentModel? lastThisTurn = MultiEnchantmentApi.GetMostRecentlyAppliedEnchantmentThisTurn(source);
        if (lastThisTurn == null)
        {
            return null;
        }

        return MultiEnchantmentApi.CopyEnchantment(target, lastThisTurn);
    }

    /// <summary>Flags <paramref name="card"/> as an 激发 (Ignite) card for the marker hook below.</summary>
    public static void MarkIgnite(CardModel card) => IgniteCards.AddOrUpdate(card, new object());

    /// <summary>
    /// Installs the card-level 激发 marker hook once. The handler fires only on the async enchant
    /// paths (<c>EnchantAsync</c> / <c>CopyEnchantmentAsync</c>), and the applied enchantment is
    /// already live on the card, so it is safe to auto-play here.
    /// </summary>
    public static void InstallIgniteMarker()
    {
        _igniteSubscription ??= MultiEnchantmentApi.AfterCardEnchanted(static context =>
        {
            if (context.ChoiceContext == null || !IgniteCards.TryGetValue(context.Card, out _))
            {
                return Task.CompletedTask;
            }

            // 激发: the card just gained an enchantment and is live on the board. A real mod would
            // auto-play it here, e.g.:
            //   return CardCmd.Play(context.ChoiceContext, context.Card, ...);
            SampleRegistration.Logger.Info(
                $"[SampleIgnite] {context.Card.Id} ignited by {context.AppliedEnchantment.Id}; would auto-play now.");
            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// 旧神咏唱 — "whenever you enchant a card, also apply the same enchantment to a random hand
    /// card." The handler itself enchants, which re-enters <c>AfterCardEnchanted</c>; the
    /// <see cref="AfterCardEnchantedContext.CascadeDepth"/> guard stops that from recursing forever.
    /// </summary>
    public static void InstallEchoOfOldGods(IReadOnlyList<CardModel> hand)
    {
        _echoSubscription ??= MultiEnchantmentApi.AfterCardEnchanted(context =>
        {
            // Only react to top-level enchants — never to the cascade we ourselves trigger below.
            if (context.CascadeDepth > 0)
            {
                return Task.CompletedTask;
            }

            CardModel? other = hand.FirstOrDefault(c => !ReferenceEquals(c, context.Card));
            if (other == null)
            {
                return Task.CompletedTask;
            }

            // Spreading the same enchantment to another card. Because this runs inside the handler,
            // the nested application reports CascadeDepth == 1 and is ignored by the guard above.
            return MultiEnchantmentApi.CopyEnchantmentAsync(context.ChoiceContext, other, context.AppliedEnchantment);
        });
    }

    /// <summary>
    /// 灵纹转写 — move every enchantment from <paramref name="source"/> to <paramref name="target"/>,
    /// preserving each one's remaining lifetime (unlike a plain copy, which restarts scope counters).
    /// </summary>
    public static void MoveAllEnchantments(CardModel source, CardModel target)
    {
        foreach (EnchantmentModel enchantment in MultiEnchantmentApi.GetSiblings(source))
        {
            MultiEnchantmentApi.MoveEnchantment(source, target, enchantment);
        }
    }

    /// <summary>
    /// 条件判定 helpers: "若这张牌有附魔" and "手牌中带附魔的牌数量" / "所有手牌都有附魔".
    /// </summary>
    public static bool CardHasEnchantment(CardModel card) => MultiEnchantmentApi.HasAnyEnchantment(card);

    public static int CountEnchantedCards(IEnumerable<CardModel> cards) =>
        cards.Count(MultiEnchantmentApi.HasAnyEnchantment);

    public static bool AllCardsEnchanted(IReadOnlyCollection<CardModel> hand) =>
        hand.Count > 0 && hand.All(MultiEnchantmentApi.HasAnyEnchantment);

    public static void Uninstall()
    {
        _igniteSubscription?.Dispose();
        _igniteSubscription = null;
        _echoSubscription?.Dispose();
        _echoSubscription = null;
    }
}
