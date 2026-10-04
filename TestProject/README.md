# USB test host

A minimal Unity 2022.3.62f3 project that consumes USB directly from the repository.
Tests remain in the package's `Tests` directories, so they exercise shipped code.
The local package is enabled in `testables`; changes need no copy or publication.

Use this host for tests, not for game development. Integration fixtures replace
scenes, create/delete scratch assets and temporarily move fixture resources.
Unity caches, generated project settings, imported fixtures and results are ignored.
Keep the Editor closed before launching another test process against this project.

## Run

On macOS with the baseline Editor installed:

```sh
bash scripts/test-unity.sh
```

Run from the repository root. To use another installed Editor:

```sh
USB_UNITY_EDITOR="/path/to/Unity" bash scripts/test-unity.sh
```

The runner first resolves packages and runs `UsbTestProjectSetup.Prepare`, importing
TMP Essential Resources and creating a small mesh fixture. These resources are
obtained from Unity's installed TMP package rather than checked into USB. It then
runs all EditMode tests with graphics enabled. Initial package resolution may
require network access and an active Unity license. Logs and NUnit XML go under
`TestProject/TestResults/`.

A focused compatibility run:

```sh
bash scripts/test-unity.sh -testFilter 'SemanticBatchTests;UiAuthoringTests;ModelInspectionTests'
```

Open this project in Unity to use Window > General > Test Runner. Run
USB Tests > Prepare Fixtures first. Some integration fixtures intentionally run
only in batch mode; interactive runs skip them. No `-nographics` for UI tests.

Python transport/schema tests are separate:

```sh
cd mcp-editor-bridge
.venv/bin/python -m unittest discover -s tests -v
```

If the Python environment is missing, run `uv sync` in `mcp-editor-bridge` first.

## Coverage and known limitations

The package includes semantic batches/prefab queries, uGUI authoring, model
inspection, asset refresh, Editor throttling and serialized mesh references.
See [batch validation](../docs/semantic-batches.md) and
[UI validation](../docs/ugui-verification.md).

Two explicit opt-in refresh integration checks have pre-existing batch-mode
settling failures (`RealNoOpRefreshSettles`, `RealNonCodeRefreshImportsTextAndSettles`).
They are documented in UI validation. NUnit excludes explicit tests from a normal
full-suite run; select them by test filter when investigating refresh. The
optional Synty model fixture skips when absent. A full-suite nonzero exit should
be investigated using the XML, rather than treating every failure as a regression.

The host pins the minimum supported Unity family. Upgrading this host to Unity 6
requires updating uGUI/TMP dependencies; simply overriding the executable is not
a verified cross-version configuration. Use a separate host copy for upgrades.

## Verified host run

The runner completed end to end on macOS / Unity 2022.3.62f3: **49 passed,
zero failed, seven skipped**. Skips were four explicit Editor-preference tests,
two explicit refresh integration tests and the optional absent Synty model fixture.
TMP import completion and the generated mesh fixture were verified. The initial
resource-import setup bug was fixed before this successful run.
