# MSIX package-identity launch

This document describes the compatibility launch path used by the `msix-identity-launch` branch.

## Why this path exists

Recent Windows Codex/ChatGPT Desktop builds can reject direct execution that does not retain the installed MSIX package identity. The compatibility branch therefore does **not** copy the Codex App runtime and does **not** directly start `ChatGPT.exe` as an unpackaged process.

The implementation was validated on OpenAI.Codex `26.924.2738.0` (Windows x64). Future desktop builds may change these integration surfaces.

## Launch transaction

For an isolated profile the launcher:

1. acquires the existing profile-operation lock and a separate per-user MSIX environment lock;
2. recovers any compatible unfinished environment journal from an earlier abnormal termination;
3. snapshots the raw HKCU `Environment` values and registry value kinds for `CODEX_HOME` and `CODEX_SQLITE_HOME`;
4. durably writes a recovery journal;
5. temporarily points those two user environment values at the selected profile's Codex Home and broadcasts `WM_SETTINGCHANGE`;
6. activates the registered `OpenAI.Codex_2p2nqsd0c76g0!App` through `IApplicationActivationManager`, passing an absolute `--user-data-dir` for the profile's Electron data;
7. requires a new PID and verifies the registered package identity, process command line, expected Electron directory, and fresh filesystem activity in both the Codex Home and Electron profile;
8. restores the original registry values/value kinds, broadcasts the environment change again, and only then reports a successful launch.

No `IPackageDebugSettings`, runtime copy, direct-executable fallback, `--no-sandbox`, or singleton-disabling flag is used.

## Process ownership and recovery

Managed process ownership is not inferred from the executable path alone. The launcher correlates PID, creation time, executable path, package identity and the command line's absolute `--user-data-dir`. A directory only maps to a managed profile when the profile registry and launcher marker agree.

Unresolved Codex processes are handled conservatively and can block managed mutation/launch operations rather than being silently treated as the personal instance.

The environment journal uses compare-before-write restoration. If a current user environment value is neither the temporary value nor the saved original value, the launcher preserves the external edit and retains the journal for diagnosis instead of overwriting it.

A manual recovery helper is available at:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tools\Restore-MsixEnvironment.ps1
```

Run it only after exiting the launcher.

## Important limitations

- The temporary `CODEX_HOME` / `CODEX_SQLITE_HOME` change is a **user-level Windows environment transaction**, not a private child-process environment. The lock coordinates patched launcher instances only. Do not start Codex/Codex CLI from another launcher, Start, taskbar, or terminal while an isolated profile is in the activation phase.
- File activity plus PID/arguments/package checks prove the selected local paths were used, but are not a complete audit of every plugin, credential store, daemon, or subprocess.
- MSIX activation may route an activation request to an existing app process. A new isolated-profile launch that does not obtain a new verifiable PID is rejected rather than rebound.
- WMI-unreadable or otherwise unresolved Codex processes are treated conservatively.
- Some user/machine-level credential or endpoint overrides are rejected because the MSIX broker cannot reproduce the old per-child environment scrub safely.
- The current MSIX path does not generate per-profile tray-icon badge variants because it does not modify/copy official application files.
- Successful operation on one Codex version does not guarantee future compatibility.

## Validation status

On 2026-09-27 the branch was validated on Windows x64 with OpenAI.Codex `26.924.2738.0`:

- `dotnet restore CodexMultiLauncher.slnx` — passed;
- Release build — passed;
- xUnit suite — **61 passed, 0 failed, 0 skipped**;
- personal profile launch — passed;
- existing isolated profile launch — passed;
- personal + isolated profile running concurrently — passed;
- launcher restart / process re-identification — passed;
- isolated profile close and relaunch — passed.

The automated regression tests do not edit the real HKCU environment and do not themselves launch Codex. Manual multi-instance acceptance remains important after major Codex Desktop updates.

## Provenance

This branch is based on the MIT-licensed upstream project `yyyyyp233/codex-multi-launcher`. The upstream history and license attribution are retained.

Primary implementation references:

- <https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-iapplicationactivationmanager-activateapplication>
- <https://learn.microsoft.com/en-us/windows/win32/procthread/environment-variables>
- <https://learn.microsoft.com/en-us/windows/win32/winmsg/wm-settingchange>
- <https://learn.microsoft.com/en-us/windows/win32/api/appmodel/nf-appmodel-getpackagefullname>