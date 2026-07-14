using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace DockerZip;

/// <summary>
/// Orchestrates fetching manifests and downloading image layers.
/// Optionally produces a docker-save–compatible .tar file.
/// </summary>
public sealed class DownloadManager
{
    private readonly DockerRegistryClient _client;
    private readonly IProgress<DownloadProgress> _progress;

    // Media-type constants
    private const string MediaTypeManifestList = "application/vnd.docker.distribution.manifest.list.v2+json";
    private const string MediaTypeOciIndex = "application/vnd.oci.image.index.v1+json";

    public DownloadManager(DockerRegistryClient client, IProgress<DownloadProgress> progress)
    {
        _client = client;
        _progress = progress;
    }

    // ── Public entry point ────────────────────────────────────────────────────

    /// <summary>
    /// Fetches the manifest (or manifest list) for <paramref name="image"/>:<paramref name="reference"/>
    /// and returns information about available platforms.
    /// </summary>
    public async Task<FetchResult> FetchInfoAsync(string image, string reference, CancellationToken ct)
    {
        Report("Fetching manifest…", 0, 0, 0, 0);

        var (json, mediaType) = await _client.GetManifestAsync(image, reference, ct);

        if (IsManifestList(mediaType, json))
        {
            var list = JsonSerializer.Deserialize<ManifestList>(json)
                       ?? throw new InvalidDataException("Cannot parse manifest list.");

            var platforms = list.Manifests?
                .Where(m => m.Platform != null)
                .Select(m => m.Platform!)
                .ToList() ?? [];

            return new FetchResult(IsManifestList: true, Platforms: platforms, ManifestJson: json, MediaType: mediaType);
        }

        // Single-arch manifest
        var manifest = JsonSerializer.Deserialize<DockerManifest>(json)
                       ?? throw new InvalidDataException("Cannot parse manifest.");

        long totalBytes = (manifest.Config?.Size ?? 0)
                        + (manifest.Layers?.Sum(l => l.Size) ?? 0);

        Report($"Manifest fetched — {manifest.Layers?.Count ?? 0} layer(s), {FormatBytes(totalBytes)} total.", 0, 0, 0, 0);

        return new FetchResult(IsManifestList: false, Platforms: [], ManifestJson: json, MediaType: mediaType);
    }

    /// <summary>
    /// Downloads the image (resolving a manifest-list entry if needed) and saves it
    /// to <paramref name="outputDir"/>.  When <paramref name="saveAsTar"/> is <c>true</c>
    /// the result is a docker-loadable <c>.tar</c> file; otherwise raw blobs are saved.
    /// </summary>
    public async Task DownloadAsync(
        string image,
        string reference,
        string? platformOverride,   // e.g. "linux/amd64" — null = first / only
        string outputDir,
        bool saveAsTar,
        CancellationToken ct)
    {
        Directory.CreateDirectory(outputDir);

        // ── 1. Get manifest ──────────────────────────────────────────────────
        Report("Fetching manifest…", 0, 0, 0, 0);
        var (manifestJson, mediaType) = await _client.GetManifestAsync(image, reference, ct);

        if (IsManifestList(mediaType, manifestJson))
        {
            var list = JsonSerializer.Deserialize<ManifestList>(manifestJson)!;
            var entry = PickPlatform(list, platformOverride);

            Report($"Resolved manifest-list → {entry.Platform} ({entry.Digest![..19]}…)", 0, 0, 0, 0);
            (manifestJson, mediaType) = await _client.GetManifestAsync(image, entry.Digest!, ct);
        }

        var manifest = JsonSerializer.Deserialize<DockerManifest>(manifestJson)
                       ?? throw new InvalidDataException("Cannot parse image manifest.");

        if (manifest.Layers == null || manifest.Config == null)
            throw new InvalidDataException("Manifest missing layers or config.");

        int layerCount = manifest.Layers.Count;
        long totalBytes = manifest.Config.Size + manifest.Layers.Sum(l => l.Size);

        Report($"Image has {layerCount} layer(s) — {FormatBytes(totalBytes)} compressed.", 0, layerCount, 0, totalBytes);

        // ── 2. Download ──────────────────────────────────────────────────────
        if (saveAsTar)
        {
            var safeName = SanitizeFileName($"{image.Replace('/', '_')}_{reference}");
            var tarPath = Path.Combine(outputDir, $"{safeName}.tar");
            await SaveAsFlatAsync(image, manifest, tarPath, layerCount, totalBytes, ct);
            Report($"Saved: {tarPath}", layerCount, layerCount, totalBytes, totalBytes);
        }
        else
        {
            await SaveRawBlobsAsync(image, reference, manifest, outputDir, layerCount, totalBytes, ct);
            Report($"Blobs saved to: {outputDir}", layerCount, layerCount, totalBytes, totalBytes);
        }
    }

