namespace MultiEnchantmentMod.Api;

/// <summary>
/// Stack-aware async hook capability surface of <see cref="IEnchantmentRegistration"/>. Unlike
/// the per-instance vanilla bridges in <see cref="ILifecycleRegistration"/>, these handlers are
/// invoked exactly once per enchantment type per event with a full
/// <see cref="EnchantmentStackSnapshot"/>, letting authors aggregate prompts / animations /
/// numeric amounts across the whole stack instead of multiplying the handler N times.
/// </summary>
public interface IStackedHookRegistration
{
    IEnchantmentRegistration OnPlayStacked(StackedOnPlayHandler handler);
    IEnchantmentRegistration BeforeCardPlayedStacked(StackedBeforeCardPlayedHandler handler);
    IEnchantmentRegistration AfterCardPlayedStacked(StackedAfterCardPlayedHandler handler);
    IEnchantmentRegistration AfterCardDrawnStacked(StackedAfterCardDrawnHandler handler);
    IEnchantmentRegistration AfterAnyCardDrawnStacked(StackedAfterAnyCardDrawnHandler handler);
    IEnchantmentRegistration BeforeFlushStacked(StackedBeforeFlushHandler handler);
    IEnchantmentRegistration AfterDamageGivenStacked(StackedAfterDamageGivenHandler handler);
}
