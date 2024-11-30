using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using IMGUI.HiDPI.Patcher.Common;
using Mono.Cecil;
using UnityEngine;

namespace IMGUI.HiDPI.Patcher.Mono;

public static class Patcher
{
    // Needed to be a valid patcher
    public static IEnumerable<string> TargetDLLs { get; } = [];

    // Needed to be a valid patcher
    public static void Patch(AssemblyDefinition assembly) { }

    private static readonly ManualLogSource Log = BepInEx.Logging.Logger.CreateLogSource(
        MyPluginInfo.PLUGIN_NAME
    );

    private static readonly ConfigFile Config = new(
        Path.Combine(Paths.ConfigPath, $"{MyPluginInfo.PLUGIN_GUID}.cfg"),
        false
    );

    private static Harmony harmony;

    private static readonly HashSet<string> patchedComps = [];

    public static void Finish()
    {
        harmony = new Harmony(MyPluginInfo.PLUGIN_GUID);

        harmony.PatchAll(typeof(InitHook));
    }

    private static class InitHook
    {
        [HarmonyPostfix]
        [HarmonyPatch(typeof(Chainloader), nameof(Chainloader.Initialize))]
        private static void Initialize()
        {
            // XXX: Patching Hooks here also works. But just follow BepInEx.Debug/StartupProfiler's practice.
            //      Is that because this make sure that Chainloader is further initialized?
            harmony.PatchAll(typeof(StartHook));
        }
    }

    private static class StartHook
    {
        [HarmonyPrefix]
        [HarmonyPatch(typeof(Chainloader), nameof(Chainloader.Start))]
        private static void Start()
        {
            Hooks.InitAndApply(Log, Config, harmony, typeof(ComponentHook));
        }
    }

    private static class ComponentHook
    {
        [HarmonyPostfix]
        [HarmonyPatch(typeof(GameObject), nameof(GameObject.AddComponent), [typeof(Type)])]
        private static void AddComponent(Type __0)
        {
            try
            {
                Hooks.PatchComponent(__0);
            }
            catch (Exception e)
            {
                Log.LogError(e);
            }
        }
    }
}
