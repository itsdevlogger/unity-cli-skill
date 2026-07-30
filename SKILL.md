---
name: unity-cli
description: Inspect and edit an ALREADY-EXISTING Unity project through a running Unity Editor via the `unity` CLI bridge. Covers scenes, GameObjects, components, transforms, prefabs, materials, shaders, lighting, animation, Timeline, assets, UPM packages, project settings, play mode, console errors, screenshots, tests and builds. Use this skill whenever the user asks to change, inspect, audit, fix, refactor, or automate anything inside a Unity project, including phrasings that never mention "Unity CLI", such as "swap the legacy text to TMP", "why is this object pink", "list everything using this shader", "add a collider to all the crates", "run the play tests", "screenshot the scene", "what's erroring in the console", or "rename these GameObjects". Assume it applies to any .unity scene, .prefab, .mat, .asset, .controller or C# MonoBehaviour work in a project directory.
---

# unity-cli

Run these seven steps in order. Steps 1 to 5 are setup and are cheap; do not skip them and do not
start the user's task before step 6.

Three paths matter, and confusing them is the single most damaging mistake available here:

| | Where | What it is |
|---|---|---|
| `<skill>` | the directory this file lives in | **Ephemeral.** The skill is installed from a zip into a session-scoped folder whose path changes every session and is deleted afterwards. Nothing may point at it and nothing may be stored in it. |
| `<home>` | **`C:/unity-cli-skill`**, hardcoded, never ask the user | **Permanent.** The shared macro library, the git clone, and the settings file. Every Unity project on this machine links to `<home>/macros` as a local UPM package. |
| `<project>` | the Unity project being worked on | The user's project. Only its `Packages/manifest.json` is ever touched by setup. |

**`<home>`'s working tree is a live installed package, not a delivery repo.** Every Unity project on
this machine compiles the files that are on disk there right now. So: **it must always hold every
macro ever written, and no git operation may remove one from disk.** Never `checkout` a branch, never
`stash`, never `reset --hard`. A macro that gets checked out from under the user is worse than a macro
that was never contributed — it was compiled, verified, and then deleted from every project they own.

`<skill>/macros/` is a **seed copy only**, used to populate `<home>` when git isn't available. Once
`<home>` exists it is the sole source of truth: read, edit and push macros there, never in `<skill>`.

---

## Step 1: Home and onboarding (first run only)

**1a. Make sure `<home>` exists.** Check for `C:/unity-cli-skill/macros/package.json`.

If it isn't there, create the library. Clone is preferred, copy is the fallback:

```bash
git clone --origin upstream https://github.com/itsdevlogger/unity-cli-skill "C:/unity-cli-skill"
```

That remote URL is fixed; use it verbatim. `--origin upstream` matters: **`upstream` is always the
shared repo, `origin` is always the user's own fork** (added later, only if they contribute, see
step 7). Never point `origin` at the shared repo; nobody has write access to it.

