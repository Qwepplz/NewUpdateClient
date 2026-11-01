# UpdateClient

UpdateClient is a .NET 10 console updater for Windows and Linux. It synchronizes a predefined repository into the directory containing the program, keeps local sync state and logs, and exits after one run. The shell's working directory does not select the target.

## Platforms and Deployment

Each platform has a separate x64 self-contained single file. Users do not need to install .NET.

| Platform | Scope |
| --- | --- |
| Windows 11 x64 | Published executable and applicable automated, menu and filesystem checks verified |
| Ubuntu 26.04 LTS x64 | Published executable runs directly; applicable automated, SSH TTY, permissions, links and cross-session mutex checks verified |
| Windows 10 Home / Pro 22H2 x64 | Compatibility target, not tested; these editions are not listed in the official .NET 10 supported OS matrix |
| Windows Server 2019 / 2022 / 2025 x64 | Compatibility targets in the .NET 10 matrix, not tested |

See the [.NET 10 supported OS matrix](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md). Windows 11 results do not establish Windows 10 or Server compatibility. Other distributions and architectures are outside the current scope.

Choose the artifact for the destination platform and place it in the directory to update:

- Windows: `UpdateClient.exe`.
- Linux: `UpdateClient`. Give the file user execute permission and run it from a terminal:

```bash
chmod u+x ./UpdateClient
./UpdateClient
```

Self-contained deployment includes the .NET runtime, but the operating system still needs its native libraries and certificate store. For Ubuntu 26.04, see Microsoft's [dependency list](https://learn.microsoft.com/en-us/dotnet/core/install/linux-ubuntu-install#dependencies). The single file may extract bundled native libraries to the runtime's private cache. On Linux this normally uses `$HOME/.net`; `DOTNET_BUNDLE_EXTRACT_BASE_DIR` can select another user-owned private cache directory.

Synchronization does not establish that upstream SourceMod plugins or other binaries can load on Linux. Plugin portability and game-server installation are separate concerns.

## Menu and Sources

The first menu selects Betterbot or Get5. Enter selects Betterbot. Its branch menu defaults to stable `main`; Advanced offers `dev`. Esc returns from Advanced to the branch menu, from the branch menu to repository selection, or exits from repository selection.

Betterbot uses GitHub `Qwepplz/Betterbot` and only the selected branch. Get5 uses GitHub `Qwepplz/get5`, trying the repository's default branch, then `main` and `master` without duplicate attempts. If Get5 preparation fails, the user may type `YES` to accept the Gitee `SaUrrr/get5` mirror. The mirror is queried independently and may lag behind GitHub. The source stays fixed during synchronization.

The product retains its interactive menu and has no unattended command interface. Unavailable console input keeps the existing Betterbot/main default behavior. Redirected input or output does not wait for an additional exit key.

## Synchronization Policy

Remote files are checked against cached size, write time and SHA metadata, or verified using Git blob SHA-1. Files that need changes are downloaded and SHA-checked before replacement.

Ordinary directories only receive additions and updates. Upstream deletion, movement or renaming does not delete old local files there. Betterbot `main` and `dev` share the same strict synchronization rules for these directories, relative to the program directory:

- `addons/`
- `cfg/sourcemod/`
- `materials/models/weapons/`
- `materials/panorama/images/icons/equipment/`
- `materials/panorama/images/icons/skillgroups/`
- `materials/panorama/images/tournaments/teams/`
- `materials/lvl_overlays/`
- `models/inventory_items/`
- `models/weapons/`
- `resource/flash/econ/tournaments/teams/`

After all downloads and updates succeed, Betterbot recursively removes local non-protected files in these directories that are absent from the selected branch, including user-created files, then removes empty directories. A strict directory absent from a valid complete remote tree is treated as empty, so its local non-protected files are removed; a complete empty tree applies this rule to all strict directories. Parent and adjacent directories retain their ordinary synchronization behavior. Get5 preserves extra local files throughout the target.

Root files named `LICENSE`, `README` or `.gitattributes`, including nonempty extension variants such as `README.md`, are excluded case-insensitively and local matching root files are removed when safe. Subdirectory files and names such as `README-old.md` do not match this rule. Matching directories are not recursively deleted.

