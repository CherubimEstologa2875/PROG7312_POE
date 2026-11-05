using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;
using SmartX.Contracts;

namespace SmartX.Api.Stores;

public sealed class UploadRejectedException(
    string message,
    int status = 400)
    : Exception(message)
{
    public int Status { get; } = status;
}

public sealed class AttachmentStore
{
    public const long MaxFileBytes = 10 * 1024 * 1024;

    private readonly string root;

    // File-system and manifest mutations are serialized inside the process
    // so two requests cannot update the in-memory attachment list together.
    private readonly object gate = new();

    // Limit the number of simultaneous uploads handled by this process.
    private readonly SemaphoreSlim uploadSlots = new(2);

    private List<SensorAttachment> attachments;

    private string Manifest =>
        Path.Combine(root, "index.json");

    public AttachmentStore(
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        root = Path.GetFullPath(
            configuration["Attachments:Root"] ??
            "App_Data/attachments",
            environment.ContentRootPath);

        Directory.CreateDirectory(root);

        attachments = File.Exists(Manifest)
            ? JsonSerializer.Deserialize<List<SensorAttachment>>(
                File.ReadAllText(Manifest))
                ?? throw new InvalidDataException(
                    "Invalid attachment index.")
            : [];

        // Existing metadata is checked on startup so corrupt, missing or
        // inconsistent attachment records are not silently accepted.
        if (attachments.Any(a =>
            a == null ||
            !Guid.TryParseExact(a.Id, "N", out _) ||
            !Guid.TryParseExact(a.SensorId, "N", out _) ||
            a.Size < 1 ||
            a.Size > MaxFileBytes ||
            !File.Exists(PathFor(a.Id)) ||
            new FileInfo(PathFor(a.Id)).Length != a.Size))
        {
            throw new InvalidDataException(
                "The attachment index contains invalid or missing files.");
        }
    }

    public SensorAttachment[] Read(string sensorId)
    {
        lock (gate)
        {
            return attachments
                .Where(a => a.SensorId == sensorId)
                .OrderByDescending(a => a.UploadedAt)
                .ToArray();
        }
    }

    public string PathFor(string id) =>
        Path.Combine(root, id + ".bin");

