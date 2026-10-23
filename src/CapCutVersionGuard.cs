using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;

namespace CapCutBatchClient
{
    internal static class CapCutVersionGuard
    {
        internal const string VersionChangedCode = "CAPCUT_PINNED_VERSION_CHANGED";
        internal const string UpdaterEnabledCode = "CAPCUT_UPDATER_REENABLED";

        public static void Validate(AppConfig config)
        {
            if (config == null || !config.RequirePinnedCapCutVersion) return;

            string executable = FullPath(config.CapCutPath);
            if (String.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
                throw new InvalidOperationException(
                    VersionChangedCode + ": fixed CapCut executable is missing");

            string expectedVersion = (config.PinnedCapCutVersion ?? "").Trim();
            string actualVersion = FileVersionInfo.GetVersionInfo(executable).FileVersion ?? "";
            if (expectedVersion.Length == 0 ||
                !String.Equals(expectedVersion, actualVersion,
                    StringComparison.OrdinalIgnoreCase)) {
                throw new InvalidOperationException(String.Format(
                    "{0}: expected {1}, found {2}", VersionChangedCode,
                    expectedVersion.Length == 0 ? "<not configured>" : expectedVersion,
                    actualVersion.Length == 0 ? "<unknown>" : actualVersion));
            }

            string expectedHash = NormalizeHash(config.PinnedCapCutSha256);
            string actualHash = Sha256(executable);
            if (expectedHash.Length != 64 ||
                !String.Equals(expectedHash, actualHash,
                    StringComparison.OrdinalIgnoreCase)) {
                throw new InvalidOperationException(
                    VersionChangedCode + ": fixed CapCut executable hash changed");
            }

            string updater = Path.Combine(Path.GetDirectoryName(executable),
                "CapCut-DiffUpgrade.exe");
            if (File.Exists(updater))
                throw new InvalidOperationException(
                    UpdaterEnabledCode + ": CapCut update executable is active");

            string disabledUpdater = FullPath(config.DisabledCapCutUpdaterPath);
            if (String.IsNullOrWhiteSpace(disabledUpdater) ||
                !File.Exists(disabledUpdater))
                throw new InvalidOperationException(
                    UpdaterEnabledCode + ": disabled updater backup is missing");

            string expectedUpdaterHash = NormalizeHash(
                config.DisabledCapCutUpdaterSha256);
            if (expectedUpdaterHash.Length != 64 ||
                !String.Equals(expectedUpdaterHash, Sha256(disabledUpdater),
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    UpdaterEnabledCode + ": disabled updater backup hash changed");
        }

        private static string FullPath(string value)
        {
            if (String.IsNullOrWhiteSpace(value)) return "";
            try {
                return Path.GetFullPath(Environment.ExpandEnvironmentVariables(
                    value.Trim()));
            } catch {
                return "";
            }
        }

        private static string NormalizeHash(string value)
        {
            return (value ?? "").Replace("-", "").Trim().ToUpperInvariant();
        }

        private static string Sha256(string path)
        {
            using (FileStream stream = File.Open(path, FileMode.Open,
                    FileAccess.Read, FileShare.Read))
            using (SHA256 hash = SHA256.Create()) {
                return BitConverter.ToString(hash.ComputeHash(stream))
                    .Replace("-", "");
            }
        }
    }
}
