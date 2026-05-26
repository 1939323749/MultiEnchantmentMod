using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace MultiEnchantmentMod.Analyzers;

internal static class SymbolHelpers
{
    public static bool HasAttribute(ISymbol symbol, INamedTypeSymbol? attributeSymbol)
    {
        return GetAttribute(symbol, attributeSymbol) != null;
    }

    public static AttributeData? GetAttribute(ISymbol symbol, INamedTypeSymbol? attributeSymbol)
    {
        if (attributeSymbol == null)
        {
            return null;
        }

        return symbol.GetAttributes().FirstOrDefault(attribute =>
            SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, attributeSymbol));
    }

    public static bool InheritsFrom(INamedTypeSymbol? type, INamedTypeSymbol? baseType)
    {
        if (type == null || baseType == null)
        {
            return false;
        }

        for (INamedTypeSymbol? current = type; current != null; current = current.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(current, baseType))
            {
                return true;
            }
        }

        return false;
    }

    public static bool DerivesFromOpenGeneric(INamedTypeSymbol type, INamedTypeSymbol? openGeneric, out INamedTypeSymbol? constructedBase)
    {
        constructedBase = null;
        if (openGeneric == null)
        {
            return false;
        }

        for (INamedTypeSymbol? current = type; current != null; current = current.BaseType)
        {
            if (current.OriginalDefinition != null &&
                SymbolEqualityComparer.Default.Equals(current.OriginalDefinition, openGeneric))
            {
                constructedBase = current;
                return true;
            }
        }

        return false;
    }

    public static string? GetNamedArgumentString(AttributeData attribute, string name)
    {
        foreach (KeyValuePair<string, TypedConstant> pair in attribute.NamedArguments)
        {
            if (pair.Key == name && pair.Value.Value != null)
            {
                return pair.Value.Value.ToString();
            }
        }

        return null;
    }

    public static bool HasAccessibleParameterlessConstructor(INamedTypeSymbol type)
    {
        return type.InstanceConstructors.Any(ctor =>
            ctor.Parameters.Length == 0 &&
            ctor.DeclaredAccessibility is Accessibility.Public or Accessibility.Internal or Accessibility.ProtectedOrInternal);
    }

    public static bool OverridesAnyPresentationMember(INamedTypeSymbol type)
    {
        foreach (ISymbol member in type.GetMembers())
        {
            if (!member.IsOverride)
            {
                continue;
            }

            switch (member.Name)
            {
                case "TryFormatExtraText":
                case "GetVisualSliceAmounts":
                case "GetVisualSlices":
                    return true;
            }
        }

        return false;
    }

    public static Location GetBestLocation(ISymbol symbol)
    {
        return symbol.Locations.FirstOrDefault(static location => location.IsInSource) ?? Location.None;
    }

    public static ImmutableArray<INamedTypeSymbol> GetAllNamedTypes(INamespaceSymbol ns)
    {
        var builder = ImmutableArray.CreateBuilder<INamedTypeSymbol>();
        Collect(ns, builder);
        return builder.ToImmutable();
    }

    private static void Collect(INamespaceOrTypeSymbol container, ImmutableArray<INamedTypeSymbol>.Builder builder)
    {
        foreach (ISymbol member in container.GetMembers())
        {
            switch (member)
            {
                case INamespaceSymbol nestedNs:
                    Collect(nestedNs, builder);
                    break;
                case INamedTypeSymbol namedType:
                    builder.Add(namedType);
                    Collect(namedType, builder);
                    break;
            }
        }
    }
}
