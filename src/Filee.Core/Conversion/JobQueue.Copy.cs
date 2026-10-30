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
                var result = await FolderCopy.CopyAsync(file.SourcePath, job.Preset.Output, job.CancellationToken);
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
