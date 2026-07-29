using System.Collections.Generic;
using System.Text;
using Unity.Pipeline.Commands;
using Unity.Pipeline.Models;
using UnityEditor;
using UnityEngine;

/// What one object is actually wired to, and what a prefab instance has diverged on.
///
/// Two questions come up on almost every authoring job and neither had an answer here. The first is
/// "does this component point at the right thing" — after any duplication, references are as likely
/// to point back at the original as at the copy, and nothing about the scene outline shows it. The
/// second is "what does this prefab instance override", which the skill's prefab rule requires you to
/// know before editing an instance, and which `m_scene_digest` can only answer with a bare `@`.
///
/// Both were previously ten lines of SerializedObject iteration in `eval`, rewritten per session.
public static class M_Inspect
{
    /// Remaining entry allowance, and whether anything was actually dropped. Kept apart so the
    /// output only claims truncation when something really was left out, rather than whenever the
    /// allowance happened to land exactly on the last entry.
    private class Budget
    {
        public int left;
        public bool truncated;

        public bool Take()
        {
            if (left <= 0)
            {
                truncated = true;
                return false;
            }

            left--;
            return true;
        }
    }

    private const int MAX_ENTRIES = 400;

    private const string LEGEND =
        "t=target{n name,p hierarchyPath,a active} c=components[{t type,r refs,v values}] " +
        "r/v entries are [property,value]; a ref value is the target's hierarchyPath (:Type when the " +
        "reference is to a component rather than the GameObject), an assetPath for assets, or null when " +
        "unassigned - all usable verbatim as a target; " +
        "pf=prefab{root instance root path, src asset path, ov overridden objects, add added components, " +
        "rem removed components, mods overridden [owner type,property path]} - absent when the object is " +
        "not a prefab instance, and carries only 'root' for an object inside one, since the overrides " +
        "belong to the instance rather than to each object in it; " +
        "nomatch=objects skipped because no component matched the type filter";

    [CliCommand("m_inspect", "What a GameObject's components reference and what a prefab instance overrides. Returns object-reference properties resolved to hierarchy paths, plus prefab source and override detail.")]
    public static string Inspect(
        [CliArg("target", "The GameObject to inspect. Accepts a hierarchyPath, instanceId, guid or globalId; a Component handle resolves to its owner.", Required = true)] ObjectRef target,
        [CliArg("type", "Only report components whose type name contains this substring")] string type = "",
        [CliArg("refs", "Report object-reference properties")] bool refs = true,
        [CliArg("values", "Also report non-reference serialized values, which is a lot more output")] bool values = false,
        [CliArg("children", "Also inspect every descendant, not just this object")] bool children = false,
        [CliArg("max", "Maximum property entries reported before truncating")] int max = 120)
    {
        GameObject gameObject;
        string error;

        if (!RefaranceHelper.TryResolveGameObject(target, out gameObject, out error))
        {
            return JsonHelper.Err("M_Inspect.Inspect", "no-such-target", error);
        }

        max = Mathf.Clamp(max, 1, MAX_ENTRIES);

        if (!refs && !values)
        {
            return JsonHelper.Err("M_Inspect.Inspect", "nothing-requested",
                "both refs and values were false, so there is nothing to report");
        }

        var targets = new List<GameObject> { gameObject };

        if (children)
        {
            foreach (var transform in gameObject.GetComponentsInChildren<Transform>(true))
            {
                if (transform.gameObject != gameObject)
                {
                    targets.Add(transform.gameObject);
                }
            }
        }

        var sb = new StringBuilder();

        sb.Append("{\"k\":\"");
        JsonHelper.AppendProse(sb, LEGEND);
        sb.Append("\",\"o\":[");

        var budget = new Budget { left = max };

        var wroteObject = false;
        var skipped = 0;

        foreach (var candidate in targets)
        {
            var rendered = RenderObject(candidate, type, refs, values, budget);

            if (rendered == null)
            {
                skipped++;
                continue;
            }

            if (wroteObject)
            {
                sb.Append(',');
            }

            sb.Append(rendered);
            wroteObject = true;
        }

        sb.Append(']');

        if (skipped > 0)
        {
            sb.Append(",\"nomatch\":").Append(skipped);
        }

        if (budget.truncated)
        {
            sb.Append(",\"note\":\"truncated at max ").Append(max).Append(" entries - raise max, set a type filter, or drop values\"");
        }

        sb.Append('}');

        return sb.ToString();
    }

