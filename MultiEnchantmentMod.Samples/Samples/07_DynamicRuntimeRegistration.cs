using System;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MultiEnchantmentMod.Api;

namespace MultiEnchantmentMod.Samples;

// Tier C — fluent builder, runtime registration.
//
// Goal: show how to register an enchantment without an attribute or companion class. Use
// this when you can't (or don't want to) annotate the model type — typical cases:
//   * The model class is generated at runtime.
//   * Different conditions (mod settings, save data, ...) need different stack contracts.
//   * Tests / hot-reload that need to install and revert registrations on demand.
//
// Note: the fluent builder does not require the enchantment to be a sample-defined model. A
// caller could in theory register a non-sample mod's enchantment too, as long as the type is
// reachable at runtime — but this is poor manners; downstream code should own its own
// registrations.

public sealed class SampleDynamicEnchantment : EnchantmentModel
{
    public override bool ShowAmount => true;
}

public static class SampleDynamicRegistration
{
    private static IDisposable? _activeRegistration;

    /// <summary>
    /// Installs the registration. Calling it more than once is a no-op (the existing handle
    /// is reused). Mods should call this once from their own <c>[ModInitializer]</c>.
    /// </summary>
    public static void Install()
    {
        _activeRegistration ??= MultiEnchantmentApi.Register<SampleDynamicEnchantment>()
            .Stack(StackBehavior.MergeAmount, StatusAggregation.SharedAcrossStack)
            .Execution(p => p.All(global::MultiEnchantmentMod.HookExecutionMode.MergedTotal))
            .OnMergedDelta((SampleDynamicEnchantment e, int added) =>
            {
                // Concrete-typed lambda — the extension method on IEnchantmentRegistration
                // infers T = SampleDynamicEnchantment from the lambda's parameter type.
                _ = e;
                _ = added;
            })
            .TrackKeyword(CardKeyword.Exhaust, snap => snap.ActiveTotalAmount)
            .Commit();
    }

    /// <summary>
    /// Releases the registration. Useful for test harnesses or hot-reload scenarios where the
    /// caller needs to reinstall the registration with different parameters.
    /// </summary>
    public static void Uninstall()
    {
        _activeRegistration?.Dispose();
        _activeRegistration = null;
    }
}
