using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.RegularExpressions;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

#pragma warning disable IDE0130
namespace IMGUI.HiDPI.Patcher.Common;

#pragma warning restore IDE0130

public static class Hooks
{
    private static ManualLogSource Log;
    private static ConfigFile Config;
    private static Harmony harmony;

    private static bool AutoApiScale;
    private static float AutoApiScaleMin;
    private static float FixedApiScale;
    private static float ZoomScale;

    private static bool AutoOverrideNs;

    private static readonly HashSet<Regex> overrideNsPat = [];

    private static Regex excludeNsPat = null;

    private static Regex optOutNsPat = null;

    private static readonly HashSet<string> handledComps = [];

    private static readonly Dictionary<string, bool> overrideCache = [];

    // Reverse patch on native method seems not be working on BepInEx 5,
    // use thread local to disable reverse-scaling of screen size when original
    // screen size is needed for DPI scale calculation.
    [ThreadStatic]
    private static bool forceDisableOverride;

    [ThreadStatic]
    private static bool isOnGUI;

    private static float ClampScale(float f)
    {
        return Mathf.Clamp(f, 0.1f, 10.0f);
    }

    public static void InitAndApply(
        ManualLogSource Log_,
        ConfigFile Config_,
        Harmony harmony_,
        Type ComponentPatch
    )
    {
        Log = Log_;
        Config = Config_;
        harmony = harmony_;

        var scaleRange = new AcceptableValueRange<float>(0.1f, 10.0f);

        AutoApiScale = Config
            .Bind(
                "General",
                "AutoDpiScale",
                true,
                "Calculate DPI scale from screen size, \nDPI Scale = Max(Width / 1920, Height / 1080) ."
            )
            .Value;
        AutoApiScaleMin = Config
            .Bind(
                "General",
                "AutoDpiScaleMin",
                0.8f,
                new ConfigDescription(
                    "Minimum value for auto-calculated DPI scale, defaults to 1.0\n"
                        + "so it wouldn't scale down further if screen size goes down 1920x1080.",
                    scaleRange
                )
            )
            .Value;
        ZoomScale = Config
            .Bind(
                "General",
                "ZoomScale",
                1.0f,
                new ConfigDescription(
                    "Relative zooming scale, would multiplies DPI scale.",
                    scaleRange
                )
            )
            .Value;
        FixedApiScale = Config
            .Bind(
                "General",
                "FixedDpiScale",
                1.5f,
                new ConfigDescription(
                    "Fixed DPI scale, set AutoDpiScale=false to activate, \n"
                        + "the size of IMGUI elements wouldn't be changed on screen size change.",
                    scaleRange
                )
            )
            .Value;

        FixedApiScale = ClampScale(FixedApiScale);
        ZoomScale = ClampScale(ZoomScale);

        AutoOverrideNs = Config
            .Bind(
                "ScreenOverride",
                "AutoOverrideNs",
                true,
                "We scale down Screen.width and Screen.height to have window border not overflowing after re-scaling up, in MonoBehavior.OnGUI callback.\n"
                    + "However some plugin uses Screen.width and Screen.height out side of OnGUI, causing window border to overflow.\n"
                    + "To work around that we collect namespaces of registered MonoBehavior.OnGUI in plugins,\n"
                    + "and when other method in the same namespace or child namespace calls Screen.width and/or Screen.height,\n"
                    + "we check call stack and scaled down screen size regardless."
            )
            .Value;

        var ExtraIncludeNs = Config
            .Bind<string>(
                "ScreenOverride",
                "ExtraOverrideNsRegex",
                "(^ConfigurationManager(\\..+)?|^ClothingStateMenu(\\..+)?)",
                "Extra namespace pattern for Screen overriding, specify .NET regular expressions here."
            )
            .Value;

        if (ExtraIncludeNs.Length > 0)
        {
            try
            {
                overrideNsPat.Add(new Regex(ExtraIncludeNs));
            }
            catch (Exception) { }
        }

        var ExcludeNs = Config
            .Bind<string>(
                "ScreenOverride",
                "ExcludeNsRegex",
                "^UnityEngine(\\..+)?",
                "Exclude matched namespace from Screen overriding, specify .NET regular expressions here."
            )
            .Value;

        if (ExcludeNs.Length > 0)
        {
            try
            {
                excludeNsPat = new Regex(ExcludeNs);
            }
            catch (Exception) { }
        }

        var OptOutNs = Config
            .Bind<string>(
                "Opt-out",
                "OptOutNsRegex",
                "",
                "Opt-out scaling for matched namespace, specify namespace of IMGUI MonoBehavior component\n"
                    + "if it has implemented HiDPI scaling like this patcher."
            )
            .Value;

        if (OptOutNs.Length > 0)
        {
            try
            {
                optOutNsPat = new Regex(OptOutNs);
            }
            catch (Exception) { }
        }

        harmony.PatchAll(typeof(Hooks));
        harmony.PatchAll(ComponentPatch);
    }

    private static float Scale
    {
        get
        {
            float scale;

            if (AutoApiScale)
            {
                forceDisableOverride = true;
                scale =
                    Math.Max(
                        AutoApiScaleMin,
                        Math.Max(Screen.height / 1080.0f, Screen.width / 1920.0f)
                    ) * ZoomScale;
                forceDisableOverride = false;
            }
            else
            {
                scale = FixedApiScale * ZoomScale;
            }

            return ClampScale(scale);
        }
    }

