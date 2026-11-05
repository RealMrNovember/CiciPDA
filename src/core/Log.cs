using System.Collections.Concurrent;
using System.Text;

namespace CiciPDA;

public enum Severity { Debug, Info, Success, Warning, Error }

public sealed record ActivityEvent(DateTimeOffset Time, Severity Severity, string Source, string Message);

/// <summary>Structured application log and in-memory activity feed. Registered secrets are masked everywhere.</summary>
public static class Log
{
    const int MaxEvents = 1000;
    static readonly object Gate = new();
    static readonly List<ActivityEvent> Events = new();
    static readonly ConcurrentDictionary<string, byte> Secrets = new();
    static string? _file;

    public static Severity MinFileLevel { get; set; } = Severity.Info;
    public static event Action<ActivityEvent>? Written;

    public static void Init(string logFile)
    {
        _file = logFile;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(logFile)!);
            var fi = new FileInfo(logFile);
            if (fi.Exists && fi.Length > 2_000_000) File.Move(logFile, logFile + ".old", true);
        }
        catch { /* logging must never break the app */ }
    }

    public static void RegisterSecret(string? s) { if (!string.IsNullOrEmpty(s) && s.Length >= 4) Secrets[s] = 0; }

    public static string Redact(string m)
    {
        foreach (var s in Secrets.Keys) m = m.Replace(s, "***");
        return m;
    }

    public static IReadOnlyList<ActivityEvent> Snapshot() { lock (Gate) return Events.ToList(); }

    public static void Write(Severity sev, string source, string message)
    {
        var ev = new ActivityEvent(DateTimeOffset.Now, sev, source, Redact(message));
        lock (Gate)
        {
            Events.Add(ev);
            if (Events.Count > MaxEvents) Events.RemoveRange(0, Events.Count - MaxEvents);
            if (_file != null && sev >= MinFileLevel)
            {
                try { File.AppendAllText(_file, $"{ev.Time:yyyy-MM-dd HH:mm:ss.fff} [{sev}] {source}: {ev.Message}{Environment.NewLine}", Encoding.UTF8); }
                catch { }
            }
        }
        Written?.Invoke(ev);
    }

    public static void Debug(string src, string m) => Write(Severity.Debug, src, m);
    public static void Info(string src, string m) => Write(Severity.Info, src, m);
    public static void Ok(string src, string m) => Write(Severity.Success, src, m);
    public static void Warn(string src, string m) => Write(Severity.Warning, src, m);
    public static void Error(string src, string m) => Write(Severity.Error, src, m);
    public static void Error(string src, Exception ex, string? context = null) =>
        Write(Severity.Error, src, (context != null ? context + ": " : "") + ex.GetType().Name + ": " + ex.Message);
}
