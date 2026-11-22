using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Inkswap;

/// <summary>
/// The local AI: downloads llama.cpp (Vulkan build, any GPU) and a Qwen3-VL vision model once, checks
/// their SHA-256, then runs llama-server on 127.0.0.1 only. The page talks to it through /api/chat.
/// </summary>
static class Engine
{
    public record Asset(string Url, string File, long Size, string Sha256);
    public record Model(string Label, Asset Weights, Asset Vision);

    const string LlamaBuild = "b12387";
    static readonly Asset Llama = new(
        $"https://github.com/ggml-org/llama.cpp/releases/download/{LlamaBuild}/llama-{LlamaBuild}-bin-win-vulkan-x64.zip",
        $"llama-{LlamaBuild}-bin-win-vulkan-x64.zip", 38119681, "68462280e0801af67a5a02617cf155a0fa8018fd5f805b2285157b771c10da12");

    static Asset Hf(string repo, string file, long size, string sha) =>
        new($"https://huggingface.co/Qwen/{repo}/resolve/main/{file}", file, size, sha);

    public static readonly Dictionary<string, Model> Models = new()
    {
        ["4b"] = new("Standard: Qwen3-VL 4B, 3.4 GB download, needs about 5 GB of graphics memory",
            Hf("Qwen3-VL-4B-Instruct-GGUF", "Qwen3VL-4B-Instruct-Q4_K_M.gguf", 2497281664, "66358cb18bb6b3b1b6675aa412c7a88ef01d228f481184d13668e5201c730a0a"),
            Hf("Qwen3-VL-4B-Instruct-GGUF", "mmproj-Qwen3VL-4B-Instruct-F16.gguf", 836180256, "256f3a43bd4205ffef48d6b92715e1e70b5b0e9aef06522584967513a9985331")),
        ["8b"] = new("Best: Qwen3-VL 8B, 5.8 GB download, fastest with 8 GB of graphics memory (runs partly on the CPU with less)",
            Hf("Qwen3-VL-8B-Instruct-GGUF", "Qwen3VL-8B-Instruct-Q4_K_M.gguf", 5027784800, "67d1659bfe71b89d50b45a4ad1a9e5b997e5bb16ce5da66a6a6167abd569e9e2"),
            // the 8-bit vision file leaves ~400 MB more graphics memory for the model itself on 8 GB cards
            Hf("Qwen3-VL-8B-Instruct-GGUF", "mmproj-Qwen3VL-8B-Instruct-Q8_0.gguf", 752289728, "c6ba85508d82f42590e6eb77d5340369ab6fecf107a7561d809523d8aa5f3bfd")),
    };

    // read by /api/status from the UI thread while the worker writes them; plain fields are fine for a progress display
    public static volatile string State = "stopped";   // stopped | downloading | starting | ready | error
    public static volatile string Message = "";
    public static long Done, Total;
    public static volatile string? Running;

