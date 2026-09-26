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

####  Optional
- **API key for LLM with strong vision capabiltiies** - lighting subagent can be powered by a separate LLM (ideally with vision-in-the-loop like Astra or Kimi K3); see [`mcp-editor-bridge/core/llm_provider.py`](../mcp-editor-bridge/core/llm_provider.py). Tools are still available to your main/orchestrator agent even without setting up the subagent. Previously used mcp sampling but this was deprecated.

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
`http://127.0.0.1:1074/rpc`. The Unity `/rpc` URL is a downstream connection, **not
an MCP server URL**. In Unity, open **Tools > Unity Semantic Bridge** and click
**Start HTTP Listener**. Health check: `GET http://127.0.0.1:1073/health`.
Core tools do not require an LLM API key; optional lighting diagnostics use the
provider settings documented in [`mcp-editor-bridge/LightingAgent/README.md`](../mcp-editor-bridge/LightingAgent/README.md).

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

