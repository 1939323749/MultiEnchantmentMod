using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeFixes;

namespace MultiEnchantmentMod.Analyzers;

[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(LegacyProviderCodeFixProvider))]
public sealed class LegacyProviderCodeFixProvider : CodeFixProvider
{
    public override System.Collections.Immutable.ImmutableArray<string> FixableDiagnosticIds => System.Collections.Immutable.ImmutableArray<string>.Empty;

    public override FixAllProvider? GetFixAllProvider()
    {
        return WellKnownFixAllProviders.BatchFixer;
    }

    public override Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        return Task.CompletedTask;
    }
}
