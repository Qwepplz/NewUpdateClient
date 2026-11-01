using System.Collections.Generic;

namespace UpdateClient.Config
{
    internal static class AppOptions
    {
        public const int RequestTimeoutMs = 15000;
        public const string RemoteUserAgent = "UpdateClient";
        public const string ApiAcceptHeader = "application/vnd.github+json";
        public const string BinaryAcceptHeader = "application/octet-stream, */*";
        public const string LogDirectoryName = "log";
        public const string LogFilePrefix = "UpdateClient-";
        public const string LogFileDateFormat = "yyyy-MM-dd";
        public const string LogFileExtension = ".log";
        public const string LogArchiveExtension = ".7z";
        public const string LogArchiveTempExtension = ".tmp";
        public const string SyncStateFileName = "sync-state.json";
        public const string LegacyManifestFileName = "tracked-files.txt";
        public const string StagingArtifactMarker = ".__updateclient_sync_staging__";
        public const string BackupArtifactMarker = ".__updateclient_sync_backup__";
        public const string LegacyStagingArtifactMarker = ".__betterbot_sync_staging__";
        public const string LegacyBackupArtifactMarker = ".__betterbot_sync_backup__";
        public const string MutexNamePrefix = @"Global\UpdateClientSync_";
        public const int SyncStateVersion = 1;
        public const string PrimaryStateRootDirectoryName = "UpdateClientSync";
        public const string LegacyStateRootDirectoryName = "BetterBotSync";

        public const string StableBranchName = "main";
        public const string DevelopmentBranchName = "dev";

        public static readonly IReadOnlyList<string> StateRootEnvironmentVariables = new[]
        {
            "UPDATECLIENT_SYNC_STATE",
            "BETTERBOT_SYNC_STATE"
        };

        public static readonly IReadOnlyList<string> StateRootDirectoryNames = new[]
        {
            PrimaryStateRootDirectoryName,
            LegacyStateRootDirectoryName
        };

        public static readonly IReadOnlyList<string> ArtifactMarkers = new[]
        {
            StagingArtifactMarker,
            BackupArtifactMarker,
            LegacyStagingArtifactMarker,
            LegacyBackupArtifactMarker
        };

        public static readonly IReadOnlyList<string> ProtectedHelperFileNames = new[]
        {
            "_UpdateClient.bat",
            "_UpdateClient.ps1",
            "UpdateClient.cs",
            "Build-UpdateClient.bat",
            "Build-UpdateClient.cmd",
            "UpdateClient.exe",
            "UpdateClient",
            "_UpdateBetterBot.bat",
            "_UpdateBetterBot.ps1",
            "UpdateBetterBot.cs",
            "Build-UpdateBetterBot.bat",
            "Build-UpdateBetterBot.cmd",
            "UpdateBetterBot.exe"
        };

        public static readonly RepositoryTarget Betterbot =
            new RepositoryTarget("Betterbot", "betterbot", "Qwepplz", "Betterbot", null, null,
                new[]
                {
                    "addons",
                    "cfg/sourcemod",
                    "materials/models/weapons",
                    "materials/panorama/images/icons/equipment",
                    "materials/panorama/images/icons/skillgroups",
                    "materials/panorama/images/tournaments/teams",
                    "materials/lvl_overlays",
                    "models/inventory_items",
                    "models/weapons",
                    "resource/flash/econ/tournaments/teams"
                });

        public static readonly RepositoryTarget Get5 =
            new RepositoryTarget("Get5", "get5", "Qwepplz", "get5", "SaUrrr", "get5",
                new string[0]);
    }
}
