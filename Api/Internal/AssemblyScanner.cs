using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MegaCrit.Sts2.Core.Models;

namespace MultiEnchantmentMod.Api.Internal;

/// <summary>
/// Single discovery / registration entry point for the v2 stacking API. Supports explicit
/// <see cref="MultiEnchantmentApi.ScanAssembly"/> plus a lazy fallback when the runtime resolves
/// a provider for the first time.
/// </summary>
/// <remarks>
/// One scan pass covers two categories per assembly:
///   1. <see cref="EnchantmentDefinition{T}"/> concrete subclasses → instantiated and
///      <see cref="IEnchantmentDefinition.Register"/>'ed.
///   2. <see cref="EnchantmentModel"/> subclasses tagged with <see cref="EnchantmentAttribute"/>
///      (and friends) but not yet covered by a Definition class → registered via the fluent
///      builder using the attribute values.
/// </remarks>
internal static class AssemblyScanner
{
    private static readonly object Sync = new();
    private static readonly HashSet<Assembly> ScannedAssemblies = new();
    private static bool _sealed;
    private static int _lastSeenAssemblyCount;

    /// <summary>
    /// True once <see cref="Seal"/> has been called. Consulted by <c>EnchantmentRegistry.Install</c>
    /// so direct fluent <c>Register&lt;T&gt;().Commit()</c> calls after seal log + no-op the same
    /// way <see cref="ScanAssembly"/> does.
    /// </summary>
    internal static bool IsSealed
    {
        get
        {
            lock (Sync)
            {
                return _sealed;
            }
        }
    }

