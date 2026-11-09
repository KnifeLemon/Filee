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
    public SharedFileMetadata ToMetadata() => new(Id, Name, RelativePath, Size, Sender, MimeType, CreatedAt);
}

public sealed record SharedFileMetadata(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("relativePath")] string RelativePath,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("sender")] string Sender,
    [property: JsonPropertyName("mimeType")] string MimeType,
    [property: JsonPropertyName("createdAt")] long CreatedAt);

public sealed record StorageUsage(
    [property: JsonPropertyName("used")] long Used,
    [property: JsonPropertyName("limit")] long Limit,
    [property: JsonPropertyName("files")] int Files,
    [property: JsonPropertyName("maxFiles")] int MaxFiles);

public sealed record ConnectSessionState(
    string Token,
    string MobileUrl,
    long ExpiresAt,
    int PcClients,
    int MobileClients,
    string? ConnectedDeviceName);
