using System;
using System.Globalization;
using System.IO;
using System.Text;
using Unity.Pipeline.Commands;
using UnityEngine;

namespace UnityCliMacros
{
    /// A PNG of the scene seen from any point, without moving a real camera.
    ///
    /// The built-in `screenshot` and `capture_game_view` only render what an existing camera already
    /// sees, so checking "does the seat read correctly from the driver's eye" or "is the hood clear of
    /// the engine when open" meant moving the player's camera and putting it back. This spins up a
    /// throwaway camera at the given point, aims it, renders once and destroys it - nothing in the scene
    /// changes, nothing is dirtied.
    ///
    /// Post-processing is turned on reflectively when URP is present, so the shot matches what a player
    /// camera with volumes would show. Works in Edit and Play mode.
    public static class M_RenderView
    {
        [CliCommand("m_render_view",
            "Render the scene from an arbitrary position/look-at point with a temporary camera and return the PNG path. For checking a viewpoint (interior, close-up, top-down) without moving any scene camera.")]
        public static string RenderView(
            [CliArg("pos", "Camera position in world space as 'x,y,z'.", Required = true)]
            string pos,
            [CliArg("look", "World point to aim at as 'x,y,z'. Ignored when rot is given.")]
            string look = "",
            [CliArg("rot", "Camera rotation as Euler degrees 'x,y,z'. Overrides look.")]
            string rot = "",
            [CliArg("fov", "Vertical field of view in degrees.")]
            float fov = 60f,
            [CliArg("w", "Output width in pixels.")]
            int w = 960,
            [CliArg("h", "Output height in pixels.")]
            int h = 540,
            [CliArg("post", "Enable URP post-processing (volumes) on the temporary camera.")]
            bool post = true,
            [CliArg("near", "Near clip plane - lower it for tight interior shots.")]
            float near = 0.02f,
            [CliArg("output", "Output PNG path (absolute, or relative to the project root). Defaults to a timestamped file under <project>/Temp/pipeline-screenshots/.")]
            string output = "")
        {
            if (!TryParse(pos, out Vector3 position))
            {
                Debug.LogError("[m_render_view] pos must be 'x,y,z', got '" + pos + "'");
                return "ERR bad-pos";
            }

            Quaternion rotation;
            if (!string.IsNullOrWhiteSpace(rot))
            {
                if (!TryParse(rot, out Vector3 euler))
                {
                    Debug.LogError("[m_render_view] rot must be 'x,y,z', got '" + rot + "'");
                    return "ERR bad-rot";
                }
                rotation = Quaternion.Euler(euler);
            }
            else if (!string.IsNullOrWhiteSpace(look))
            {
                if (!TryParse(look, out Vector3 target) || (target - position).sqrMagnitude < 1e-6f)
                {
                    Debug.LogError("[m_render_view] look must be 'x,y,z' and differ from pos, got '" + look + "'");
                    return "ERR bad-look";
                }
                rotation = Quaternion.LookRotation(target - position, Vector3.up);
            }
            else
            {
                Debug.LogError("[m_render_view] pass look or rot");
                return "ERR need-look-or-rot";
            }

            w = Mathf.Clamp(w, 16, 4096);
            h = Mathf.Clamp(h, 16, 4096);

            GameObject go = new GameObject("__m_render_view") { hideFlags = HideFlags.HideAndDontSave };
            RenderTexture rt = null;
            Texture2D tex = null;
            try
            {
                go.transform.SetPositionAndRotation(position, rotation);
                Camera cam = go.AddComponent<Camera>();
                cam.fieldOfView = fov;
                cam.nearClipPlane = Mathf.Max(0.001f, near);
                cam.farClipPlane = 1000f;
                cam.allowHDR = true;
                bool postApplied = post && TryEnablePost(go);

                rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32);
                cam.targetTexture = rt;
                cam.Render();

                RenderTexture previous = RenderTexture.active;
                RenderTexture.active = rt;
                tex = new Texture2D(w, h, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                tex.Apply();
                RenderTexture.active = previous;
                cam.targetTexture = null;

                string path = ResolveOutputPath(output);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllBytes(path, tex.EncodeToPNG());

                var fb = new StringBuilder();
                fb.Append(path.Replace('\\', '/'));
                fb.Append(" | ").Append(w).Append('x').Append(h).Append(" fov=").Append(fov.ToString("0.#", CultureInfo.InvariantCulture));
                fb.Append(" post=").Append(postApplied ? "on" : "off");
                fb.Append(" rot=").Append(Format(rotation.eulerAngles));
                return fb.ToString();
            }
            catch (Exception exception)
            {
                Debug.LogError("[m_render_view] Render failed: " + exception.Message);
                return "ERR render-failed";
            }
            finally
            {
                if (tex != null) UnityEngine.Object.DestroyImmediate(tex);
                if (rt != null) { rt.Release(); UnityEngine.Object.DestroyImmediate(rt); }
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        // URP keeps post-processing on UniversalAdditionalCameraData, which this assembly does not
        // reference - resolve it reflectively so the macro still compiles in Built-in or HDRP projects.
        private static bool TryEnablePost(GameObject go)
        {
            Type type = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                type = asm.GetType("UnityEngine.Rendering.Universal.UniversalAdditionalCameraData");
                if (type != null) break;
            }
            if (type == null) return false;

            Component data = go.GetComponent(type) ?? go.AddComponent(type);
            var prop = type.GetProperty("renderPostProcessing");
            if (prop == null || !prop.CanWrite) return false;
            prop.SetValue(data, true);
            return true;
        }

        private static bool TryParse(string text, out Vector3 value)
        {
            value = default;
            if (string.IsNullOrWhiteSpace(text)) return false;
            string[] parts = text.Trim().Trim('(', ')').Split(',');
            if (parts.Length != 3) return false;
            float[] f = new float[3];
            for (int i = 0; i < 3; i++)
                if (!float.TryParse(parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out f[i]))
                    return false;
            value = new Vector3(f[0], f[1], f[2]);
            return true;
        }

        private static string Format(Vector3 v) =>
            v.x.ToString("0.#", CultureInfo.InvariantCulture) + "," +
            v.y.ToString("0.#", CultureInfo.InvariantCulture) + "," +
            v.z.ToString("0.#", CultureInfo.InvariantCulture);

        private static string ResolveOutputPath(string output)
        {
            if (string.IsNullOrWhiteSpace(output))
                output = Path.Combine("Temp", "pipeline-screenshots",
                    "render_view_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + ".png");

            return Path.IsPathRooted(output)
                ? output
                : Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), output));
        }
    }
}
