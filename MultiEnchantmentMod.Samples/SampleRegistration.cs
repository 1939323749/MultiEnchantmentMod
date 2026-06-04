using Godot;
using MegaCrit.Sts2.Core.Modding;
using MultiEnchantmentMod.Api;

namespace MultiEnchantmentMod.Samples;

/// <summary>
/// [ModInitializer] entry point for the samples assembly. Wires every sample registration tier
/// in one shot:
/// <list type="bullet">
///   <item>Tier A / B — assembly scan picks up every <see cref="EnchantmentAttribute"/>-tagged
///         <see cref="MegaCrit.Sts2.Core.Models.EnchantmentModel"/> subclass and its companion
///         <see cref="EnchantmentDefinition{T}"/>.</item>
///   <item>Tier C — explicit <c>Install()</c> calls for the fluent-builder samples that cannot
///         live on attributes alone (Tier C registrations need runtime predicates / handlers).</item>
/// </list>
/// </summary>
/// <remarks>
/// Once the samples mod is installed (<c>dotnet publish -p:InstallSamples=true</c>) the game's
/// mod loader instantiates this class on startup and invokes <see cref="Initialize"/> via the
/// <see cref="ModInitializerAttribute"/>.
/// <para>
/// The sample <see cref="MegaCrit.Sts2.Core.Models.EnchantmentModel"/> subclasses themselves are
/// auto-discovered by <c>ModelDb</c> via reflection (it scans all assemblies under the mods
/// folder). What's missing without this initializer is the v2 API plumbing — stack behavior,
/// status aggregation, lifecycle callbacks — all of which live in
/// <see cref="MultiEnchantmentApi"/>'s registry.
/// </para>
/// </remarks>
[ModInitializer(nameof(Initialize))]
public partial class SampleRegistration : Node
{
    private const string ModId = "MultiEnchantmentMod.Samples";

    public static MegaCrit.Sts2.Core.Logging.Logger Logger { get; } =
        new(ModId, MegaCrit.Sts2.Core.Logging.LogType.Generic);

    public static void Initialize()
    {
        if (!MultiEnchantmentApi.RequireApiVersion(2))
        {
            Logger.Warn($"[{ModId}] Aborting initialization: host API version is below 2.");
            return;
        }
        

        // Tier A / B — attributes + companion EnchantmentDefinition<T> subclasses.
        int scanned = MultiEnchantmentApi.ScanCallingAssembly();
        Logger.Info($"[{ModId}] Scanned {scanned} attribute-based enchantment registration(s).");

        // Tier C — fluent registrations that need runtime predicates / handlers.
        SampleDynamicRegistration.Install();
        SampleLingeringSharpenRegistration.Install();
        SampleChargedSharpenRegistration.Install();
        SampleHandOnlySharpenRegistration.Install();
        SamplePhoenixRegistration.Install();
        SampleChargedSurgeRegistration.Install();
        SampleBerserkRegistration.Install();
        SampleBoundedQueueRegistration.Install();
        SampleFlexibleScopeRegistration.Install();
        SampleNumericContributionRegistration.Install();
        SampleHandStatusSharpenRegistration.Install();
        SampleLibraryMarkerRegistration.Install();
        SampleRightSideMarkerRegistration.Install();
        Logger.Info($"[{ModId}] Installed 13 fluent/display sample registration(s).");
    }
}
