namespace MultiEnchantmentMod.Analyzers;

internal static class DiagnosticIds
{
    public const string EnchantmentOnWrongType = "MEM001";
    public const string ModelDefinitionMismatch = "MEM002";
    public const string KeywordModeMismatch = "MEM003";
    public const string MissingParameterlessCtor = "MEM004";
    public const string ExecutionModeMismatch = "MEM005";
    public const string PresentationWithoutOverride = "MEM006";
    public const string MissingCompatibilityAttribute = "MEM007";
    public const string DuplicateDefinitions = "MEM008";
    public const string ModifyDynamicVarBadSignature = "MEM009";
    public const string MaxActivationsWithoutTrigger = "MEM011";
    public const string MergedDeltaWithoutMergeAmount = "MEM012";
    public const string NumericContributionBadSignature = "MEM013";
}
