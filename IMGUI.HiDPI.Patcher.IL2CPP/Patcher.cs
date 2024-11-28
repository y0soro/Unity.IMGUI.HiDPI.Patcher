using System;
using BepInEx.Logging;
using BepInEx.Preloader.Core.Patching;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using IMGUI.HiDPI.Patcher.Common;

namespace IMGUI.HiDPI.Patcher.IL2CPP;

[PatcherPluginInfo(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class Patcher : BasePatcher
{
    private static new ManualLogSource Log;
    private static Harmony harmony;

    public override void Finalizer()
    {
        Log = base.Log;

        harmony = new Harmony(MyPluginInfo.PLUGIN_GUID);

        Hooks.InitAndApply(Log, Config, harmony, typeof(ComponentHook));
    }

    private static class ComponentHook
    {
        [HarmonyPostfix]
        [HarmonyPatch(
            typeof(ClassInjector),
            nameof(ClassInjector.RegisterTypeInIl2Cpp),
            [typeof(Type), typeof(RegisterTypeOptions)]
        )]
        private static void RegisterType(Type __0)
        {
            Hooks.PatchComponent(__0);
        }
    }
}
