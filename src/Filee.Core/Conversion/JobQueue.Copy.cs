using Filee.Core.Presets;

namespace Filee.Core.Conversion;

public sealed partial class JobQueue
{
    private async Task RunCopiesAsync(ConversionJob job)
    {
        foreach (var file in job.Files.Where(f => !Stopped(f)))
        {
            file.State = FileState.Running;
            Notify(job);
            try
            {
                string? result;
                // A file from a browser has no original to remove: it is saved as a copying slice saves it.
                if (job.Preset.Output.MoveInsteadOfCopy && !file.NeedsFetch)
                {
                    (result, var kept) = await FolderCopy.MoveAsync(file.SourcePath, job.Preset.Output, job.CancellationToken);
                    if (kept is not null)
                    {
                        file.NoticeKey = "notice.move_original_kept";
                        file.NoticeDetail = kept;
                    }
                }
                else
                    result = await FolderCopy.CopyAsync(file.SourcePath, job.Preset.Output, job.CancellationToken);
                if (result is not null)
                    file.Outputs.Add(result);
                file.Progress = 1;
                file.State = result is null ? FileState.Skipped : FileState.Done;
            }
            catch (OperationCanceledException) { file.State = FileState.Cancelled; }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                file.State = FileState.Failed;
                file.ErrorKey = "error.conversion_failed";
                file.ErrorDetail = ex.Message;
            }
            Notify(job);
        }
    }
}
