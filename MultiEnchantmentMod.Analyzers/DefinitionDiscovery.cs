using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace MultiEnchantmentMod.Analyzers;

internal sealed class DefinitionInfo
{
    public DefinitionInfo(
        INamedTypeSymbol definitionType,
        INamedTypeSymbol enchantmentType,
        AttributeData? definitionAttribute,
        AttributeData? enchantmentAttribute,
        string stack,
        string status,
        string? declaredStack,
        string? declaredStatus,
        ImmutableArray<string> executionModes,
        ImmutableArray<AttributeData> keywordAttributes,
        bool hasPresentationAttribute)
    {
        DefinitionType = definitionType;
        EnchantmentType = enchantmentType;
        DefinitionAttribute = definitionAttribute;
        EnchantmentAttribute = enchantmentAttribute;
        Stack = stack;
        Status = status;
        DeclaredStack = declaredStack;
        DeclaredStatus = declaredStatus;
        ExecutionModes = executionModes;
        KeywordAttributes = keywordAttributes;
        HasPresentationAttribute = hasPresentationAttribute;
    }

    public INamedTypeSymbol DefinitionType { get; }
    public INamedTypeSymbol EnchantmentType { get; }
    public AttributeData? DefinitionAttribute { get; }
    public AttributeData? EnchantmentAttribute { get; }
    public string Stack { get; }
    public string Status { get; }
    public string? DeclaredStack { get; }
    public string? DeclaredStatus { get; }
    public ImmutableArray<string> ExecutionModes { get; }
    public ImmutableArray<AttributeData> KeywordAttributes { get; }
    public bool HasPresentationAttribute { get; }
}

internal sealed class DefinitionIndex
{
    private readonly ImmutableDictionary<INamedTypeSymbol, ImmutableArray<DefinitionInfo>> definitionsByModel;

    private DefinitionIndex(ImmutableDictionary<INamedTypeSymbol, ImmutableArray<DefinitionInfo>> definitionsByModel)
    {
        this.definitionsByModel = definitionsByModel;
    }

    public static DefinitionIndex Create(Compilation compilation, AnalyzerSymbols symbols)
    {
        Dictionary<INamedTypeSymbol, List<DefinitionInfo>> map = new(SymbolEqualityComparer.Default);

        foreach (INamedTypeSymbol type in SymbolHelpers.GetAllNamedTypes(compilation.Assembly.GlobalNamespace))
        {
            if (type.TypeKind != TypeKind.Class || type.IsAbstract)
            {
                continue;
            }

            if (!SymbolHelpers.DerivesFromOpenGeneric(type, symbols.EnchantmentDefinitionOfT, out INamedTypeSymbol? constructedBase) ||
                constructedBase == null ||
                constructedBase.TypeArguments.Length != 1 ||
                constructedBase.TypeArguments[0] is not INamedTypeSymbol enchantmentType)
            {
                continue;
            }

            AttributeData? definitionAttribute = SymbolHelpers.GetAttribute(type, symbols.EnchantmentDefinitionAttribute);
            AttributeData? enchantmentAttribute = SymbolHelpers.GetAttribute(enchantmentType, symbols.EnchantmentAttribute);
            AttributeData? executionAttribute =
                SymbolHelpers.GetAttribute(type, symbols.EnchantmentExecutionAttribute)
                ?? SymbolHelpers.GetAttribute(enchantmentType, symbols.EnchantmentExecutionAttribute);
            string modelStack = SymbolHelpers.GetOptionalNamedArgumentString(enchantmentAttribute, "Stack") ?? "DisallowDuplicate";
            string modelStatus = SymbolHelpers.GetOptionalNamedArgumentString(enchantmentAttribute, "Status") ?? "AnyInstanceCountsAsOne";
            string? declaredStack = SymbolHelpers.GetOptionalNamedArgumentString(definitionAttribute, "Stack");
            string? declaredStatus = SymbolHelpers.GetOptionalNamedArgumentString(definitionAttribute, "Status");
            DefinitionInfo info = new(
                type,
                enchantmentType,
                definitionAttribute,
                enchantmentAttribute,
                declaredStack ?? modelStack,
                declaredStatus ?? modelStatus,
                declaredStack,
                declaredStatus,
                SymbolHelpers.GetNamedArgumentStrings(
                    executionAttribute,
                    "All",
                    "OnEnchant",
                    "OnPlay",
                    "AfterCardPlayed",
                    "AfterCardDrawn",
                    "AfterPlayerTurnStart",
                    "BeforePlayPhaseStart",
                    "BeforeFlush"),
                enchantmentType.GetAttributes()
                    .Concat(type.GetAttributes())
                    .Where(attribute => symbols.EnchantmentKeywordAttribute != null &&
                        SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, symbols.EnchantmentKeywordAttribute))
                    .ToImmutableArray(),
                SymbolHelpers.HasAttribute(type, symbols.EnchantmentPresentationAttribute) ||
                    SymbolHelpers.HasAttribute(enchantmentType, symbols.EnchantmentPresentationAttribute));

            if (!map.TryGetValue(enchantmentType, out List<DefinitionInfo>? list))
            {
                list = new List<DefinitionInfo>();
                map[enchantmentType] = list;
            }

            list.Add(info);
        }

        var builder = ImmutableDictionary.CreateBuilder<INamedTypeSymbol, ImmutableArray<DefinitionInfo>>(SymbolEqualityComparer.Default);

        foreach (KeyValuePair<INamedTypeSymbol, List<DefinitionInfo>> pair in map)
        {
            builder[pair.Key] = pair.Value.ToImmutableArray();
        }

        return new DefinitionIndex(builder.ToImmutable());
    }

    public ImmutableArray<DefinitionInfo> GetDefinitions(INamedTypeSymbol enchantmentType)
    {
        return definitionsByModel.TryGetValue(enchantmentType, out ImmutableArray<DefinitionInfo> definitions)
            ? definitions
            : ImmutableArray<DefinitionInfo>.Empty;
    }

    public IEnumerable<KeyValuePair<INamedTypeSymbol, ImmutableArray<DefinitionInfo>>> AllDefinitionsByModel => definitionsByModel;
}
