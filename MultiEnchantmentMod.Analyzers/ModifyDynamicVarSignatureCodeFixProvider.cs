using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Simplification;

namespace MultiEnchantmentMod.Analyzers;

/// <summary>
/// Code-fix for MEM009: rewrites the <c>[ModifyDynamicVar]</c> method signature to the required
/// <c>decimal MethodName(EnchantmentStackSnapshot snapshot, decimal currentValue)</c> form.
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(ModifyDynamicVarSignatureCodeFixProvider))]
[Shared]
public sealed class ModifyDynamicVarSignatureCodeFixProvider : CodeFixProvider
{
    private const string Title = "Fix [ModifyDynamicVar] method signature";

    public override ImmutableArray<string> FixableDiagnosticIds =>
        ImmutableArray.Create(DiagnosticIds.ModifyDynamicVarBadSignature);

    public override FixAllProvider? GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        SyntaxNode? root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        if (root == null)
        {
            return;
        }

        Diagnostic diagnostic = context.Diagnostics.First();
        SyntaxNode? node = root.FindNode(diagnostic.Location.SourceSpan);

        MethodDeclarationSyntax? methodDecl = node.FirstAncestorOrSelf<MethodDeclarationSyntax>();
        if (methodDecl == null)
        {
            return;
        }

        context.RegisterCodeFix(
            CodeAction.Create(
                title: Title,
                createChangedDocument: ct => FixSignatureAsync(context.Document, methodDecl, ct),
                equivalenceKey: Title),
            diagnostic);
    }

    private static async Task<Document> FixSignatureAsync(
        Document document,
        MethodDeclarationSyntax methodDecl,
        CancellationToken cancellationToken)
    {
        SyntaxNode? root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        if (root == null)
        {
            return document;
        }

        SemanticModel? semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        INamedTypeSymbol? snapshotSymbol = semanticModel?.Compilation.GetTypeByMetadataName(MetadataNames.EnchantmentStackSnapshot);
        if (snapshotSymbol == null)
        {
            return document;
        }

        // Build the correct return type: decimal
        TypeSyntax decimalType = SyntaxFactory.PredefinedType(
            SyntaxFactory.Token(SyntaxKind.DecimalKeyword));
        TypeSyntax snapshotType = SyntaxFactory.ParseTypeName(
                snapshotSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))
            .WithAdditionalAnnotations(Simplifier.Annotation)
            .WithTrailingTrivia(SyntaxFactory.Space);

        // Build the correct parameter list: (EnchantmentStackSnapshot snapshot, decimal currentValue)
        ParameterListSyntax newParams = SyntaxFactory.ParameterList(
            SyntaxFactory.SeparatedList(new[]
            {
                SyntaxFactory.Parameter(SyntaxFactory.Identifier("snapshot"))
                    .WithType(snapshotType),
                SyntaxFactory.Parameter(SyntaxFactory.Identifier("currentValue"))
                    .WithType(SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.DecimalKeyword)).WithTrailingTrivia(SyntaxFactory.Space))
            }));

        MethodDeclarationSyntax newMethod = methodDecl
            .WithReturnType(decimalType.WithTrailingTrivia(SyntaxFactory.Space))
            .WithParameterList(newParams);

        SyntaxNode newRoot = root.ReplaceNode(methodDecl, newMethod);
        return document.WithSyntaxRoot(newRoot);
    }
}
