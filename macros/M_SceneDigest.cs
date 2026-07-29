using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using Unity.Pipeline.Commands;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

/// A readable substitute for get_scene_hierarchy.
///
/// `get_scene_hierarchy` returns the whole tree and is enormous on a real scene, so the standing
/// advice is to avoid it — which leaves no cheap way to see the shape of a scene at all. This walks
/// the scene once and returns a depth-limited outline annotated with each object's components,
/// collapsing runs of identical siblings so a hundred props cost one line.
///
/// It also fills a real gap: `find_gameobjects --name` is an exact whole-name match, so there is no
/// substring search anywhere in the command set. `filter` is that search, and it matches component
/// type names as well as object names.
///
/// Every name and path here is emitted byte-for-byte and flagged when it carries whitespace that
/// cannot be retyped, because both are used as lookup keys: an exact-match API plus a name that was
/// quietly tidied on the way out is a null reference waiting to be written somewhere.
public static class M_SceneDigest
{
    private class Node
    {
        public string name;
        public string comps;
        public bool active;
        public bool prefab;

        /// This node's own name survives being retyped by hand
        public bool clean;

        /// Every name on the way down here does, so the whole path can be retyped
        public bool pathClean;

        public int hidden;
        public int depth;
        public string path;
        public List<Node> kids = new List<Node>();
        public string signature;
        public string nameSignature;
        public int size = -1;
    }

    /// Shared name prefix long enough to call two siblings the same family
    private const int MIN_PREFIX_LENGTH = 4;

    /// Depth used when a filter is set: a search should reach the whole scene
    private const int FILTER_DEPTH = 99;

    /// Smallest subtree worth replacing with a pointer at an earlier identical one. Below this the
    /// pointer costs about as much as the lines it saves.
    private const int MIN_DEDUPE_SIZE = 3;

    private const int MAX_CAP = 20000;

    private static readonly Regex TRAILING_INDEX = new Regex(@"[ _\-\.]?\d+$");

    private const string LEGEND =
        "s=scene{n name,p path,r roots,t transforms} o=outline lines '<depth> <label>'; " +
        "label markers: ! inactive, @ prefab-instance root, ~ name has odd whitespace so copy it " +
        "exactly instead of retyping it, xN collapsed identical siblings, (=path) subtree identical " +
        "to the one at that path and not repeated, +N children not shown, [..] components (Transform omitted)";

    private const string FILTER_LEGEND =
        "h=hits o=matching hierarchyPaths, usable verbatim as a target; " +
        "markers: ! inactive, @ prefab-instance root, ~ some name on this path has odd whitespace so " +
        "copy the path exactly instead of retyping it, [..] components";

    [CliCommand("m_scene_digest", "Compact annotated outline of a scene, or a substring search over object and component names. Use instead of get_scene_hierarchy.")]
    public static string SceneDigest(
        [CliArg("depth", "Tree depth. 0 means auto: 3 normally, whole scene when a filter is set.")] int depth = 0,
        [CliArg("root", "Start at one object instead of the scene roots, e.g. /GrayboxMap")] string root = "",
        [CliArg("scene", "Open scene by name or path. Defaults to the active scene.")] string scene = "",
        [CliArg("filter", "Substring matched against object names AND component type names. Returns a flat list of hierarchy paths instead of an outline.")] string filter = "",
        [CliArg("collapse", "Collapse runs of N or more identical siblings. 0 disables collapsing.")] int collapse = 3,
        [CliArg("cap", "Maximum objects to walk before truncating")] int cap = 4000)
    {
        var hasFilter = !string.IsNullOrEmpty(filter);

        if (depth <= 0)
        {
            depth = hasFilter ? FILTER_DEPTH : 3;
        }

        cap = Mathf.Clamp(cap, 1, MAX_CAP);

        var target = SceneManager.GetActiveScene();

        if (!string.IsNullOrEmpty(scene))
        {
            var found = false;

            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var candidate = SceneManager.GetSceneAt(i);

                if (candidate.name == scene || candidate.path == scene)
                {
                    target = candidate;
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                Debug.LogError($"[M_SceneDigest.SceneDigest] no open scene named or pathed '{scene}'");
                return "ERR no-such-scene";
            }
        }

        if (!target.IsValid())
        {
            Debug.LogError("[M_SceneDigest.SceneDigest] the resolved scene is not valid");
            return "ERR invalid-scene";
        }

        var roots = target.GetRootGameObjects();
        var totalTransforms = 0;

        foreach (var gameObject in roots)
        {
            totalTransforms += gameObject.GetComponentsInChildren<Transform>(true).Length;
        }

        var starts = new List<Transform>();

        if (string.IsNullOrEmpty(root))
        {
            foreach (var gameObject in roots)
            {
                starts.Add(gameObject.transform);
            }
        }
        else
        {
            var want = root.StartsWith("/") ? root.Substring(1) : root;

            foreach (var gameObject in roots)
            {
                foreach (var transform in gameObject.GetComponentsInChildren<Transform>(true))
                {
                    if (PathOf(transform) == want)
                    {
                        starts.Add(transform);
                    }
                }
            }

            if (starts.Count == 0)
            {
                Debug.LogError($"[M_SceneDigest.SceneDigest] no object at '{root}'");
                return "ERR no-such-root";
            }
        }

        var walked = 0;
        var hiddenByDepth = 0;
        var forest = new List<Node>();

        foreach (var start in starts)
        {
            var node = Build(start, 0, depth, cap, "", true, ref walked, ref hiddenByDepth);

            if (node != null)
            {
                forest.Add(node);
            }
        }

        var lines = new List<string>();

        if (hasFilter)
        {
            CollectMatches(forest, filter.ToLowerInvariant(), lines);
            return FormatFilter(target, roots.Length, totalTransforms, lines);
        }

        Emit(forest, collapse <= 0 ? int.MaxValue : collapse, lines, new Dictionary<string, string>());

        return FormatOutline(target, roots.Length, totalTransforms, lines, walked, hiddenByDepth, cap);
    }

