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
        string? stack,
        string? status,
        string? execution,
        ImmutableArray<AttributeData> keywordAttributes,
        bool hasPresentationAttribute)
    {
        DefinitionType = definitionType;
        EnchantmentType = enchantmentType;
        DefinitionAttribute = definitionAttribute;
        Stack = stack;
        Status = status;
        Execution = execution;
        KeywordAttributes = keywordAttributes;
        HasPresentationAttribute = hasPresentationAttribute;
    }

    public INamedTypeSymbol DefinitionType { get; }
    public INamedTypeSymbol EnchantmentType { get; }
    public AttributeData? DefinitionAttribute { get; }
    public string? Stack { get; }
    public string? Status { get; }
    public string? Execution { get; }
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
            DefinitionInfo info = new(
                type,
                enchantmentType,
                definitionAttribute,
                SymbolHelpers.GetAttribute(enchantmentType, symbols.EnchantmentAttribute) is { } enchantmentAttribute
                    ? SymbolHelpers.GetNamedArgumentString(enchantmentAttribute, "Stack")
                    : null,
                SymbolHelpers.GetAttribute(enchantmentType, symbols.EnchantmentAttribute) is { } statusAttribute
                    ? SymbolHelpers.GetNamedArgumentString(statusAttribute, "Status")
                    : null,
                SymbolHelpers.GetAttribute(type, symbols.EnchantmentExecutionAttribute) is { } executionAttribute
                    ? SymbolHelpers.GetNamedArgumentString(executionAttribute, "Mode")
                    : null,
                type.GetAttributes()
                    .Where(attribute => symbols.EnchantmentKeywordAttribute != null && SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, symbols.EnchantmentKeywordAttribute))
                    .ToImmutableArray(),
                SymbolHelpers.HasAttribute(type, symbols.EnchantmentPresentationAttribute));

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
