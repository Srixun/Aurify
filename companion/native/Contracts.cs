using System;
using System.Collections.Generic;

namespace AurifyClient
{
    // Persisted names deliberately match the existing companion's JSON and DPAPI records.
    internal sealed class ClientConfig
    {
        public int version { get; set; }
        public string gameDir { get; set; }
        public string serverUrl { get; set; }
        public string guildName { get; set; }
        public int pollMs { get; set; }
        public string foreverVisibility { get; set; }
    }

    internal sealed class ClientCredentials
    {
        public string deviceToken { get; set; }
        public string deviceId { get; set; }
        public string sourceKey { get; set; }
    }

    internal sealed class AddonInfo
    {
        public string Status = "missing";
        public string Version;
        public int Interface;
    }

    internal sealed class GameInstallation
    {
        public string GameDir;
        public string Executable;
        public bool Running;
        public Dictionary<string, AddonInfo> Addons = new Dictionary<string, AddonInfo>(StringComparer.Ordinal);
    }

    internal sealed class DiscoveryResult
    {
        public List<GameInstallation> Installations = new List<GameInstallation>();
        public bool ProbeUnavailable;
    }

    internal sealed class AddonResult
    {
        public string Status;
        public string Message;
        public string Version;
        public bool RestartRequired;
        public bool Retryable;
    }

    internal sealed class AddonSetup
    {
        public string Status = "pending";
        public bool RestartRequired;
        public Dictionary<string, AddonResult> Results = new Dictionary<string, AddonResult>(StringComparer.Ordinal);
        public Dictionary<string, AddonUpdateResult> Updates = new Dictionary<string, AddonUpdateResult>(StringComparer.Ordinal);
    }

    internal sealed class PairingRequest
    {
        public string DeviceCode;
        public string UserCode;
        public string VerificationUri;
        public int ExpiresIn;
        public int Interval;
    }

    internal sealed class PairingApproval
    {
        public string Status;
        public string Token;
        public string DeviceId;
        public string ServerUrl;
    }

    internal sealed class CombatReply
    {
        public long Offset;
        public string ReportId;
    }

    internal sealed class StableFileMetadata
    {
        public string Identity;
        public string ParentIdentity;
        public long Length;
        public long LastWriteTicks;
    }

    internal sealed class StableFile
    {
        public byte[] Bytes;
        public long LastWriteMs;
        public string Identity;
        public StableFileMetadata Metadata;
    }
}
