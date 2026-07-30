# The eval escape hatch — running C# inside the Editor

`eval` compiles and runs C# in the live Editor with full access to the UnityEngine and UnityEditor
APIs. It handles everything the dedicated tools don't: bulk edits, conditional logic, component
swaps, cross-referencing, and any custom query. It is also the cheapest way to do anything
touching more than a few objects, because one call replaces N round trips.

Before writing one, check `m_find_macro` — a macro that already does the job is cheaper and
better-tested than a fresh snippet. And if the snippet is something you'd want again, make it a
macro in the shared library at `C:/unity-cli-skill/macros/` instead (see SKILL.md).

Two snippets in particular are already macros, because they got rewritten once per session before
they were: iterating `SerializedObject` to list object references is `m_inspect`, and matching on
`GetType().Name.Contains(...)` because you don't know a type's namespace is `m_type_info`.

## The workflow

Write the snippet to a `.cs` file in your scratch directory — **never under `Assets/`**, where a
stray script triggers a recompile and may not even compile as a MonoBehaviour — then:

```bash
unity command eval_file --file "<scratchdir>/snippet.cs"
```

**Prefer `eval_file` over `eval --code`.** The response echoes every parameter back, so `--code`
bills you for your entire script twice; `eval_file` echoes only the filename. It also keeps C#
string interpolation (`$"..."`) away from shell quoting, which fights back on Windows and wastes
retries. Iterating on a compile error becomes an edit rather than a re-paste.

`eval --code` is fine for a genuine one-liner. Use PowerShell for it, and single-quote the code so
`$"..."` survives:

```powershell
unity command eval --code 'return UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;'
```

Both take `--timeout <ms>` (default 5000). Raise it for a sweep over thousands of assets — but
narrowing the scope is usually the better answer, since `eval` blocks the Editor while it runs.

## What the compiler gives you

The snippet is a **method body**, not a file — it gets spliced inside a generated method. That
rules out all file-level syntax:

- **`using` directives do not compile.** `using UnityEngine.SceneManagement;` parses as a
  using-*statement* and fails with `Identifier expected`, plus two more confusing diagnostics on
  the same line. Fully qualify instead. (`using (var x = …) { }` and `using var x = …;` are
  statements, and are fine.)
- **No `namespace`, `class` or method declarations.** Local functions inside the body are fine.
- **Implicitly in scope:** `UnityEngine`, `UnityEditor`, `System`, `System.Collections.Generic`,
  `System.Linq`. Note `System.Collections` (non-generic) is *not* — `ArrayList` needs qualifying.
- **Must be fully qualified:** `UnityEngine.SceneManagement.SceneManager`, `TMPro.*`,
  `UnityEngine.UI.*`, `UnityEngine.Rendering.*`, `UnityEditor.SceneManagement.EditorSceneManager`,
  `System.Text.StringBuilder`, `System.IO.*`.
- **`Object` is ambiguous.** `UnityEngine` and `System` are both open, so bare `Object` fails with
  "ambiguous reference between 'UnityEngine.Object' and 'object'". Write `UnityEngine.Object`.
- End with `return <something>;`. Return **one compact string** you've assembled — the result is
  the only thing you get back, and a giant serialized blob costs more than it tells you.

A bare expression (`Application.version`) is auto-wrapped as a return. Anything longer needs the
explicit `return`.

## Undo discipline

Go through `UnityEditor.Undo` so the user keeps Ctrl+Z on changes they didn't make themselves:

| Instead of | Use |
|---|---|
| `Object.DestroyImmediate(x)` | `UnityEditor.Undo.DestroyObjectImmediate(x)` |
| `go.AddComponent<T>()` | `UnityEditor.Undo.AddComponent<T>(go)` |
| mutating a component | `UnityEditor.Undo.RecordObject(c, "label")` first |

After scene edits, mark the scene dirty in the eval and then save with a **separate `save_scene`
command**:

```csharp
UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(scene);
```

