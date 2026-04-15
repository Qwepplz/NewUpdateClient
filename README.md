# UpdateClient

UpdateClient is a repository sync updater for Windows directories. Its purpose is to safely synchronize the contents of a predefined remote repository into the directory where the program is running, while preserving local sync state and runtime logs so that repeated execution, interrupted updates, and troubleshooting remain traceable.

This README is intended to be the long-term project description. It focuses only on the stable identity, behavior, architectural boundaries, and runtime conventions of the project, and does not record version history, recent changes, temporary maintenance notes, or phase-specific development plans.

## Project Positioning

UpdateClient is a Windows console application built on `.NET Framework 4.8`. It is neither a general-purpose package manager nor a resident background service. Instead, it is a single-run directory synchronization tool: the user confirms the target directory, the program performs one full synchronization from a remote repository into that directory, and then exits after reporting the result.

The core object of the project is the directory in which the program is located. In practice, this means UpdateClient is expected to run inside the directory that is being updated. The target directory is therefore the managed object itself rather than a separate data directory created or controlled by the program. During synchronization, the program decides which files should be added, updated, kept, or removed by comparing the remote repository tree, the local file state, and the recorded synchronization history.

## How It Works

When the program starts, it displays the target directory and the configured sync target, then waits for user confirmation. After confirmation, UpdateClient resolves the remote repository's default branch, retrieves the full repository tree, and loads the existing local synchronization state to build a comparison view. For files that exist upstream, the program first checks whether local files already match by using cached state and Git blob hashes; only files that truly require changes are downloaded and written into the target directory.

Once synchronization is complete, the program summarizes how many files were added, updated, removed, or left unchanged, and then exports the new sync state. Files that were previously managed by the tool but have been deleted upstream are removed from the target directory when it is safe to do so. The entire process is bounded as a single execution cycle: confirm before starting, show progress while running, print a summary at the end, and then exit.

Remote access follows an ordered primary-source and mirror fallback strategy. This strategy improves the chances of successfully retrieving repository metadata and file contents, but it does not change the nature of the project: UpdateClient remains a tool for synchronizing predefined repository content into a local directory, not a general-purpose mirror management platform.

## Safety and State

Before writing any file, UpdateClient validates paths to ensure synchronized content cannot escape the target directory boundary. It also checks for directory conflicts and uses temporary directories while preparing downloads and replacements. The program establishes a mutex for each target directory so that multiple synchronization processes do not modify the same file set at the same time.

Because the updater usually lives inside the directory that it updates, UpdateClient protects its own helper scripts, source-style filenames, and executable filenames from being overwritten during synchronization. Temporary artifacts left behind by previous runs are detected and cleaned up when possible, which helps reduce residue after abnormal termination.

Runtime logs are stored in the `log/` directory under the target folder by default. Synchronization state is stored in `sync-state.json`, with compatibility for the older `tracked-files.txt` manifest format. The state root can be overridden through the `UPDATECLIENT_SYNC_STATE` or `BETTERBOT_SYNC_STATE` environment variables; if no override is provided, the program chooses an available state location under local application data, roaming application data, or the system temporary directory.

## Project Structure

The source code is located under `src/UpdateClient`. Within that tree, `App` is responsible for application startup, flow orchestration, exception handling, and log lifecycle management; `Config` defines constants, repository targets, and runtime conventions; `ConsoleUi` handles startup prompts, pause behavior, and progress display; `Remote` encapsulates remote repository metadata, tree retrieval, and file download access; `State` manages sync state import, export, cache matching, and legacy format compatibility; and `Sync` contains the core synchronization workflow, mutex control, and result aggregation.

File-system, security, and logging concerns are separated into dedicated areas. `FileSystem` handles safe paths, directory-conflict checks, and atomic writes; `Security` provides Git blob hashing and content matching; and `Logging` manages session logs, file writers, and shared console output behavior. This structure keeps synchronization flow, remote access, local state, and infrastructure concerns clearly separated.

## Build and License

The solution entry point is `UpdateClient.sln`, the main project file is `src/UpdateClient/UpdateClient.csproj`, and directory-level build properties are defined in `Directory.Build.props`. The local development build script entry point is `tools/Build-UpdateClient.bat`.

The license text for this repository is provided in `LICENSE`. Any use or distribution of the project should follow the terms defined in that file.
