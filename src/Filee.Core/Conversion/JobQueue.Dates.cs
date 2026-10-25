// JobQueue: copies the source's creation and modification dates to the outputs (OutputRule.KeepDates).

using System.Collections.Concurrent;
using System.Threading.Channels;
using Filee.Core.Formats;
using Filee.Core.Presets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Filee.Core.Conversion;

public sealed partial class JobQueue
{
    /// <summary>The creation and modification dates of <paramref name="source"/>, or null when they can't be read.</summary>
    private static (DateTime Created, DateTime Modified)? DatesOf(string source)
    {
        try
        {
            return (File.GetCreationTimeUtc(source), File.GetLastWriteTimeUtc(source));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Gives the outputs the creation and modification dates of the source (<see cref="OutputRule.KeepDates"/>).</summary>
    private void CopyDates((DateTime Created, DateTime Modified) dates, IEnumerable<string> outputs)
    {
        try
        {
            var (created, modified) = dates;
            foreach (var output in outputs)
            {
                if (File.Exists(output))
                {
                    File.SetCreationTimeUtc(output, created);
                    File.SetLastWriteTimeUtc(output, modified);
                }
                else if (Directory.Exists(output))
                {
                    Directory.SetCreationTimeUtc(output, created);
                    Directory.SetLastWriteTimeUtc(output, modified);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The conversion itself worked: keep the files, only the dates are new.
            _log.LogWarning(ex, "Could not copy the source's dates to {Outputs}", string.Join(", ", outputs));
        }
    }
}