    public async Task<SensorAttachment> UploadAsync(
        HttpRequest request,
        SensorRegistration sensor,
        SensorRegistrationStore registry,
        string kind,
        CancellationToken cancellation)
    {
        // Each attachment category has an explicit extension allow-list.
        string[] extensions = kind switch
        {
            "Configuration" =>
                [".json", ".xml", ".ini", ".cfg", ".conf", ".yaml", ".yml"],

            "Photo" =>
                [".png", ".jpg", ".jpeg"],

            "Log" =>
                [".txt", ".log", ".csv"],

            "Deployment" =>
                [".json"],

            _ => throw new UploadRejectedException(
                "Choose Configuration, Photo, Log or Deployment.")
        };

        if (!MediaTypeHeaderValue.TryParse(
                request.ContentType,
                out var media) ||
            !string.Equals(
                media.MediaType.Value,
                "multipart/form-data",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new UploadRejectedException(
                "Use multipart/form-data with one file part named file.");
        }

        string boundary =
            HeaderUtilities.RemoveQuotes(media.Boundary).Value ?? "";

        if (boundary.Length == 0 || boundary.Length > 128)
        {
            throw new UploadRejectedException(
                "Invalid multipart boundary.");
        }

        // Reject excess concurrent uploads instead of allowing an
        // unrestricted number of large file operations to accumulate.
        if (!await uploadSlots.WaitAsync(0, cancellation))
        {
            throw new UploadRejectedException(
                "Two uploads are already running. Try again shortly.",
                429);
        }

        string id = Guid.NewGuid().ToString("N");
        string temporary =
            Path.Combine(root, id + ".upload");

        bool committed = false;

        try
        {
            var reader = new MultipartReader(
                boundary,
                request.Body)
            {
                HeadersCountLimit = 16,
                HeadersLengthLimit = 8192
            };

            var section =
                await reader.ReadNextSectionAsync(cancellation);

            if (section == null ||
                !ContentDispositionHeaderValue.TryParse(
                    section.ContentDisposition,
                    out var disposition) ||
                disposition.DispositionType != "form-data" ||
                HeaderUtilities.RemoveQuotes(disposition.Name).Value != "file")
            {
                throw new UploadRejectedException(
                    "Provide exactly one file part named file.");
            }

            string fileName =
                HeaderUtilities.RemoveQuotes(
                    disposition.FileNameStar.HasValue
                        ? disposition.FileNameStar
                        : disposition.FileName)
                .Value ?? "";

            // Strip any path supplied by the client and retain only the
            // filename that is displayed in attachment metadata.
            fileName =
                Path.GetFileName(
                    fileName.Replace('\\', '/'));

            if (fileName.Length == 0 ||
                fileName.Length > 160 ||
                fileName.Any(char.IsControl) ||
                !extensions.Contains(
                    Path.GetExtension(fileName),
                    StringComparer.OrdinalIgnoreCase))
            {
                throw new UploadRejectedException(
                    "The filename or extension is not allowed for this attachment type.");
            }

            long size = 0;
            string digest;

            // Calculate the SHA-256 digest while streaming the file so a
            // second pass over the complete uploaded content is unnecessary.
            using (var hash =
                IncrementalHash.CreateHash(
                    HashAlgorithmName.SHA256))
            {
                await using var file =
                    new FileStream(
                        temporary,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None,
                        65536,
                        true);

                byte[] buffer = new byte[65536];
                int count;

                while ((count =
                    await section.Body.ReadAsync(
                        buffer,
                        cancellation)) > 0)
                {
                    size += count;

                    if (size > MaxFileBytes ||
                        kind == "Deployment" &&
                        size > 65536)
                    {
                        throw new UploadRejectedException(
                            kind == "Deployment"
                                ? "Deployment JSON is limited to 64 KB."
                                : "Files are limited to 10 MB.",
                            413);
                    }

                    hash.AppendData(
                        buffer,
                        0,
                        count);

                    await file.WriteAsync(
                        buffer.AsMemory(0, count),
                        cancellation);
                }

                digest =
                    Convert.ToHexString(
                        hash.GetHashAndReset());
            }

            if (size == 0)
            {
                throw new UploadRejectedException(
                    "Empty files cannot be attached.");
            }

            if (await reader.ReadNextSectionAsync(cancellation) != null)
            {
                throw new UploadRejectedException(
                    "Upload only one file at a time.");
            }

            // Photo contents are checked against their basic PNG/JPEG
            // signatures instead of trusting the filename extension alone.
            if (kind == "Photo")
            {
                byte[] signature = new byte[8];

                using var stream =
                    File.OpenRead(temporary);

                int read =
                    stream.Read(
                        signature,
                        0,
                        signature.Length);

                bool png =
                    read == 8 &&
                    signature.SequenceEqual(
                        new byte[]
                        {
                            137, 80, 78, 71,
                            13, 10, 26, 10
                        });

                bool jpeg =
                    read >= 3 &&
                    signature[0] == 255 &&
                    signature[1] == 216 &&
                    signature[2] == 255;

                if (Path.GetExtension(fileName)
                        .Equals(
                            ".png",
                            StringComparison.OrdinalIgnoreCase)
                    ? !png
                    : !jpeg)
                {
                    throw new UploadRejectedException(
                        "Photo contents must match the PNG or JPEG extension.");
                }
            }

            // Deployment attachments are deserialized and passed through the
            // same deployment validation used by the API.
            if (kind == "Deployment")
            {
                DeploymentNode? tree;

                try
                {
                    tree =
                        JsonSerializer.Deserialize<DeploymentNode>(
                            await File.ReadAllTextAsync(
                                temporary,
                                cancellation));
                }
                catch (JsonException)
                {
                    throw new UploadRejectedException(
                        "Invalid deployment JSON.");
                }

                var result =
                    DeploymentValidator.Validate(
                        tree!,
                        registry.Read(),
                        sensor.DeviceId);

                if (!result.IsValid)
                {
                    throw new UploadRejectedException(
                        string.Join(" ", result.Errors));
                }
            }

            var attachment =
                new SensorAttachment
                {
                    Id = id,
                    SensorId = sensor.Id,
                    FileName = fileName,
                    Kind = kind,
                    Size = size,
                    UploadedAt =
                        DateTimeOffset.UtcNow.ToString("O"),
                    Sha256 = digest
                };

            lock (gate)
            {
                if (attachments.Count(
                        a => a.SensorId == sensor.Id) >= 100)
                {
                    throw new UploadRejectedException(
                        "This sensor already has 100 attachments.",
                        409);
                }

                var next =
                    attachments
                        .Append(attachment)
                        .ToList();

                // Move the completed upload into its committed location
                // before updating the manifest.
                File.Move(
                    temporary,
                    PathFor(id));

                string pending =
                    Manifest + ".tmp";

                try
                {
                    File.WriteAllText(
                        pending,
                        JsonSerializer.Serialize(next));

                    File.Move(
                        pending,
                        Manifest,
                        true);
                }
                finally
                {
                    if (File.Exists(pending))
                        File.Delete(pending);
                }

                attachments = next;
                committed = true;
            }

            return attachment;
        }
        finally
        {
            // Temporary and partially committed files are cleaned up if any
            // stage of the upload fails.
            if (File.Exists(temporary))
                File.Delete(temporary);

            if (!committed &&
                File.Exists(PathFor(id)))
            {
                File.Delete(PathFor(id));
            }

            uploadSlots.Release();
        }
    }
}
// REFERENCE LIST 
// Jamro, M. 2018. C# Data Structures and Algorithms. Packt Publishing.
// Nakov, S. and Kolev, V. e. a., 2013. Fundamentals of Computer Programming with C#. 
