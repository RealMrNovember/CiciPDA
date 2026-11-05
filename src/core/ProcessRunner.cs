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

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)] static extern bool AttachConsole(uint pid);
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)] static extern bool FreeConsole();
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)] static extern bool SetConsoleCtrlHandler(IntPtr handler, bool add);
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)] static extern bool GenerateConsoleCtrlEvent(uint ctrlEvent, uint processGroupId);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern IntPtr GetConsoleWindow();
    static readonly object ConsoleGate = new();

    /// <summary>
    /// Asks a console child process to shut down by sending it Ctrl+Break (Go programs such as tun2socks treat it as an interrupt), so it can close its Wintun adapter
    /// itself. A hard kill leaves the adapter behind for minutes and blocks the next connection. Falls back to Kill after the timeout.
    /// Returns true if the process exited on its own.
    /// </summary>
    public async Task<bool> StopGracefullyAsync(int timeoutMs = 5000)
    {
        if (HasExited) return true;
        // If this process has a console (e.g. started from a terminal) the child may share it and a console control event would reach the
        // terminal and its other programs. Only do the graceful stop from a console-less GUI process, which gets the child its own hidden console.
        if (GetConsoleWindow() != IntPtr.Zero) { Kill(); return false; }
        bool sent = false;
        lock (ConsoleGate)                                   // the console attach/detach dance is process-wide
        {
            try
            {
                FreeConsole();                               // a GUI app normally has none; detach if it does
                if (AttachConsole((uint)_p.Id))
                {
                    SetConsoleCtrlHandler(IntPtr.Zero, true);   // our own process must ignore the event it is about to send
                    // CTRL_BREAK_EVENT (1), not CTRL_C: a parent that ignores Ctrl+C makes children inherit "ignore", but Ctrl+Break cannot be disabled that way.
                    // Go programs such as tun2socks map both to SIGINT and shut down cleanly.
                    sent = GenerateConsoleCtrlEvent(1 /* CTRL_BREAK_EVENT */, 0);
                }
            }
            catch { sent = false; }
        }
        bool exited = false;
        if (sent)
            try { using var cts = new CancellationTokenSource(timeoutMs); await _p.WaitForExitAsync(cts.Token); exited = true; } catch (OperationCanceledException) { }
        lock (ConsoleGate) { try { SetConsoleCtrlHandler(IntPtr.Zero, false); FreeConsole(); } catch { } }
        if (!exited) { Kill(); return false; }
        return true;
    }
    public void Dispose() { Kill(); _p.Dispose(); }
}
