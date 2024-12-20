using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
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

    private static bool MousePosOverride;

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
    private static int onGuiDepth;

    private static bool IsOnGUI
    {
        get => onGuiDepth > 0;
    }

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
                    "Minimum value for auto-calculated DPI scale, defaults to 0.8\n"
                        + "so it wouldn't scale down further if screen size goes down 1536x864 .",
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
                    "Fixed DPI scale, set AutoDpiScale=false to activate,\n"
                        + "the size of IMGUI elements wouldn't be changed on screen size change.",
                    scaleRange
                )
            )
            .Value;

        FixedApiScale = ClampScale(FixedApiScale);
        ZoomScale = ClampScale(ZoomScale);

        MousePosOverride = Config
            .Bind(
                "MouseOverride",
                "MousePosOverride",
                true,
                "Scale down absolute mouse input position, fixes UI window drag-resizing on some plugins.\n"
                    + "However it also breaks mouse inspection in RuntimeUnityEditor.\n"
                    + "This is too hacky to handle so I am not going to work around it here.\n"
                    + "And I believe this should be handled in plugins to not referencing absolute mouse input position for UI drawing.\n"
                    + "Hence the option is reserved for disabling mouse position overriding once this case has been handled in those plugins."
            )
            .Value;

        AutoOverrideNs = Config
            .Bind(
                "ScreenOverride",
                "AutoOverrideNs",
                true,
                "We scale down Screen.width and Screen.height to have window border not overflowing after re-scaling up, in MonoBehavior.OnGUI callback.\n"
                    + "However some plugin uses Screen.width and Screen.height out side of OnGUI, causing window border to overflow.\n"
                    + "To work around that we collect namespaces of registered MonoBehavior.OnGUI in plugins,\n"
                    + "and when other method in the same namespace or child namespace calls Screen.width and/or Screen.height,\n"
                    + "we check call stack and scale down screen size regardless."
            )
            .Value;

        var ExtraIncludeNs = Config
            .Bind(
                "ScreenOverride",
                "ExtraOverrideNsRegex",
                "^ConfigurationManager(\\..+)?",
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
            .Bind(
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
            .Bind(
                "Opt-out",
                "OptOutNsRegex",
                "^(SVS_CharaFilter|CharaFilterCore)(\\..+)?",
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

        if (IsOnGUI)
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

            void addOverrideCache(bool state)
            {
                // handle racing
                try
                {
                    lock (overrideCache)
                        overrideCache.Add(ns, state);
                }
                catch (Exception)
                {
                    return;
                }

                if (state)
                {
                    Log.LogInfo($"Force overriding Screen size for namespace {ns}");
                }
                else
                {
                    Log.LogInfo($"Exclude namespace {ns} from non-OnGUI Screen size overriding");
                }
            }

            foreach (var pat in overrideNsPat)
            {
                if (pat.IsMatch(ns))
                {
                    if (
                        excludeNsPat != null && excludeNsPat.IsMatch(ns)
                        || optOutNsPat != null && optOutNsPat.IsMatch(ns)
                    )
                    {
                        // add override false and continue outer loop
                        break;
                    }
                    else
                    {
                        addOverrideCache(true);
                        return true;
                    }
                }
            }

            addOverrideCache(false);
        }

        return false;
    }

    private static class HookMonoBehavior
    {
        internal static void OnGUIPrefix(ref Matrix4x4 __state)
        {
            onGuiDepth = 1;

            __state = GUI.matrix;

            var scale = Scale;

            // Scale globally for this component
            GUI.matrix *= Matrix4x4.Scale(new Vector3(scale, scale, 1.0f));
        }

        internal static void OnGUIPostfix(Matrix4x4 __state)
        {
            GUI.matrix = __state;
            onGuiDepth = 0;
        }

        internal static void UpdatePrefix()
        {
            onGuiDepth = 1;
        }

        internal static void UpdatePostfix()
        {
            onGuiDepth = 0;
        }
    }

    private static MethodBase FindMethod(Type ty, string name)
    {
        do
        {
            var method = ty.GetMethod(
                name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                [],
                []
            );
            if (method != null)
                return method;

            ty = ty.BaseType;
        } while (ty != null);

        return null;
    }

    public static void PatchComponent(Type ty)
    {
        if (!ty.IsSubclassOf(typeof(MonoBehaviour)))
            return;

        var onGUI = FindMethod(ty, "OnGUI");
        if (onGUI == null)
            return;

        if (!handledComps.Add(ty.FullName))
            return;

        if (ty.Namespace != null)
        {
            if (optOutNsPat != null && optOutNsPat.IsMatch(ty.Namespace))
                return;

            if (AutoOverrideNs)
            {
                overrideNsPat.Add(new Regex($"^{ty.Namespace}(\\..+)?"));
            }
        }

        static void PatchMethod(MethodBase method, string prefixName, string postFixName)
        {
            var prefix = new HarmonyMethod(
                AccessTools.Method(typeof(HookMonoBehavior), prefixName)
            );
            var postfix = new HarmonyMethod(
                AccessTools.Method(typeof(HookMonoBehavior), postFixName)
            );
            harmony.Patch(method, prefix: prefix, postfix: postfix, finalizer: postfix);
        }

        Log.LogInfo($"Patch OnGUI of {ty.FullName}");

        PatchMethod(
            onGUI,
            nameof(HookMonoBehavior.OnGUIPrefix),
            nameof(HookMonoBehavior.OnGUIPostfix)
        );

        // There are plugins retrieve Screen size in *Update callbacks.
        // And check stack trace in Screen hooks for each frame could be expensive, so also hook these.
        List<string> methods = ["Update", "FixedUpdate", "LateUpdate"];

        foreach (var methodName in methods)
        {
            var method = FindMethod(ty, methodName);
            if (method == null)
                continue;

            Log.LogDebug($"Patch {methodName} of {ty.FullName}");

            PatchMethod(
                method,
                nameof(HookMonoBehavior.UpdatePrefix),
                nameof(HookMonoBehavior.UpdatePostfix)
            );
        }
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(GUI), nameof(GUI.DoWindow))]
    [HarmonyPatch(typeof(GUI), nameof(GUI.DoModalWindow))]
    // Seems GUILayout.DoWindow on HoneyCome does not reach GUI.DoWindow,
    // so also hook this.
    [HarmonyPatch(typeof(GUILayout), nameof(GUILayout.DoWindow))]
    private static void DoWindow(ref GUI.WindowFunction __2)
    {
        WrapWindowFunc(ref __2);
    }

    private static void WrapWindowFunc(ref GUI.WindowFunction func)
    {
        if (!IsOnGUI)
            return;

        GUI.WindowFunction orig = func;

#pragma warning disable IDE0004 // we need explict conversion for IL2CPP
        func = (GUI.WindowFunction)(
#pragma warning restore IDE0004
            delegate(int id)
            {
                onGuiDepth++;
                try
                {
                    orig.Invoke(id);
                }
                finally
                {
                    onGuiDepth--;
                }
            }
        );
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

    [HarmonyPostfix]
    [HarmonyPatch(typeof(Input), nameof(Input.mousePosition), MethodType.Getter)]
    private static void LegacyMousePosition(ref Vector3 __result)
    {
        if (MousePosOverride && IsOnGUI)
        {
            var scale = Scale;
            __result.x /= scale;
            __result.y /= scale;
        }
    }
}
