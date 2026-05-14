using MultiEnchantmentMod.Api;
using MegaCrit.Sts2.Core.Models;

namespace MultiEnchantmentMod.Samples;

// Tier A — attribute-only, DuplicateInstance flavor.
//
// Goal: show how to keep one independent instance per application.
//
// Use this when each application carries its own evolving runtime state (e.g. Goopy's Amount
// grows after every play). The mod creates a fresh EnchantmentModel each time, and
// StatusAggregation.PerInstanceOwned routes each visual slice to its own instance's Status.
[Enchantment(Stack = StackBehavior.DuplicateInstance, Status = StatusAggregation.PerInstanceOwned)]
public sealed class SampleStateAccumulator : EnchantmentModel
{
    // The base EnchantmentModel exposes Amount + Status; subclasses typically use them to
    // express stack-specific runtime growth. This sample leaves OnPlay etc. unimplemented to
    // keep the focus on the registration declaration.
}
