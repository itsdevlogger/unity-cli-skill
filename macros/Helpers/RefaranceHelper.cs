using System.Text;
using Unity.Pipeline.Editor.Authoring;
using Unity.Pipeline.Models;
using UnityEditor;
using UnityEngine;

/// Object-handle plumbing shared by the macros in this folder. Not a macro itself — no [CliCommand].
///
/// Declaring an argument as `ObjectRef` gets the whole handle grammar for free — hierarchy path,
/// asset path, guid, instanceId, globalId — because the package's JSON converter parses the string
/// before the macro ever sees it. What the package does not hand over is the unwrap that every
/// GameObject command needs: its own `GameObjectCommands.ResolveGameObject` is `internal`, so it is
/// unreachable from Assembly-CSharp-Editor and has to live here once instead of in every macro.
public static class RefaranceHelper
{
    /// Resolves a handle to a GameObject, accepting a Component handle and taking its owner.
    /// The error text is meant to be returned to the caller, not swallowed.
    public static bool TryResolveGameObject(ObjectRef handle, out GameObject gameObject, out string error)
    {
        gameObject = null;

        UnityEngine.Object resolved;

        if (!ObjectResolver.TryResolve(handle, out resolved, out error))
        {
            return false;
        }

        gameObject = resolved as GameObject;

        if (gameObject == null)
        {
            var component = resolved as Component;

            if (component != null)
            {
                gameObject = component.gameObject;
            }
        }

        if (gameObject == null)
        {
            error = $"handle resolved to a {resolved.GetType().Name}, which is not a GameObject or Component";
            return false;
        }

        return true;
    }

    /// Absolute scene path with a leading slash, in the form every macro here emits and the handle
    /// grammar accepts back
    public static string HierarchyPath(GameObject gameObject)
    {
        var sb = new StringBuilder(gameObject.name);
        var cursor = gameObject.transform.parent;

        while (cursor != null)
        {
            sb.Insert(0, cursor.name + "/");
            cursor = cursor.parent;
        }

        return "/" + sb;
    }

    /// Renders whatever an object reference points at as something that can be looked up again:
    /// an asset path for assets, a hierarchy path for scene objects, with the component type appended
    /// when the reference is to a component rather than to the GameObject itself.
    ///
    /// A reference field holding a component and one holding its GameObject look identical in the
    /// Inspector and behave differently, so the distinction is worth the characters.
    public static string DescribeTarget(UnityEngine.Object target)
    {
        if (target == null)
        {
            return null;
        }

        var assetPath = AssetDatabase.GetAssetPath(target);

        if (!string.IsNullOrEmpty(assetPath))
        {
            return assetPath + " (" + target.GetType().Name + ")";
        }

        var gameObject = target as GameObject;

        if (gameObject != null)
        {
            return HierarchyPath(gameObject);
        }

        var component = target as Component;

        if (component != null)
        {
            return HierarchyPath(component.gameObject) + ":" + component.GetType().Name;
        }

        return target.name + " (" + target.GetType().Name + ")";
    }
}
