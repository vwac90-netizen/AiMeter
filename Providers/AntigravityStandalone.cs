using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace AiMeter.Providers;

// [Part 252] Antigravity 앱이 꺼져 있을 때, 함께 설치된 Antigravity(Google.Antigravity) 제품의 엔진을 화면 없이 잠깐 띄워 사용량을 묻는다.
// - 2026-10-07 실측: %LOCALAPPDATA%\Programs\antigravity\resources\bin\language_server.exe 를 --standalone 으로 띄우면
//   디스크의 로그인(~/.gemini/antigravity)으로 스스로 인증해 4초 만에 RetrieveUserQuotaSummary 200.
//   (Antigravity IDE 의 엔진은 같은 방식으로 띄우면 CREDENTIALS_MISSING 후 자체 종료 — 그쪽은 앱이 토큰을 넘겨준다)
// - 인자 구성은 AgentGauge 의 공개 설명(AGENTS.md)을 참고했고, 이 PC 에서 다시 재서 확인했다. 코드는 새로 작성.
// - AiMeter 는 로그인 정보를 읽거나 쓰지 않는다 — 엔진이 스스로 인증한다. csrf 토큰은 매번 새로 만든다.
// - 엔진과 그 보조 프로세스는 Job Object(KILL_ON_JOB_CLOSE)에 넣는다 → 읽기가 끝나거나 AiMeter 가 갑자기 죽어도 남지 않는다.
internal static class AntigravityStandalone
{
    public static string EnginePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs", "antigravity", "resources", "bin", "language_server.exe");

    public static bool Available => File.Exists(EnginePath);

    /// <summary>엔진을 띄우고, 응답할 때까지 기다렸다가 <paramref name="read"/> 를 부르고, 엔진을 끝낸다.</summary>
    public static async Task<T?> RunAsync<T>(Func<Uri, string, Task<T?>> read, CancellationToken ct) where T : class
    {
        string token = Guid.NewGuid().ToString();
        int port = FreeLoopbackPort();
        var args = new[]
        {
            "--standalone", "--override_ide_name", "antigravity", "--subclient_type", "hub",
            "--http_server_port", port.ToString(), "--https_server_port", "0",
            "--csrf_token", token, "--app_data_dir", "antigravity",
            "--api_server_url", "https://generativelanguage.googleapis.com",
            "--cloud_code_endpoint", "https://daily-cloudcode-pa.googleapis.com",
            "--enable_sidecars",
        };
        var psi = new ProcessStartInfo(EnginePath) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var job = JobObject.Create();
        Process? proc = null;
        try
        {
            proc = Process.Start(psi);
            if (proc is null) return null;
            job?.Assign(proc.Handle);

            var baseUri = new Uri($"http://127.0.0.1:{port}");
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);
            while (DateTimeOffset.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                await Task.Delay(1000, ct).ConfigureAwait(false);
                if (proc.HasExited) return null; // 로그인 정보가 없으면 엔진이 스스로 끝난다
                var result = await read(baseUri, token).ConfigureAwait(false);
                if (result is not null) return result;
            }
            return null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or TaskCanceledException)
        {
            return null;
        }
        finally
        {
            try
            {
                if (proc is { HasExited: false }) proc.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // 이미 끝났으면 그만 — 남은 보조 프로세스는 job 이 닫히며 정리된다
            }
            proc?.Dispose();
        }
    }

    private static int FreeLoopbackPort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    /// <summary>닫히면 안의 프로세스를 모두 끝내는 작업 개체.</summary>
    private sealed class JobObject : IDisposable
    {
        private IntPtr handle;

        private JobObject(IntPtr h) => handle = h;

        public static JobObject? Create()
        {
            IntPtr h = CreateJobObject(IntPtr.Zero, null);
            if (h == IntPtr.Zero) return null;
            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            info.BasicLimitInformation.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            int size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            IntPtr buf = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(info, buf, false);
                if (!SetInformationJobObject(h, 9 /* JobObjectExtendedLimitInformation */, buf, (uint)size))
                {
                    CloseHandle(h);
                    return null;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
            return new JobObject(h);
        }

        public void Assign(IntPtr process) => AssignProcessToJobObject(handle, process);

        public void Dispose()
        {
            if (handle != IntPtr.Zero) CloseHandle(handle);
            handle = IntPtr.Zero;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateJobObject(IntPtr attrs, string? name);
        [DllImport("kernel32.dll")] private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint size);
        [DllImport("kernel32.dll")] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
    }
}
