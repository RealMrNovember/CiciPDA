namespace CiciPDA;

public static class AppPaths
{
    static string? _data;
    /// <summary>Test hook: pretend the application is installed somewhere else.</summary>
    public static string? BaseDirOverride { get; set; }
    public static string BaseDir => BaseDirOverride ?? AppContext.BaseDirectory;

    public static string DataDir
    {
        get
        {
            if (_data != null) return _data;
            var env = Environment.GetEnvironmentVariable("CICIPDA_DATA");
            var candidates = new List<string>();
            if (!string.IsNullOrWhiteSpace(env)) candidates.Add(env);
            // Installed under Program Files (read-only for users), so settings, logs and history live per user.
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CiciByte", "CiciPDA"));
            foreach (var c in candidates)
            {
                try
                {
                    Directory.CreateDirectory(c);
                    var probe = Path.Combine(c, ".w");
                    File.WriteAllText(probe, "1"); File.Delete(probe);
                    return _data = c;
                }
                catch { }
            }
            return _data = Path.GetTempPath();
        }
        set { _data = value; try { Directory.CreateDirectory(value); } catch { } }
    }

    public static string ToolsDir
    {
        get
        {
            var d = Path.Combine(BaseDir, "tools");
            return Directory.Exists(d) ? d : BaseDir;
        }
    }
    public static string Tun2Socks => Path.Combine(ToolsDir, "tun2socks.exe");
    public static string WintunDll => Path.Combine(ToolsDir, "wintun.dll");
    public static string AgentDex => Path.Combine(BaseDir, "agent.dex");
    public static string DataFile(string name) => Path.Combine(DataDir, name);
}
