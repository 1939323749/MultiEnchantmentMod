using System;
using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.DevConsole.ConsoleCommands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MultiEnchantmentMod.Api;

namespace MultiEnchantmentMod;

public class DisenchantConsoleCmd : AbstractConsoleCmd
{
    public override string CmdName => "disenchant";

    public override string Args => "[enchantment-id:string] [hand-index:int]";

    public override string Description =>
        "Removes enchantment(s) from a card in hand. No args = clear all from card 0. Specify id to remove one type.";

    public override bool IsNetworked => true;

    public override CmdResult Process(Player? issuingPlayer, string[] args)
    {
        if (!CombatManager.Instance.IsInProgress)
        {
            return new CmdResult(success: false, "Combat is not currently in progress!");
        }

        CardPile hand = PileType.Hand.GetPile(issuingPlayer!);
        IReadOnlyList<CardModel> cards = hand.Cards;
        if (cards.Count == 0)
        {
            return new CmdResult(success: false, "Hand is empty!");
        }

        string? enchantmentFilter = null;
        int handIndex = 0;

        if (args.Length >= 1)
        {
            if (int.TryParse(args[0], out int idx))
            {
                handIndex = idx;
            }
            else
            {
                enchantmentFilter = args[0].ToUpperInvariant();
                if (args.Length >= 2 && !int.TryParse(args[1], out handIndex))
                {
                    return new CmdResult(success: false, $"Arg 2 must be hand index (int), got '{args[1]}'.");
                }
            }
        }

        if (handIndex < 0 || handIndex >= cards.Count)
        {
            return new CmdResult(success: false, $"Invalid hand index {handIndex}. Valid range: 0-{cards.Count - 1}.");
        }

        CardModel card = cards[handIndex];
        List<EnchantmentModel> enchantments = MultiEnchantmentApi.GetSiblings(card).ToList();

        if (enchantments.Count == 0)
        {
            return new CmdResult(success: false, $"Card '{card.Title}' has no enchantments.");
        }

        if (enchantmentFilter == null)
        {
            int removed = 0;
            foreach (EnchantmentModel e in enchantments)
            {
                if (MultiEnchantmentApi.RemoveEnchantment(card, e, RemovalReason.Manual))
                {
                    removed++;
                }
            }

            return new CmdResult(success: removed > 0,
                removed == enchantments.Count
                    ? $"Removed all {removed} enchantment(s) from '{card.Title}'."
                    : $"Removed {removed}/{enchantments.Count} enchantment(s) from '{card.Title}'; remaining removals were vetoed or failed.");
        }

        List<EnchantmentModel> matching = enchantments
            .Where(e => e.Id.Entry.Equals(enchantmentFilter, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matching.Count == 0)
        {
            string available = string.Join(", ", enchantments.Select(e => e.Id.Entry));
            return new CmdResult(success: false,
                $"No enchantment '{enchantmentFilter}' on '{card.Title}'. Has: {available}");
        }

        int matchingRemoved = 0;
        foreach (EnchantmentModel e in matching)
        {
            if (MultiEnchantmentApi.RemoveEnchantment(card, e, RemovalReason.Manual))
            {
                matchingRemoved++;
            }
        }

        return new CmdResult(success: matchingRemoved > 0,
            matchingRemoved == matching.Count
                ? $"Removed {matchingRemoved} '{enchantmentFilter}' enchantment(s) from '{card.Title}'."
                : $"Removed {matchingRemoved}/{matching.Count} '{enchantmentFilter}' enchantment(s) from '{card.Title}'; remaining removals were vetoed or failed.");
    }

    public override CompletionResult GetArgumentCompletions(Player? player, string[] args)
    {
        if (args.Length <= 1)
        {
            List<string> candidates = ModelDb.DebugEnchantments
                .Select(e => e.Id.Entry)
                .ToList();
            return CompleteArgument(candidates, Array.Empty<string>(), args.FirstOrDefault() ?? "");
        }

        return new CompletionResult
        {
            Type = CompletionType.Argument,
            ArgumentContext = CmdName
        };
    }
}
