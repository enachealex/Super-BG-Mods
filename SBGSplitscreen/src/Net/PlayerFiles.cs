using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx;
using HarmonyLib;
using Steamworks;

namespace SBGSplitscreen
{
    /// <summary>
    /// Each joined player keeps their own look. The outfit and customization-menu files that the
    /// game keeps in Steam Cloud stay player 1's; joined players read and write the same file names
    /// in BepInEx/SBGSplitscreen/player2 (3, 4) instead. A player with no file yet starts with the
    /// game's default outfit.
    /// </summary>
    internal static class PlayerFiles
    {
        private static string LocalPath(string fileName)
        {
            string dir = Path.Combine(Paths.BepInExRootPath, "SBGSplitscreen", $"player{Contexts.Current + 1}");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, fileName);
        }

        public static bool FileExists(string filename)
        {
            return Contexts.Current == 0 ? SteamRemoteStorage.FileExists(filename) : File.Exists(LocalPath(filename));
        }

        public static byte[] FileRead(string filename)
        {
            return Contexts.Current == 0 ? SteamRemoteStorage.FileRead(filename) : File.ReadAllBytes(LocalPath(filename));
        }

        public static bool FileWrite(string filename, byte[] data)
        {
            if (Contexts.Current == 0)
            {
                return SteamRemoteStorage.FileWrite(filename, data);
            }
            File.WriteAllBytes(LocalPath(filename), data);
            return true;
        }
    }

    /// <summary>Swaps the Steam Cloud calls in the outfit load/save methods for PlayerFiles.</summary>
    [HarmonyPatch]
    internal static class PlayerFilesPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(PlayerCosmetics), nameof(PlayerCosmetics.LoadCosmetics));
            yield return AccessTools.Method(typeof(PlayerCosmetics), "SaveCosmeticsInternal");
            yield return AccessTools.Method(typeof(PlayerCustomizationMenu), "LoadPersistentData");
            yield return AccessTools.Method(typeof(PlayerCustomizationMenu), "SavePersistentData");
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
        {
            foreach (var instruction in instructions)
            {
                if ((instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt)
                    && instruction.operand is MethodInfo called && called.DeclaringType == typeof(SteamRemoteStorage))
                {
                    var replacement = AccessTools.Method(typeof(PlayerFiles), called.Name);
                    if (replacement != null && replacement.ReturnType == called.ReturnType
                        && replacement.GetParameters().Select(p => p.ParameterType).SequenceEqual(called.GetParameters().Select(p => p.ParameterType)))
                    {
                        instruction.opcode = OpCodes.Call;
                        instruction.operand = replacement;
                    }
                    else
                    {
                        Plugin.Log.LogWarning($"Left {called.Name} in {__originalMethod.Name} unchanged (signature differs).");
                    }
                }
                yield return instruction;
            }
        }
    }
}