    // ── Flat filesystem snapshot ──────────────────────────────────────────────

    /// <summary>
    /// Downloads all layers, merges them in order (applying whiteout semantics),
    /// and writes a single flat tar representing the final container filesystem.
    /// </summary>
    private async Task SaveAsFlatAsync(
        string image,
        DockerManifest manifest,
        string tarPath,
        int layerCount, long totalBytes,
        CancellationToken ct)
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"dockerzip_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);

        try
        {
            // ── 1. Download and decompress every layer ───────────────────────
            var layerTars = new List<string>();
            long doneSoFar = 0;

            for (int i = 0; i < manifest.Layers!.Count; i++)
            {
                var layer = manifest.Layers[i];
                var layerHex = DigestToHex(layer.Digest!);
                var gzPath = Path.Combine(tmp, $"{layerHex}.tar.gz");
                var tarLayerPath = Path.Combine(tmp, $"{layerHex}.tar");

                Report($"Downloading layer {i + 1}/{layerCount} ({FormatBytes(layer.Size)})…",
                       i, layerCount, doneSoFar, totalBytes);

                await using (var fs = new FileStream(gzPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true))
                {
                    var progress = new Progress<(long Done, long Total)>(p =>
                        Report($"Layer {i + 1}/{layerCount} — {FormatBytes(p.Done)}/{FormatBytes(layer.Size)}",
                               i, layerCount, doneSoFar + p.Done, totalBytes, isLogEntry: false));
                    await _client.DownloadBlobAsync(image, layer.Digest!, fs, progress, ct);
                    doneSoFar += layer.Size;
                }

                await using (var gzStream = new FileStream(gzPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true))
                await using (var decompressed = new GZipStream(gzStream, CompressionMode.Decompress))
                await using (var outFile = new FileStream(tarLayerPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true))
                    await decompressed.CopyToAsync(outFile, ct);

                File.Delete(gzPath);
                layerTars.Add(tarLayerPath);
            }

            // ── 2. Build merged file map (later layers win) ──────────────────
            // path → owning layer index; -1 means deleted by whiteout
            Report("Merging layers…", layerCount - 1, layerCount, totalBytes - 1, totalBytes);

            var fileMap = new Dictionary<string, int>(StringComparer.Ordinal);

            for (int i = 0; i < layerTars.Count; i++)
            {
                await using var layerStream = new FileStream(layerTars[i], FileMode.Open, FileAccess.Read, FileShare.Read);
                using var reader = new TarReader(layerStream, leaveOpen: false);

                while (await reader.GetNextEntryAsync(copyData: false, ct) is { } entry)
                {
                    var name = NormalizeTarPath(entry.Name);
                    var fileName = Path.GetFileName(name.TrimEnd('/'));

                    if (fileName == ".wh..wh..opq")
                    {
                        // Opaque whiteout: replace entire parent directory contents
                        var dir = Path.GetDirectoryName(name.TrimEnd('/'))?.Replace('\\', '/') ?? "";
                        if (dir.Length > 0 && !dir.EndsWith('/')) dir += "/";
                        foreach (var key in fileMap.Keys.Where(k => k == dir.TrimEnd('/') || k.StartsWith(dir)).ToList())
                            fileMap[key] = -1;
                        continue;
                    }

                    if (fileName.StartsWith(".wh."))
                    {
                        // Regular whiteout: delete the named target
                        var dir = Path.GetDirectoryName(name)?.Replace('\\', '/') ?? "";
                        var target = dir.Length > 0 ? $"{dir}/{fileName[4..]}" : fileName[4..];
                        fileMap[target] = -1;
                        fileMap[name] = -1;
                        continue;
                    }

                    fileMap[name] = i;
                }
            }

            // ── 3. Write flat output tar ─────────────────────────────────────
            Report("Assembling flat snapshot…", layerCount - 1, layerCount, totalBytes - 1, totalBytes);

            await using var outTarStream = new FileStream(tarPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true);
            await using var tarWriter = new TarWriter(outTarStream, TarEntryFormat.Gnu, leaveOpen: false);

            var written = new HashSet<string>(StringComparer.Ordinal);

            for (int i = 0; i < layerTars.Count; i++)
            {
                await using var layerStream = new FileStream(layerTars[i], FileMode.Open, FileAccess.Read, FileShare.Read);
                using var reader = new TarReader(layerStream, leaveOpen: false);

                while (await reader.GetNextEntryAsync(copyData: false, ct) is { } entry)
                {
                    var name = NormalizeTarPath(entry.Name);
                    var fileName = Path.GetFileName(name.TrimEnd('/'));

                    // Skip whiteout marker files
                    if (fileName.StartsWith(".wh.")) continue;

                    // Skip if this layer isn't the owner of this path
                    if (!fileMap.TryGetValue(name, out var owner) || owner != i) continue;

                    // Skip duplicates (safety)
                    if (!written.Add(name)) continue;

                    await tarWriter.WriteEntryAsync(entry, ct);
                }
            }
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { /* best effort */ }
        }
    }

    private static string NormalizeTarPath(string path)
    {
        path = path.Replace('\\', '/');
        if (path.StartsWith("./")) path = path[2..];
        else if (path.StartsWith("/")) path = path[1..];
        return path;
    }

    // ── Raw blobs ─────────────────────────────────────────────────────────────

    private async Task SaveRawBlobsAsync(
        string image, string reference,
        DockerManifest manifest,
        string outputDir,
        int layerCount, long totalBytes,
        CancellationToken ct)
    {
        long done = 0;

        // config
        var configPath = Path.Combine(outputDir, $"{DigestToHex(manifest.Config!.Digest!)}.json");
        Report($"Downloading config…", 0, layerCount, done, totalBytes);
        await using (var fs = new FileStream(configPath, FileMode.Create))
            await _client.DownloadBlobAsync(image, manifest.Config.Digest!, fs, null, ct);
        done += manifest.Config.Size;

        // layers
        for (int i = 0; i < manifest.Layers!.Count; i++)
        {
            var layer = manifest.Layers[i];
            var layerPath = Path.Combine(outputDir, $"{DigestToHex(layer.Digest!)}.tar.gz");
            Report($"Downloading layer {i + 1}/{layerCount} ({FormatBytes(layer.Size)})…",
                   i, layerCount, done, totalBytes);

            await using var fs = new FileStream(layerPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true);
            var layerProgress = new Progress<(long Done, long Total)>(p =>
                Report($"Layer {i + 1}/{layerCount} — {FormatBytes(p.Done)}/{FormatBytes(layer.Size)}",
                       i, layerCount, done + p.Done, totalBytes, isLogEntry: false));

            await _client.DownloadBlobAsync(image, layer.Digest!, fs, layerProgress, ct);
            done += layer.Size;
        }

        // Write a simple info file
        var infoPath = Path.Combine(outputDir, "manifest.json");
        await File.WriteAllTextAsync(infoPath,
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }), ct);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ManifestListEntry PickPlatform(ManifestList list, string? platformOverride)
    {
        var entries = list.Manifests ?? throw new InvalidDataException("Empty manifest list.");

        if (string.IsNullOrEmpty(platformOverride))
            return entries.FirstOrDefault(e => e.Platform?.Os == "linux" && e.Platform?.Architecture == "amd64")
                   ?? entries[0];

        var parts = platformOverride.Split('/');
        var os = parts.Length > 0 ? parts[0] : null;
        var arch = parts.Length > 1 ? parts[1] : null;
        var variant = parts.Length > 2 ? parts[2] : null;

        return entries.FirstOrDefault(e =>
                   e.Platform?.Os == os &&
                   e.Platform?.Architecture == arch &&
                   (variant == null || e.Platform?.Variant == variant))
               ?? throw new InvalidOperationException($"Platform '{platformOverride}' not found in manifest list.");
    }

    private static bool IsManifestList(string mediaType, string json)
    {
        if (mediaType == MediaTypeManifestList || mediaType == MediaTypeOciIndex) return true;

        // Fallback: inspect schemaVersion + mediaType field in the JSON
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("manifests", out _)) return true;
        }
        catch { /* ignore */ }

        return false;
    }

    private static string DigestToHex(string digest)
    {
        // digest format: "sha256:<hex>" → strip algorithm prefix
        var colon = digest.IndexOf(':');
        return colon >= 0 ? digest[(colon + 1)..] : digest;
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 0) return "? B";
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }

    private static string SanitizeFileName(string name) =>
        string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    private void Report(string status, int current, int total, long done, long bytes, bool isLogEntry = true) =>
        _progress.Report(new DownloadProgress(status, current, total, done, bytes, isLogEntry));
}

public record FetchResult(
    bool IsManifestList,
    List<PlatformInfo> Platforms,
    string ManifestJson,
    string MediaType);
