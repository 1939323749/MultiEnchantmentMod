using System;

namespace MultiEnchantmentMod.Api;

/// <summary>
/// Marks a method as a contribution to the named dynamic variable. The assembly scanner discovers
/// these methods on both <c>EnchantmentModel</c> subclasses tagged with
/// <see cref="EnchantmentAttribute"/> (Tier A — attribute-only enchantments) and
/// <see cref="EnchantmentDefinition{TEnchantment}"/> subclasses (Tier B — definition classes), and
/// converts each match into a <see cref="DynamicVarContribution"/>.
/// </summary>
/// <remarks>
/// <para>Required method signature:</para>
/// <code>
/// decimal MethodName(EnchantmentStackSnapshot snapshot, decimal currentValue);
/// </code>
/// <para>
/// Methods can be tagged with multiple <see cref="ModifyDynamicVarAttribute"/> instances, one per
/// dynamic-variable key the method contributes to. The scanner treats them independently.
/// </para>
/// <para>
/// Example on an <see cref="MegaCrit.Sts2.Core.Models.EnchantmentModel"/> subclass:
/// </para>
/// <code>
/// [Enchantment]
/// public class SamplePlusFive : EnchantmentModel
/// {
///     [ModifyDynamicVar("Echo")]
///     public decimal AddFive(EnchantmentStackSnapshot snapshot, decimal current)
///         =&gt; current + snapshot.TotalAmount * 5m;
/// }
/// </code>
/// <para>
/// The scanner converts the discovered instance method into an open delegate so it can be invoked
/// without constructing a fresh model instance — the receiver argument is ignored and replaced
/// with a stable placeholder. Authors should treat the method body as pure-stateless code over the
/// snapshot + current value; reading <c>this</c> state is unsupported.
/// </para>
/// <para>
/// Invocation count by stack behavior — same rule as
/// <see cref="IEnchantmentRegistration.ModifyDynamicVar"/>:
/// </para>
/// <list type="bullet">
///   <item><c>MergeAmount</c>: once per active gameplay slice (write per-stack formulas).</item>
///   <item><c>ExistenceStack</c> / <c>DuplicateInstance</c>: once per type (scale by
///   <c>snapshot.ActiveInstanceCount</c> if needed).</item>
/// </list>
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true, Inherited = false)]
public sealed class ModifyDynamicVarAttribute : Attribute
{
    /// <summary>
    /// The dynamic-variable key this method contributes to. Matched case-insensitively against
    /// the runtime <c>DynamicVar.Name</c> (which is PascalCase in vanilla — e.g. <c>"Damage"</c>,
    /// <c>"Block"</c>); authors may write the lowercase placeholder form (<c>"damage"</c>) here.
    /// </summary>
    public string VarKey { get; }

    public ModifyDynamicVarAttribute(string varKey)
    {
        if (string.IsNullOrEmpty(varKey))
        {
            throw new ArgumentException("VarKey must be a non-empty string.", nameof(varKey));
        }

        VarKey = varKey;
    }
}
