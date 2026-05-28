using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace MultiEnchantmentMod.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class MultiEnchantmentAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor Mem001 = new(
        DiagnosticIds.EnchantmentOnWrongType,
        "[Enchantment] must target an enchantment model",
        "[Enchantment] can only be applied to classes deriving from EnchantmentModel",
        "Usage",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor Mem002 = new(
        DiagnosticIds.ModelDefinitionMismatch,
        "Model and definition stack semantics differ",
        "[Enchantment] on '{0}' disagrees with definition '{1}' for {2}",
        "Usage",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor Mem003 = new(
        DiagnosticIds.KeywordModeMismatch,
        "Keyword mode requires MergeAmount",
        "[EnchantmentKeyword(Mode = {0})] on '{1}' requires Stack = MergeAmount",
        "Usage",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor Mem004 = new(
        DiagnosticIds.MissingParameterlessCtor,
        "Definition requires parameterless constructor",
        "EnchantmentDefinition '{0}' must declare an accessible parameterless constructor",
        "Usage",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor Mem005 = new(
        DiagnosticIds.ExecutionModeMismatch,
        "Execution mode does not match stack semantics",
        "Execution mode '{0}' on definition '{1}' does not match Stack = {2}",
        "Usage",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor Mem006 = new(
        DiagnosticIds.PresentationWithoutOverride,
        "Presentation attribute has no visible effect",
        "Definition '{0}' is marked with [EnchantmentPresentation] but '{1}' overrides no presentation-related members",
        "Usage",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor Mem007 = new(
        DiagnosticIds.MissingCompatibilityAttribute,
        "Assembly should declare API compatibility",
        "Assembly is missing [assembly: EnchantmentApiCompatibility(...)]",
        "Usage",
        // Bumped from Info to Warning during stabilization: silently missing the version tag means
        // a downstream mod's load-time compatibility check is impossible — worth a yellow squiggle.
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        customTags: new[] { WellKnownDiagnosticTags.CompilationEnd });

    private static readonly DiagnosticDescriptor Mem008 = new(
        DiagnosticIds.DuplicateDefinitions,
        "Only one definition per enchantment model is allowed",
        "Assembly contains multiple EnchantmentDefinition<T> types for '{0}'",
        "Usage",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        customTags: new[] { WellKnownDiagnosticTags.CompilationEnd });

    private static readonly DiagnosticDescriptor Mem009 = new(
        DiagnosticIds.ModifyDynamicVarBadSignature,
        "[ModifyDynamicVar] method has wrong signature",
        "{0}.{1} marked with [ModifyDynamicVar] must be 'decimal({2}, decimal)' but is '{3}({4})'",
        "Usage",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor Mem011 = new(
        DiagnosticIds.MaxActivationsWithoutTrigger,
        "MaxActivations without explicit Activation defaults to OnPlay",
        "[Enchantment(MaxActivations={0})] on '{1}' does not set Activation; defaulting to OnPlay — set Activation explicitly to silence this warning",
        "Usage",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor Mem012 = new(
        DiagnosticIds.MergedDeltaWithoutMergeAmount,
        "OnMergedDelta override has no effect outside MergeAmount",
        "Definition '{0}' overrides OnMergedDelta but Stack = {1}; the override is dead code outside MergeAmount",
        "Usage",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor Mem013 = new(
        DiagnosticIds.NumericContributionBadSignature,
        "Numeric contribution method has wrong signature",
        "{0}.{1} marked with [{2}] must be '{3}({4}, {5})' but is '{6}({7})'",
        "Usage",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(Mem001, Mem002, Mem003, Mem004, Mem005, Mem006, Mem007, Mem008, Mem009, Mem011, Mem012, Mem013);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(StartAnalysis);
    }

    private static void StartAnalysis(CompilationStartAnalysisContext context)
    {
        AnalyzerSymbols symbols = AnalyzerSymbols.Create(context.Compilation);
        DefinitionIndex definitionIndex = DefinitionIndex.Create(context.Compilation, symbols);

        context.RegisterSymbolAction(symbolContext => AnalyzeNamedType(symbolContext, symbols, definitionIndex), SymbolKind.NamedType);
        context.RegisterSymbolAction(symbolContext => AnalyzeMethod(symbolContext, symbols), SymbolKind.Method);
        context.RegisterCompilationEndAction(compilationContext => AnalyzeCompilation(compilationContext, symbols, definitionIndex));
    }

    private static void AnalyzeMethod(SymbolAnalysisContext context, AnalyzerSymbols symbols)
    {
        if (symbols.EnchantmentStackSnapshot == null)
        {
            return;
        }

        IMethodSymbol method = (IMethodSymbol)context.Symbol;

        if (HasAttribute(method, symbols.ModifyDynamicVarAttribute))
        {
            AnalyzeNumericContributionSignature(
                context,
                symbols,
                method,
                Mem009,
                "ModifyDynamicVar",
                expectedReturn: SpecialType.System_Decimal,
                expectedValue: SpecialType.System_Decimal);
        }

        if (HasAttribute(method, symbols.ModifyEnergyCostAttribute))
        {
            AnalyzeNumericContributionSignature(
                context,
                symbols,
                method,
                Mem013,
                "ModifyEnergyCost",
                expectedReturn: SpecialType.System_Decimal,
                expectedValue: SpecialType.System_Decimal);
        }

        if (HasAttribute(method, symbols.ModifyCardPlayCountAttribute))
        {
            AnalyzeNumericContributionSignature(
                context,
                symbols,
                method,
                Mem013,
                "ModifyCardPlayCount",
                expectedReturn: SpecialType.System_Int32,
                expectedValue: SpecialType.System_Int32);
        }
    }

    private static bool HasAttribute(IMethodSymbol method, INamedTypeSymbol? attributeSymbol)
    {
        return attributeSymbol != null &&
               method.GetAttributes().Any(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, attributeSymbol));
    }

    private static void AnalyzeNumericContributionSignature(
        SymbolAnalysisContext context,
        AnalyzerSymbols symbols,
        IMethodSymbol method,
        DiagnosticDescriptor descriptor,
        string attributeName,
        SpecialType expectedReturn,
        SpecialType expectedValue)
    {
        bool returnOk = method.ReturnType.SpecialType == expectedReturn;
        bool paramsOk =
            method.Parameters.Length == 2 &&
            SymbolEqualityComparer.Default.Equals(method.Parameters[0].Type, symbols.EnchantmentStackSnapshot) &&
            method.Parameters[1].Type.SpecialType == expectedValue;

        if (returnOk && paramsOk)
        {
            return;
        }

        string expectedReturnName = SpecialTypeDisplayName(expectedReturn);
        string expectedValueName = SpecialTypeDisplayName(expectedValue);

        string actualParams = string.Join(", ", method.Parameters.Select(p => p.Type.Name));
        if (descriptor == Mem009)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                Mem009,
                SymbolHelpers.GetBestLocation(method),
                method.ContainingType?.Name ?? "?",
                method.Name,
                symbols.EnchantmentStackSnapshot!.Name,
                method.ReturnType.Name,
                actualParams));
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(
            descriptor,
            SymbolHelpers.GetBestLocation(method),
            method.ContainingType?.Name ?? "?",
            method.Name,
            attributeName,
            expectedReturnName,
            symbols.EnchantmentStackSnapshot!.Name,
            expectedValueName,
            method.ReturnType.Name,
            actualParams));
    }

    private static string SpecialTypeDisplayName(SpecialType specialType)
    {
        return specialType switch
        {
            SpecialType.System_Decimal => "decimal",
            SpecialType.System_Int32 => "int",
            _ => specialType.ToString(),
        };
    }

    private static void AnalyzeNamedType(SymbolAnalysisContext context, AnalyzerSymbols symbols, DefinitionIndex definitionIndex)
    {
        INamedTypeSymbol type = (INamedTypeSymbol)context.Symbol;
        if (type.TypeKind != TypeKind.Class)
        {
            return;
        }

        AttributeData? enchantmentAttribute = SymbolHelpers.GetAttribute(type, symbols.EnchantmentAttribute);
        if (enchantmentAttribute != null && !SymbolHelpers.InheritsFrom(type, symbols.EnchantmentModel))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                Mem001,
                enchantmentAttribute.ApplicationSyntaxReference?.GetSyntax(context.CancellationToken).GetLocation() ?? SymbolHelpers.GetBestLocation(type)));
        }

        // MEM011: [Enchantment(MaxActivations=N)] without explicit Activation falls back to OnPlay,
        // which is rarely the author's intent for damage / turn-tied lifetimes.
        if (enchantmentAttribute != null)
        {
            string? maxActivationsStr = SymbolHelpers.GetNamedArgumentString(enchantmentAttribute, "MaxActivations");
            bool hasActivation = enchantmentAttribute.NamedArguments.Any(static n => n.Key == "Activation");
            if (int.TryParse(maxActivationsStr, out int maxActivations) && maxActivations > 0 && !hasActivation)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    Mem011,
                    enchantmentAttribute.ApplicationSyntaxReference?.GetSyntax(context.CancellationToken).GetLocation() ?? SymbolHelpers.GetBestLocation(type),
                    maxActivations,
                    type.Name));
            }
        }

        if (!SymbolHelpers.DerivesFromOpenGeneric(type, symbols.EnchantmentDefinitionOfT, out INamedTypeSymbol? constructedBase) ||
            constructedBase == null ||
            constructedBase.TypeArguments.Length != 1 ||
            constructedBase.TypeArguments[0] is not INamedTypeSymbol enchantmentType)
        {
            return;
        }

        if (!SymbolHelpers.HasAccessibleParameterlessConstructor(type))
        {
            context.ReportDiagnostic(Diagnostic.Create(Mem004, SymbolHelpers.GetBestLocation(type), type.Name));
        }

        DefinitionInfo? current = definitionIndex.GetDefinitions(enchantmentType)
            .FirstOrDefault(definition => SymbolEqualityComparer.Default.Equals(definition.DefinitionType, type));

        if (current == null)
        {
            return;
        }

        AnalyzeModelMismatch(context, symbols, current, enchantmentType);
        AnalyzeKeywordMode(context, current);
        AnalyzeExecutionMode(context, current);
        AnalyzePresentation(context, current);
        AnalyzeMergedDeltaOverride(context, current);
    }

    // MEM012: definition overrides OnMergedDelta but Stack != MergeAmount → dead code. Same idea
    // as MEM005 (execution mode vs stack) but for the lifecycle override hook.
    private static void AnalyzeMergedDeltaOverride(SymbolAnalysisContext context, DefinitionInfo definition)
    {
        if (definition.Stack == null || definition.Stack == "MergeAmount")
        {
            return;
        }

        foreach (ISymbol member in definition.DefinitionType.GetMembers("OnMergedDelta"))
        {
            if (member is IMethodSymbol method && method.IsOverride)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    Mem012,
                    SymbolHelpers.GetBestLocation(method),
                    definition.DefinitionType.Name,
                    definition.Stack));
                return;
            }
        }
    }

    private static void AnalyzeModelMismatch(SymbolAnalysisContext context, AnalyzerSymbols symbols, DefinitionInfo definition, INamedTypeSymbol enchantmentType)
    {
        AttributeData? enchantmentAttribute = SymbolHelpers.GetAttribute(enchantmentType, symbols.EnchantmentAttribute);
        if (enchantmentAttribute == null)
        {
            return;
        }

        string? modelStack = SymbolHelpers.GetNamedArgumentString(enchantmentAttribute, "Stack") ?? "DisallowDuplicate";
        string? modelStatus = SymbolHelpers.GetNamedArgumentString(enchantmentAttribute, "Status") ?? "AnyInstanceCountsAsOne";

        string? definitionStack = definition.DeclaredStack;
        string? definitionStatus = definition.DeclaredStatus;
        bool stackMismatch = definitionStack != null && definitionStack != modelStack;
        bool statusMismatch = definitionStatus != null && definitionStatus != modelStatus;
        if (!stackMismatch && !statusMismatch)
        {
            return;
        }

        string mismatchPart = stackMismatch && statusMismatch
            ? $"Stack/Status ({modelStack}/{modelStatus} vs {definitionStack}/{definitionStatus})"
            : stackMismatch
                ? $"Stack ({modelStack} vs {definitionStack})"
                : $"Status ({modelStatus} vs {definitionStatus})";

        context.ReportDiagnostic(Diagnostic.Create(
            Mem002,
            SymbolHelpers.GetBestLocation(definition.DefinitionType),
            enchantmentType.Name,
            definition.DefinitionType.Name,
            mismatchPart));
    }

    private static void AnalyzeKeywordMode(SymbolAnalysisContext context, DefinitionInfo definition)
    {
        if (definition.Stack == "MergeAmount")
        {
            return;
        }

        foreach (AttributeData attribute in definition.KeywordAttributes)
        {
            string? mode = SymbolHelpers.GetNamedArgumentString(attribute, "Mode");
            if (mode != "PerTotalAmount")
            {
                continue;
            }

            context.ReportDiagnostic(Diagnostic.Create(
                Mem003,
                attribute.ApplicationSyntaxReference?.GetSyntax(context.CancellationToken).GetLocation() ?? SymbolHelpers.GetBestLocation(definition.DefinitionType),
                mode,
                definition.DefinitionType.Name));
        }
    }

    private static void AnalyzeExecutionMode(SymbolAnalysisContext context, DefinitionInfo definition)
    {
        if (definition.Stack == null || definition.ExecutionModes.Length == 0)
        {
            return;
        }

        foreach (string execution in definition.ExecutionModes)
        {
            if (!IsExecutionMismatch(definition.Stack, execution))
            {
                continue;
            }

            context.ReportDiagnostic(Diagnostic.Create(
                Mem005,
                SymbolHelpers.GetBestLocation(definition.DefinitionType),
                execution,
                definition.DefinitionType.Name,
                definition.Stack));
            return;
        }
    }

    private static void AnalyzePresentation(SymbolAnalysisContext context, DefinitionInfo definition)
    {
        if (!definition.HasPresentationAttribute)
        {
            return;
        }

        if (SymbolHelpers.OverridesAnyPresentationMember(definition.DefinitionType))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(
            Mem006,
            SymbolHelpers.GetBestLocation(definition.DefinitionType),
            definition.DefinitionType.Name,
            definition.EnchantmentType.Name));
    }

    private static bool IsExecutionMismatch(string stack, string execution)
    {
        return (stack, execution) switch
        {
            ("MergeAmount", "PerLiveInstance") => true,
            ("MergeAmount", "FirstActiveInstanceOnly") => true,
            ("DuplicateInstance", "MergedTotal") => true,
            ("ExistenceStack", "MergedTotal") => true,
            ("ExistenceStack", "PerLiveInstance") => true,
            _ => false,
        };
    }

    private static void AnalyzeCompilation(CompilationAnalysisContext context, AnalyzerSymbols symbols, DefinitionIndex definitionIndex)
    {
        if (symbols.EnchantmentApiCompatibilityAttribute != null &&
            !context.Compilation.Assembly.GetAttributes().Any(attribute =>
                SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, symbols.EnchantmentApiCompatibilityAttribute)))
        {
            Location location = context.Compilation.SyntaxTrees.FirstOrDefault()?.GetRoot(context.CancellationToken).GetLocation() ?? Location.None;
            context.ReportDiagnostic(Diagnostic.Create(Mem007, location));
        }

        foreach (KeyValuePair<INamedTypeSymbol, ImmutableArray<DefinitionInfo>> pair in definitionIndex.AllDefinitionsByModel)
        {
            if (pair.Value.Length < 2)
            {
                continue;
            }

            foreach (DefinitionInfo definition in pair.Value)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    Mem008,
                    SymbolHelpers.GetBestLocation(definition.DefinitionType),
                    pair.Key.Name));
            }
        }
    }
}
