<#
    Installs this skill into the agents you choose by creating a directory
    junction from each agent's global skills folder back to this repo.

    A junction, not a copy: the repo stays the single source of truth, and
    edits here go live immediately. A junction, not a symlink, because
    symlinks need Developer Mode or elevation on Windows and junctions don't.

    Double-click install.bat to run this interactively, or call it directly:
        .\install.ps1 -Agents claude,cursor
        .\install.ps1 -Agents claude -Force
        .\install.ps1 -Agents claude -Uninstall
        .\install.ps1 -Agents claude -SkipPath

    Installing also puts macros\.bin on the user PATH, so the helper scripts
    in there (unity-recompile) can be called by name from any directory.
    -SkipPath leaves the variable alone for anyone who manages PATH themselves.
#>
[CmdletBinding()]
param(
    [string[]] $Agents,
    [switch] $Force,
    [switch] $Uninstall,
    [switch] $SkipPath
)

$ErrorActionPreference = 'Stop'

# Agent name -> the dot-folder it keeps global skills in, under $HOME.
# Claude Code is what this skill is built for and the only one verified here;
# the rest follow the same <home>\<dir>\skills\<name> convention.
$AgentRegistry = [ordered]@{
    'claude'      = @{ Dir = '.claude';   Display = 'Claude Code' }
    'cursor'      = @{ Dir = '.cursor';   Display = 'Cursor' }
    'codex'       = @{ Dir = '.agents';   Display = 'Codex CLI' }
    'antigravity' = @{ Dir = '.agent';    Display = 'Antigravity' }
    'gemini'      = @{ Dir = '.gemini';   Display = 'Gemini CLI' }
    'copilot'     = @{ Dir = '.github';   Display = 'GitHub Copilot' }
    'opencode'    = @{ Dir = '.opencode'; Display = 'OpenCode' }
}

$CANONICAL_HOME = 'C:\unity-cli-skill'
$RepoRoot = $PSScriptRoot
# Host-side helper scripts, not Unity code. This goes on PATH so the skill can
# call `unity-recompile` by name instead of hardcoding a path into its docs.
$BinDir = Join-Path $RepoRoot 'macros\.bin'

function Edit-PathValue {
    # Pure string surgery on a PATH value: no registry, so the awkward cases
    # (duplicate entry, trailing semicolons, quoted entries) are easy to reason
    # about. Returns $null when nothing needs to change, which is what keeps a
    # re-run from rewriting the variable at all.
    param(
        [string] $Raw,
        [string] $Dir,
        [switch] $Remove
    )

    $target = $Dir.TrimEnd('\')
    $kept = @()
    $found = $false

    foreach ($entry in @($Raw -split ';')) {
        # -eq on strings is case-insensitive, which is what Windows paths want.
        if ($entry.Trim().Trim('"').TrimEnd('\') -eq $target) {
            $found = $true
            continue
        }
        $kept += $entry
    }

    if ($Remove) {
        if (-not $found) { return $null }
        return ((@($kept | Where-Object { $_.Trim() })) -join ';')
    }

    if ($found) { return $null }
    return ((@($kept | Where-Object { $_.Trim() }) + $Dir) -join ';')
}

function Publish-EnvironmentChange {
    # Tell Explorer the environment moved, the way setx does. Without this,
    # anything launched from the already-running Explorer keeps the old PATH
    # until the next sign-in.
    if (-not ('UnityCliSkill.Native' -as [type])) {
        Add-Type -Namespace 'UnityCliSkill' -Name 'Native' -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Auto)]
public static extern System.IntPtr SendMessageTimeout(
    System.IntPtr hWnd, uint Msg, System.IntPtr wParam, string lParam,
    uint fuFlags, uint uTimeout, out System.IntPtr lpdwResult);
'@
    }

    $echo = [System.IntPtr]::Zero
    # HWND_BROADCAST, WM_SETTINGCHANGE, SMTO_ABORTIFHUNG, 100ms.
    [void] [UnityCliSkill.Native]::SendMessageTimeout(
        [System.IntPtr] 0xffff, 0x1A, [System.IntPtr]::Zero, 'Environment', 2, 100, [ref] $echo)
}

