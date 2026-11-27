using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Xml.Linq;

namespace PixelWorkshop;

internal static class WindowsSchedule
{
    internal static void Register(Worker worker, bool update = false)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User!.Value;
        var exe = Environment.ProcessPath!;
        if (string.Equals(System.IO.Path.GetFileNameWithoutExtension(exe), "dotnet", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("PixelWorkshop.exe를 직접 실행해 주세요.");

        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        XElement Daily(TimeOnly time) => new(ns + "CalendarTrigger",
            new XElement(ns + "StartBoundary",
                DateTime.Today.Add(time.ToTimeSpan()).ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture)),
            new XElement(ns + "ScheduleByDay", new XElement(ns + "DaysInterval", 1)));

        var task = new XElement(ns + "Task", new XAttribute("version", "1.2"),
            new XElement(ns + "RegistrationInfo", new XElement(ns + "Description", $"픽셀작업소 · {worker.Name}")),
            new XElement(ns + "Triggers", Daily(worker.CleanAt)),
            new XElement(ns + "Principals", new XElement(ns + "Principal", new XAttribute("id", "Worker"),
                new XElement(ns + "UserId", user),
                new XElement(ns + "LogonType", "InteractiveToken"),
                new XElement(ns + "RunLevel", "LeastPrivilege"))),
            new XElement(ns + "Settings",
                new XElement(ns + "MultipleInstancesPolicy", "Queue"),
                new XElement(ns + "DisallowStartIfOnBatteries", false),
                new XElement(ns + "StopIfGoingOnBatteries", false),
                new XElement(ns + "StartWhenAvailable", false),
                new XElement(ns + "ExecutionTimeLimit", "PT2M")),
            new XElement(ns + "Actions", new XAttribute("Context", "Worker"),
                new XElement(ns + "Exec", new XElement(ns + "Command", exe),
                    new XElement(ns + "Arguments", $"--run {worker.Id:N}"))));

        WithRoot(root =>
        {
            // TASK_CREATE or TASK_CREATE_OR_UPDATE, with TASK_LOGON_INTERACTIVE_TOKEN.
            object registered = root.RegisterTask(Name(worker.Id), task.ToString(), update ? 6 : 2, user, null, 3);
            Marshal.ReleaseComObject(registered);
        });
    }

    internal static void Remove(Guid id) => WithRoot(root =>
    {
        try { root.DeleteTask(Name(id), 0); }
        catch (COMException ex) when (ex.HResult == unchecked((int)0x80070002))
        {
            // The user may have already removed this task in Task Scheduler.
        }
    });

    private static string Name(Guid id) => $"PixelWorkshop-{id:N}";

    private static void WithRoot(Action<dynamic> action)
    {
        dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", true)!)!;
        try
        {
            service.Connect();
            dynamic root = service.GetFolder(@"\");
            try { action(root); }
            finally { Marshal.ReleaseComObject(root); }
        }
        finally
        {
            Marshal.ReleaseComObject(service);
        }
    }
}