    /// Builds the subtree under transform, returning null once the cap is exhausted so a runaway
    /// scene truncates instead of returning a payload nobody can read.
    private static Node Build(Transform transform, int depth, int maxDepth, int cap, string parentPath,
        bool parentPathClean, ref int walked, ref int hiddenByDepth)
    {
        if (walked >= cap)
        {
            return null;
        }

        walked++;

        var name = transform.name;
        var clean = JsonHelper.IsClean(name);
        var node = new Node
        {
            name = name,
            comps = ComponentsOf(transform),
            active = transform.gameObject.activeSelf,
            prefab = PrefabUtility.IsAnyPrefabInstanceRoot(transform.gameObject),
            clean = clean,
            pathClean = parentPathClean && clean,
            depth = depth,
            path = parentPath + "/" + name
        };

        if (depth >= maxDepth)
        {
            node.hidden = transform.childCount;
            hiddenByDepth += transform.childCount;
            return node;
        }

        for (var i = 0; i < transform.childCount; i++)
        {
            var child = Build(transform.GetChild(i), depth + 1, maxDepth, cap, node.path, node.pathClean,
                ref walked, ref hiddenByDepth);

            if (child == null)
            {
                node.hidden += transform.childCount - i;
                break;
            }

            node.kids.Add(child);
        }

        return node;
    }

    private static string ComponentsOf(Transform transform)
    {
        var names = new List<string>();
        var missing = 0;

        foreach (var component in transform.GetComponents<Component>())
        {
            if (component == null)
            {
                missing++;
                continue;
            }

            var name = component.GetType().Name;

            // Every object has one, so naming it in every line is pure noise
            if (name == "Transform" || name == "RectTransform")
            {
                continue;
            }

            names.Add(name);
        }

        if (missing > 0)
        {
            names.Add("MISSING_SCRIPT x" + missing);
        }

        return string.Join(",", names.ToArray());
    }

    private static string PathOf(Transform transform)
    {
        var path = transform.name;
        var cursor = transform.parent;

        while (cursor != null)
        {
            path = cursor.name + "/" + path;
            cursor = cursor.parent;
        }

        return path;
    }

    /// The recursive shape of a subtree: this node's components plus every descendant's shape.
    /// Two siblings only collapse when these match exactly, so collapsing can never hide a child
    /// that differs somewhere deep.
    private static string Signature(Node node)
    {
        if (node.signature == null)
        {
            var sb = new StringBuilder();
            sb.Append(node.comps).Append('(');

            for (var i = 0; i < node.kids.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(',');
                }

                sb.Append(Signature(node.kids[i]));
            }

            sb.Append(')');
            node.signature = sb.ToString();
        }

