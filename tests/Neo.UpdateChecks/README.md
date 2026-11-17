# Kernel update checks

This harness compiles the production release discovery, downloader, hash verifier,
archive installer and rollback code. Only the application/session boundary is
replaced. Session restoration is tested separately by `Neo.DeepRegression`.

Run deterministic checks with a new, empty output directory:

```powershell
dotnet run --project tests/Neo.UpdateChecks -c Release -- checks C:\Temp\neo-update-checks
node tests/verify-update-bridge.cjs
```

Check version decisions and read installed kernel versions without changing them:

```powershell
dotnet run --project tests/Neo.UpdateChecks -c Release -- version-checks C:\Temp\neo-version-checks "E:\Program Files\neo\app\runtime"
```

The first directory is disposable output. The second is read-only: only each
executable's version command is run. Probe recovery and concurrent cache reads
use a copied executable in the output directory.

Fetch current official metadata without installing anything:

```powershell
dotnet run --project tests/Neo.UpdateChecks -c Release -- discover C:\Temp\neo-releases.json
```

The `install <output-directory> <release-json> [first-engine-index] [isolated]`
mode performs actual official downloads and file replacement strictly in
`<output-directory>\runtime`. Use a disposable directory with the initial kernel
bundle and its resource files. Engine indices: Xray 0, sing-box 1, mihomo 2, Naive 3.
Do not point this mode at an installed application directory.

For the optional isolated download tunnel, set these process environment variables:

- `NEO_UPDATE_TEST_SUBSCRIPTION_CACHE`: path to a Neo subscription cache JSON file.
- `NEO_UPDATE_TEST_NODE`: name fragment selecting the intended VLESS test node.

The test tunnel uses local ports 43208, 43209 and 43210. It never changes the
Windows system proxy or the running application's selected node. It is disposed
when the harness exits. Keep generated configs, caches and diagnostic output out
of public release assets because they may contain credentials.
