// Checks a preset.json shared through the preset gallery (#1530) against the allow-list (SharedPresetRules), first as
// JSON (every field known, every value in range: nothing the app would read is left unchecked) and then as presets
// (kinds, tools, target formats, references between presets and the donut). The gallery's CI runs it through
// 'filee presets check', and the app runs it again before it installs anything, without trusting the server.

using System.Security.Cryptography;
using System.Text.Json;
using Filee.Core.Formats;
using Filee.Core.Presets;
using Filee.Core.Settings;

namespace Filee.Core.Gallery;

/// <summary>Checks one preset.json (a <see cref="PresetBundle"/>) for the preset gallery.</summary>
public static class SharedBundleCheck
{
    /// <summary>The file name problems in a bundle are reported against.</summary>
    public const string FileName = "preset.json";

    /// <summary>
    /// The lowest Filee that can install a shared preset: the first with the gallery. A field added to the allow-list
    /// later raises it for the presets that use it.
    /// </summary>
    public const string BaselineVersion = "2.5.0";

    /// <summary>Largest preset.json, in bytes.</summary>
    public const int MaxBytes = SharedPresetRules.MaxBundleBytes;

    /// <summary>Checks <paramref name="json"/>, the bytes of a preset.json.</summary>
    public static PresetCheckResult Check(ReadOnlySpan<byte> json) => Check(json, SharedPresetRules.MaxPresets);

