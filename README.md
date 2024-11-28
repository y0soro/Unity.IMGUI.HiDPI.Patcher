# Unity.IMGUI.HiDPI.Patcher

A BepInEx patcher that fixes IMGUI UI on HiDPI screen.

Demo image of non-loss scaling.

![Zoom Comparison](./docs/zoom_comparison.png)

## How?

Actually it's very simple.

So the IMGUI utility actually come with a transformation matrix as `GUI.matrix`, and with

```cs
var scale = 2.0f;
GUI.matrix *= Matrix4x4.Scale(new Vector3(scale, scale, 1.0f));
```

in OnGUI() callback, all following UI draws would have UI scaled by `2.0` automatically.

And event like mouse position would still works. It's that simple.

However if UI component draws elements with absolute size referencing screen rectangle,
those elements would probably overflow out of screen.

To address this issue, we hook on `Screen.width` and `Screen.height` to return scale downed screen size by the same factor we used for scaling up UI elements. And such UI component would drawing elements in this scale downed region, and when re-scaled up, every element would stays in the same relative screen position but with pixels-per-UI scale up.

## Implement HiDPI scaling for your IMGUI application

If you are a plugin author, it should be easy to adapt this method to your OnGUI loop.
And this don't requires you any structural change for UI drawing.

Here are the basic steps to implement HiDPI scaling.

1. Calculate DPI `scale` with `Math.Max(Screen.height / 1080.0f, Screen.width / 1920.0f)`, your UI is presumably designed for 1920x1080 so it's reasonable to just calculate scale factor from that.

2. Scale `GUI.matrix` by `scale` using transformation matrix in the head of `MonoBehaviour.OnGUI` callback, all UI elements draw after this would be scaled up transparently from top-left to bottom-right.

3. Instead of referencing screen border with `Screen.width` and `Screen.height`, reference scale downed version of them, i.e. `Screen.width/scale` and `Screen.height/scale`. After UI scaling in step 2, the final rendering result would have your UI element referencing to actual screen border.

4. If you use coordinate conversion utilities like `GUIUtility.ScreenToGUIPoint` and `GUIUtility.GUIToScreenPoint`, set `GUI.matrix` to `Matrix4x4.identity` otherwise you would get scaled results. Just like other drawing operations, they are transparent to final scaling, so as these coordinate should remain unscaled. Also make sure save to revert back previous "scaling" `GUI.matrix`.

5. If your plugin needs to coexists with this patcher, edit the config of `IMGUI.HiDPI.Patcher.*.IL2CPP.cfg` and add the namespace matcher of your UI component to `OptOutNsRegex` in `[Opt-out]`.

And that's it!
