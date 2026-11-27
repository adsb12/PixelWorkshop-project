using System.Globalization;
using System.IO;
using System.Text.Json;

namespace PixelWorkshop;

internal sealed record Worker(Guid Id, string Name, string Parent, TimeOnly CreateAt, TimeOnly CleanAt);
internal sealed record JobState(string? FolderDate, FolderIdentity? Identity, DateTimeOffset Updated, string Message);

internal static class Jobs
{
    internal const int MaxWorkers = 5;
    private static readonly string Data = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PixelWorkshop");
    private static readonly string Settings = Path.Combine(Data, "workers.json");

    internal static List<Worker> Workers() => Read<List<Worker>>(Settings) ?? [];
    internal static JobState? Status(Guid id) => Read<JobState>(StatePath(id));

    internal static void Add(Worker worker) => Locked(() =>
    {
        var workers = Workers();
        if (workers.Count >= MaxWorkers)
            throw new InvalidOperationException("일꾼은 최대 5마리까지 만들 수 있어요.");
        if (workers.Any(item => StringComparer.OrdinalIgnoreCase.Equals(item.Parent, worker.Parent)))
            throw new InvalidOperationException("이 경로에는 이미 일꾼이 배정되어 있어요.");

        workers.Add(worker);
        Write(Settings, workers);
        try
        {
            WindowsSchedule.Register(worker);
        }
        catch
        {
            workers.Remove(worker);
            Write(Settings, workers);
            throw;
        }
        return true;
    });

    internal static void Remove(Guid id) => Locked(() =>
    {
        var workers = Workers();
        WindowsSchedule.Remove(id);
        workers.RemoveAll(worker => worker.Id == id);
        Write(Settings, workers);
        File.Delete(StatePath(id));
        return true;
    });

    internal static int Run(Guid id)
    {
        try
        {
            return Locked(() =>
            {
                var worker = Workers().Find(item => item.Id == id);
                if (worker is null)
                    return 0;

                var now = DateTime.Now;
                var time = TimeOnly.FromDateTime(now);
                if (time < worker.CreateAt)
                    return 0;

                var date = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                var path = Path.Combine(worker.Parent, date);
                var previous = Status(id);
                var identity = previous?.FolderDate == date ? previous.Identity : null;
                string message;
                var result = 0;
                try
                {
                    if (time < worker.CleanAt)
                    {
                        if (FolderOperations.TryCreate(path) is { } created)
                        {
                            identity = created;
                            message = "오늘 폴더를 만들었어요!";
                        }
                        else
                        {
                            message = "오늘 폴더가 이미 있어요.";
                        }
                    }
                    else if (identity is null)
                    {
                        message = "오늘 제가 만든 폴더가 없어요.";
                    }
                    else
                    {
                        message = FolderOperations.Clean(path, identity.Value);
                        identity = null;
                    }
                }
                catch (Exception ex)
                {
                    message = $"작업 실패 · {ex.Message}";
                    result = 1;
                }
                Write(StatePath(id), new JobState(date, identity, DateTimeOffset.Now, message));
                return result;
            });
        }
        catch
        {
            // Task Scheduler records the nonzero exit code if the status cannot be saved.
            return 1;
        }
    }

    private static string StatePath(Guid id) => Path.Combine(Data, $"{id:N}.json");

    private static T? Read<T>(string path) where T : class
    {
        try
        {
            using var file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            return JsonSerializer.Deserialize<T>(file);
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Data);
        var temp = Path.Combine(Data, $"{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(value));
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    private static T Locked<T>(Func<T> action)
    {
        using var mutex = new Mutex(false, @"Local\PixelWorkshop-Jobs");
        try
        {
            if (!mutex.WaitOne(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("일꾼이 작업 중이에요. 잠시 뒤 다시 시도해 주세요.");
        }
        catch (AbandonedMutexException)
        {
            // A terminated owner released the mutex.
        }

        try { return action(); }
        finally { mutex.ReleaseMutex(); }
    }
}