    internal static PresetCheckResult Check(ReadOnlySpan<byte> json, int maxPresets)
    {
        var result = new PresetCheckResult();
        if (json.Length > SharedPresetRules.MaxBundleBytes)
        {
            result.Add("bundle.too_large", FileName, "", $"is {json.Length:N0} bytes; a shared preset.json may have at most {SharedPresetRules.MaxBundleBytes:N0}");
            return result;
        }
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(json));
        // An editor's byte order mark is the only thing tolerated before the JSON.
        if (json.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
            json = json[3..];

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json.ToArray(), new JsonDocumentOptions
            {
                MaxDepth = SharedPresetRules.MaxDepth,
                CommentHandling = JsonCommentHandling.Disallow,
                AllowTrailingCommas = false,
            });
        }
        catch (JsonException ex)
        {
            var deep = ex.Message.Contains("maximum configured depth", StringComparison.OrdinalIgnoreCase);
            result.Add(deep ? "bundle.too_deep" : "bundle.invalid_json", FileName, "",
                deep ? $"nests deeper than {SharedPresetRules.MaxDepth} levels" : "is not valid JSON: " + FirstLine(ex.Message));
            return result;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                result.Add("bundle.not_object", FileName, "", "must be a JSON object with \"presets\" (as Settings → General → Export writes it)");
                return result;
            }
            var rules = maxPresets == SharedPresetRules.MaxPresets
                ? SharedPresetRules.BundleFields
                : SharedPresetRules.BundleFields with
                {
                    Fields = new Dictionary<string, ValueRule>(SharedPresetRules.BundleFields.Fields)
                    {
                        ["presets"] = new ListRule(SharedPresetRules.PresetFields, maxPresets),
                    },
                };
            new Walker(result, FileName).Check(root, rules, "");
            if (!root.TryGetProperty("presets", out var presets) || presets.ValueKind != JsonValueKind.Array || presets.GetArrayLength() == 0)
                result.Add("bundle.no_presets", FileName, "presets", "has no presets");
            if (!result.Ok)
                return result;
        }

        PresetBundle bundle;
        try
        {
            bundle = JsonSerializer.Deserialize(json, FileeJsonContext.Default.PresetBundle)
                     ?? throw new JsonException("null");
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            result.Add("bundle.invalid_json", FileName, "", "can't be read as a preset bundle: " + FirstLine(ex.Message));
            return result;
        }

        CheckPresets(bundle, result);
        if (result.Ok)
            result.Bundle = bundle;
        result.Derived = Derive(bundle, sha256);
        return result;
    }

    /// <summary>The rules that span fields: kinds, tools, targets and the ids presets and the donut refer to.</summary>
    private static void CheckPresets(PresetBundle bundle, PresetCheckResult result)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < bundle.Presets.Count; i++)
        {
            var preset = bundle.Presets[i];
            var at = $"presets[{i}]";
            if (!ids.Add(preset.Id))
                result.Add("preset.duplicate_id", FileName, at + ".id", $"the id \"{preset.Id}\" is used by another preset in the file");
            if (string.IsNullOrWhiteSpace(preset.Name) && string.IsNullOrEmpty(preset.NameKey))
                result.Add("preset.no_name", FileName, at + ".name", "needs a name");

            switch (preset.Kind)
            {
                case PresetKind.Tool when !SharedPresetRules.Tools.Contains(preset.Tool):
                    result.Add("preset.tool_not_allowed", FileName, at + ".tool",
                        $"the tool \"{preset.Tool}\" can't be shared (allowed: {string.Join(", ", SharedPresetRules.Tools.Select(t => t ?? Preset.CropTool).Distinct())})");
                    break;
                case not PresetKind.Tool when preset.Tool is not null:
                    result.Add("preset.tool_not_allowed", FileName, at + ".tool", "only a preset of the kind Tool has a tool");
                    break;
            }

            if (preset.IsGroup)
            {
                if (preset.TargetFormat != Preset.GroupTarget)
                    result.Add("preset.target_unknown", FileName, at + ".targetFormat", $"a group's target is \"{Preset.GroupTarget}\"");
            }
            else if (preset.IsCopyToFolder)
            {
                if (preset.TargetFormat != Preset.CopyTarget)
                    result.Add("preset.target_unknown", FileName, at + ".targetFormat", $"Copy to folder's target is \"{Preset.CopyTarget}\"");
            }
            else if (preset.TargetFormat != BuiltInData.SameAsSource
                     && (FormatRegistry.FindById(preset.TargetFormat) is null || preset.TargetFormat == Preset.CopyTarget))
            {
                result.Add("preset.target_unknown", FileName, at + ".targetFormat", $"\"{preset.TargetFormat}\" is not a format Filee writes");
            }

            for (var r = 0; r < preset.Output.Renames.Count; r++)
            {
                var step = preset.Output.Renames[r];
                if (step.Kind == RenameKind.Regex && OutputPathResolver.RenameError(step.Find) is { } why)
                    result.Add("output.rename_regex", FileName, $"{at}.output.renames[{r}].find", "is not a valid regular expression: " + FirstLine(why));
            }
        }

        var actions = bundle.Presets.Where(p => !p.IsGroup).Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        for (var i = 0; i < bundle.Presets.Count; i++)
        {
            var preset = bundle.Presets[i];
            if (!preset.IsGroup && preset.SubPresetIds.Count > 0)
                result.Add("preset.reference", FileName, $"presets[{i}].subPresetIds", "only a group fans out other presets");
            if (!preset.IsGroup)
                continue;
            foreach (var id in preset.SubPresetIds.Where(id => !actions.Contains(id)))
                result.Add("preset.reference", FileName, $"presets[{i}].subPresetIds", $"\"{id}\" is not a conversion or tool in this file");
        }

        for (var i = 0; i < bundle.Profiles.Count; i++)
        {
            var profile = bundle.Profiles[i];
            var at = $"profiles[{i}]";
            if (string.IsNullOrWhiteSpace(profile.Name) && string.IsNullOrEmpty(profile.NameKey))
                result.Add("profile.no_name", FileName, at + ".name", "needs a name");
            foreach (var id in profile.PresetIds.Concat(profile.BackPresetIds ?? []).Where(id => !ids.Contains(id)).Distinct())
                result.Add("profile.reference", FileName, at + ".presetIds", $"\"{id}\" is not a preset in this file");
            foreach (var extension in profile.Extensions.Where(e => FormatRegistry.FindByExtension(e) is null))
                result.Add("profile.extension_unknown", FileName, at + ".extensions", $"\"{extension}\" is not an extension Filee knows");
        }
    }

    /// <summary>What the bundle is, for meta.json (see <see cref="PresetCheckDerived"/>).</summary>
    private static PresetCheckDerived Derive(PresetBundle bundle, string sha256)
    {
        var kinds = new List<string>();
        if (bundle.Presets.Any(p => p.Kind is PresetKind.Convert or PresetKind.Group))
            kinds.Add("convert");
        if (bundle.Presets.Any(p => p.IsTool))
            kinds.Add("tool");
        if (bundle.Profiles.Count > 0)
            kinds.Add("toolbar");
        var from = bundle.Profiles.SelectMany(p => p.Extensions)
            .Select(SharedPresetRules.FormatOfExtension).OfType<string>()
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var to = bundle.Presets.Where(p => p.Kind == PresetKind.Convert && p.TargetFormat != BuiltInData.SameAsSource)
            .Select(p => p.TargetFormat).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        return new PresetCheckDerived(kinds, from, to, BaselineVersion, bundle.Presets.Count, bundle.Profiles.Count, sha256)
        {
            Warnings = SharedPresetWarnings.Of(bundle.Presets),
        };
    }

    private static string FirstLine(string text)
    {
        var end = text.IndexOfAny(['\r', '\n']);
        return end < 0 ? text : text[..end];
    }

    /// <summary>Walks JSON along an <see cref="ObjectRule"/>, reporting every value the rules don't allow.</summary>
    internal sealed class Walker(PresetCheckResult result, string file)
    {
        public void Check(JsonElement value, ValueRule rule, string path)
        {
            if (result.Full)
                return;
            if (rule is LeftOutRule leftOut)
            {
                if (!IsEmpty(value))
                    result.Add("field.left_out", file, path, $"must be empty: {leftOut.Why}");
                return;
            }
            if (value.ValueKind == JsonValueKind.Null)
            {
                if (!rule.Nullable)
                    result.Add("field.type", file, path, "may not be null");
                return;
            }
            switch (rule)
            {
                case ObjectRule obj:
                    if (value.ValueKind != JsonValueKind.Object)
                    {
                        result.Add("field.type", file, path, "must be an object");
                        return;
                    }
                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var property in value.EnumerateObject())
                    {
                        var at = path.Length == 0 ? property.Name : $"{path}.{property.Name}";
                        if (!seen.Add(property.Name))
                            result.Add("field.duplicate", file, at, "appears twice");
                        else if (!obj.Fields.TryGetValue(property.Name, out var field))
                            result.Add("field.unknown", file, at, "is not a field a shared preset may have");
                        else
                            Check(property.Value, field, at);
                    }
                    break;
                case BoolRule flag:
                    if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                        result.Add("field.type", file, path, "must be true or false");
                    else if (flag.Only is { } only && value.GetBoolean() != only)
                        result.Add("field.not_allowed", file, path, $"must be {(only ? "true" : "false")} in a shared preset");
                    break;
                case IntRule number:
                    if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var whole))
                        result.Add("field.type", file, path, "must be a whole number");
                    else if (whole < number.Min || whole > number.Max || number.Allowed?.Contains(whole) == false)
                        result.Add("field.range", file, path, number.Allowed is { } allowed
                            ? $"must be one of {string.Join(", ", allowed)}"
                            : $"must be from {number.Min} to {number.Max}");
                    break;
                case NumberRule real:
                    if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var d) || !double.IsFinite(d))
                        result.Add("field.type", file, path, "must be a number");
                    else if (d < real.Min || d > real.Max)
                        result.Add("field.range", file, path, FormattableString.Invariant($"must be from {real.Min} to {real.Max}"));
                    break;
                case EnumRule choice:
                    if (value.ValueKind != JsonValueKind.String)
                        result.Add("field.type", file, path, "must be a name, as a string");
                    else if (!choice.Names.Contains(value.GetString(), StringComparer.OrdinalIgnoreCase))
                        result.Add("field.value", file, path, $"\"{Shorten(value.GetString())}\" is not allowed (allowed: {string.Join(", ", choice.Names)})");
                    break;
                case StringRule text:
                    if (value.ValueKind != JsonValueKind.String)
                    {
                        result.Add("field.type", file, path, "must be a string");
                        break;
                    }
                    var s = value.GetString() ?? "";
                    if (s.Length > text.MaxLength)
                        result.Add("text.length", file, path, $"is {s.Length} characters long; at most {text.MaxLength}");
                    else if (SharedText.Common(s) is var (code, message))
                        result.Add(code, file, path, message);
                    else if (text.Check?.Invoke(s) is { } why)
                        result.Add("field.value", file, path, $"\"{Shorten(s)}\" {why}");
                    break;
                case MapRule map:
                    if (value.ValueKind != JsonValueKind.Object)
                    {
                        result.Add("field.type", file, path, "must be an object");
                        break;
                    }
                    var names = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var property in value.EnumerateObject())
                    {
                        var at = $"{path}.{property.Name}";
                        if (!names.Add(property.Name))
                            result.Add("field.duplicate", file, at, "appears twice");
                        else if (names.Count > map.MaxCount)
                        {
                            result.Add("field.range", file, path, $"has more than {map.MaxCount} fields");
                            break;
                        }
                        else if (map.KeyCheck(property.Name) is { } bad)
                            result.Add("field.value", file, at, $"\"{Shorten(property.Name)}\" {bad}");
                        else
                            Check(property.Value, map.ValueFor(property.Name), at);
                    }
                    break;
                case ListRule list:
                    if (value.ValueKind != JsonValueKind.Array)
                    {
                        result.Add("field.type", file, path, "must be a list");
                        break;
                    }
                    var count = value.GetArrayLength();
                    if (count > list.MaxCount)
                    {
                        result.Add("field.range", file, path, $"has {count} items; at most {list.MaxCount}");
                        break;
                    }
                    var index = 0;
                    foreach (var item in value.EnumerateArray())
                        Check(item, list.Item, $"{path}[{index++}]");
                    break;
            }
        }

        /// <summary>Null, "", false, 0, [] or {}: what an export writes for a field nobody set.</summary>
        private static bool IsEmpty(JsonElement value) => value.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.False or JsonValueKind.Undefined => true,
            JsonValueKind.String => value.GetString()!.Length == 0,
            JsonValueKind.Number => value.TryGetDouble(out var d) && d == 0,
            JsonValueKind.Array => value.GetArrayLength() == 0,
            JsonValueKind.Object => !value.EnumerateObject().Any(),
            _ => false,
        };

        private static string Shorten(string? text) =>
            text is null ? "" : text.Length <= 40 ? text : text[..40] + "…";
    }
}
