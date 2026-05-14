using MultiEnchantmentMod.Api;

// The mod's own assembly declares the API version it is built against. This makes Discovery /
// scan / version-check paths uniform: the mod doesn't get to skip the same check it imposes on
// downstream consumers.
[assembly: EnchantmentApiCompatibility(MultiEnchantmentApiVersion.Current)]