Do not call `SaveScene` inside the snippet. It returns success, and then anything the snippet mutates
afterwards re-dirties the scene — so you get a reported save that didn't cover the change you were
making. `list_open_scenes` showing `isDirty: true` right after a "successful" save is that bug. Save
after the eval returns, then confirm.

## Recipes

### Audit / query before you change anything

Cheap reconnaissance. Count and name what you'd affect, confirm the plan, then act.

```csharp
var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
var sb = new System.Text.StringBuilder();
int n = 0;
foreach (var root in scene.GetRootGameObjects())
    foreach (var c in root.GetComponentsInChildren<MeshRenderer>(true))
        if (c.sharedMaterial == null) { n++; sb.Append(c.name).Append(';'); }
return $"missingMaterial={n} objects={sb}";
```

`GetComponentsInChildren<T>(true)` — the `true` includes inactive objects. Leaving it out silently
misses disabled hierarchies, which is a common source of "you said you fixed all of them".

### Prefab-safe bulk edit

The pattern to reach for on any "change all the X" request. Collect first, then mutate — mutating
while enumerating a hierarchy you're modifying gives unpredictable results.

```csharp
var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
var targets = new List<BoxCollider>();
foreach (var root in scene.GetRootGameObjects())
    targets.AddRange(root.GetComponentsInChildren<BoxCollider>(true));

var sb = new System.Text.StringBuilder();
int done = 0, skipped = 0;
foreach (var c in targets)
{
    if (UnityEditor.PrefabUtility.IsPartOfPrefabInstance(c.gameObject))
    { skipped++; sb.Append(c.name).Append(":SKIP-prefab\n"); continue; }

    UnityEditor.Undo.RecordObject(c, "Set trigger");
    c.isTrigger = true;
    done++; sb.Append(c.name).Append(":OK\n");
}
UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(scene);
return $"changed={done} skipped={skipped}\n{sb}";
```

Then `save_scene`, as a separate command.

Report the skipped ones to the user. Silently skipping prefab instances turns "converted
everything" into a false claim.

### Swapping one component type for another

Component swaps lose all serialized data unless you carry it across by hand, and the replacement
rarely maps 1:1 — sizes, alignment enums and units usually differ. Capture the old values, destroy,
add, then translate. This is the legacy `TextMesh` → `TextMeshPro` case, which also has to rescale
because legacy `fontSize`/`characterSize` and TMP's `fontSize` are unrelated units:

```csharp
var go = tm.gameObject;
var oldRenderer = go.GetComponent<MeshRenderer>();
var originalSize = oldRenderer != null ? oldRenderer.bounds.size : Vector3.one;

string text = tm.text; Color color = tm.color; TextAnchor anchor = tm.anchor;

UnityEditor.Undo.DestroyObjectImmediate(tm);
var tmp = UnityEditor.Undo.AddComponent<TMPro.TextMeshPro>(go);
tmp.text = text; tmp.color = color;
tmp.alignment = anchor == TextAnchor.MiddleCenter
    ? TMPro.TextAlignmentOptions.Center : TMPro.TextAlignmentOptions.TopLeft;

// Match the original on-screen size: render once, measure, rescale.
tmp.fontSize = 36; tmp.ForceMeshUpdate();
var nr = go.GetComponent<MeshRenderer>();
if (nr != null && nr.bounds.size.x > 0.0001f && originalSize.x > 0.0001f)
{ tmp.fontSize *= originalSize.x / nr.bounds.size.x; tmp.ForceMeshUpdate(); }
```

`ForceMeshUpdate()` matters: TMP lays out lazily, so bounds read before it are stale.

### Duplicating scene objects

Both obvious approaches are wrong, and both fail *silently* — the copy appears, looks right in the
outline, and is broken in a way you only find later:

- **`Object.Instantiate(go)`** on a prefab instance produces a plain GameObject. The prefab connection
  is gone, so the copy stops tracking the asset.
