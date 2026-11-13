using System.Text.Json.Serialization;

namespace Filee.App.Services.Connect;

public sealed record ConnectMessage(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("from")] string From,
    [property: JsonPropertyName("timestamp")] long Timestamp);

public sealed record SharedFileItem(
    string Id,
    string Name,
    string RelativePath,
    long Size,
    string Sender,
    string MimeType,
    long CreatedAt,
    string DiskName,
    string DiskPath)
{
    public SharedFileMetadata ToMetadata() => new(Id, Name, RelativePath, Size, Sender, MimeType, CreatedAt, FileExecutionSafety.GetSafeIconKind(Name));
}

public sealed record SharedFileMetadata(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("relativePath")] string RelativePath,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("sender")] string Sender,
    [property: JsonPropertyName("mimeType")] string MimeType,
    [property: JsonPropertyName("createdAt")] long CreatedAt,
    [property: JsonPropertyName("icon")] string Icon);

public sealed record StorageUsage(
    [property: JsonPropertyName("used")] long Used,
    [property: JsonPropertyName("limit")] long Limit,
    [property: JsonPropertyName("files")] int Files,
    [property: JsonPropertyName("maxFiles")] int MaxFiles);

public sealed record PairedDevice(
    string Id,
    string Token,
    string Name,
    string UserAgent,
    DateTime PairedAt,
    DateTime LastSeen);

public sealed record PinPairRequest(
    [property: JsonPropertyName("pin")] string Pin,
    [property: JsonPropertyName("deviceName")] string? DeviceName);
