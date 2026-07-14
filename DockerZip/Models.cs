using System.Text.Json.Serialization;

namespace DockerZip;

// ── Manifest v2 / OCI ──────────────────────────────────────────────────────────

public class DockerManifest
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; }

    [JsonPropertyName("mediaType")]
    public string? MediaType { get; set; }

    [JsonPropertyName("config")]
    public BlobDescriptor? Config { get; set; }

    [JsonPropertyName("layers")]
    public List<BlobDescriptor>? Layers { get; set; }
}

public class BlobDescriptor
{
    [JsonPropertyName("mediaType")]
    public string? MediaType { get; set; }

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("digest")]
    public string? Digest { get; set; }
}

// ── Multi-arch manifest list / OCI index ───────────────────────────────────────

public class ManifestList
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; }

    [JsonPropertyName("mediaType")]
    public string? MediaType { get; set; }

    [JsonPropertyName("manifests")]
    public List<ManifestListEntry>? Manifests { get; set; }
}

public class ManifestListEntry
{
    [JsonPropertyName("mediaType")]
    public string? MediaType { get; set; }

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("digest")]
    public string? Digest { get; set; }

    [JsonPropertyName("platform")]
    public PlatformInfo? Platform { get; set; }
}

public class PlatformInfo
{
    [JsonPropertyName("architecture")]
    public string? Architecture { get; set; }

    [JsonPropertyName("os")]
    public string? Os { get; set; }

    [JsonPropertyName("variant")]
    public string? Variant { get; set; }

    public override string ToString()
    {
        var s = $"{Os}/{Architecture}";
        if (!string.IsNullOrEmpty(Variant)) s += $"/{Variant}";
        return s;
    }
}

// ── Internal progress/result types ────────────────────────────────────────────

public record DownloadProgress(string Status, int LayerCurrent, int LayerTotal, long BytesDone, long BytesTotal, bool IsLogEntry = false);

// ── Persisted application configuration ───────────────────────────────────────

public class AppConfig
{
    public string Registry { get; set; } = "https://registry-1.docker.io";
    public string Image { get; set; } = "library/ubuntu";
    public string Tag { get; set; } = "latest";
    public string OutputDir { get; set; } = "";
    public string Username { get; set; } = "";
    public bool SaveAsTar { get; set; } = true;
    public bool UseAzureSSO { get; set; } = false;
}