- **`PrefabUtility.InstantiatePrefab(asset)`** keeps the connection but starts from the asset, so every
  override on the instance you were copying — its position, its wired references, its renamed children
  — is lost.
- Neither remaps **cross-references**. Duplicate a rig whose camera points at its own root, and the
  copy's camera still points at the *original's* root.

What the Editor's own Ctrl+D does, and the only thing that gets all three right, is the pasteboard
path. It is undiscoverable — `Unsupported` is not a namespace anyone searches:

```csharp
var source = GameObject.Find("/Players/Player_01");
UnityEditor.Selection.objects = new UnityEngine.Object[] { source };
UnityEditor.Unsupported.DuplicateGameObjectsUsingPasteboard();

// The duplicates come back as the new selection, in the same order
var made = new List<string>();
foreach (var obj in UnityEditor.Selection.gameObjects)
{
    UnityEditor.Undo.SetTransformParent(obj.transform, source.transform.parent, "Duplicate");
    obj.name = "Player_02";
    made.Add(obj.name);
}
UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(source.scene);
return "duplicated=" + string.Join(",", made.ToArray());
```

**Select everything that references everything else and duplicate it in one call.** The reference
remapping only happens between objects duplicated *together* — that is the whole reason to use this
API. Duplicate a rig and its camera separately and you are back to the cross-wiring case above.

Then verify with `m_inspect --target <the copy> --children` and read the reference paths: each should
point inside the copy, not back at the source. That check is not optional here — cross-wiring is the
normal failure, not the rare one.

### Editing the prefab asset instead of instances

Changes every instance at once, including ones not in the open scene:

```csharp
var path = "Assets/Prefabs/Crate.prefab";
var root = UnityEditor.PrefabUtility.LoadPrefabContents(path);
foreach (var r in root.GetComponentsInChildren<Renderer>(true)) r.shadowCastingMode =
    UnityEngine.Rendering.ShadowCastingMode.Off;
UnityEditor.PrefabUtility.SaveAsPrefabAsset(root, path);
UnityEditor.PrefabUtility.UnloadPrefabContents(root);   // leaks the stage if you forget
return "prefab updated";
```

### Project-wide asset sweep

```csharp
var sb = new System.Text.StringBuilder(); int n = 0;
foreach (var guid in UnityEditor.AssetDatabase.FindAssets("t:Material"))
{
    var p = UnityEditor.AssetDatabase.GUIDToAssetPath(guid);
    var m = UnityEditor.AssetDatabase.LoadAssetAtPath<Material>(p);
    if (m != null && m.shader != null && m.shader.name.Contains("Standard"))
    { n++; sb.Append(p).Append('\n'); }
}
return $"count={n}\n{sb}";
```

Cap the output when a sweep could match hundreds of assets — return the count plus the first
handful, then narrow the filter, rather than paying for a thousand paths you won't read.

## Gotchas

- **Domain reloads wipe eval state.** Recompiles, package changes and play-mode transitions reset
  everything. Don't carry values across them; re-query instead.
- **`eval` runs on the main thread and blocks the Editor.** Default timeout is 5s. For long
  sweeps, narrow the scope rather than raising `--timeout`.
- **Play mode changes aren't persisted.** Objects edited during play revert on exit. Check
  `EditorApplication.isPlaying` first if it matters.
- **Compile errors are cheap, but read them for the message and not the position.** Fix and rerun —
  that loop is far cheaper than defensive over-engineering up front. Three things about the
  diagnostics:
  - **Positions don't map to your snippet.** It is spliced into a generated wrapper, so line numbers
    are offset and `line 0, col 29` happens. A diagnostic pointing past the end of your code is the
    wrapper's, not yours.
  - **Warnings arrive mixed in with errors.** `Unreachable code detected` next to the real failure is
    noise from the wrapper's trailing `return null;`. Find the actual error text first.
  - Everything is compiled fresh each call, so there is no stale-state explanation for a failure.