    /// Renders one object, or null when a type filter is set and nothing on it matched. Dropping the
    /// misses matters on `children`: reporting every descendant as an empty shell buries the handful
    /// that carry the component actually being asked about.
    private static string RenderObject(GameObject gameObject, string type, bool refs, bool values,
        Budget budget)
    {
        var components = new StringBuilder();
        var wroteComponent = false;

        foreach (var component in gameObject.GetComponents<Component>())
        {
            // A missing script has no serialized data to report and no type name to filter on;
            // m_scene_digest is where MISSING_SCRIPT is meant to surface
            if (component == null)
            {
                continue;
            }

            var typeName = component.GetType().Name;

            if (type.Length > 0 && typeName.IndexOf(type, System.StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            if (wroteComponent)
            {
                components.Append(',');
            }

            components.Append("{\"t\":\"");
            JsonHelper.AppendString(components, typeName);
            components.Append('"');

            AppendProperties(components, component, refs, values, budget);

            components.Append('}');
            wroteComponent = true;
        }

        if (!wroteComponent && type.Length > 0)
        {
            return null;
        }

        var sb = new StringBuilder();

        sb.Append("{\"t\":{\"n\":\"");
        JsonHelper.AppendString(sb, gameObject.name);
        sb.Append("\",\"p\":\"");
        JsonHelper.AppendString(sb, RefaranceHelper.HierarchyPath(gameObject));
        sb.Append("\",\"a\":").Append(gameObject.activeSelf ? "true" : "false");
        sb.Append('}');

        AppendPrefab(sb, gameObject);

        sb.Append(",\"c\":[").Append(components).Append("]}");

        return sb.ToString();
    }

    /// Walks the component's serialized properties once, splitting them into references and plain
    /// values. `EnterChildren = false` on a resolved reference stops the walk descending into the
    /// referenced object's own fields, which is where a naive iteration turns into thousands of lines.
    private static void AppendProperties(StringBuilder sb, Component component, bool refs, bool values,
        Budget budget)
    {
        var refEntries = new List<KeyValuePair<string, string>>();
        var valueEntries = new List<KeyValuePair<string, string>>();

        var serialized = new SerializedObject(component);
        var property = serialized.GetIterator();
        var enterChildren = true;

        while (property.NextVisible(enterChildren))
        {
            enterChildren = true;

            // Every component has one and it only ever points at its own type, which is already
            // reported — on a busy object that is a wasted entry per component
            if (property.propertyPath == "m_Script")
            {
                continue;
            }

            if (property.propertyType == SerializedPropertyType.ObjectReference)
            {
                enterChildren = false;

                if (refs && budget.Take())
                {
                    refEntries.Add(new KeyValuePair<string, string>(property.propertyPath,
                        RefaranceHelper.DescribeTarget(property.objectReferenceValue)));
                }

                continue;
            }

            if (!values)
            {
                continue;
            }

            var rendered = RenderValue(property);

            if (rendered == null)
            {
                continue;
            }

            if (budget.Take())
            {
                valueEntries.Add(new KeyValuePair<string, string>(property.propertyPath, rendered));
            }
        }

        serialized.Dispose();

        if (refs)
        {
            AppendEntries(sb, ",\"r\":", refEntries);
        }

        if (values)
        {
            AppendEntries(sb, ",\"v\":", valueEntries);
        }
    }

    /// Null for the property types worth skipping rather than rendering badly: a container's header
    /// row carries no value of its own, and an arbitrary managed reference has no compact form.
    private static string RenderValue(SerializedProperty property)
    {
        switch (property.propertyType)
        {
            case SerializedPropertyType.Integer:
                return property.intValue.ToString();
            case SerializedPropertyType.Boolean:
                return property.boolValue ? "true" : "false";
            case SerializedPropertyType.Float:
                return property.floatValue.ToString("0.####");
            case SerializedPropertyType.String:
                return property.stringValue;
            case SerializedPropertyType.Enum:
                return property.enumValueIndex >= 0 && property.enumValueIndex < property.enumNames.Length
                    ? property.enumNames[property.enumValueIndex]
                    : property.intValue.ToString();
            case SerializedPropertyType.Vector2:
                return property.vector2Value.ToString("0.###");
            case SerializedPropertyType.Vector3:
                return property.vector3Value.ToString("0.###");
            case SerializedPropertyType.Vector4:
                return property.vector4Value.ToString("0.###");
            case SerializedPropertyType.Quaternion:
                return property.quaternionValue.eulerAngles.ToString("0.###") + " (euler)";
            case SerializedPropertyType.Color:
                return "#" + ColorUtility.ToHtmlStringRGBA(property.colorValue);
            case SerializedPropertyType.LayerMask:
                return "mask:" + property.intValue;
            case SerializedPropertyType.Rect:
                return property.rectValue.ToString();
            case SerializedPropertyType.Bounds:
                return property.boundsValue.ToString();
            case SerializedPropertyType.ArraySize:
                return property.intValue.ToString();
            default:
                return null;
        }
    }

    /// Prefab facts for the instance this object belongs to. Overrides are recorded against the
    /// instance root, so that is what has to be asked — querying a child reports nothing.
    ///
    /// A child of an instance gets only a pointer at its root. The override detail belongs to the
    /// instance, not to each object in it, and repeating the same list once per descendant was most of
    /// the payload of any `children` call.
    private static void AppendPrefab(StringBuilder sb, GameObject gameObject)
    {
        if (!PrefabUtility.IsPartOfPrefabInstance(gameObject))
        {
            return;
        }

        var root = PrefabUtility.GetNearestPrefabInstanceRoot(gameObject);

        if (root == null)
        {
            return;
        }

        sb.Append(",\"pf\":{\"root\":\"");
        JsonHelper.AppendString(sb, RefaranceHelper.HierarchyPath(root));
        sb.Append('"');

        if (root != gameObject)
        {
            sb.Append('}');
            return;
        }

        sb.Append(",\"src\":\"");
        JsonHelper.AppendString(sb, PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(gameObject));
        sb.Append('"');

        sb.Append(",\"ov\":").Append(PrefabUtility.GetObjectOverrides(root).Count);
        sb.Append(",\"add\":").Append(PrefabUtility.GetAddedComponents(root).Count);
        sb.Append(",\"rem\":").Append(PrefabUtility.GetRemovedComponents(root).Count);

        var modifications = PrefabUtility.GetPropertyModifications(root);

        if (modifications != null && modifications.Length > 0)
        {
            var paths = new List<KeyValuePair<string, string>>();

            foreach (var modification in modifications)
            {
                if (modification == null)
                {
                    continue;
                }

                var owner = modification.target == null ? "?" : modification.target.GetType().Name;
                paths.Add(new KeyValuePair<string, string>(owner, modification.propertyPath));
            }

            AppendEntries(sb, ",\"mods\":", paths);
        }

        sb.Append('}');
    }

    private static void AppendEntries(StringBuilder sb, string key, List<KeyValuePair<string, string>> entries)
    {
        sb.Append(key).Append('[');

        for (var i = 0; i < entries.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            sb.Append("[\"");
            JsonHelper.AppendString(sb, entries[i].Key);
            sb.Append("\",");

            if (entries[i].Value == null)
            {
                sb.Append("null");
            }
            else
            {
                sb.Append('"');
                JsonHelper.AppendString(sb, entries[i].Value);
                sb.Append('"');
            }

            sb.Append(']');
        }

        sb.Append(']');
    }
}
