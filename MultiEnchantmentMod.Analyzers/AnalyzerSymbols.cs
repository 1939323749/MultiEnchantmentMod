using Microsoft.CodeAnalysis;

namespace MultiEnchantmentMod.Analyzers;

internal sealed class AnalyzerSymbols
{
    public AnalyzerSymbols(
        INamedTypeSymbol? enchantmentAttribute,
        INamedTypeSymbol? enchantmentDefinitionAttribute,
        INamedTypeSymbol? enchantmentDefinitionOfT,
        INamedTypeSymbol? enchantmentKeywordAttribute,
        INamedTypeSymbol? enchantmentExecutionAttribute,
        INamedTypeSymbol? enchantmentPresentationAttribute,
        INamedTypeSymbol? enchantmentApiCompatibilityAttribute,
        INamedTypeSymbol? enchantmentModel)
    {
        EnchantmentAttribute = enchantmentAttribute;
        EnchantmentDefinitionAttribute = enchantmentDefinitionAttribute;
        EnchantmentDefinitionOfT = enchantmentDefinitionOfT;
        EnchantmentKeywordAttribute = enchantmentKeywordAttribute;
        EnchantmentExecutionAttribute = enchantmentExecutionAttribute;
        EnchantmentPresentationAttribute = enchantmentPresentationAttribute;
        EnchantmentApiCompatibilityAttribute = enchantmentApiCompatibilityAttribute;
        EnchantmentModel = enchantmentModel;
    }

    public INamedTypeSymbol? EnchantmentAttribute { get; }
    public INamedTypeSymbol? EnchantmentDefinitionAttribute { get; }
    public INamedTypeSymbol? EnchantmentDefinitionOfT { get; }
    public INamedTypeSymbol? EnchantmentKeywordAttribute { get; }
    public INamedTypeSymbol? EnchantmentExecutionAttribute { get; }
    public INamedTypeSymbol? EnchantmentPresentationAttribute { get; }
    public INamedTypeSymbol? EnchantmentApiCompatibilityAttribute { get; }
    public INamedTypeSymbol? EnchantmentModel { get; }

    public static AnalyzerSymbols Create(Compilation compilation)
    {
        return new AnalyzerSymbols(
            compilation.GetTypeByMetadataName(MetadataNames.EnchantmentAttribute),
            compilation.GetTypeByMetadataName(MetadataNames.EnchantmentDefinitionAttribute),
            compilation.GetTypeByMetadataName(MetadataNames.EnchantmentDefinitionOfT),
            compilation.GetTypeByMetadataName(MetadataNames.EnchantmentKeywordAttribute),
            compilation.GetTypeByMetadataName(MetadataNames.EnchantmentExecutionAttribute),
            compilation.GetTypeByMetadataName(MetadataNames.EnchantmentPresentationAttribute),
            compilation.GetTypeByMetadataName(MetadataNames.EnchantmentApiCompatibilityAttribute),
            compilation.GetTypeByMetadataName(MetadataNames.EnchantmentModel));
    }
}
