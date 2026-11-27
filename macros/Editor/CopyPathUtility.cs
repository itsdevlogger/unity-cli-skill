using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEditor.ShortcutManagement;
using UnityEngine;

namespace Omnix.Editor
{
    public static class CopyPathUtility
    {
        private const string COMPONENT_SEPARATOR = "::";
        private const string PATH_SEPARATOR = "/";

        [Shortcut("Tools/Copy Path (Asset or GameObject)", KeyCode.C, ShortcutModifiers.Action | ShortcutModifiers.Alt)]
        private static void CopySelectionPath()
        {
            Object[] selectedObjects = Selection.objects;

            if (selectedObjects == null || selectedObjects.Length == 0)
            {
                EditorUtility.DisplayDialog("Cant Copy Path", "Nothing is selected.", "Okay");
                return;
            }

            List<string> paths = new List<string>();

            foreach (Object selectedObject in selectedObjects)
            {
                string path = GetPath(selectedObject);

                if (string.IsNullOrEmpty(path))
                {
                    var objectName = (selectedObject != null ? selectedObject.name : "null");
                    EditorUtility.DisplayDialog("Cant Copy Path", $"[CopyPathUtility.CopySelectionPath] Could not resolve a path for '{objectName}'.", "Okay");
                    continue;
                }

                paths.Add(path);
            }

            if (paths.Count == 0)
            {
                return;
            }

            string result = string.Join("\n", paths);
            EditorGUIUtility.systemCopyBuffer = result;
        }

        [MenuItem("CONTEXT/Component/Copy Path to Component")]
        private static void CopyComponentPath(MenuCommand command)
        {
            Component component = command.context as Component;

            if (component == null)
            {
                EditorUtility.DisplayDialog("Cant Copy Path", "[CopyPathUtility.CopyComponentPath] Context is not a valid component.", "Okay");
                return;
            }

            string result = GetComponentPath(component);
            EditorGUIUtility.systemCopyBuffer = result;
        }

        /// <summary>
        /// Returns the asset path for persistent assets, the hierarchy path for scene GameObjects, or the component path for scene components.
        /// </summary>
        public static string GetPath(Object target)
        {
            if (target == null)
            {
                return null;
            }

            if (!EditorUtility.IsPersistent(target))
            {
                if (target is GameObject gameObject)
                {
                    return GetGameObjectPath(gameObject);
                }

                if (target is Component component)
                {
                    return GetComponentPath(component);
                }

                return null;
            }

            string assetPath = AssetDatabase.GetAssetPath(target);
            return string.IsNullOrEmpty(assetPath) ? null : assetPath;
        }

        /// <summary>
        /// Returns the hierarchy path of a GameObject, from its root down to itself, separated by slashes.
        /// </summary>
        public static string GetGameObjectPath(GameObject gameObject)
        {
            if (gameObject == null)
            {
                return string.Empty;
            }

            StringBuilder builder = new StringBuilder(gameObject.name);
            Transform current = gameObject.transform.parent;

            while (current != null)
            {
                builder.Insert(0, PATH_SEPARATOR);
                builder.Insert(0, current.name);
                current = current.parent;
            }

            return builder.ToString();
        }

        /// <summary>
        /// Returns the hierarchy path of the component's GameObject followed by a colon and the component type name.
        /// </summary>
        public static string GetComponentPath(Component component)
        {
            if (component == null)
            {
                return string.Empty;
            }

            return GetGameObjectPath(component.gameObject) + COMPONENT_SEPARATOR + component.GetType().Name;
        }
    }
}