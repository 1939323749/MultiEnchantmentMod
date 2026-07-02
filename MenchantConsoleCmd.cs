using System;
using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.DevConsole.ConsoleCommands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Exceptions;
using MultiEnchantmentMod.Api;

namespace MultiEnchantmentMod;

/// <summary>
/// Same args as vanilla's <c>enchant</c>, but forces <see cref="EnchantmentScope.Permanent"/> so the
/// enchantment is mirrored onto the card's <see cref="CardModel.DeckVersion"/> and survives past this
/// combat. Vanilla <c>enchant</c> (and plain <c>CardCmd.Enchant</c>) only touches the in-combat clone.
/// </summary>
public class MenchantConsoleCmd : AbstractConsoleCmd
{
    public override string CmdName => "menchant";

    public override string Args => "<id:string> [amount:int] [hand-index:int]";

    public override string Description =>
        "Enchants a card in hand and force-syncs it to the deck version, so it persists after combat ends.";

    public override bool IsNetworked => true;

    public override CmdResult Process(Player? issuingPlayer, string[] args)
    {
        if (args.Length == 0)
        {
            return new CmdResult(success: false, "Must specify an enchantment ID!");
        }
        if (!CombatManager.Instance.IsInProgress)
        {
            return new CmdResult(success: false, "Combat is not currently in progress!");
        }

        ModelId modelId = new ModelId(ModelId.SlugifyCategory<EnchantmentModel>(), args[0].ToUpperInvariant());
        EnchantmentModel enchantmentModel;
        try
        {
            enchantmentModel = ModelDb.GetById<EnchantmentModel>(modelId).ToMutable();
        }
        catch (ModelNotFoundException)
        {
            return new CmdResult(success: false, "Enchantment '" + modelId.Entry + "' not found");
        }

        int amount = 1;
        int handIndex = 0;
        if (args.Length > 1 && !int.TryParse(args[1], out amount))
        {
            return new CmdResult(success: false, $"Arg 2 must be the enchantment amount (int), got '{args[1]}'.");
        }
        if (args.Length > 2 && !int.TryParse(args[2], out handIndex))
        {
            return new CmdResult(success: false, $"Arg 3 must be the hand index (int), got '{args[2]}'.");
        }

        CardPile pile = PileType.Hand.GetPile(issuingPlayer!);
        IReadOnlyList<CardModel> cards = pile.Cards;
        int count = cards.Count;
        if (handIndex < 0 || handIndex >= count)
        {
            return new CmdResult(success: false, $"Invalid hand index {handIndex}. Valid range: 0-{count - 1}.");
        }

        CardModel cardModel = pile.Cards[handIndex];

        // MultiEnchantmentApi.Enchant (unlike vanilla CardCmd.Enchant, which the Harmony prefix on
        // it guards) throws InvalidOperationException instead of no-opping when the card already
        // carries a non-stackable instance of this type. Mirror that guard here so re-running this
        // networked (lockstep) command never throws.
        EnchantmentModel? existing = MultiEnchantmentSupport.GetEnchantment(cardModel, enchantmentModel.GetType());
        if (existing != null && !MultiEnchantmentStackSupport.CanStackOnto(cardModel, enchantmentModel.GetType()))
        {
            return new CmdResult(success: true,
                $"Card {cardModel.Title} already has {existing.Title.GetFormattedText()}; re-apply is a no-op (not stackable).");
        }

        EnchantmentModel? applied;
        try
        {
            applied = MultiEnchantmentApi.Enchant(cardModel, enchantmentModel, amount, EnchantmentScope.Permanent);
        }
        catch (InvalidOperationException ex)
        {
            return new CmdResult(success: false,
                $"Failed to enchant card {cardModel.Title} with {enchantmentModel.Title.GetFormattedText()}: {ex.Message}");
        }

        if (applied == null)
        {
            return new CmdResult(success: false,
                $"Failed to enchant card {cardModel.Title} with {enchantmentModel.Title.GetFormattedText()}.");
        }

        return new CmdResult(success: true,
            $"Enchanted card {cardModel.Title} with {amount} {enchantmentModel.Title.GetFormattedText()} and synced to deck.");
    }

    public override CompletionResult GetArgumentCompletions(Player? player, string[] args)
    {
        if (args.Length <= 1)
        {
            List<string> candidates = ModelDb.DebugEnchantments.Select((EnchantmentModel e) => e.Id.Entry).ToList();
            return CompleteArgument(candidates, Array.Empty<string>(), args.FirstOrDefault() ?? "");
        }

        return new CompletionResult
        {
            Type = CompletionType.Argument,
            ArgumentContext = CmdName
        };
    }
}
