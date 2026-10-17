# Installation reference

For the quick start, see the [README](../README.md#installation). This guide
covers package identity, migration, development installs and client configuration.

## Prerequisites

 - **Unity 2022.3 LTS up to Unity 6.3** 
	- uses `UnityEditor.ObjectChangeEvents.changesPublished` available in 2022.3 LTS and later
	- versions of Unity 6.5+ swap `InstanceIds` for `EntityIds` - current ID protocol still uses `int` instance IDs and `GetInstanceID()`, which is fine up to 6000.3 but deprecated in Unity 6.5. A full `EntityId` migration (protocol + Selection.entityIds throughout) is planned for supporting newer versions.
- **Unity UI** (`com.unity.ugui`) is now declared as a UPM dependency and installs automatically.
- **Git** installed and available on `PATH` to Unity Package Manager.
- **uv** (https://docs.astral.sh/uv/getting-started/installation/) for the separate Python MCP server (Python >=3.13; uv can provision a compatible interpreter).

## Installation

USB has two separately installed parts: the **Unity UPM package** runs inside the
Editor, and the **Python MCP server** is launched by your agent over stdio. Install
both. Adding the UPM package does not install or launch the Python server.

### 1. Unity package — recommended: Git URL

In **Window > Package Manager**, select **+ > Add package from Git URL**. The
package URL is:

```text
https://github.com/Programalyst/unity-semantic-bridge.git?path=/com.gamenami.unity-semantic-bridge
```

The URL installs the default branch when first resolved. Unity records the
resolved commit in `Packages/packages-lock.json`; committing that file keeps
team installs consistent. It does not automatically follow every new Git commit.

Optionally append `#<existing-tag-or-commit>` after `?path=...` when you want an
explicit version in the manifest. Use the same revision for the Python clone.

**Package identity:** current source declares `com.gamenami.unity-semantic-bridge`,
matching the repository folder. Older revisions declared the misspelled
`com.gamenami.unity-scemantic-bridge`. The dependency key must match the
`package.json` at the selected revision. Package Manager chooses it automatically.

Current source declares Newtonsoft JSON `3.2.2` and Unity UI `1.0.0` as UPM
dependencies (Unity 6.3 resolves its built-in Unity UI `2.0.0`). Let Unity finish
resolving packages and compiling. Commit **both** `Packages/manifest.json` and
Unity's generated `Packages/packages-lock.json`. See Unity's
[Git dependency documentation](https://docs.unity3d.com/2022.3/Documentation/Manual/upm-git.html).

#### Upgrading from the misspelled package name

For a local installation, the package's name changes as soon as its working copy
is updated. Unity may then refuse to remove it with “Package name cannot be found
in the project manifest”: the loaded package has the new name, but the manifest
still has the old key. Repair the manifest directly in this case:

1. Close the affected Unity project and preserve any bridge source edits.
2. In `Packages/manifest.json`, rename only the dependency key
   `com.gamenami.unity-scemantic-bridge` to
   `com.gamenami.unity-semantic-bridge`. Keep the existing `file:` value if it
   points to the updated local package. Preserve unrelated dependencies.
3. Reopen Unity and let it resolve and compile. Unity regenerates
   `Packages/packages-lock.json`; verify the old key is gone and the new key is
   present. Do not hand-edit the lock hash or keep both package identities.

For an older Git installation whose package and manifest still agree on the old
name, remove it in Package Manager first, then add the Git URL once the rename is published
on the default branch (or select a published commit containing the rename). Alternatively, with Unity closed, update both the
manifest key and Git revision together. A corrected key cannot point to an older
commit that still declares the misspelled name.

Review both package files and commit them together for shared Git installations.
Do not commit personal absolute paths from local disk installations. Update any
custom hardcoded `Packages/com.gamenami.unity-scemantic-bridge/...` paths. C#
namespaces, asset GUIDs, the package folder and Python MCP configuration are
unchanged.

#### Switching an existing local installation to Git

Preserve your local source edits, then remove the local package in Package
Manager and re-add it using the Git URL above. If this also crosses the
package rename, follow the steps above. Verify the lock entry has `"source":
"git"` and the expected commit hash instead of a personal `file:` path. Review
and commit both files:

```sh
git diff -- Packages/manifest.json Packages/packages-lock.json
git add Packages/manifest.json Packages/packages-lock.json
git commit -m "Install Unity Semantic Bridge from Git"
```

Keep the Python clone used by your MCP client; changing the UPM dependency does
not relocate that server. Check out the same revision in the Python clone and
sync its dependencies as below. For later upgrades, re-add the Git URL in Package Manager to
resolve its latest revision (or change an explicit pin), let Unity regenerate
the lock entry, and commit both files again. Update the Python clone to the
matching revision.

#### Bridge development: Add package from disk

To edit USB's Unity code, clone this repository and select **+ > Add package from
disk**, then choose `com.gamenami.unity-semantic-bridge/package.json`. Unity reads
the working copy directly. This is a local bridge-development workflow.

Unity can write a personal absolute `file:` path into the consuming project's
`Packages/manifest.json` and `Packages/packages-lock.json`. **Do not commit those
machine-specific entries.** Review both files before staging; keep the local
bridge changes out of shared commits while preserving unrelated package updates.
Do not ignore the entire manifest or lock file. Switch back to the team's
Git dependency when you want to share the installation.

### 2. Python MCP server — local agent setup

Install `uv`, then clone the repository separately from Unity's package cache.
A fresh clone uses the default branch:

```sh
git clone https://github.com/Programalyst/unity-semantic-bridge.git
uv --directory unity-semantic-bridge/mcp-editor-bridge sync --locked
```

If your Unity lock file points to an older revision, check out that same commit
in this clone before syncing dependencies.

Register `mcp-editor-bridge/main.py` with your agent using a configuration below.
The example absolute path belongs in **your local MCP client configuration**, not
in the Unity project's package files or shared machine-specific client settings.
Each developer configures their own clone location. Do not point the MCP client
at Unity's generated `Library/PackageCache` directory.

The agent launches Python over **stdio**; Python POSTs JSON-RPC to Unity at
`http://127.0.0.1:1073/rpc`. Unity sends events to Python at
`http://127.0.0.1:1074/rpc`. These are the default ports; see
[Several Unity projects at once](#several-unity-projects-at-once). The Unity `/rpc` URL is a downstream connection, **not
an MCP server URL**. In Unity, open **Tools > Unity Semantic Bridge** and click
**Start HTTP Listener**. Health check: `GET http://127.0.0.1:1073/health`.

#### Several Unity projects at once

Each Unity project stores its own pair of ports in
`UserSettings/UnitySemanticBridgeSettings.asset` (per user, not committed), so two
Editors can run the bridge at the same time. Give every concurrently open project
a different pair and register one MCP server per project with the same ports:

1. In the second project, open **Tools > Unity Semantic Bridge**, set **Listener
   port** and **Event port** (for example 1075 and 1076), and click **Apply**. A
   running listener restarts on the new port.
2. Register the same `main.py` a second time under a new name, with matching
   arguments (see [Registering one server per project](#registering-one-server-per-project)).

##### Registering one server per project

The server name is only a label in **your agent's MCP configuration**. Unity never
sees it; the Unity project stores only its ports. Each entry runs the same
`mcp-editor-bridge/main.py` from the same clone, and its `--unity-port`/`--event-port`
arguments pick which Editor it talks to. A typical setup:

| Server name | Arguments | Unity project ports |
|---|---|---|
| `unity-semantic-bridge` | none (defaults 1073/1074) | defaults, no change needed |
| `unity-ap` | `--unity-port 1075 --event-port 1076` | set to 1075/1076 in **Tools > Unity Semantic Bridge** |

Keeping the first project on the defaults under the usual name means existing
sessions, tool names and approvals stay as they are. Name each added server after
its project. The agent prefixes tool names with the server name (for example
`mcp__unity-ap__enter_play` in Claude), so the agent and you can tell which Editor
a call goes to.

**Codex CLI:**

```sh
codex mcp add unity-ap -- uv --directory "/absolute/path/to/unity-semantic-bridge/mcp-editor-bridge" run main.py --unity-port 1075 --event-port 1076
```

or in `~/.codex/config.toml`:

```toml
[mcp_servers.unity-ap]
command = "uv"
args = [
    "--directory",
    "/absolute/path/to/unity-semantic-bridge/mcp-editor-bridge",
    "run",
    "main.py",
    "--unity-port", "1075",
    "--event-port", "1076",
]
```

Per-tool settings such as `[mcp_servers.unity-semantic-bridge.tools.<tool>]`
`approval_mode` belong to one server name. Copy them under
`[mcp_servers.unity-ap.tools.<tool>]` if the second server should behave the same.

**Claude Code:**

```sh
claude mcp add unity-ap -- uv --directory "/absolute/path/to/unity-semantic-bridge/mcp-editor-bridge" run main.py --unity-port 1075 --event-port 1076
```

`claude mcp add` defaults to the local scope, which applies only when Claude Code
runs in the current directory. Run it in the second project's folder to give that
project its own server without adding its tools to every other project. Use
`--scope user` to make it available everywhere.

**JSON clients** (for example the Claude desktop app's
`claude_desktop_config.json`): add a second entry next to the first:

```json
{
    "mcpServers": {
        "unity-semantic-bridge": {
            "command": "uv",
            "args": ["--directory", "/absolute/path/to/unity-semantic-bridge/mcp-editor-bridge", "run", "main.py"]
        },
        "unity-ap": {
            "command": "uv",
            "args": ["--directory", "/absolute/path/to/unity-semantic-bridge/mcp-editor-bridge", "run", "main.py",
                     "--unity-port", "1075", "--event-port", "1076"]
        }
    }
}
```

After editing, restart or reload the client so it launches the new server.
Sessions that see every registered server load both tool sets, which costs context.
Disable the one a session does not need if your client allows it.

To check the mapping, open `GET http://127.0.0.1:<listener port>/health`: it reports
the answering project and its ports. Each server also logs its Unity RPC and event
URLs at startup.

The server also reads `USB_UNITY_PORT` and `USB_EVENT_PORT` when no arguments are
given; prefer arguments, since an `.env` in the clone is shared by every project
using it. `GET http://127.0.0.1:<listener port>/health` reports which project
answers, which helps when the ports of two projects are swapped. The listener's
auto-start after domain reloads is per Editor session and is not shared between
projects.

#### Several agent sessions on one Editor

Every agent session starts its own MCP server, but only one of them can bind the
project's event port. The others bind a free port and register it with Unity
(`register_event_listener`, renewed every 30 seconds, expiring after 120), and Unity
sends each event to the event port plus every registered server. So
`get_recent_unity_events` works in every session. If a server cannot receive events,
for example because the Unity package predates registration, an empty result says
why. RPC calls from several sessions are processed one at a time, as before.
No tool requires an LLM API key.

### Codex CLI

Run this in your shell, replacing the example path with your clone's absolute path:

```sh
codex mcp add unity-semantic-bridge -- uv --directory "/absolute/path/to/unity-semantic-bridge/mcp-editor-bridge" run main.py
```

Alternatively, add this TOML entry to `~/.codex/config.toml`:

```toml
[mcp_servers.unity-semantic-bridge]
command = "uv"
args = [
    "--directory",
    "/absolute/path/to/unity-semantic-bridge/mcp-editor-bridge",
    "run",
    "main.py",
]
```

Each array entry is **one command-line argument**. Do not combine arguments into
strings such as `'"run", "main.py"'`: that passes the quotes and comma literally
as one argument. In a UI with separate argument fields, enter one value per
field without JSON punctuation.

Check the saved configuration with `codex mcp get unity-semantic-bridge`.
After changing the configuration or Python tool definitions, exit the running
CLI with **Ctrl+D**, run `codex resume` from the same project directory, and
select your conversation. Use `/mcp` to check connection/tool discovery.
If it reports **failed (0 tools)**, check the Python startup command and error;
restarting Unity's listener cannot fix malformed Python launch arguments.

### Clients using JSON configuration

Merge this entry into your client's MCP configuration, replacing the path:

```json
{
    "mcpServers": {
        "unity-semantic-bridge": {
            "command": "uv",
            "args": [
                "--directory",
                "/absolute/path/to/unity-semantic-bridge/mcp-editor-bridge",
                "run",
                "main.py"
            ]
        }
    }
}
```

