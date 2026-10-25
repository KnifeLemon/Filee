// JobQueue: saves the sources of a job first when they come from a web browser (ISourceFetcher).

using System.Collections.Concurrent;
using System.Threading.Channels;
using Filee.Core.Formats;
using Filee.Core.Presets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Filee.Core.Conversion;

public sealed partial class JobQueue
{
    /// <summary>Share of a fetched file's progress that saving it takes; converting it fills the rest.</summary>
    private const double FetchShare = 0.5;

    /// <summary>
    /// Saves the sources the job's fetcher brings (files dragged from a browser) before anything is converted. A file
    /// that can't be saved fails with its reason; the others still convert.
    /// </summary>
    private async Task FetchSourcesAsync(ConversionJob job, ISourceFetcher fetcher)
    {
        var files = job.Files.Where(f => f.NeedsFetch).ToList();
        await Parallel.ForEachAsync(files, new ParallelOptions { MaxDegreeOfParallelism = 4 }, async (file, _) =>
        {
            file.State = FileState.Running;
            file.Transfer = new TransferProgress(0, null);
            Notify(job);
            var progress = new SyncProgress<TransferProgress>(p =>
            {
                if (file.State != FileState.Running || file.Saved)
                    return;
                file.Transfer = p;
                var value = (p.Fraction ?? 0) * FetchShare;
                if (value > file.Progress)
                    file.Progress = value;
                Notify(job);
            });
            try
            {
                file.SourcePath = await fetcher.FetchAsync(file.SourcePath, progress, job.CancellationToken);
                file.Saved = true;
                file.State = FileState.Pending;
                file.Progress = FetchShare;
            }
            catch (OperationCanceledException)
            {
                file.State = FileState.Cancelled;
            }
            catch (ConversionRefusedException ex)
            {
                _log.LogInformation("Saving {File} was refused: {Reason}", file.SourcePath, ex.Message);
                FailFetch(file, ex.ErrorKey, ex.Detail);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Saving {File} failed", file.SourcePath);
                FailFetch(file, "error.web_download_failed", ex.Message);
            }
            Notify(job);
        });

        static void FailFetch(FileResult file, string key, string? detail)
        {
            file.State = FileState.Failed;
            file.ErrorKey = key;
            file.ErrorDetail = detail;
            file.Progress = 1;
        }
    }

    /// <summary>
    /// Reports on the reporting thread, in order: Progress&lt;T&gt; posts to the thread pool, where a late byte count could
    /// land after the file was saved.
    /// </summary>
    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
