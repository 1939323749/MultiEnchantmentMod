// using System.Threading.Tasks;
// using MegaCrit.Sts2.Core.Commands;
// using MegaCrit.Sts2.Core.Entities.Cards;
// using MegaCrit.Sts2.Core.Models;
//
// namespace MultiEnchantmentMod;
//
// // Example source: reference snippets for third-party mods that want transform/replacement flows
// // to preserve MultiEnchantmentMod stacks.
// internal static class ExampleTransformApiUsage
// {
//     public static async Task ReplaceCardKeepingCompatibleEnchantments(CardModel original, CardModel replacement)
//     {
//         MultiEnchantmentTransformApi.CopyCompatibleEnchantments(original, replacement);
//         await CardCmd.Transform(original, replacement);
//     }
//
//     public static CardTransformation CreatePreviewableTransformation(CardModel original, CardModel replacement)
//     {
//         return MultiEnchantmentTransformApi.CreateCompatibleTransformation(original, replacement);
//     }
// }