    /// <summary>
    /// Scans <paramref name="assembly"/> for v2 definitions / attributes.
    /// Idempotent: re-scanning the same assembly does nothing. Returns the number of new
    /// registrations performed.
    /// </summary>
    public static int ScanAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        lock (Sync)
        {
            if (_sealed)
            {
                global::MultiEnchantmentMod.MultiEnchantmentMod.Logger.Warn(
                    $"[StackApi] Ignored scan request for {assembly.GetName().Name}: registry is sealed.");
                return 0;
            }

            if (!ScannedAssemblies.Add(assembly))
            {
                return 0;
            }

            if (!PassesApiCompatibilityCheck(assembly))
            {
                return 0;
            }

            return ScanCore(assembly);
        }
    }

    /// <summary>
    /// Lazy scan triggered from the provider resolve helpers. Walks every loaded assembly that
    /// references this mod and that hasn't been scanned yet.
    /// </summary>
    /// <remarks>
    /// Re-runs whenever the <see cref="AppDomain.CurrentDomain"/>'s assembly count grows since
    /// the previous scan — picks up mods that load AFTER first Resolve without forcing every
    /// such mod to call <see cref="ScanAssembly"/> explicitly. The hot-path cost is a cheap
    /// integer compare; only when the count actually grows do we acquire the lock and reflect.
    /// </remarks>
    public static void EnsureScanned()
    {
        if (_sealed)
        {
            return;
        }

        Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
        if (assemblies.Length == _lastSeenAssemblyCount)
        {
            return;
        }

        lock (Sync)
        {
            if (_sealed)
            {
                return;
            }

            // Re-read in case other threads loaded more assemblies while we waited for the lock.
            assemblies = AppDomain.CurrentDomain.GetAssemblies();
            if (assemblies.Length == _lastSeenAssemblyCount)
            {
                return;
            }

            foreach (Assembly assembly in assemblies)
            {
                if (ScannedAssemblies.Contains(assembly))
                {
                    continue;
                }

                if (!CouldReferenceModAssembly(assembly))
                {
                    continue;
                }

                if (!PassesApiCompatibilityCheck(assembly))
                {
                    ScannedAssemblies.Add(assembly);
                    continue;
                }

                ScannedAssemblies.Add(assembly);
                ScanCore(assembly);
            }

            _lastSeenAssemblyCount = assemblies.Length;
        }
    }

    /// <summary>
    /// Freezes the registry. Further <see cref="ScanAssembly"/> calls log a warning and do
    /// nothing; <see cref="EnsureScanned"/> becomes a no-op. Useful once the game enters its
    /// "active gameplay" phase and no further mods can be loaded.
    /// </summary>
    public static void Seal()
    {
        lock (Sync)
        {
            _sealed = true;
        }
    }

    private static int ScanCore(Assembly assembly)
    {
        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types.Where(static t => t != null).Cast<Type>().ToArray();
        }
        catch (Exception ex)
        {
            global::MultiEnchantmentMod.MultiEnchantmentMod.Logger.Warn(
                $"[StackApi] Failed to enumerate types in {assembly.GetName().Name}: {ex.GetBaseException().Message}");
            return 0;
        }

        HashSet<Type> coveredByDefinition = new();
        int registeredCount = 0;

        // Pass 1: EnchantmentDefinition<T> subclasses (preferred — they bring full virtual-method bag).
        foreach (Type type in types)
        {
            if (TryRegisterDefinitionClass(type, out Type? enchantmentType) && enchantmentType != null)
            {
                coveredByDefinition.Add(enchantmentType);
                registeredCount++;
            }
        }

        // Pass 2: [Enchantment] attribute on enchantment models that no Definition class covers.
        foreach (Type type in types)
        {
            if (coveredByDefinition.Contains(type))
            {
                continue;
            }

            if (TryRegisterAttributeOnly(type))
            {
                registeredCount++;
            }
        }

        return registeredCount;
    }

    private static bool TryRegisterDefinitionClass(Type type, out Type? enchantmentType)
    {
        enchantmentType = null;

        if (type.IsAbstract || type.IsInterface || type.ContainsGenericParameters)
        {
            return false;
        }

        if (!typeof(IEnchantmentDefinition).IsAssignableFrom(type))
        {
            return false;
        }

        ConstructorInfo? ctor = type.GetConstructor(Type.EmptyTypes);
        if (ctor == null)
        {
            global::MultiEnchantmentMod.MultiEnchantmentMod.Logger.Warn(
                $"[StackApi] {type.FullName} (assembly={type.Assembly.GetName().Name}) extends EnchantmentDefinition<T> but lacks a parameterless constructor; skipping (analyzer rule MEM004).");
            return false;
        }

        IEnchantmentDefinition definition;
        try
        {
            definition = (IEnchantmentDefinition)ctor.Invoke(null);
        }
        catch (Exception ex)
        {
            global::MultiEnchantmentMod.MultiEnchantmentMod.Logger.Warn(
                $"[StackApi] Failed to instantiate {type.FullName} (assembly={type.Assembly.GetName().Name}): {ex.GetBaseException().Message}");
            return false;
        }

        try
        {
            definition.Register();
        }
        catch (Exception ex)
        {
            global::MultiEnchantmentMod.MultiEnchantmentMod.Logger.Warn(
                $"[StackApi] Failed to register {type.FullName} (assembly={type.Assembly.GetName().Name}): {ex.GetBaseException().Message}");
            return false;
        }

        enchantmentType = definition.EnchantmentType;
        return true;
    }

    private static bool TryRegisterAttributeOnly(Type type)
    {
        EnchantmentAttribute? attribute = (EnchantmentAttribute?)Attribute.GetCustomAttribute(
            type, typeof(EnchantmentAttribute));
        if (attribute == null)
        {
            return false;
        }

        if (type.IsAbstract || type.IsInterface || type.ContainsGenericParameters)
        {
            return false;
        }

        if (!typeof(EnchantmentModel).IsAssignableFrom(type))
        {
            global::MultiEnchantmentMod.MultiEnchantmentMod.Logger.Warn(
                $"[StackApi] [Enchantment] applied to {type.FullName} (assembly={type.Assembly.GetName().Name}) which is not an EnchantmentModel; skipping (analyzer rule MEM001).");
            return false;
        }

        try
        {
            IEnchantmentRegistration registration = MultiEnchantmentApi.Register(type)
                .Stack(attribute.Stack, attribute.Status);

            if (attribute.MaxActivations > 0)
            {
                registration.MaxActivations(attribute.MaxActivations, attribute.Activation);
            }
            else if (attribute.LingerTurns > 0)
            {
                registration.LingerForTurns(attribute.LingerTurns);
            }
            else
            {
                switch (attribute.Scope)
                {
                    case ScopeKind.UntilCombatEnds:
                        registration.WithScope(EnchantmentScope.UntilCombatEnds);
                        break;
                    case ScopeKind.UntilTurnEnds:
                        registration.WithScope(EnchantmentScope.UntilTurnEnds);
                        break;
                }
            }

            EnchantmentExecutionAttribute? executionAttribute =
                (EnchantmentExecutionAttribute?)Attribute.GetCustomAttribute(
                    type, typeof(EnchantmentExecutionAttribute));
            if (executionAttribute != null)
            {
                registration.Execution(builder => ApplyExecutionAttribute(builder, executionAttribute));
            }

            foreach (Attribute raw in Attribute.GetCustomAttributes(type, typeof(EnchantmentKeywordAttribute)))
            {
                if (raw is not EnchantmentKeywordAttribute keywordAttribute)
                {
                    continue;
                }

                EnchantmentKeywordAttribute captured = keywordAttribute;
                registration.TrackKeyword(captured.Keyword, snapshot => EvaluateKeywordAmount(captured, snapshot));
            }

            // [ModifyDynamicVar] tagged methods on Tier A enchantment classes — Tier B definitions
            // pick these up through EnchantmentDefinition<T>.DynamicVarContributions instead.
            foreach (DynamicVarContribution contribution in ModifyDynamicVarScanner.ScanType(type))
            {
                registration.ModifyDynamicVar(contribution.VarKey, contribution.Contribution);
            }

            registration.Commit();
            return true;
        }
        catch (Exception ex)
        {
            global::MultiEnchantmentMod.MultiEnchantmentMod.Logger.Warn(
                $"[StackApi] Failed to register attribute-only enchantment {type.FullName} (assembly={type.Assembly.GetName().Name}): {ex.GetBaseException().Message}");
            return false;
        }
    }

    private static void ApplyExecutionAttribute(ExecutionPolicyBuilder builder, EnchantmentExecutionAttribute attribute)
    {
        builder.All(attribute.All)
            .OnEnchant(attribute.OnEnchant)
            .OnPlay(attribute.OnPlay)
            .AfterCardPlayed(attribute.AfterCardPlayed)
            .AfterCardDrawn(attribute.AfterCardDrawn)
            .AfterPlayerTurnStart(attribute.AfterPlayerTurnStart)
            .BeforePlayPhaseStart(attribute.BeforePlayPhaseStart)
            .BeforeFlush(attribute.BeforeFlush);
    }

    private static int EvaluateKeywordAmount(
        EnchantmentKeywordAttribute attribute,
        global::MultiEnchantmentMod.EnchantmentStackSnapshot snapshot)
    {
        return attribute.Mode switch
        {
            KeywordEvalMode.PerInstance => snapshot.ActiveInstanceCount,
            KeywordEvalMode.PerTotalAmount => snapshot.ActiveTotalAmount,
            KeywordEvalMode.Constant => attribute.Constant,
            // Custom: attribute-only path can't override KeywordSourceAmount; treat as 0 and let
            // a sibling Definition class take over. Analyzer rule MEM003 nudges authors to use a
            // Definition class when Custom is selected.
            _ => 0,
        };
    }

    private static bool PassesApiCompatibilityCheck(Assembly assembly)
    {
        EnchantmentApiCompatibilityAttribute? attribute =
            (EnchantmentApiCompatibilityAttribute?)Attribute.GetCustomAttribute(
                assembly, typeof(EnchantmentApiCompatibilityAttribute));

        if (attribute == null)
        {
            // Missing attribute is a soft warning — own assembly and the legacy v1 mods don't
            // carry it. Don't refuse to scan.
            return true;
        }

        if (attribute.MinVersion > MultiEnchantmentApiVersion.Current)
        {
            global::MultiEnchantmentMod.MultiEnchantmentMod.Logger.Error(
                $"[StackApi] Refusing to scan {assembly.GetName().Name}: requires MultiEnchantmentMod API v{attribute.MinVersion} but runtime is v{MultiEnchantmentApiVersion.Current}.");
            return false;
        }

        if (attribute.MaxVersion > 0 && attribute.MaxVersion < MultiEnchantmentApiVersion.Current)
        {
            // Suppress the warning for the mod's own assembly — it's authoritatively the
            // runtime, and if the version bumped the new code edited this attribute too. Any
            // other assembly genuinely is out-of-date and deserves a heads-up.
            if (assembly != typeof(MultiEnchantmentApi).Assembly)
            {
                global::MultiEnchantmentMod.MultiEnchantmentMod.Logger.Warn(
                    $"[StackApi] Scanning {assembly.GetName().Name} which declares MaxVersion={attribute.MaxVersion} but runtime is v{MultiEnchantmentApiVersion.Current}; behavior may drift.");
            }
        }

        return true;
    }

    private static bool CouldReferenceModAssembly(Assembly assembly)
    {
        Assembly apiAssembly = typeof(MultiEnchantmentApi).Assembly;
        if (assembly == apiAssembly)
        {
            return true;
        }

        try
        {
            return assembly.GetReferencedAssemblies()
                .Any(reference => string.Equals(
                    reference.Name,
                    apiAssembly.GetName().Name,
                    StringComparison.Ordinal));
        }
        catch
        {
            return false;
        }
    }
}