A complete empty tree is valid. Missing, truncated, conflicting or unsafe repository trees are rejected before probing or synchronization cleanup. Remote symbolic-link blobs and unknown blob modes are refused; regular `100644` and `100755` blobs can supply content.

## Safety, State and Logs

Windows filesystem paths compare without case; Linux paths compare with case. The updater checks target boundaries, link/reparse-point ancestors and file/directory conflicts. It protects its actual host executable, including after renaming, and known updater helper filenames. Filesystem roots cannot be normal synchronization targets.

Replacements stage a file beside the destination and use atomic replace/move operations. Linux replacements preserve the Unix mode of an existing ordinary file. New files use normal download-file permissions; Git execution bits are not separately applied. Atomicity covers one file. Failed runs can retain files already updated; there is no whole-run transaction or rollback.

A named global mutex prevents concurrent runs for the same target across sessions. Different targets can run concurrently. Older .NET Framework clients use a different lock namespace, so avoid running an old and new client against the same target.

State uses the existing `sync-state.json` version and fields, with compatibility for `tracked-files.txt`. Legacy manifests do not drive ordinary-file deletion. Betterbot stores state under the target hash; Get5 uses its `get5/` subdirectory.

State candidates begin with `UPDATECLIENT_SYNC_STATE` and `BETTERBOT_SYNC_STATE`, in that order. Use absolute paths for explicit roots.

- Windows reuses existing target state before creating a new directory. It retains the `UpdateClientSync` and `BetterBotSync` roots under local application data, roaming application data or temporary storage, plus the existing target-hash algorithm.
- Linux uses candidates in order: explicit roots, an absolute `XDG_STATE_HOME/UpdateClientSync`, `$HOME/.local/state/UpdateClientSync`, then temporary `UpdateClientSync`. Relative XDG paths are ignored. Existing temporary state does not take priority over an available persistent candidate.

Logs append to `log/UpdateClient-yyyy-MM-dd.log` under the target. Older daily logs are archived as individual `.7z` files using the included managed implementation. Archive failure retains the source log. No external 7-Zip program is needed by the product.

## Build and Verification

Install a .NET 10 SDK. The solution is `UpdateClient.sln`; the product project is `src/UpdateClient/UpdateClient.csproj`. On Windows:

```powershell
./tools/Build-UpdateClient.bat --no-pause
```

The script publishes both `win-x64` and `linux-x64`; either failure returns a nonzero exit code. Final artifacts are `local/dist/UpdateClient.exe` and `local/dist/linux-x64/UpdateClient`. Intermediate builds and publications stay under `local/`.

To publish on a host with the SDK directly:

```bash
dotnet publish src/UpdateClient/UpdateClient.csproj -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=embedded -p:PublishTrimmed=false -p:PublishAot=false -o local/publish/UpdateClient/linux-x64
```

Use `win-x64` and a corresponding output directory for Windows. The deployment artifact needs no external DLL, deps, runtimeconfig or PDB files. Trimming and AOT are disabled.

The existing console verification project is `tests/UpdateClient.SyncTests`. It links production code and stays outside product packaging. Its automatic groups are `policy`, `state`, `remote`, `sync`, `app`, `logging`, `platform`, `mutex` and `all`. `menu` checks interactive selection; `self-protect` requires a published single-file test host.

Linux archive-content validation uses `UPDATECLIENT_TEST_7ZZ` to point at an independent official 7zz executable. This is a test tool, and absence or extraction failure fails that validation. Tests use isolated targets; never perform a normal sync in the source checkout or publication directory. Real repository synchronization also depends on upstream API quotas and file availability; offline fixture checks do not establish live network success.

## Structure and License

Product source is under `src/UpdateClient/`: `App` orchestrates a run; `ConsoleUi` handles menus and progress; `Remote` retrieves and validates repository trees and content; `Sync` applies repository policy and mutex control; `State` stores caches; `FileSystem` handles paths and atomic writes; `Security` checks Git blob hashes; and `Logging` manages daily logs and archives.

The repository's terms are provided in `LICENSE`.