    private static bool ShouldOverrideScreen()
    {
        if (forceDisableOverride)
            return false;

        if (isOnGUI)
            return true;

        if (!AutoOverrideNs && overrideNsPat.Count == 0)
        {
            return false;
        }

        var st = new StackTrace();

        for (int i = 2; i < st.FrameCount; i++)
        {
            var sf = st.GetFrame(i);
            if (sf == null)
            {
                continue;
            }
            var mt = sf.GetMethod();
            if (mt == null || mt.DeclaringType == null)
            {
                continue;
            }

            var ns = mt.DeclaringType.Namespace;
            if (ns == null)
            {
                continue;
            }

            if (overrideCache.TryGetValue(ns, out bool cacheState))
            {
                if (cacheState)
                    return true;
                else
                    continue;
            }

            foreach (var pat in overrideNsPat)
            {
                if (pat.IsMatch(ns))
                {
                    if (excludeNsPat != null && excludeNsPat.IsMatch(ns))
                    {
                        Log.LogInfo($"Exclude namespace {ns} from Screen size overriding");
                        overrideCache.Add(ns, false);
                        continue;
                    }
                    else
                    {
                        Log.LogInfo($"Overriding Screen size for namespace {ns}");
                        overrideCache.Add(ns, true);
                        return true;
                    }
                }
            }

            Log.LogInfo($"Exclude namespace {ns} from Screen size overriding");
            overrideCache.Add(ns, false);
        }

        return false;
    }

    private static class HookOnGUI
    {
        internal static void Prefix(ref Matrix4x4 __state)
        {
            isOnGUI = true;

            __state = GUI.matrix;

            var scale = Scale;

            // Scale globally for this component
            GUI.matrix *= Matrix4x4.Scale(new Vector3(scale, scale, 1.0f));
        }

        internal static void Postfix(Matrix4x4 __state)
        {
            GUI.matrix = __state;

            isOnGUI = false;
        }
    }

    public static void PatchComponent(Type ty)
    {
        if (!ty.IsSubclassOf(typeof(MonoBehaviour)))
            return;

        var onGUI = AccessTools.Method(ty, "OnGUI", []);
        if (onGUI == null)
            return;

        if (!handledComps.Add(ty.FullName))
            return;

        if (ty.Namespace != null)
        {
            if (optOutNsPat != null && optOutNsPat.IsMatch(ty.Namespace))
                return;

            if (AutoOverrideNs && ty.Namespace != null)
            {
                overrideNsPat.Add(new Regex($"^{ty.Namespace}(\\..+)?"));
            }
        }

        Log.LogInfo($"Patch OnGUI of {ty.FullName}");

        var onGUIPrefix = new HarmonyMethod(AccessTools.Method(typeof(HookOnGUI), "Prefix"));
        var onGUIPostfix = new HarmonyMethod(AccessTools.Method(typeof(HookOnGUI), "Postfix"));
        harmony.Patch(onGUI, onGUIPrefix, onGUIPostfix);
    }

    // We want GUI components to calculate positions in unscaled coordinate space,
    // so wrap these calls with identity GUI.matrix. This also seems to be a common practice in
    // UnityCsReference to set an identity GUI.matrix before doing coordinate conversion, see
    // <https://github.com/Unity-Technologies/UnityCsReference/blob/6000.1/Modules/GraphViewEditor/Views/GraphView.cs#L1466-L1468>
    // Also screen point conversion seems to be broken with non-identity matrix.
    // (e.g. GUIToScreenPoint(ScreenToGUIPoint(pos)) != pos)
    [HarmonyPrefix]
    [HarmonyPatch(typeof(GUIUtility), nameof(GUIUtility.ScreenToGUIPoint), [typeof(Vector2)])]
    [HarmonyPatch(typeof(GUIUtility), nameof(GUIUtility.GUIToScreenPoint), [typeof(Vector2)])]
    // XXX: Should we wrap other conversion utilities in GUIUtility and GUIClip?
    // [HarmonyPatch(typeof(GUIClip), nameof(GUIClip.ClipToWindow), [typeof(Vector2)])]
    // [HarmonyPatch(typeof(GUIClip), nameof(GUIClip.ClipToWindow), [typeof(Rect)])]
    // [HarmonyPatch(typeof(GUIClip), nameof(GUIClip.UnclipToWindow), [typeof(Vector2)])]
    // [HarmonyPatch(typeof(GUIClip), nameof(GUIClip.UnclipToWindow), [typeof(Rect)])]
    private static void GUIToScreenPointPrefix(ref Matrix4x4 __state)
    {
        __state = GUI.matrix;
        GUI.matrix = Matrix4x4.identity;
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(GUIUtility), nameof(GUIUtility.ScreenToGUIPoint), [typeof(Vector2)])]
    [HarmonyPatch(typeof(GUIUtility), nameof(GUIUtility.GUIToScreenPoint), [typeof(Vector2)])]
    private static void GUIToScreenPointPostfix(Matrix4x4 __state)
    {
        GUI.matrix = __state;
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(Screen), nameof(Screen.width), MethodType.Getter)]
    private static void Width(ref int __result)
    {
        if (ShouldOverrideScreen())
        {
            __result = (int)Mathf.Round(__result / Scale);
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(Screen), nameof(Screen.height), MethodType.Getter)]
    private static void Height(ref int __result)
    {
        if (ShouldOverrideScreen())
        {
            __result = (int)Mathf.Round(__result / Scale);
        }
    }
}