    static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };
    static readonly object Gate = new();
    static readonly Queue<string> Tail = new();
    static Process? _server;
    static int _port;

    static string ModelsDir => Path.Combine(PortablePaths.DataDir, "models");
    static string LlamaDir => Path.Combine(PortablePaths.DataDir, "llama", LlamaBuild);
    static string ServerLog => Path.Combine(PortablePaths.DataDir, "llama.log");
    static string? ServerExe => Directory.Exists(LlamaDir)
        ? Directory.EnumerateFiles(LlamaDir, "llama-server.exe", SearchOption.AllDirectories).FirstOrDefault() : null;

    static bool Have(Asset a) => new FileInfo(Path.Combine(ModelsDir, a.File)) is { Exists: true } f && f.Length == a.Size;

    public static object Status() => new
    {
        state = State, message = Message, done = Interlocked.Read(ref Done), total = Interlocked.Read(ref Total), running = Running,
        models = Models.Select(m => new { key = m.Key, label = m.Value.Label, installed = Have(m.Value.Weights) && Have(m.Value.Vision) }),
    };

    /// <summary>Downloads whatever is missing for this model, then (re)starts the server with it. Returns at once.</summary>
    public static void Start(string key)
    {
        if (!Models.TryGetValue(key, out var model)) throw new ArgumentException("Unknown model: " + key);
        lock (Gate)
        {
            if (State is "downloading" or "starting" || (State == "ready" && Running == key)) return;
            State = "starting";
            Message = "";
        }
        _ = Task.Run(async () =>
        {
            try
            {
                Stop();
                if (ServerExe is null)
                {
                    var zip = Path.Combine(PortablePaths.DataDir, "llama", Llama.File);
                    await Fetch(Llama, zip);
                    if (Directory.Exists(LlamaDir)) Directory.Delete(LlamaDir, true);
                    ZipFile.ExtractToDirectory(zip, LlamaDir);
                    File.Delete(zip);
                    if (ServerExe is null) throw new IOException("llama-server.exe is missing from the llama.cpp download.");
                }
                await Fetch(model.Weights, Path.Combine(ModelsDir, model.Weights.File));
                await Fetch(model.Vision, Path.Combine(ModelsDir, model.Vision.File));
                // model files an older version downloaded but no model uses any more (e.g. the 8B's old F16 vision file)
                var used = Models.Values.SelectMany(v => new[] { v.Weights.File, v.Vision.File }).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var f in Directory.GetFiles(ModelsDir, "*.gguf").Where(f => !used.Contains(Path.GetFileName(f)))) File.Delete(f);
                State = "starting";
                Message = "Loading the model onto the graphics card...";
                Launch(model);
                await WaitReady();
                Running = key;
                Message = "";
                State = "ready";
            }
            catch (Exception ex)
            {
                PortablePaths.Log("engine: " + ex);
                Stop();
                Message = ex.Message;
                State = "error";
            }
        });
    }

    /// <summary>Resumable download that refuses a file whose SHA-256 doesn't match.</summary>
    static async Task Fetch(Asset a, string path)
    {
        if (File.Exists(path) && new FileInfo(path).Length == a.Size) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var part = path + ".part";
        State = "downloading";
        Message = "Downloading " + a.File;
        Interlocked.Exchange(ref Total, a.Size);

        var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buf = new byte[1 << 20];
        long have = File.Exists(part) ? new FileInfo(part).Length : 0;
        if (have >= a.Size) { File.Delete(part); have = 0; }
        if (have > 0)
            await using (var old = File.OpenRead(part))
                for (int n; (n = await old.ReadAsync(buf)) > 0;) hash.AppendData(buf, 0, n);

        using var req = new HttpRequestMessage(HttpMethod.Get, a.Url);
        if (have > 0) req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(have, null);
        using var res = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        res.EnsureSuccessStatusCode();
        var resumed = have > 0 && res.StatusCode == HttpStatusCode.PartialContent;
        if (!resumed)
        {
            hash.Dispose();
            hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            have = 0;
        }
        Interlocked.Exchange(ref Done, have);
        await using (var src = await res.Content.ReadAsStreamAsync())
        await using (var dst = new FileStream(part, resumed ? FileMode.Append : FileMode.Create))
            for (int n; (n = await src.ReadAsync(buf)) > 0;)
            {
                await dst.WriteAsync(buf.AsMemory(0, n));
                hash.AppendData(buf, 0, n);
                Interlocked.Add(ref Done, n);
            }
        var got = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        hash.Dispose();
        if (got != a.Sha256)
        {
            File.Delete(part);
            throw new IOException($"{a.File} didn't download correctly (checksum mismatch). Press Start to try again.");
        }
        File.Move(part, path, true);
    }

    static void Launch(Model m)
    {
        using (var l = new TcpListener(IPAddress.Loopback, 0)) { l.Start(); _port = ((IPEndPoint)l.LocalEndpoint).Port; }
        var exe = ServerExe!;
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(exe)!,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var arg in new[]
                 {
                     "-m", Path.Combine(ModelsDir, m.Weights.File), "--mmproj", Path.Combine(ModelsDir, m.Vision.File),
                     // no -ngl: llama.cpp then fits as many layers as free graphics memory allows and runs the rest on the CPU.
                     // One request at a time and 8k tokens (a page is about 2k) keep the cache small.
                     "--host", "127.0.0.1", "--port", _port.ToString(), "-c", "8192", "-np", "1", "--no-ui", "--image-min-tokens", "1024",
                     "--device", PickDevice(exe),
                 })
            psi.ArgumentList.Add(arg);
        File.WriteAllText(ServerLog, "");
        lock (Tail) Tail.Clear();
        _server = Process.Start(psi) ?? throw new IOException("llama-server didn't start.");
        Job.Add(_server);
        NoEcoMode(_server);
        DataReceivedEventHandler log = (_, e) =>
        {
            if (e.Data is null) return;
            lock (Tail)
            {
                Tail.Enqueue(e.Data);
                if (Tail.Count > 8) Tail.Dequeue();
                try { File.AppendAllText(ServerLog, e.Data + Environment.NewLine); } catch { }
            }
        };
        _server.OutputDataReceived += log;
        _server.ErrorDataReceived += log;
        _server.BeginOutputReadLine();
        _server.BeginErrorReadLine();
    }

    /// <summary>
    /// llama.cpp splits the model over every GPU it sees, so on a laptop the slow built-in graphics drag the
    /// real card down. Use one device: the first that isn't integrated graphics.
    /// </summary>
    // shortcut: integrated graphics are spotted by name, not by Vulkan device type; fine for Intel/AMD/Microsoft names
    static string PickDevice(string exe)
    {
        var psi = new ProcessStartInfo(exe, "--list-devices")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        };
        using var p = Process.Start(psi)!;
        var stderr = p.StandardError.ReadToEndAsync();
        var lines = (p.StandardOutput.ReadToEnd() + stderr.Result).Split('\n')
            .Select(l => l.Trim()).Where(l => l.StartsWith("Vulkan", StringComparison.Ordinal) && l.Contains(':')).ToList();
        p.WaitForExit(10000);
        var integrated = new[] { "Intel", "Radeon(TM) Graphics", "Radeon Graphics", "Microsoft", "llvmpipe" };
        var pick = lines.FirstOrDefault(l => !integrated.Any(n => l.Contains(n, StringComparison.OrdinalIgnoreCase))) ?? lines.FirstOrDefault();
        PortablePaths.Log("gpu: " + (pick ?? "none found") + " (of " + lines.Count + ")");
        return pick is null ? "none" : pick[..pick.IndexOf(':')];
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PowerThrottling { public uint Version, ControlMask, StateMask; }
    [DllImport("kernel32.dll")] static extern bool SetProcessInformation(IntPtr process, int cls, ref PowerThrottling info, int size);

    /// <summary>
    /// Windows slows windowless processes of a background app to save power (EcoQoS), which made reading
    /// about ten times slower. The AI is work the user asked for, so opt it out.
    /// </summary>
    static void NoEcoMode(Process p)
    {
        var info = new PowerThrottling { Version = 1, ControlMask = 1 /* EXECUTION_SPEED */, StateMask = 0 };
        if (!SetProcessInformation(p.Handle, 4 /* ProcessPowerThrottling */, ref info, Marshal.SizeOf<PowerThrottling>()))
            PortablePaths.Log("power throttling opt-out failed: " + Marshal.GetLastWin32Error());
    }

    static async Task WaitReady()
    {
        for (var t = 0; t < 600; t++)
        {
            if (_server is null || _server.HasExited)
            {
                string tail;
                lock (Tail) tail = string.Join("\n", Tail);
                throw new IOException(tail.Contains("OutOfDeviceMemory")
                    ? "Not enough free graphics memory for this model. Close apps that use the graphics card (games, video, browsers) or use the Standard model."
                    : "The AI server stopped while loading:\n" + tail);
            }
            try
            {
                using var res = await Http.GetAsync($"http://127.0.0.1:{_port}/health");
                if (res.IsSuccessStatusCode) return;
            }
            catch (HttpRequestException) { }
            await Task.Delay(500);
        }
        throw new IOException("The AI server took more than 5 minutes to load.");
    }

    /// <summary>Forwards an OpenAI-style chat request to llama-server and returns its status and JSON.</summary>
    public static async Task<(int Code, string Json)> Chat(string body)
    {
        if (State != "ready") return (503, "{\"error\":\"The AI model isn't running yet. Open AI setup to start it.\"}");
        using var res = await Http.PostAsync($"http://127.0.0.1:{_port}/v1/chat/completions",
            new StringContent(body, Encoding.UTF8, "application/json"));
        return ((int)res.StatusCode, await res.Content.ReadAsStringAsync());
    }

    public static void Stop()
    {
        var p = _server;
        _server = null;
        Running = null;
        if (State == "ready") State = "stopped";
        try
        {
            if (p is { HasExited: false }) { p.Kill(true); p.WaitForExit(5000); }
        }
        catch (Exception ex) { PortablePaths.Log("stop server: " + ex.Message); }
    }

    /// <summary>Puts llama-server in a job that Windows closes with this app, so a crash never leaves it holding the GPU.</summary>
    static class Job
    {
        [StructLayout(LayoutKind.Sequential)]
        struct Basic
        {
            public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass, SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct Extended
        {
            public Basic BasicLimits;
            public ulong IoReadOps, IoWriteOps, IoOtherOps, IoReadBytes, IoWriteBytes, IoOtherBytes;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateJobObject(IntPtr attrs, string? name);
        [DllImport("kernel32.dll")] static extern bool SetInformationJobObject(IntPtr job, int cls, ref Extended info, int size);
        [DllImport("kernel32.dll")] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        static readonly IntPtr Handle = Create();

        static IntPtr Create()
        {
            var job = CreateJobObject(IntPtr.Zero, null);
            var info = new Extended { BasicLimits = { LimitFlags = 0x2000 /* KILL_ON_JOB_CLOSE */ } };
            SetInformationJobObject(job, 9 /* ExtendedLimitInformation */, ref info, Marshal.SizeOf<Extended>());
            return job;
        }

        public static void Add(Process p)
        {
            if (!AssignProcessToJobObject(Handle, p.Handle)) PortablePaths.Log("job assign failed: " + Marshal.GetLastWin32Error());
        }
    }
}
