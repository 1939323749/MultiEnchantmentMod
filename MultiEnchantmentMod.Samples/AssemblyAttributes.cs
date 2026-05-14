using MultiEnchantmentMod.Api;

// Third-party mods declare which API version they were built against. The mod scanner
// consults this attribute before discovering any enchantment registrations; missing means
// "best effort, no version pinning".
[assembly: EnchantmentApiCompatibility(MultiEnchantmentApiVersion.Current)]