function Update-UserPath {
    param([string] $Dir, [switch] $Remove)

    # Only ever the per-user variable: the machine one needs elevation and is
    # not this installer's business.
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Environment', $true)
    if (-not $key) {
        Write-Host '  ! PATH: could not open HKCU\Environment, leaving PATH alone' -ForegroundColor Yellow
        return
    }

    try {
        # DoNotExpandEnvironmentNames keeps entries like %USERPROFILE%\bin
        # literal. Writing back an expanded copy would silently freeze them,
        # and preserving the value kind keeps REG_EXPAND_SZ from decaying to
        # REG_SZ, which is the same bug one layer down.
        $raw = [string] $key.GetValue('Path', '', [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
        $kind = [Microsoft.Win32.RegistryValueKind]::ExpandString
        try { $kind = $key.GetValueKind('Path') } catch { }

        $next = Edit-PathValue -Raw $raw -Dir $Dir -Remove:$Remove
        if ($null -eq $next) {
            $note = if ($Remove) { 'was not in your user PATH' } else { 'already in your user PATH' }
            Write-Host "  = PATH: $Dir $note" -ForegroundColor DarkGray
            return
        }

        $key.SetValue('Path', $next, $kind)
    } finally {
        $key.Close()
    }

    Publish-EnvironmentChange

    if ($Remove) {
        Write-Host "  - PATH: removed $Dir" -ForegroundColor Green
    } else {
        $env:Path = "$env:Path;$Dir"
        Write-Host "  + PATH: added $Dir" -ForegroundColor Green
        Write-Host '    Open a new terminal before calling unity-recompile.' -ForegroundColor DarkGray
    }
}

function Get-SkillName {
    # The junction is named after the skill, so read it from the frontmatter
    # rather than hardcoding it here and letting the two drift.
    $skillFile = Join-Path $RepoRoot 'SKILL.md'
    if (-not (Test-Path -LiteralPath $skillFile)) {
        throw "SKILL.md not found in $RepoRoot. Run this from inside the cloned repo."
    }
    foreach ($line in Get-Content -LiteralPath $skillFile -TotalCount 10) {
        if ($line -match '^\s*name:\s*(\S+)\s*$') { return $Matches[1] }
    }
    throw "Could not read 'name:' from the frontmatter of $skillFile."
}

function Get-AgentState {
    param([string] $Key, [string] $SkillName)

    $agentHome = Join-Path $HOME $AgentRegistry[$Key].Dir
    $dest = Join-Path $agentHome "skills\$SkillName"
    $item = Get-Item -LiteralPath $dest -Force -ErrorAction SilentlyContinue

    $state = 'none'
    $target = $null
    if ($item) {
        if ($item.LinkType -eq 'Junction' -or $item.LinkType -eq 'SymbolicLink') {
            $target = @($item.Target)[0]
            $resolved = if ($target) { Resolve-Path -LiteralPath $target -ErrorAction SilentlyContinue } else { $null }
            # -eq on strings is case-insensitive, which is what we want on NTFS.
            $state = if ($resolved -and $resolved.Path -eq $RepoRoot) { 'linked' } else { 'foreign' }
        } else {
            $state = 'conflict'
        }
    }

    [pscustomobject]@{
        Key     = $Key
        Display = $AgentRegistry[$Key].Display
        Dest    = $dest
        Exists  = Test-Path -LiteralPath $agentHome
        State   = $state
        Target  = $target
    }
}

function Format-State {
    param($Row)
    switch ($Row.State) {
        'linked'   { 'already linked to this repo' }
        'foreign'  { "linked elsewhere -> $($Row.Target)" }
        'conflict' { 'a real folder is in the way' }
        default    { if ($Row.Exists) { 'not installed' } else { 'not installed (agent not detected)' } }
    }
}

function Install-One {
    param($Row, [switch] $Replace)

    if ($Row.State -eq 'linked') {
        Write-Host "  = $($Row.Display): already linked, nothing to do" -ForegroundColor DarkGray
        return
    }
    if ($Row.State -eq 'conflict' -or $Row.State -eq 'foreign') {
        if (-not $Replace) {
            Write-Host "  ! $($Row.Display): $(Format-State $Row). Re-run with -Force to replace it." -ForegroundColor Yellow
            return
        }
        if ($Row.State -eq 'foreign') {
            # Unlink the reparse point without recursing into whatever it points at.
            [System.IO.Directory]::Delete($Row.Dest)
        } else {
            Remove-Item -LiteralPath $Row.Dest -Recurse -Force -Confirm:$false
        }
    }

    $skillsDir = Split-Path -Parent $Row.Dest
    if (-not (Test-Path -LiteralPath $skillsDir)) {
        New-Item -ItemType Directory -Path $skillsDir -Force | Out-Null
    }
    New-Item -ItemType Junction -Path $Row.Dest -Target $RepoRoot | Out-Null
    Write-Host "  + $($Row.Display): linked $($Row.Dest)" -ForegroundColor Green
}

function Uninstall-One {
    param($Row)

    if ($Row.State -eq 'none') {
        Write-Host "  = $($Row.Display): not installed" -ForegroundColor DarkGray
    } elseif ($Row.State -eq 'conflict') {
        # A real folder here is not ours to delete.
        Write-Host "  ! $($Row.Display): a real folder is in the way, leaving it alone" -ForegroundColor Yellow
    } else {
        # Delete the junction itself, never its contents.
        [System.IO.Directory]::Delete($Row.Dest)
        Write-Host "  - $($Row.Display): unlinked $($Row.Dest)" -ForegroundColor Green
    }
}

function Select-AgentsInteractively {
    param([array] $Rows)

    Write-Host ''
    Write-Host 'Which agents should this skill be installed for?' -ForegroundColor Cyan
    Write-Host ''
    for ($i = 0; $i -lt $Rows.Count; $i++) {
        $row = $Rows[$i]
        $mark = if ($row.Exists) { ' ' } else { '?' }
        $color = if ($row.Exists -and $row.State -ne 'linked') { 'White' } else { 'DarkGray' }
        Write-Host ("  {0}{1}. {2,-16} {3}" -f $mark, ($i + 1), $row.Display, (Format-State $row)) -ForegroundColor $color
    }
    Write-Host ''
    Write-Host '  ? = agent folder not found in your home directory' -ForegroundColor DarkGray
    Write-Host ''
    Write-Host 'Enter numbers separated by commas (e.g. 1,3), or:' -ForegroundColor Cyan
    Write-Host '  d = detected agents only    a = all    q = quit'
    Write-Host ''

    $answer = (Read-Host 'Your choice').Trim().ToLower()

    if ($answer -eq 'q' -or $answer -eq 'quit' -or $answer -eq '') { return @() }
    if ($answer -eq 'a') { return $Rows }
    if ($answer -eq 'd') { return @($Rows | Where-Object { $_.Exists }) }

    $tokens = $answer -split '[,\s]+' | Where-Object { $_ }
    $picked = foreach ($token in $tokens) {
        $n = 0
        if ([int]::TryParse($token, [ref] $n) -and $n -ge 1 -and $n -le $Rows.Count) {
            $Rows[$n - 1]
        } else {
            Write-Host "Ignoring '$token': not a number on the list." -ForegroundColor Yellow
        }
    }
    return @($picked)
}

# ---------------------------------------------------------------------------

$skillName = Get-SkillName

Write-Host ''
Write-Host 'unity-cli skill installer' -ForegroundColor Cyan
Write-Host "Repo: $RepoRoot"

if ($RepoRoot -ne $CANONICAL_HOME) {
    Write-Host ''
    Write-Host "WARNING: this repo is not at $CANONICAL_HOME." -ForegroundColor Yellow
    Write-Host 'The skill hardcodes that path for the shared macro library, so it will' -ForegroundColor Yellow
    Write-Host "not work correctly from here. Move the clone to $CANONICAL_HOME and re-run." -ForegroundColor Yellow
}

$rows = @(foreach ($key in $AgentRegistry.Keys) { Get-AgentState -Key $key -SkillName $skillName })

if ($Agents) {
    # `powershell -File` hands every argument over as a plain string, so
    # `-Agents claude,cursor` arrives as one token. Split it back apart here
    # so the batch launcher and a native PowerShell call behave the same.
    $Agents = @($Agents | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
    $selected = @(foreach ($name in $Agents) {
        $wanted = $name.ToLower()
        $match = $rows | Where-Object { $_.Key -eq $wanted }
        if ($match) {
            $match
        } else {
            Write-Host "Unknown agent '$name'. Known: $($AgentRegistry.Keys -join ', ')" -ForegroundColor Yellow
        }
    })
} else {
    $selected = Select-AgentsInteractively -Rows $rows
}

if ($selected.Count -eq 0) {
    Write-Host ''
    Write-Host 'Nothing selected. No changes made.' -ForegroundColor DarkGray
    exit 0
}

Write-Host ''
if ($Uninstall) {
    Write-Host 'Uninstalling:' -ForegroundColor Cyan
    foreach ($row in $selected) { Uninstall-One -Row $row }

    if (-not $SkipPath) {
        # The PATH entry isn't per-agent, so it only goes away once no agent
        # links here any more. Uninstalling one of several agents leaves it.
        $stillLinked = @(
            foreach ($key in $AgentRegistry.Keys) { Get-AgentState -Key $key -SkillName $skillName }
        ) | Where-Object { $_.State -eq 'linked' }

        if (@($stillLinked).Count -gt 0) {
            Write-Host "  = PATH: kept, still linked for $((@($stillLinked).Display) -join ', ')" -ForegroundColor DarkGray
        } else {
            Update-UserPath -Dir $BinDir -Remove
        }
    }
} else {
    Write-Host 'Installing:' -ForegroundColor Cyan
    foreach ($row in $selected) { Install-One -Row $row -Replace:$Force }
    if (-not $SkipPath) { Update-UserPath -Dir $BinDir }
}

Write-Host ''
Write-Host 'Done. Restart your agent to pick up the change.' -ForegroundColor Cyan
Write-Host ''
