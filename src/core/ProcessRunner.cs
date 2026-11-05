using System.Diagnostics;
using System.Text;

namespace CiciPDA;

public sealed record ProcResult(int ExitCode, string Stdout, string Stderr, bool TimedOut)
{
    public bool Ok => ExitCode == 0 && !TimedOut;
    public string Combined => (Stdout + "\n" + Stderr).Trim();
}

public static class ProcessRunner
{
    /// <summary>Runs a process with an explicit argument list (no shell, no string concatenation).</summary>
    public static async Task<ProcResult> RunAsync(string file, IEnumerable<string> args, TimeSpan? timeout = null,
        CancellationToken ct = default, string? stdin = null, IDictionary<string, string>? env = null, string? workDir = null)
    {
        var psi = new ProcessStartInfo(file)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = stdin != null,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        if (workDir != null) psi.WorkingDirectory = workDir;
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (env != null) foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;

        using var p = new Process { StartInfo = psi };
        var so = new StringBuilder(); var se = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (so) so.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (se) se.AppendLine(e.Data); };
        try { p.Start(); }
        catch (Exception ex) { return new ProcResult(-1, "", ex.Message, false); }
        p.BeginOutputReadLine(); p.BeginErrorReadLine();
        if (stdin != null) { try { await p.StandardInput.WriteAsync(stdin); p.StandardInput.Close(); } catch { } }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(30));
        bool timedOut = false;
        try { await p.WaitForExitAsync(cts.Token); p.WaitForExit(); }
        catch (OperationCanceledException)
        {
            timedOut = !ct.IsCancellationRequested;
            try { p.Kill(true); } catch { }
            if (ct.IsCancellationRequested) throw;
        }
        return new ProcResult(timedOut ? -1 : p.ExitCode, so.ToString(), se.ToString(), timedOut);
    }
}

/// <summary>A long-running child process whose output is surfaced line by line and which is killed with its tree.</summary>
public sealed class ManagedProcess : IDisposable
{
    readonly Process _p;
    public event Action<string>? OutLine;
    public event Action<string>? ErrLine;
    public int Id => _p.Id;
    public bool HasExited { get { try { return _p.HasExited; } catch { return true; } } }
    public int ExitCode => _p.ExitCode;

    public ManagedProcess(string file, IEnumerable<string> args, bool redirectStdin = false, string? workDir = null)
    {
        var psi = new ProcessStartInfo(file)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = redirectStdin,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        if (workDir != null) psi.WorkingDirectory = workDir;
        foreach (var a in args) psi.ArgumentList.Add(a);
        _p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _p.OutputDataReceived += (_, e) => { if (e.Data != null) OutLine?.Invoke(e.Data); };
        _p.ErrorDataReceived += (_, e) => { if (e.Data != null) ErrLine?.Invoke(e.Data); };
    }

    public void Start() { _p.Start(); _p.BeginOutputReadLine(); _p.BeginErrorReadLine(); }
    public async Task WriteLineAsync(string s) { await _p.StandardInput.WriteLineAsync(s); await _p.StandardInput.FlushAsync(); }
    public Task WaitExitAsync(CancellationToken ct = default) => _p.WaitForExitAsync(ct);
    public void Kill() { try { if (!_p.HasExited) _p.Kill(true); } catch { } }
    public void Dispose() { Kill(); _p.Dispose(); }
}