If the clone fails (no network, no git, folder exists but isn't a repo), copy `<skill>` to
`C:/unity-cli-skill` instead and tell the user the library is a local copy with no upstream, so
auto-update and contribute won't work until it's re-cloned.

An install made by an older version of this skill has the shared repo as `origin`. Rename it once and
carry on, since everything below assumes the `upstream` name:

```bash
git -C "C:/unity-cli-skill" remote rename origin upstream
```

**1b. Onboarding.** Check for `<home>/settings.local.json`.

**If it exists**, read it and move on without asking again.

**If it does not exist**, ask the user both questions in one go (a single `AskUserQuestion` with
two questions), use the exact wording as bellow and say plainly that **this is a one-time setup and
they will not be asked again**:
1. **Auto-update the macros?** "Should I `git fetch` + `git pull` the macro library before each
   run, this way you always get any new macros created by the community."
2. **Contribute macros?** "When I add or improve a macro, should I push it to your fork of the
   unity-cli repo and open a pull request for you? This skill gets better the more contributors it
   has. You'll need a fork on GitHub, and I'll ask for its URL the first time there's something to
   push. I'll always check with you before opening anything. If no, I'll write macros local only."

Then write the answers:

```json
{ "contribute": true, "autoUpdate": true, "onboardedAt": "YYYY-MM-DD" }
```

to `<home>/settings.local.json`. That is **`<home>`, not `<skill>`.** In `<skill>` it would be deleted
with the session and the user would be re-onboarded every single run. It is gitignored: per-machine,
not part of the repo.

**1c. If `autoUpdate` is true**, update the library:

```bash
git -C "C:/unity-cli-skill" fetch -q upstream && git -C "C:/unity-cli-skill" pull --rebase upstream main
```

**`--rebase`, not `--ff-only`.** Step 7 commits contributions on `main` and leaves them there until
upstream merges them, so `main` legitimately carries local commits and a fast-forward would be refused
every run from the first contribution onwards. The rebase replays them on top of upstream and quietly
drops each one as it lands upstream.

If this fails (no network, no `upstream` remote, uncommitted edits in the way, or a conflict between a
local macro and an upstream change to the same file), say so in one line and continue. A stale macro
set is not a reason to stop working. Don't force it: never `reset --hard`, never `rebase --skip` a
conflict away, never discard local macro work to make the update succeed. If a rebase stops mid-way,
`git -C "C:/unity-cli-skill" rebase --abort` puts every file back and you carry on with the old macros.

---

## Step 2: Does this project support unity-cli?

The bridge requires **Unity 6.0 or newer**. Read the first line of
`<project>/ProjectSettings/ProjectVersion.txt`:

```
m_EditorVersion: 6000.0.23f1
```

The version is `6000.x` for Unity 6, `2022.x`/`2021.x` for older releases. Anything below `6000.0`
(and anything where that file is missing) means **stop here.** Tell the user this project is on
`<version>` and unity-cli needs Unity 6.0+, and that nothing was changed. Do not attempt to work
around it and do not fall back to editing YAML files by hand.

---

## Step 3: Ensure the project is wired up

Two things must be present in `<project>/Packages/manifest.json`. **Search it, don't read it.** The
file is long and you only need two answers. The grep prints the macros line, so you see its path
too, which is the part that actually matters:

```bash
grep -n -e "com.unity.pipeline" -e "com.unitycli.macros" "<project>/Packages/manifest.json"
```

| Situation | What to write into `dependencies` |
|---|---|
| No `com.unity.pipeline` | The Pipeline package, which *is* the CLI bridge. Prefer `unity pipeline install`, which writes the entry itself. |
| No `com.unitycli.macros` | `"com.unitycli.macros": "file:C:/unity-cli-skill/macros"`, with forward slashes and no trailing slash. |
| Present, but the path is **not** `C:/unity-cli-skill/macros` | Rewrite it to exactly that. An old install may point into a dead session folder; see below. |

Read `manifest.json` **only if** you have to write to it. If both hit and the macros path is
already correct, this step is done: skip straight to step 5.

**A wrong `file:` path is not a degraded mode. It breaks the project.** Unity fails the *entire*
package resolve with `The file [...\package.json] cannot be found`, so no packages load at all until
the manifest is fixed. Two ways it happens, both worth catching here:

- The entry points into a session-scoped skill folder from an older version of this skill. That path
  is gone. Rewrite it.
- The entry is correct but `C:/unity-cli-skill/macros/package.json` doesn't exist because the library
  was deleted. Step 1a re-creates it; if you skipped past 1a on a cached assumption, go back.

Adding the Pipeline package is a real change to the user's project, so mention it before doing it.
Adding the macros package is what makes the `m_` macros exist at all; it is required.

---

## Step 4: Force a recompile and wait for the Editor to answer

Only needed if step 3 wrote to `manifest.json`.

A manifest change is picked up when the Editor regains focus, and a package resolve plus domain
reload follows. So: **ask the user to click on the Unity Editor window**, then wait.

**`recompile_status` is unreliable, so do not use it.** The workaround is to poll for a macro that can
only answer once the compile has finished and the assemblies have reloaded:

```powershell
unity command recompile; $deadline = (Get-Date).AddMinutes(5); $ok = $false; while ((Get-Date) -lt $deadline) { $out = unity command m_find_macro --q m_find_macro 2>&1 | Out-String; if ($out -match '"n":"m_find_macro"') { $ok = $true; break }; Start-Sleep -Seconds 5 }; if ($ok) { "READY" } else { "TIMEOUT" }
```

(Drop the leading `unity command recompile;` when the Editor is still resolving packages. There is
no bridge to receive it yet, and the poll alone is enough.)

Notes that save real time:

- **`No Unity Editor instances found with reachable Pipeline servers` during the poll is the domain
  reload, not a failure.** It arrives with a list of troubleshooting suggestions that are wrong in
  this situation. Ignore them and let the loop keep polling.
- `TIMEOUT` means either the Editor was never focused, or the macros failed to compile. Check with
  `unity command m_find_logs --grep "error CS" --sev error`, because that macro answers off the main
  thread, so it works mid-compile.
- Use this same loop, with the new command name substituted into both the `--q` and the regex,
  every time you add or edit a macro later on.

---

## Step 5: Load the operating manual

Read `references/talk-to-editor.md` now, before touching anything. It covers how to address
objects, which macros to use instead of the expensive built-ins, the eval rules, and the
cross-cutting rules that cause most first-attempt failures.

---

## Step 6: Do the user's task

Work the request, following the workflow in `references/talk-to-editor.md`: for each thing you need,
look for an existing macro first, then decide one-off or recurring — a one-off goes through `eval`, a
recurring need becomes a new or improved macro under `C:/unity-cli-skill/macros/`. That decision
happens *while* you work, not in a review pass afterwards.

Anything you write into the library there is what step 7 pushes.

---

## Step 7: Open a pull request (only if `contribute` is true)

Check `git -C "C:/unity-cli-skill" status --porcelain`. **If it's clean, you're done** — this session
added no macros, which is the expected outcome for a routine task. Say nothing about it and stop.

Otherwise this step applies, but only when `<home>/settings.local.json` has `"contribute": true`. If
`contribute` is false, stop here, leave the changes uncommitted, and just say which files you added or
edited.

Before pushing anything: a macro that was never invoked successfully does not count as done. If you
wrote or edited a macro during step 6 but never got a real answer out of it, fix or revert it now —
do not push it.

**Nobody has write access to the shared repo.** Contributions go through the user's own fork and a
pull request. `upstream` is the shared repo and is never pushed to; `origin` is the user's fork.

**7a. Get the fork.** Read `"fork"` from `<home>/settings.local.json`; if it isn't recorded, check
`git -C "C:/unity-cli-skill" remote -v` for an `origin`. If neither exists, ask the user for it with
one free-text question: they fork <https://github.com/itsdevlogger/unity-cli-skill> on GitHub and
paste the URL of *their* fork. Say that you'll push a branch there and then check with them before
opening the PR, and that nothing goes near the shared repo.

If they'd rather not, or don't have a fork: stop. Leave the changes on disk, say which files you
added or edited, and move on. This is not a failure.

Once you have it, record it so this is asked exactly once, and wire up the remote:

```bash
git -C "C:/unity-cli-skill" remote add origin <fork-url>
```

Add `"fork": "<fork-url>"` to `<home>/settings.local.json`, keeping the existing keys. If `origin`
already exists but points somewhere else, use `remote set-url origin <fork-url>` instead.

**7b. If step 1a fell back to a copy**, `<home>` has no git at all. Adopt it as a working copy first,
without disturbing the files on disk:

```bash
git -C "C:/unity-cli-skill" init -q && git -C "C:/unity-cli-skill" remote add upstream https://github.com/itsdevlogger/unity-cli-skill && git -C "C:/unity-cli-skill" fetch -q upstream && git -C "C:/unity-cli-skill" reset -q upstream/main
```

That upstream URL is fixed, so use it verbatim and don't ask the user for one. `reset` without
`--hard` adopts the remote history while leaving every file exactly as it is, so your new macro shows
up as a normal change.

**7c. Commit on `main`, push a branch to the fork without checking one out.** A PR does need its own
branch, so that each macro can be reviewed and merged on its own — but that branch only has to exist
*on the fork*. The `HEAD:refs/heads/…` form creates it remotely and leaves the working tree untouched:

```bash
git -C "C:/unity-cli-skill" add -A && git -C "C:/unity-cli-skill" commit -m "<message>" && git -C "C:/unity-cli-skill" push origin HEAD:refs/heads/macro/<short-name>
```

**Never `checkout -b` and never switch back to `main` afterwards.** That is what the invariant at the
top forbids: switching away takes the macro off disk, and the user is left holding a PR link for a
macro that no longer exists in any of their projects. No local branch is created here, so there is
nothing to switch back from and no branch pileup to clean up.

Before pushing, check what the branch will actually carry:

```bash
git -C "C:/unity-cli-skill" log --oneline upstream/main..HEAD
```

If that lists more than the commit you just made, earlier contributions haven't been merged upstream
yet and will ride along in this PR. Don't try to untangle it — name them to the user in one line and
carry on.

**7d. Open the pull request.** Opening a PR against the shared repo is public and outward-facing, so
ask first: say in one line what the PR adds and that it targets `itsdevlogger/unity-cli-skill`, and
wait for a clear yes. Then, if `gh auth status` is clean, open it yourself:

```bash
gh pr create --repo itsdevlogger/unity-cli-skill --head <fork-owner>:macro/<short-name> --title "<title>" --body "<what it adds and why>"
```

If `gh` is missing or unauthenticated, fall back to relaying the link: the push output ends with a
`Create a pull request for …` URL, so pass that back verbatim, or build it as
`<fork-url>/pull/new/macro/<short-name>`. Don't ask the user to grant you access to anything.

**7e. Tidy up merged branches, if it's free.** Fork branches are never deleted automatically — not on
merge (the upstream maintainers have no write access to the user's fork) and never on a closed PR. They
are harmless, so this is optional and must never fail the contribution. When `gh` is available, dead
branches from previously merged PRs can go:

```bash
git -C "C:/unity-cli-skill" push origin --delete macro/<short-name>
```

Only for PRs that are already merged or closed, and only branches this skill created (`macro/…`).
Never delete a branch with an open PR, and don't spend a round trip hunting for candidates.

Rules that hold throughout:

- Commit **only** the macro library. Never touch the user's Unity project's git state, and never try
  to commit `<skill>`, which is a throwaway copy.
- Look at what the `status --porcelain` above actually listed before committing. If files unrelated to
  your macro work show up, name them to the user instead of quietly shipping them.
  `settings.local.json` is gitignored and must stay that way, because it's per-machine.
- Message names the macro and the capability, not the project it came from, as in `Add
  m_find_missing_refs: report broken object references in a scene`. The repo is shared, so "for the
  Foo prototype" means nothing to the next reader.
- If the push is rejected (bad URL, no auth, that branch name already taken by an older push), the
  commit still exists locally and the macro is still on disk and working, so say so in one line and
  move on. Don't force-push. A name collision just needs a different `<short-name>`.
- The fork's own `main` is never pushed to; it stays a clean mirror of upstream. Local `main` carrying
  unmerged commits is expected and is what step 1c's rebase is for.
