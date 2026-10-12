using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Unity.Pipeline.Commands;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace UnityCliMacros
{
    /// A PNG of an Editor window, so custom editor UI can actually be looked at.
    ///
    /// The built-in `screenshot` macro covers the Scene and Game views, which leaves every inspector,
    /// custom EditorWindow, settings provider and tool panel unverifiable — the exact surfaces an editor
    /// tooling session spends its time building. Reading the UIElements tree back tells you the elements
    /// exist; it does not tell you that a label is clipped, a row has collapsed to zero height, or two
    /// controls are overlapping.
    ///
    /// Works by reading the pixels the window occupies straight off the screen, so the Editor must be
    /// visible and not minimised, and whatever is drawn on top of the window is what gets captured. The
    /// window is focused first for that reason.
    public static class M_CaptureWindow
    {
        private const string LEGEND =
            "p=png path t=window title y=window type r=captured screen rect w/h=pixel size; " +
            "pixels are read off the live screen, so the Editor must be visible and not minimised, and " +
            "anything drawn over the window (a popup, another window) is captured too. " +
            "Read the returned file to look at it.";

        [CliCommand("m_capture_window",
            "Screenshot an Editor window (inspector, custom EditorWindow, tool panel) to a PNG and return its path, for verifying custom editor UI that the Scene/Game view screenshot cannot reach.")]
        public static string CaptureWindow(
            [CliArg("q", "Window to capture: part of its tab title ('Pivot++ Demo') or its type name ('SceneView', 'InspectorWindow'). Omit to capture the focused window.")]
            string q = "",
            [CliArg("output", "Output PNG path (absolute, or relative to the project root). Defaults to a timestamped file under <project>/Temp/pipeline-screenshots/.")]
            string output = "",
            [CliArg("focus", "Focus the window before capturing. Leave on unless you are deliberately capturing it while something else has focus.")]
            bool focus = true,
            [CliArg("m", "When several windows match, how many to list in the error instead of guessing.")]
            int m = 8)
        {
            // The pixels come off the live screen, so anything in front of the Editor is what would be
            // captured. Without this guard the macro happily returns a PNG of whatever app is on top -
            // a plausible-looking file with none of the window in it, which is the worst failure
            // available here. Refuse instead and say what to do about it.
            if (!InternalEditorUtility.isApplicationActive)
            {
                Debug.LogError("[m_capture_window] The Unity Editor is not the foreground application, " +
                               "so the screen read would capture whatever is in front of it.");
                return "ERR unity-not-foreground - click the Unity Editor window, then retry";
            }

            EditorWindow window = Resolve(q, m, out string failure);
            if (window == null)
            {
                Debug.LogError("[m_capture_window] " + failure);
                return "ERR " + failure;
            }

            if (focus)
            {
                window.Focus();
                window.Repaint();
                // One editor tick so the focus change and repaint are on screen before the pixels are read.
                InternalEditorUtility.RepaintAllViews();
            }

            Rect position = window.position;
            int width = Mathf.RoundToInt(position.width);
            int height = Mathf.RoundToInt(position.height);

            if (width <= 0 || height <= 0)
            {
                Debug.LogError("[m_capture_window] Window has no size - it may be minimised or closed.");
                return "ERR empty-window-rect";
            }

            Color[] pixels;
            try
            {
                // ReadScreenPixel wants the BOTTOM-left corner in screen space, while EditorWindow.position
                // is measured from the top-left of the virtual desktop.
                Vector2 bottomLeft = new Vector2(position.x, GetScreenHeight() - position.y - position.height);
                pixels = InternalEditorUtility.ReadScreenPixel(bottomLeft, width, height);
            }
            catch (Exception exception)
            {
                Debug.LogError("[m_capture_window] Could not read screen pixels: " + exception.Message);
                return "ERR screen-read-failed";
            }

            if (pixels == null || pixels.Length != width * height)
            {
                Debug.LogError("[m_capture_window] Screen read returned no usable pixels.");
                return "ERR screen-read-empty";
            }

            string path = ResolveOutputPath(output, window);

            try
            {
                Texture2D texture = new Texture2D(width, height, TextureFormat.RGB24, false);
                texture.SetPixels(pixels);
                texture.Apply();

                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllBytes(path, texture.EncodeToPNG());
                UnityEngine.Object.DestroyImmediate(texture);
            }
            catch (Exception exception)
            {
                Debug.LogError("[m_capture_window] Could not write the PNG: " + exception.Message);
                return "ERR write-failed";
            }

            StringBuilder sb = new StringBuilder();
            sb.Append("{\"k\":").Append(Quote(LEGEND));
            sb.Append(",\"p\":").Append(Quote(path.Replace('\\', '/')));
            sb.Append(",\"t\":").Append(Quote(window.titleContent != null ? window.titleContent.text : ""));
            sb.Append(",\"y\":").Append(Quote(window.GetType().Name));
            sb.Append(",\"r\":\"").Append(position.x.ToString("F0")).Append(',').Append(position.y.ToString("F0")).Append('"');
            sb.Append(",\"w\":").Append(width).Append(",\"h\":").Append(height).Append('}');
            return sb.ToString();
        }

        private static EditorWindow Resolve(string query, int max, out string failure)
        {
            failure = null;

            EditorWindow[] all = Resources.FindObjectsOfTypeAll<EditorWindow>()
                .Where(w => w != null && w.position.width > 0f)
                .ToArray();

            if (string.IsNullOrWhiteSpace(query))
            {
                EditorWindow focused = EditorWindow.focusedWindow;
                if (focused != null)
                    return focused;

                failure = "no window is focused - pass q with part of a window title or type name";
                return null;
            }

            query = query.Trim();

            List<EditorWindow> matches = all.Where(w =>
                    string.Equals(w.GetType().Name, query, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(Title(w), query, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (matches.Count == 0)
            {
                matches = all.Where(w =>
                        w.GetType().Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                        Title(w).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                    .ToList();
            }

            if (matches.Count == 1)
                return matches[0];

            if (matches.Count == 0)
            {
                failure = "no open window matches '" + query + "'. Open: " +
                          string.Join(", ", all.Take(Mathf.Max(1, max)).Select(Describe));
                return null;
            }

            // Several hits: a docked-but-hidden duplicate is common, so prefer the one on screen.
            EditorWindow visible = matches.FirstOrDefault(w => w == EditorWindow.focusedWindow);
            if (visible != null)
                return visible;

            failure = matches.Count + " windows match '" + query + "': " +
                      string.Join(", ", matches.Take(Mathf.Max(1, max)).Select(Describe)) +
                      ". Pass an exact title or type name.";
            return null;
        }

        private static string Title(EditorWindow window) =>
            window.titleContent != null && window.titleContent.text != null ? window.titleContent.text : "";

        private static string Describe(EditorWindow window) => Title(window) + " (" + window.GetType().Name + ")";

        private static float GetScreenHeight()
        {
            // Multi-monitor layouts put windows at negative or very large y, so the primary display's
            // resolution is the only reference point available from managed code.
            return Screen.currentResolution.height;
        }

        private static string ResolveOutputPath(string output, EditorWindow window)
        {
            if (string.IsNullOrWhiteSpace(output))
            {
                string safe = new string(Describe(window).Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
                output = Path.Combine("Temp", "pipeline-screenshots",
                    safe + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png");
            }

            return Path.IsPathRooted(output)
                ? output
                : Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), output));
        }

        private static string Quote(string value)
        {
            StringBuilder sb = new StringBuilder(value.Length + 2);
            sb.Append('"');
            foreach (char c in value)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default: sb.Append(c); break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }
    }
}