        return node.signature;
    }

    /// Emits nodes as depth-prefixed lines, folding runs of identical adjacent siblings.
    ///
    /// A run folds only when the whole subtree shape matches AND the names are related — same stem
    /// (Crate_01, Crate_02) or a shared prefix. Without the name test an adjacent Floor and Wall
    /// carrying the same components would merge, and the Floor would silently vanish.
    ///
    /// `seen` folds repeats that are not siblings at all: two environment roots built room-for-room
    /// from each other cost the tree twice, and sibling collapsing cannot see across that distance.
    private static void Emit(List<Node> nodes, int collapse, List<string> lines, Dictionary<string, string> seen)
    {
        var i = 0;

        while (i < nodes.Count)
        {
            var node = nodes[i];
            var run = new List<Node> { node };
            var j = i + 1;

            while (j < nodes.Count)
            {
                var other = nodes[j];

                if (Signature(other) != Signature(node)
                    || other.active != node.active
                    || other.prefab != node.prefab
                    || other.hidden != node.hidden)
                {
                    break;
                }

                run.Add(other);
                j++;
            }

            if (run.Count >= collapse && Related(run))
            {
                var first = run[0].name;
                var last = run[run.Count - 1].name;
                var title = first == last ? first : first + ".." + last;

                // The shared subtree is identical across the run, so show it once
                EmitOne(node, title + " x" + run.Count, collapse, lines, seen);

                i = j;
                continue;
            }

            EmitOne(node, node.name, collapse, lines, seen);

            i++;
        }
    }

    /// Emits one node's line, then either its children or a pointer at the earlier subtree they
    /// duplicate.
    ///
    /// The registry key includes every descendant's name, not just the shape `Signature` compares —
    /// two subtrees with matching components but different names are different subtrees, and folding
    /// them would hide the names, which is the whole thing this output exists to report.
    private static void EmitOne(Node node, string title, int collapse, List<string> lines,
        Dictionary<string, string> seen)
    {
        if (node.kids.Count == 0)
        {
            lines.Add(Line(node, title));
            return;
        }

        var worthFolding = Size(node) >= MIN_DEDUPE_SIZE;
        var key = worthFolding ? ChildrenKey(node) : null;

        if (worthFolding)
        {
            string original;

            if (seen.TryGetValue(key, out original))
            {
                lines.Add(Line(node, title + " (=" + original + ")"));
                return;
            }

            seen[key] = node.path;
        }

        lines.Add(Line(node, title));
        Emit(node.kids, collapse, lines, seen);
    }

    /// The name-inclusive signature of a node's children, which is what makes two subtrees
    /// interchangeable for a reader
    private static string ChildrenKey(Node node)
    {
        var sb = new StringBuilder();

        foreach (var kid in node.kids)
        {
            sb.Append(NameSignature(kid)).Append(',');
        }

        return sb.ToString();
    }

    private static string NameSignature(Node node)
    {
        if (node.nameSignature == null)
        {
            var sb = new StringBuilder();

            sb.Append(node.name).Append('|').Append(node.comps).Append('|')
                .Append(node.active ? '1' : '0').Append(node.prefab ? '1' : '0')
                .Append('|').Append(node.hidden).Append('(');

            for (var i = 0; i < node.kids.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(',');
                }

                sb.Append(NameSignature(node.kids[i]));
            }

            sb.Append(')');
            node.nameSignature = sb.ToString();
        }

        return node.nameSignature;
    }

    /// Descendant count, memoized
    private static int Size(Node node)
    {
        if (node.size < 0)
        {
            var total = 0;

            foreach (var kid in node.kids)
            {
                total += 1 + Size(kid);
            }

            node.size = total;
        }

        return node.size;
    }

    private static bool Related(List<Node> run)
    {
        var stems = new HashSet<string>();

        foreach (var node in run)
        {
            stems.Add(TRAILING_INDEX.Replace(node.name, ""));
        }

        if (stems.Count == 1)
        {
            return true;
        }

        var prefix = run[0].name;

        foreach (var node in run)
        {
            prefix = CommonPrefix(prefix, node.name);

            if (prefix.Length < MIN_PREFIX_LENGTH)
            {
                return false;
            }
        }

        return true;
    }

    private static string CommonPrefix(string a, string b)
    {
        var i = 0;

        while (i < a.Length && i < b.Length && a[i] == b[i])
        {
            i++;
        }

        return a.Substring(0, i);
    }

    private static string Line(Node node, string title)
    {
        var sb = new StringBuilder();
        sb.Append(node.depth).Append(' ');
        AppendFlags(sb, node);
        sb.Append(title);

        if (node.comps.Length > 0)
        {
            sb.Append(" [").Append(node.comps).Append(']');
        }

        if (node.hidden > 0)
        {
            sb.Append(" +").Append(node.hidden);
        }

        return sb.ToString();
    }

    private static void AppendFlags(StringBuilder sb, Node node)
    {
        if (!node.active)
        {
            sb.Append('!');
        }

        if (node.prefab)
        {
            sb.Append('@');
        }

        // Preserving the name is not enough on its own — a trailing space is invisible even when it
        // survived the trip, so the reader has to be told the name needs copying rather than retyping
        if (!node.clean)
        {
            sb.Append('~');
        }
    }

    private static void CollectMatches(List<Node> nodes, string needle, List<string> lines)
    {
        foreach (var node in nodes)
        {
            if (node.name.ToLowerInvariant().Contains(needle) || node.comps.ToLowerInvariant().Contains(needle))
            {
                var sb = new StringBuilder();

                if (!node.active)
                {
                    sb.Append('!');
                }

                if (node.prefab)
                {
                    sb.Append('@');
                }

                // Here the whole path is the payload, so any unclean name anywhere along it makes the
                // path one to copy rather than retype — not just an unclean name on the match itself
                if (!node.pathClean)
                {
                    sb.Append('~');
                }

                sb.Append(node.path);

                if (node.comps.Length > 0)
                {
                    sb.Append(" [").Append(node.comps).Append(']');
                }

                lines.Add(sb.ToString());
            }

            CollectMatches(node.kids, needle, lines);
        }
    }

    /// Compact single-line JSON, for the same reason M_FindMacro uses it: the CLI renders the
    /// return value as one cell of a tab-separated row, so tabs and newlines are destroyed in
    /// transit. Depth rides as a numeric prefix on each line rather than as indentation.
    private static string FormatOutline(Scene scene, int rootCount, int totalTransforms,
        List<string> lines, int walked, int hiddenByDepth, int cap)
    {
        var sb = new StringBuilder();

        AppendHeader(sb, LEGEND, scene, rootCount, totalTransforms);
        AppendLines(sb, lines);

        if (walked >= cap)
        {
            sb.Append(",\"note\":\"truncated at cap ").Append(cap).Append(" - raise cap or narrow root\"");
        }
        else if (hiddenByDepth > 0)
        {
            sb.Append(",\"note\":\"").Append(hiddenByDepth).Append(" deeper objects not shown - raise depth\"");
        }

        sb.Append('}');

        return sb.ToString();
    }

    private static string FormatFilter(Scene scene, int rootCount, int totalTransforms, List<string> lines)
    {
        var sb = new StringBuilder();

        AppendHeader(sb, FILTER_LEGEND, scene, rootCount, totalTransforms);
        sb.Append(",\"h\":").Append(lines.Count);
        AppendLines(sb, lines);
        sb.Append('}');

        return sb.ToString();
    }

    private static void AppendHeader(StringBuilder sb, string legend, Scene scene, int rootCount, int totalTransforms)
    {
        sb.Append("{\"k\":\"");
        JsonHelper.AppendProse(sb, legend);
        sb.Append("\",\"s\":{\"n\":\"");
        AppendJsonString(sb, scene.name);
        sb.Append("\",\"p\":\"");
        AppendJsonString(sb, scene.path);
        sb.Append("\",\"r\":").Append(rootCount);
        sb.Append(",\"t\":").Append(totalTransforms);
        sb.Append('}');
    }

    private static void AppendLines(StringBuilder sb, List<string> lines)
    {
        sb.Append(",\"o\":[");

        for (var i = 0; i < lines.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            sb.Append('"');
            AppendJsonString(sb, lines[i]);
            sb.Append('"');
        }

        sb.Append(']');
    }

    /// Every line carries object names and hierarchy paths, which have to come back byte-for-byte to
    /// be usable as a target, so lines go through the lossless writer. This used to collapse
    /// whitespace: a root named "Env_Divya " was reported as "Env_Divya", and the caller then looked
    /// up a name that does not exist and wired the null it got back.
    private static void AppendJsonString(StringBuilder sb, string value)
    {
        JsonHelper.AppendString(sb, value);
    }
}
