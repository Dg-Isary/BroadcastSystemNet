#nullable disable

using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Drawing;
using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using System.Collections.Generic;
using System.Linq;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace BroadcastSystemNet
{
    public static class LogHelper
    {
        private static readonly object _lock = new object();
        public static void Log(string message, Exception ex = null)
        {
            Task.Run(() => {
                try
                {
                    lock (_lock)
                    {
                        string logPath = Path.Combine(Program.AppDataDir, "system_error.log");
                        string logMsg = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";
                        if (ex != null) logMsg += $"\r\n   细节: {ex.Message}\r\n   追踪: {ex.StackTrace}";
                        File.AppendAllText(logPath, logMsg + "\r\n----------------------------------\r\n");
                    }
                }
                catch { }
            });
        }
    }

    internal static class Program
    {
        public static string BaseDir = AppDomain.CurrentDomain.BaseDirectory;
        public static string ResDir = Path.Combine(BaseDir, "res");
        public static string BinDir = Path.Combine(ResDir, "bin");
        public static string FfplayPath = Path.Combine(BinDir, "ffplay.exe");
        public static string FfmpegPath = Path.Combine(BinDir, "ffmpeg.exe");

        public static string AppDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CampusBroadcastSystem");
        public static string ConfigDir = Path.Combine(AppDataDir, "config");
        public static string RingDir = Path.Combine(AppDataDir, "ring");

        public static ConcurrentDictionary<string, DateTime> ValidTokens = new ConcurrentDictionary<string, DateTime>();
        public static ConcurrentDictionary<string, bool> TriggeredTasks = new ConcurrentDictionary<string, bool>();

        public static object StatusLock = new object();
        public static bool IsPlaying = false;
        public static string CurrentPlayingName = "";

        public static DateTime CurrentAudioStartTime = DateTime.Now;
        public static double CurrentAudioOffset = 0;

        public static object WaveformLock = new object();
        public static short[] CurrentWaveformSamples = new short[0];

        public static object ManualLock = new object();
        public static string ManualStatus = "stopped";
        public static string ManualMode = "sequential";
        public static int ManualVolume = 100;
        public static int ManualCurrentIndex = 0;
        public static double ManualPauseOffset = 0;
        public static double ManualCurrentDuration = 0.0;
        public static DateTime MpStartTime = DateTime.Now;
        public static JsonArray ManualQueue = new JsonArray();
        public static Process MpProcess = null;

        public static System.Threading.Timer SchedulerTimer;
        public static System.Threading.Timer ManualPlayerTimer;
        public static System.Threading.Timer SubscriptionTimer;

        public static Icon AppIcon;

        // --- 底层物理 CPU / RAM 探针 ---
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetSystemTimes(out FILETIME lpIdleTime, out FILETIME lpKernelTime, out FILETIME lpUserTime);

        [StructLayout(LayoutKind.Sequential)]
        public struct FILETIME { public uint dwLowDateTime; public uint dwHighDateTime; public ulong ToUInt64() => ((ulong)dwHighDateTime << 32) | dwLowDateTime; }

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        [StructLayout(LayoutKind.Sequential)]
        public struct MEMORYSTATUSEX { public uint dwLength; public uint dwMemoryLoad; public ulong ullTotalPhys; public ulong ullAvailPhys; public ulong ullTotalPageFile; public ulong ullAvailPageFile; public ulong ullTotalVirtual; public ulong ullAvailVirtual; public ulong ullAvailExtendedVirtual; }

        private static ulong _lastIdleTime = 0;
        private static ulong _lastSystemTime = 0;

        public static double GetCpuUsage()
        {
            try
            {
                if (GetSystemTimes(out FILETIME idleTime, out FILETIME kernelTime, out FILETIME userTime))
                {
                    ulong currentIdle = idleTime.ToUInt64();
                    ulong currentSystem = kernelTime.ToUInt64() + userTime.ToUInt64();
                    if (_lastSystemTime != 0)
                    {
                        ulong idleDiff = currentIdle - _lastIdleTime;
                        ulong sysDiff = currentSystem - _lastSystemTime;
                        if (sysDiff > 0)
                        {
                            double cpu = (1.0 - ((double)idleDiff / sysDiff)) * 100.0;
                            _lastIdleTime = currentIdle; _lastSystemTime = currentSystem;
                            return Math.Min(100.0, Math.Max(0.0, cpu));
                        }
                    }
                    _lastIdleTime = currentIdle; _lastSystemTime = currentSystem;
                }
            }
            catch (Exception ex) { LogHelper.Log("GetCpuUsage Error", ex); }
            return 0.0;
        }

        public static double GetRamUsage()
        {
            try
            {
                MEMORYSTATUSEX memStatus = new MEMORYSTATUSEX();
                memStatus.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
                if (GlobalMemoryStatusEx(ref memStatus)) return memStatus.dwMemoryLoad;
            }
            catch (Exception ex) { LogHelper.Log("GetRamUsage Error", ex); }
            return 0.0;
        }

        [STAThread]
        static void Main(string[] args)
        {
            ApplicationConfiguration.Initialize();

            Directory.CreateDirectory(AppDataDir); Directory.CreateDirectory(ConfigDir); Directory.CreateDirectory(RingDir);

            string iconPath = Path.Combine(ResDir, "favicon.ico");
            if (File.Exists(iconPath)) { try { AppIcon = new Icon(iconPath); } catch { AppIcon = SystemIcons.Information; } }
            else { AppIcon = SystemIcons.Information; }

            GetSystemTimes(out FILETIME i, out FILETIME k, out FILETIME u);
            _lastIdleTime = i.ToUInt64(); _lastSystemTime = k.ToUInt64() + u.ToUInt64();

            Task.Run(() => StartWebServer(args));

            SchedulerTimer = new System.Threading.Timer(SchedulerWorker, null, 1000, 1000);
            ManualPlayerTimer = new System.Threading.Timer(ManualPlayerWorker, null, 200, 200);

            int intervalHours = 12;
            var all = ReadJsonl("setting.jsonl");
            foreach (var item in all) { if (item["_type"]?.ToString() == "global" && item["subscription_interval"] != null) { intervalHours = item["subscription_interval"].GetValue<int>(); break; } }
            if (intervalHours <= 0) intervalHours = 12;
            SubscriptionTimer = new System.Threading.Timer(SubscriptionWorker, null, 5000, intervalHours * 3600 * 1000);

            Application.Run(new MainForm());
        }

        public static string HashPassword(string pwd)
        {
            if (string.IsNullOrEmpty(pwd)) return "";
            using (var sha256 = System.Security.Cryptography.SHA256.Create())
            {
                var bytes = sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(pwd));
                return BitConverter.ToString(bytes).Replace("-", "").ToLower();
            }
        }

        public static string ResolveAudioPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "";
            if (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || path.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return path;
            try
            {
                if (Path.IsPathRooted(path)) return Path.GetFullPath(path);
                string relPath = path.TrimStart('\\', '/');
                string finalPath = Path.GetFullPath(Path.Combine(AppDataDir, relPath));
                if (!File.Exists(finalPath) && !Directory.Exists(finalPath))
                {
                    string oldPath = Path.GetFullPath(Path.Combine(BaseDir, relPath));
                    if (File.Exists(oldPath) || Directory.Exists(oldPath)) return oldPath;
                }
                if (!finalPath.StartsWith(AppDataDir, StringComparison.OrdinalIgnoreCase)) return "";
                return finalPath;
            }
            catch (Exception ex) { LogHelper.Log($"Path Resolve Error: {path}", ex); return ""; }
        }

        public static bool IsSafeAudioPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            if (path.StartsWith("http://") || path.StartsWith("https://")) return true;
            try
            {
                string fullPath = ResolveAudioPath(path);
                if (string.IsNullOrEmpty(fullPath)) return false;
                var exts = new HashSet<string> { ".mp3", ".wav", ".flac", ".m4a", ".aac", ".ogg", ".wma", ".m3u8", ".m3u" };
                return exts.Contains(Path.GetExtension(fullPath).ToLower());
            }
            catch { return false; }
        }

        public static async Task<bool> ForceSyncSubscription()
        {
            try
            {
                var all = ReadJsonl("setting.jsonl"); string subUrl = "";
                foreach (var item in all) { if (item["_type"]?.ToString() == "global") { subUrl = item["subscription_url"]?.ToString() ?? ""; break; } }
                if (string.IsNullOrWhiteSpace(subUrl) || !subUrl.StartsWith("http")) return false;

                using var client = new HttpClient(); client.Timeout = TimeSpan.FromSeconds(15);
                var response = await client.GetStringAsync(subUrl);
                var fetchedData = JsonNode.Parse(response)?.AsArray(); if (fetchedData == null) return false;

                var newAll = new JsonArray();
                foreach (var item in all) { if (item["_type"]?.ToString() == "tiaoxiu" && item["source"]?.ToString() == "sub") continue; newAll.Add(item.DeepClone()); }

                foreach (var item in fetchedData)
                {
                    try
                    {
                        var obj = item.AsObject();
                        if (obj["date"] == null || !DateTime.TryParse(obj["date"].ToString(), out _)) continue;
                        string action = obj["action"]?.ToString(); if (action != "enable" && action != "disable") continue;
                        if (obj["marker"] != null && !int.TryParse(obj["marker"].ToString(), out _)) continue;

                        var cleanObj = new JsonObject();
                        cleanObj["_type"] = "tiaoxiu"; cleanObj["source"] = "sub";
                        cleanObj["start_date"] = obj["date"].ToString(); cleanObj["end_date"] = obj["date"].ToString();
                        cleanObj["action"] = action; cleanObj["marker"] = obj["marker"] != null ? int.Parse(obj["marker"].ToString()) : 0;
                        if (obj["note"] != null) cleanObj["note"] = obj["note"].ToString();
                        newAll.Add(cleanObj);
                    }
                    catch { }
                }
                WriteJsonl("setting.jsonl", newAll); return true;
            }
            catch (Exception ex) { LogHelper.Log("ForceSyncSubscription Failed", ex); return false; }
        }

        static void SubscriptionWorker(object state) { _ = ForceSyncSubscription(); }

        public static void UpdateSubscriptionTimer()
        {
            int intervalHours = 12; var all = ReadJsonl("setting.jsonl");
            foreach (var item in all) { if (item["_type"]?.ToString() == "global" && item["subscription_interval"] != null) { intervalHours = item["subscription_interval"].GetValue<int>(); break; } }
            if (intervalHours <= 0) intervalHours = 12;
            SubscriptionTimer?.Change(2000, intervalHours * 3600 * 1000);
        }

        static void BackgroundLoadWave(string filepath, bool isManual = false)
        {
            Task.Run(() => {
                try
                {
                    lock (WaveformLock) { CurrentWaveformSamples = new short[0]; }
                    if (!File.Exists(FfmpegPath) || (!filepath.StartsWith("http") && !File.Exists(filepath))) return;
                    var psi = new ProcessStartInfo { FileName = FfmpegPath, UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
                    psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(filepath); psi.ArgumentList.Add("-ac"); psi.ArgumentList.Add("1");
                    psi.ArgumentList.Add("-ar"); psi.ArgumentList.Add("200"); psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("s16le"); psi.ArgumentList.Add("-");

                    using (var p = Process.Start(psi)) using (var ms = new MemoryStream())
                    {
                        p.StandardOutput.BaseStream.CopyTo(ms); byte[] rawData = ms.ToArray();
                        var samples = new short[rawData.Length / 2]; Buffer.BlockCopy(rawData, 0, samples, 0, rawData.Length);
                        lock (WaveformLock) { CurrentWaveformSamples = samples; }
                        if (isManual) { lock (ManualLock) { ManualCurrentDuration = samples.Length / 200.0; } }
                    }
                }
                catch (Exception ex) { LogHelper.Log($"Waveform Load Error: {filepath}", ex); }
            });
        }

        public static JsonArray ReadJsonl(string filename)
        {
            var arr = new JsonArray(); string path = Path.Combine(ConfigDir, filename);
            if (!File.Exists(path)) return arr;
            foreach (var line in File.ReadAllLines(path)) { if (string.IsNullOrWhiteSpace(line)) continue; try { arr.Add(JsonNode.Parse(line)); } catch { } }
            return arr;
        }

        public static void WriteJsonl(string filename, JsonArray data)
        {
            string path = Path.Combine(ConfigDir, filename);
            var options = new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
            File.WriteAllLines(path, data.Select(n => n.ToJsonString(options)));
        }

        public static int GetFolderResumeIndex(string folderPath)
        {
            try { var all = ReadJsonl("resume.jsonl"); var obj = all.FirstOrDefault()?.AsObject(); if (obj != null && obj[folderPath] != null) return obj[folderPath].GetValue<int>(); }
            catch (Exception ex) { LogHelper.Log("Read Resume Error", ex); }
            return 0;
        }

        public static void SaveFolderResumeIndex(string folderPath, int index)
        {
            try { var all = ReadJsonl("resume.jsonl"); var obj = all.FirstOrDefault()?.AsObject() ?? new JsonObject(); obj[folderPath] = index; if (all.Count == 0) all.Add(obj); else all[0] = obj; WriteJsonl("resume.jsonl", all); }
            catch (Exception ex) { LogHelper.Log("Save Resume Error", ex); }
        }

        static void ChangePlayingStatus(bool isPlaying, string name) { lock (StatusLock) { IsPlaying = isPlaying; CurrentPlayingName = isPlaying ? name : ""; } }

        public static bool IsGlobalSwitchOn()
        {
            var all = ReadJsonl("setting.jsonl");
            foreach (var item in all) { if (item["_type"]?.ToString() == "global") return item["global_switch"]?.GetValue<bool>() ?? true; }
            return true;
        }

        public static void ToggleGlobalSwitch()
        {
            var all = ReadJsonl("setting.jsonl"); bool found = false;
            foreach (var item in all)
            {
                if (item["_type"]?.ToString() == "global")
                {
                    bool current = item["global_switch"]?.GetValue<bool>() ?? true; item["global_switch"] = !current; found = true;
                    if (current) { KillManualProcess(); try { foreach (var p in Process.GetProcessesByName("ffplay")) p.Kill(); } catch { } }
                    break;
                }
            }
            if (!found) { all.Insert(0, new JsonObject { ["_type"] = "global", ["global_switch"] = false, ["password"] = "" }); }
            WriteJsonl("setting.jsonl", all);
        }

        static void StartWebServer(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);
            var app = builder.Build();

            app.Use(async (context, next) => {
                string method = context.Request.Method; string path = context.Request.Path.Value;
                // 放行两个新架构接口 api/wave 和 api/sys
                if (path == "/" || path == "/m" || path.StartsWith("/res/") || path == "/api/login" || path == "/api/wave" || path == "/api/sys" || (method == "GET" && path == "/api/settings")) { await next.Invoke(); return; }

                string sysPwd = ""; var settings = ReadJsonl("setting.jsonl");
                foreach (var item in settings) { if (item["_type"]?.ToString() == "global") { sysPwd = item["password"]?.ToString() ?? ""; break; } }
                if (!string.IsNullOrEmpty(sysPwd))
                {
                    bool authValid = false; var authHeader = context.Request.Headers["Authorization"].ToString();
                    if (authHeader.StartsWith("Bearer ")) { string token = authHeader.Substring(7); if (ValidTokens.TryGetValue(token, out var exp) && exp > DateTime.Now) { authValid = true; } }
                    if (!authValid) { context.Response.StatusCode = 401; await context.Response.WriteAsJsonAsync(new { status = "error", message = "Token Invalid" }); return; }
                }
                await next.Invoke();
            });

            app.UseStaticFiles(new StaticFileOptions { FileProvider = new PhysicalFileProvider(ResDir), RequestPath = "/res" });
            app.MapGet("/", async context => await context.Response.WriteAsync(await File.ReadAllTextAsync(Path.Combine(ResDir, "index.html"))));
            app.MapGet("/m", async context => await context.Response.WriteAsync(await File.ReadAllTextAsync(Path.Combine(ResDir, "mobile.html"))));

            app.MapPost("/api/login", async context => {
                using (var reader = new StreamReader(context.Request.Body))
                {
                    var json = JsonNode.Parse(await reader.ReadToEndAsync()); string inputPwd = json?["password"]?.ToString() ?? ""; string inputHash = HashPassword(inputPwd); string sysPwd = "";
                    foreach (var item in ReadJsonl("setting.jsonl")) { if (item["_type"]?.ToString() == "global") { sysPwd = item["password"]?.ToString() ?? ""; break; } }
                    if (string.IsNullOrEmpty(sysPwd) || sysPwd == inputPwd || sysPwd == inputHash) { string token = Guid.NewGuid().ToString("N"); ValidTokens[token] = DateTime.Now.AddHours(24); await context.Response.WriteAsJsonAsync(new { status = "success", token = token }); }
                    else { context.Response.StatusCode = 401; await context.Response.WriteAsJsonAsync(new { status = "error", message = "Password Error" }); }
                }
            });

            app.MapGet("/api/music", () => { var all = ReadJsonl("music.jsonl"); var res = new JsonArray(); foreach (var item in all) { if (item["_type"]?.ToString() != "settings") res.Add(item.DeepClone()); } return res; });
            app.MapGet("/api/tasks", () => ReadJsonl("tasks.jsonl"));

            app.MapPost("/api/tasks", async context => {
                using (var reader = new StreamReader(context.Request.Body))
                {
                    var newTask = JsonNode.Parse(await reader.ReadToEndAsync()).AsObject(); var tasks = ReadJsonl("tasks.jsonl"); int maxId = 0;
                    foreach (var t in tasks) { int id = t["id"]?.GetValue<int>() ?? 0; if (id > maxId) maxId = id; }
                    newTask["id"] = maxId + 1; tasks.Add(newTask); WriteJsonl("tasks.jsonl", tasks); await context.Response.WriteAsJsonAsync(new { status = "success", id = maxId + 1 });
                }
            });

            app.MapPut("/api/tasks/{id:int}", async (int id, HttpContext context) => {
                using (var reader = new StreamReader(context.Request.Body))
                {
                    var updatedData = JsonNode.Parse(await reader.ReadToEndAsync()).AsObject(); updatedData["id"] = id; var tasks = ReadJsonl("tasks.jsonl");
                    for (int i = 0; i < tasks.Count; i++) { if (tasks[i]["id"]?.GetValue<int>() == id) { tasks[i] = updatedData; break; } }
                    WriteJsonl("tasks.jsonl", tasks); await context.Response.WriteAsJsonAsync(new { status = "success" });
                }
            });

            app.MapDelete("/api/tasks/{id:int}", async (int id, HttpContext context) => {
                var tasks = ReadJsonl("tasks.jsonl"); var newTasks = new JsonArray();
                foreach (var t in tasks) { if (t["id"]?.GetValue<int>() != id) newTasks.Add(t.DeepClone()); }
                WriteJsonl("tasks.jsonl", newTasks); await context.Response.WriteAsJsonAsync(new { status = "success" });
            });

            app.MapGet("/api/tiaoxiu", () => { var all = ReadJsonl("setting.jsonl"); var res = new JsonArray(); foreach (var item in all) { if (item["_type"]?.ToString() == "tiaoxiu") res.Add(item.DeepClone()); } return res; });

            app.MapPost("/api/tiaoxiu", async context => {
                using (var reader = new StreamReader(context.Request.Body))
                {
                    var json = JsonNode.Parse(await reader.ReadToEndAsync()); var newTiaoxiuList = json?.AsArray(); if (newTiaoxiuList == null) return;
                    var all = ReadJsonl("setting.jsonl"); var newAll = new JsonArray();
                    foreach (var item in all) { if (item["_type"]?.ToString() != "tiaoxiu") { newAll.Add(item.DeepClone()); } }
                    foreach (var item in newTiaoxiuList) { var obj = item.DeepClone().AsObject(); obj["_type"] = "tiaoxiu"; newAll.Add(obj); }
                    WriteJsonl("setting.jsonl", newAll); await context.Response.WriteAsJsonAsync(new { status = "success" });
                }
            });

            app.MapPost("/api/tiaoxiu/sync", async context => { bool success = await ForceSyncSubscription(); if (success) await context.Response.WriteAsJsonAsync(new { status = "success" }); else { context.Response.StatusCode = 500; await context.Response.WriteAsJsonAsync(new { status = "error" }); } });

            app.MapGet("/api/settings", () => {
                foreach (var item in ReadJsonl("setting.jsonl"))
                {
                    if (item["_type"]?.ToString() == "global")
                    {
                        bool gs = item["global_switch"]?.GetValue<bool>() ?? true; bool hasPwd = !string.IsNullOrEmpty(item["password"]?.ToString()); string subUrl = item["subscription_url"]?.ToString() ?? "";
                        return new JsonObject { ["global_switch"] = gs, ["has_password"] = hasPwd, ["subscription_url"] = subUrl };
                    }
                }
                return new JsonObject { ["global_switch"] = true, ["has_password"] = false, ["subscription_url"] = "" };
            });

            app.MapPost("/api/settings", async context => {
                using (var reader = new StreamReader(context.Request.Body))
                {
                    var json = JsonNode.Parse(await reader.ReadToEndAsync()); var all = ReadJsonl("setting.jsonl"); JsonObject globalItem = null;
                    foreach (var item in all) { if (item["_type"]?.ToString() == "global") { globalItem = item.AsObject(); break; } }
                    if (globalItem == null) { globalItem = new JsonObject { ["_type"] = "global", ["global_switch"] = true, ["password"] = "" }; all.Insert(0, globalItem); }
                    if (json["global_switch"] != null)
                    {
                        bool newState = json["global_switch"].GetValue<bool>(); globalItem["global_switch"] = newState;
                        if (!newState) { KillManualProcess(); try { foreach (var p in Process.GetProcessesByName("ffplay")) p.Kill(); } catch { } ChangePlayingStatus(false, ""); }
                    }
                    if (json["password"] != null) { string rawPwd = json["password"].ToString(); globalItem["password"] = string.IsNullOrEmpty(rawPwd) ? "" : HashPassword(rawPwd); }
                    if (json["subscription_url"] != null) globalItem["subscription_url"] = json["subscription_url"].ToString();
                    WriteJsonl("setting.jsonl", all); await context.Response.WriteAsJsonAsync(new { status = "success" });
                }
            });

            // ================= 高速波形轻量化通道 (60ms) =================
            app.MapGet("/api/wave", () => {
                lock (StatusLock)
                {
                    int[] frameData = new int[200];
                    if (IsPlaying)
                    {
                        double actualElapsed = (DateTime.Now - CurrentAudioStartTime).TotalSeconds + CurrentAudioOffset;
                        int startIdx = (int)((actualElapsed + 0.05) * 200); short[] samples; lock (WaveformLock) { samples = CurrentWaveformSamples; }
                        if (samples.Length > 0)
                        {
                            int maxAbs = 0;
                            for (int i = 0; i < 200; i++) { int idx = startIdx + i; frameData[i] = (idx >= 0 && idx < samples.Length) ? samples[idx] : (short)0; if (Math.Abs(frameData[i]) > maxAbs) maxAbs = Math.Abs(frameData[i]); }
                            if (maxAbs > 0) { for (int i = 0; i < 200; i++) frameData[i] = (int)((frameData[i] / (double)maxAbs) * 100); }
                        }
                        else { for (int i = 0; i < 200; i++) { frameData[i] = (int)((Math.Sin((actualElapsed + 0.05 + (i / 200.0)) * 15) * 0.5 + Math.Sin((actualElapsed + 0.05 + (i / 200.0)) * 43) * 0.5) * 20); } }
                    }
                    return new JsonObject { ["is_playing"] = IsPlaying, ["frame_data"] = JsonSerializer.SerializeToNode(frameData) };
                }
            });

            // ================= 低速系统重载通道 (500ms) =================
            app.MapGet("/api/sys", () => {
                lock (StatusLock) lock (ManualLock)
                {
                    double elapsed = ManualStatus == "playing" ? (DateTime.Now - MpStartTime).TotalSeconds + ManualPauseOffset : ManualPauseOffset;
                    return new JsonObject
                    {
                        ["is_playing"] = IsPlaying,
                        ["current_playing_name"] = CurrentPlayingName,
                        ["sys_stats"] = new JsonObject { ["cpu"] = GetCpuUsage(), ["ram"] = GetRamUsage() },
                        ["manual_player"] = new JsonObject
                        {
                            ["status"] = ManualStatus,
                            ["mode"] = ManualMode,
                            ["volume"] = ManualVolume,
                            ["current_index"] = ManualCurrentIndex,
                            ["elapsed"] = elapsed,
                            ["current_duration"] = ManualCurrentDuration,
                            ["queue"] = ManualQueue.DeepClone() // 降频到 500ms，极大降低内存 GC 压力
                        }
                    };
                }
            });

            app.MapPost("/api/player/cmd", async context => {
                using (var reader = new StreamReader(context.Request.Body))
                {
                    var json = JsonNode.Parse(await reader.ReadToEndAsync()); if (json == null) return; string cmd = json["cmd"]?.ToString();
                    lock (ManualLock)
                    {
                        if (cmd == "play") { if (ManualStatus == "paused" || (ManualStatus == "stopped" && ManualQueue.Count > 0)) ManualStatus = "playing"; }
                        else if (cmd == "pause") { if (ManualStatus == "playing") { ManualStatus = "paused"; if (MpProcess != null && !MpProcess.HasExited) { ManualPauseOffset += (DateTime.Now - MpStartTime).TotalSeconds; KillManualProcess(); } } }
                        else if (cmd == "stop") { ManualStatus = "stopped"; ManualCurrentIndex = 0; ManualPauseOffset = 0; KillManualProcess(); }
                        else if (cmd == "next" || cmd == "prev") { KillManualProcess(); ManualPauseOffset = 0; if (ManualQueue.Count > 0) { if (cmd == "next") ManualCurrentIndex = (ManualCurrentIndex + 1) % ManualQueue.Count; else ManualCurrentIndex = (ManualCurrentIndex - 1 + ManualQueue.Count) % ManualQueue.Count; } ManualStatus = "playing"; }
                        else if (cmd == "play_idx") { KillManualProcess(); ManualPauseOffset = 0; ManualCurrentIndex = json["val"]?.GetValue<int>() ?? 0; ManualStatus = "playing"; }
                        else if (cmd == "seek") { ManualPauseOffset = json["val"]?.GetValue<double>() ?? 0; if (ManualStatus == "playing") KillManualProcess(); }
                        else if (cmd == "mode") { var modes = new[] { "sequential", "loop", "single" }; ManualMode = modes[(Array.IndexOf(modes, ManualMode) + 1) % 3]; }
                        else if (cmd == "volume")
                        {
                            ManualVolume = json["val"]?.GetValue<int>() ?? 100;
                            if (ManualStatus == "playing" && MpProcess != null && !MpProcess.HasExited) { ManualPauseOffset += (DateTime.Now - MpStartTime).TotalSeconds; KillManualProcess(); }
                        }
                        else if (cmd == "add")
                        {
                            var val = json["val"];
                            if (val != null)
                            {
                                string p = val["path"]?.ToString() ?? ""; string type = val["type"]?.ToString() ?? "";
                                if (!IsSafeAudioPath(p) && type != "folder") return;
                                p = ResolveAudioPath(p);
                                if (type == "folder" && Directory.Exists(p)) { var exts = new HashSet<string> { ".mp3", ".wav", ".flac", ".m4a", ".aac", ".ogg", ".wma", ".m3u8", ".m3u" }; foreach (var f in Directory.GetFiles(p).Where(f => exts.Contains(Path.GetExtension(f).ToLower())).OrderBy(f => f)) { var item = new JsonObject(); item["name"] = Path.GetFileName(f); item["path"] = f.StartsWith(AppDataDir) ? f.Substring(AppDataDir.Length).TrimStart('\\', '/').Replace('\\', '/') : f; item["type"] = "file"; ManualQueue.Add(item); } }
                                else ManualQueue.Add(val.DeepClone());
                            }
                        }
                        else if (cmd == "remove") { int idx = json["val"]?.GetValue<int>() ?? -1; if (idx >= 0 && idx < ManualQueue.Count) { ManualQueue.RemoveAt(idx); if (ManualCurrentIndex > idx) ManualCurrentIndex--; else if (ManualCurrentIndex == idx) { KillManualProcess(); ManualPauseOffset = 0; if (ManualCurrentIndex >= ManualQueue.Count) { ManualStatus = "stopped"; ManualCurrentIndex = 0; } } } }
                    }
                    await context.Response.WriteAsJsonAsync(new { status = "success" });
                }
            });

            app.Run("http://0.0.0.0:5000");
        }

        public static void KillManualProcess() { if (MpProcess != null && !MpProcess.HasExited) { try { MpProcess.Kill(); } catch { } ChangePlayingStatus(false, ""); } MpProcess = null; }

        static bool ShouldPlayToday(JsonObject task)
        {
            var now = DateTime.Now; string todayDate = now.ToString("yyyy-MM-dd"); int todayWeekday = (int)now.DayOfWeek; if (todayWeekday == 0) todayWeekday = 7;
            int taskMarker = 1; if (task["tiaoxiu_marker"] != null) int.TryParse(task["tiaoxiu_marker"].ToString(), out taskMarker);
            var settings = ReadJsonl("setting.jsonl");

            foreach (var item in settings)
            {
                if (item["_type"]?.ToString() == "tiaoxiu")
                {
                    string tStart = item["start_date"]?.ToString() ?? item["date"]?.ToString();
                    string tEnd = item["end_date"]?.ToString() ?? tStart;
                    int tMarker = 0; if (item["marker"] != null) int.TryParse(item["marker"].ToString(), out tMarker);
                    string tAction = item["action"]?.ToString();

                    if (!string.IsNullOrEmpty(tStart) && !string.IsNullOrEmpty(tEnd))
                    {
                        if (string.Compare(todayDate, tStart) >= 0 && string.Compare(todayDate, tEnd) <= 0)
                        {
                            if (tMarker == 0 || tMarker == taskMarker) return tAction == "enable";
                        }
                    }
                }
            }

            var specificDates = task["specific_dates"]?.AsArray(); if (specificDates != null) { foreach (var d in specificDates) { if (d?.ToString() == todayDate) return true; } }
            var days = task["days"]?.AsArray(); if (days != null) { foreach (var d in days) { if (int.TryParse(d?.ToString(), out int dayInt) && dayInt == todayWeekday) return true; } }
            return false;
        }

        static void SchedulerWorker(object state)
        {
            if (!IsGlobalSwitchOn()) return;
            try
            {
                var now = DateTime.Now; string todayDate = now.ToString("yyyy-MM-dd"); int nowSec = now.Hour * 3600 + now.Minute * 60 + now.Second;
                foreach (var t in ReadJsonl("tasks.jsonl"))
                {
                    var taskObj = t.AsObject(); if (!ShouldPlayToday(taskObj)) continue;
                    string taskTime = taskObj["start_time"]?.ToString() ?? "00:00:00"; if (taskTime.Length == 5) taskTime += ":00";
                    var parts = taskTime.Split(':'); if (parts.Length != 3) continue;
                    int tSec = int.Parse(parts[0]) * 3600 + int.Parse(parts[1]) * 60 + int.Parse(parts[2]); int diff = (nowSec - tSec) % 86400;
                    if (diff >= 0 && diff <= 5)
                    {
                        string taskKey = $"{(taskObj["id"]?.ToString() ?? "0")}_{taskTime}_{todayDate}";
                        if (!TriggeredTasks.ContainsKey(taskKey)) { TriggeredTasks[taskKey] = true; Task.Run(() => PlayScheduledTask(taskObj, "[定时]")); }
                    }
                }
            }
            catch (Exception ex) { LogHelper.Log("Scheduler Exception", ex); }
        }

        static void PlayScheduledTask(JsonObject task, string prefix)
        {
            try
            {
                var musicIds = task["music_ids"]?.AsArray(); if (musicIds == null || musicIds.Count == 0) return;
                int durationM = task["duration_m"]?.GetValue<int>() ?? 0; int durationS = task["duration_s"]?.GetValue<int>() ?? 0; double totalDurationSec = durationM * 60 + durationS; DateTime taskStart = DateTime.Now;

                int volume = task["volume"]?.GetValue<int>() ?? 100;
                string seqMode = task["sequence_mode"]?.ToString() ?? "sequential"; List<string> playList = new List<string>(); foreach (var m in musicIds) playList.Add(m.ToString());
                if (seqMode == "random") { var rnd = new Random(); playList = playList.OrderBy(x => rnd.Next()).ToList(); }
                var allMusic = ReadJsonl("music.jsonl"); List<JsonObject> expandedQueue = new List<JsonObject>();

                foreach (var idStr in playList)
                {
                    var m = allMusic.FirstOrDefault(x => x["id"]?.ToString() == idStr); if (m == null) continue;
                    string type = m["type"]?.ToString(); string path = ResolveAudioPath(m["path"]?.ToString());
                    if (type == "folder" && Directory.Exists(path))
                    {
                        var exts = new HashSet<string> { ".mp3", ".wav", ".flac", ".m4a", ".aac", ".ogg", ".wma", ".m3u8", ".m3u" }; var files = Directory.GetFiles(path).Where(f => exts.Contains(Path.GetExtension(f).ToLower())).OrderBy(f => f).ToList(); if (files.Count == 0) continue;
                        int count = task["folder_play_count"]?.GetValue<int>() ?? 1;
                        if (task["folder_mode"]?.ToString() == "random") { var rndFiles = files.OrderBy(x => Guid.NewGuid()).Take(count).ToList(); foreach (var f in rndFiles) expandedQueue.Add(new JsonObject { ["name"] = Path.GetFileName(f), ["path"] = f }); }
                        else { int startIndex = GetFolderResumeIndex(path); for (int i = 0; i < count; i++) { int idx = (startIndex + i) % files.Count; expandedQueue.Add(new JsonObject { ["name"] = Path.GetFileName(files[idx]), ["path"] = files[idx] }); } SaveFolderResumeIndex(path, (startIndex + count) % files.Count); }
                    }
                    else { expandedQueue.Add(m.AsObject()); }
                }

                foreach (var m in expandedQueue)
                {
                    if (totalDurationSec > 0) { double elapsed = (DateTime.Now - taskStart).TotalSeconds; if (elapsed >= totalDurationSec) break; }
                    if (!IsGlobalSwitchOn()) break;
                    string path = m["path"]?.ToString(); string name = m["name"]?.ToString(); path = ResolveAudioPath(path);
                    if (string.IsNullOrEmpty(path) || (!File.Exists(path) && !path.StartsWith("http"))) continue;

                    ChangePlayingStatus(true, $"{prefix} {name}"); CurrentAudioOffset = 0; CurrentAudioStartTime = DateTime.Now; BackgroundLoadWave(path, false);

                    try
                    {
                        var psi = new ProcessStartInfo { FileName = FfplayPath, CreateNoWindow = true, UseShellExecute = false };
                        psi.ArgumentList.Add("-nodisp"); psi.ArgumentList.Add("-autoexit");
                        psi.ArgumentList.Add("-af"); psi.ArgumentList.Add($"volume={(volume / 100.0).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}");
                        if (totalDurationSec > 0) { double elapsed = (DateTime.Now - taskStart).TotalSeconds; double rem = totalDurationSec - elapsed; if (rem <= 0) break; psi.ArgumentList.Add("-t"); psi.ArgumentList.Add(rem.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)); }
                        psi.ArgumentList.Add(path); using (var p = Process.Start(psi)) { p?.WaitForExit(); }
                    }
                    catch (Exception ex) { LogHelper.Log($"Play FFplay Exception: {path}", ex); }
                    finally { ChangePlayingStatus(false, ""); }
                    System.Threading.Thread.Sleep(200);
                }
            }
            catch (Exception ex) { LogHelper.Log("PlayScheduledTask Error", ex); }
        }

        static void ManualPlayerWorker(object state)
        {
            lock (ManualLock)
            {
                if (ManualStatus != "playing") return;
                bool isExited = true; if (MpProcess != null) { try { isExited = MpProcess.HasExited; } catch { isExited = true; } }

                if (isExited)
                {
                    if (MpProcess != null)
                    {
                        ChangePlayingStatus(false, ""); try { MpProcess.Dispose(); } catch { }
                        MpProcess = null; ManualPauseOffset = 0; ManualCurrentDuration = 0;
                        if (ManualMode == "single") { } else if (ManualMode == "loop") { if (ManualQueue.Count > 0) ManualCurrentIndex = (ManualCurrentIndex + 1) % ManualQueue.Count; } else { ManualCurrentIndex++; if (ManualCurrentIndex >= ManualQueue.Count) { ManualStatus = "stopped"; ManualCurrentIndex = 0; return; } }
                        return;
                    }

                    while (ManualStatus == "playing" && ManualCurrentIndex < ManualQueue.Count)
                    {
                        var music = ManualQueue[ManualCurrentIndex]; string path = music["path"]?.ToString() ?? ""; path = ResolveAudioPath(path);
                        if (File.Exists(path) || path.StartsWith("http"))
                        {
                            ChangePlayingStatus(true, $"[手动] {music["name"]}"); if (ManualPauseOffset == 0) ManualCurrentDuration = 0.0; CurrentAudioOffset = ManualPauseOffset; CurrentAudioStartTime = DateTime.Now; MpStartTime = DateTime.Now; BackgroundLoadWave(path, true);
                            MpProcess = new Process(); MpProcess.StartInfo.FileName = FfplayPath; MpProcess.StartInfo.CreateNoWindow = true; MpProcess.StartInfo.UseShellExecute = false; MpProcess.StartInfo.ArgumentList.Add("-nodisp"); MpProcess.StartInfo.ArgumentList.Add("-autoexit");
                            MpProcess.StartInfo.ArgumentList.Add("-af"); MpProcess.StartInfo.ArgumentList.Add($"volume={(ManualVolume / 100.0).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}");
                            if (ManualPauseOffset > 0) { MpProcess.StartInfo.ArgumentList.Add("-ss"); MpProcess.StartInfo.ArgumentList.Add(ManualPauseOffset.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)); }
                            MpProcess.StartInfo.ArgumentList.Add(path);
                            try { MpProcess.Start(); break; } catch { MpProcess = null; }
                        }
                        ManualCurrentIndex++; if (ManualCurrentIndex >= ManualQueue.Count) { ManualStatus = "stopped"; ManualCurrentIndex = 0; }
                    }
                }
            }
        }
    }

    public static class UIHelper
    {
        public static Font MainFont = new Font("Microsoft YaHei UI", 10F, FontStyle.Regular, GraphicsUnit.Point);
        public static Font BoldFont = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold, GraphicsUnit.Point);
        public static Font TitleFont = new Font("Microsoft YaHei UI", 14F, FontStyle.Bold, GraphicsUnit.Point);
        public static Color Primary = Color.FromArgb(0, 120, 212);
        public static Color Danger = Color.FromArgb(232, 17, 35);
        public static Color Success = Color.FromArgb(16, 137, 62);
        public static Color TextDark = Color.FromArgb(27, 27, 27);
        public static Color TextGray = Color.FromArgb(102, 102, 102);

        public static Button CreateBtn(string text, Color bg, Color fg) { var btn = new Button { BackColor = bg, ForeColor = fg, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand, Font = MainFont, Text = text, Dock = DockStyle.Fill, Margin = new Padding(6), TextAlign = ContentAlignment.MiddleCenter }; btn.FlatAppearance.BorderSize = 0; return btn; }
        public static Button CreateAutoBtn(string text, Color bg, Color fg) { var btn = new Button { BackColor = bg, ForeColor = fg, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand, Font = MainFont, Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(12, 6, 12, 6), Margin = new Padding(4), TextAlign = ContentAlignment.MiddleCenter }; btn.FlatAppearance.BorderSize = 0; return btn; }
    }

    public class SubscriptionForm : Form
    {
        private TextBox txtUrl;
        private NumericUpDown numInterval;

        public SubscriptionForm()
        {
            this.AutoScaleMode = AutoScaleMode.Dpi;
            this.BackColor = Color.White;
            this.Text = "云端调休同步设置";
            this.ClientSize = new Size(540, 310);
            this.StartPosition = FormStartPosition.CenterParent;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;

            this.Icon = Program.AppIcon;

            TableLayoutPanel tlp = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 4, Padding = new Padding(25) };
            tlp.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            tlp.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            tlp.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            tlp.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            tlp.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            tlp.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            var lblTitle = new Label { Text = "远程调休订阅配置", Font = UIHelper.TitleFont, ForeColor = UIHelper.TextDark, AutoSize = true, Margin = new Padding(0, 0, 0, 25) };
            tlp.Controls.Add(lblTitle, 0, 0); tlp.SetColumnSpan(lblTitle, 2);

            tlp.Controls.Add(new Label { Text = "订阅源 (JSON):", Font = UIHelper.BoldFont, ForeColor = UIHelper.TextDark, Anchor = AnchorStyles.Right, AutoSize = true }, 0, 1);
            txtUrl = new TextBox { Dock = DockStyle.Fill, Font = new Font("Consolas", 10F), Margin = new Padding(5, 0, 0, 20) };
            tlp.Controls.Add(txtUrl, 1, 1);

            tlp.Controls.Add(new Label { Text = "轮询周期 (小时):", Font = UIHelper.BoldFont, ForeColor = UIHelper.TextDark, Anchor = AnchorStyles.Right, AutoSize = true }, 0, 2);
            numInterval = new NumericUpDown { Width = 100, Minimum = 1, Maximum = 720, Value = 12, Font = UIHelper.MainFont, Margin = new Padding(5, 0, 0, 25) };
            tlp.Controls.Add(numInterval, 1, 2);

            var btnSave = UIHelper.CreateBtn("保存并强制向云端同步一次", UIHelper.Primary, Color.White);
            btnSave.Click += async (s, e) => {
                btnSave.Text = "正在向云端拉取数据..."; btnSave.Enabled = false;
                var all = Program.ReadJsonl("setting.jsonl");
                JsonObject globalItem = null;
                foreach (var item in all) { if (item["_type"]?.ToString() == "global") { globalItem = item.AsObject(); break; } }
                if (globalItem == null) { globalItem = new JsonObject { ["_type"] = "global", ["global_switch"] = true, ["password"] = "" }; all.Insert(0, globalItem); }
                globalItem["subscription_url"] = txtUrl.Text.Trim();
                globalItem["subscription_interval"] = (int)numInterval.Value;
                Program.WriteJsonl("setting.jsonl", all);
                Program.UpdateSubscriptionTimer();
                bool success = await Program.ForceSyncSubscription();
                if (success) MessageBox.Show("同步成功！已成功将云端调休数据写入系统配置。\n你可以随时在网页端的【调休管理】中查看最新状况。", "成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
                else MessageBox.Show("无法拉取或解析，请检查订阅链接是否正确且包含合法 JSON 数组。\n配置已保存，系统后台将按预设周期持续重试。", "失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                this.Close();
            };
            tlp.Controls.Add(btnSave, 0, 3); tlp.SetColumnSpan(btnSave, 2);
            this.Controls.Add(tlp);

            var allSettings = Program.ReadJsonl("setting.jsonl");
            foreach (var item in allSettings)
            {
                if (item["_type"]?.ToString() == "global")
                {
                    txtUrl.Text = item["subscription_url"]?.ToString() ?? "";
                    if (item["subscription_interval"] != null) numInterval.Value = item["subscription_interval"].GetValue<int>();
                    break;
                }
            }
        }
    }

    public class MainForm : Form
    {
        private Label lblStatus;
        private Button btnToggle;
        private NotifyIcon trayIcon;
        private System.Windows.Forms.Timer uiTimer;

        public MainForm()
        {
            this.AutoScaleMode = AutoScaleMode.Dpi;
            this.BackColor = Color.White;
            this.Text = "校园广播 - 控制核心";
            this.ClientSize = new Size(500, 420);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.MaximizeBox = false;
            this.Icon = Program.AppIcon;

            TableLayoutPanel mainPnl = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5 };
            mainPnl.RowStyles.Add(new RowStyle(SizeType.Percent, 50F)); mainPnl.RowStyles.Add(new RowStyle(SizeType.AutoSize)); mainPnl.RowStyles.Add(new RowStyle(SizeType.AutoSize)); mainPnl.RowStyles.Add(new RowStyle(SizeType.AutoSize)); mainPnl.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));

            var lblTitle = new Label { Text = "智能校园广播核心", Font = UIHelper.TitleFont, ForeColor = UIHelper.TextDark, AutoSize = true, Anchor = AnchorStyles.None, Margin = new Padding(0, 0, 0, 15) }; mainPnl.Controls.Add(lblTitle, 0, 1);
            lblStatus = new Label { Text = "系统状态: 读取中...", Font = UIHelper.BoldFont, AutoSize = true, Anchor = AnchorStyles.None, Margin = new Padding(0, 0, 0, 25) }; mainPnl.Controls.Add(lblStatus, 0, 2);

            var gridPnl = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, RowCount = 3, Anchor = AnchorStyles.None };
            gridPnl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190)); gridPnl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
            gridPnl.RowStyles.Add(new RowStyle(SizeType.Absolute, 60)); gridPnl.RowStyles.Add(new RowStyle(SizeType.Absolute, 60)); gridPnl.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));

            var btnWeb = UIHelper.CreateBtn("打开控制看板", UIHelper.Primary, Color.White); btnWeb.Click += (s, e) => Process.Start(new ProcessStartInfo("http://127.0.0.1:5000") { UseShellExecute = true }); gridPnl.Controls.Add(btnWeb, 0, 0);
            var btnMusic = UIHelper.CreateBtn("本地音频管理", Color.FromArgb(240, 240, 240), UIHelper.TextDark); btnMusic.Click += (s, e) => new MusicToolForm().Show(); gridPnl.Controls.Add(btnMusic, 1, 0);

            btnToggle = UIHelper.CreateBtn("停用广播系统", UIHelper.Danger, Color.White); btnToggle.Click += (s, e) => { Program.ToggleGlobalSwitch(); UpdateUIState(); }; gridPnl.Controls.Add(btnToggle, 0, 1);

            var btnSub = UIHelper.CreateBtn("云端调休配置", Color.FromArgb(240, 240, 240), UIHelper.TextDark);
            btnSub.Click += (s, e) => new SubscriptionForm().ShowDialog();
            gridPnl.Controls.Add(btnSub, 1, 1);

            var btnBackup = UIHelper.CreateBtn("备份系统数据", Color.FromArgb(240, 240, 240), UIHelper.TextDark); btnBackup.Click += (s, e) => BackupConfig(); gridPnl.Controls.Add(btnBackup, 0, 2);
            var btnRestore = UIHelper.CreateBtn("从备份中恢复", Color.FromArgb(240, 240, 240), UIHelper.TextDark); btnRestore.Click += (s, e) => RestoreConfig(); gridPnl.Controls.Add(btnRestore, 1, 2);

            mainPnl.Controls.Add(gridPnl, 0, 3);
            var lblHint = new Label { Text = "提示：点击右上角关闭将最小化到托盘，后台持续运行", Font = new Font("Microsoft YaHei UI", 9F), ForeColor = UIHelper.TextGray, AutoSize = true, Anchor = AnchorStyles.None, Margin = new Padding(0, 25, 0, 0) }; mainPnl.Controls.Add(lblHint, 0, 4);
            this.Controls.Add(mainPnl);

            trayIcon = new NotifyIcon { Icon = Program.AppIcon, Text = "智能校园广播", Visible = true }; trayIcon.DoubleClick += (s, e) => { this.Show(); this.WindowState = FormWindowState.Normal; };
            var menu = new ContextMenuStrip();
            menu.Items.Add("打开主控制台 (GUI)", null, (s, e) => { this.Show(); this.WindowState = FormWindowState.Normal; }); menu.Items.Add("打开网页看板 (Web)", null, (s, e) => Process.Start(new ProcessStartInfo("http://127.0.0.1:5000") { UseShellExecute = true })); menu.Items.Add(new ToolStripSeparator()); menu.Items.Add("本地音频管理", null, (s, e) => new MusicToolForm().Show());
            menu.Items.Add("云端调休配置", null, (s, e) => new SubscriptionForm().ShowDialog()); menu.Items.Add(new ToolStripSeparator());
            var toggleMenuItem = new ToolStripMenuItem("切换系统状态"); toggleMenuItem.Click += (s, e) => { Program.ToggleGlobalSwitch(); UpdateUIState(); }; menu.Items.Add(toggleMenuItem); menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("完全退出系统", null, (s, e) => { trayIcon.Visible = false; Program.KillManualProcess(); try { foreach (var p in Process.GetProcessesByName("ffplay")) p.Kill(); } catch { } Environment.Exit(0); });
            menu.Opening += (s, e) => { toggleMenuItem.Text = Program.IsGlobalSwitchOn() ? "紧急停用广播系统" : "恢复广播系统运行"; }; trayIcon.ContextMenuStrip = menu;

            this.FormClosing += (s, e) => { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; this.Hide(); } };
            uiTimer = new System.Windows.Forms.Timer { Interval = 2000 }; uiTimer.Tick += (s, e) => UpdateUIState(); uiTimer.Start(); UpdateUIState();
        }

        private void BackupConfig()
        {
            using var sfd = new SaveFileDialog { Filter = "ZIP 压缩包 (*.zip)|*.zip", FileName = $"BroadcastBackup_{DateTime.Now:yyyyMMdd_HHmmss}.zip" };
            if (sfd.ShowDialog() == DialogResult.OK)
            {
                try
                {
                    if (File.Exists(sfd.FileName)) File.Delete(sfd.FileName);
                    ZipFile.CreateFromDirectory(Program.AppDataDir, sfd.FileName, CompressionLevel.Optimal, false);
                    MessageBox.Show("数据备份成功！\n此压缩包包含了您的所有排程、配置和本地铃声。", "备份成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex) { MessageBox.Show("备份失败: " + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            }
        }

        private void RestoreConfig()
        {
            using var ofd = new OpenFileDialog { Filter = "ZIP 压缩包 (*.zip)|*.zip" };
            if (ofd.ShowDialog() == DialogResult.OK)
            {
                if (MessageBox.Show("恢复操作将彻底覆盖您现有的所有排程配置和音频库！\n恢复成功后系统将自动重启。\n\n确定要继续吗？", "警告 - 数据覆盖", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                {
                    try
                    {
                        Program.KillManualProcess(); try { foreach (var p in Process.GetProcessesByName("ffplay")) p.Kill(); } catch { }
                        ZipFile.ExtractToDirectory(ofd.FileName, Program.AppDataDir, true);
                        MessageBox.Show("数据恢复成功！程序即将重新启动以加载新配置。", "恢复成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        Application.Restart(); Environment.Exit(0);
                    }
                    catch (Exception ex) { MessageBox.Show("恢复失败: 压缩包可能损坏或文件被占用。\n\n" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error); }
                }
            }
        }

        private void UpdateUIState()
        {
            bool isOn = Program.IsGlobalSwitchOn();
            if (isOn) { lblStatus.Text = "运行状态: 正常广播中"; lblStatus.ForeColor = UIHelper.Success; btnToggle.Text = "停用广播系统"; btnToggle.BackColor = UIHelper.Danger; btnToggle.ForeColor = Color.White; }
            else { lblStatus.Text = "运行状态: 已全局静音停用"; lblStatus.ForeColor = UIHelper.Danger; btnToggle.Text = "恢复系统运行"; btnToggle.BackColor = UIHelper.Success; btnToggle.ForeColor = Color.White; }
        }
    }

    public class MusicToolForm : Form
    {
        private TextBox txtName, txtPath; private ComboBox cmbType; private ListView lvMusic; private List<JsonObject> musicList = new List<JsonObject>(); private bool isUpdatingUI = false;
        public MusicToolForm()
        {
            this.AutoScaleMode = AutoScaleMode.Dpi; this.BackColor = Color.White; this.Text = "音乐库与流媒体资源管理"; this.Size = new Size(1150, 800); this.StartPosition = FormStartPosition.CenterScreen; this.Icon = Program.AppIcon;
            InitializeComponents(); LoadData();
        }

        private void InitializeComponents()
        {
            var pnlTop = new Panel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(15) }; var pnlBottom = new Panel { Dock = DockStyle.Bottom, Height = 90, Padding = new Padding(15) }; var pnlCenter = new Panel { Dock = DockStyle.Fill, Padding = new Padding(15, 0, 15, 0) };
            this.Controls.Add(pnlCenter); this.Controls.Add(pnlBottom); this.Controls.Add(pnlTop);
            var tlpTop = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1, RowCount = 2 }; tlpTop.RowStyles.Add(new RowStyle(SizeType.AutoSize)); tlpTop.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var actionFlow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Margin = new Padding(0, 0, 0, 10) };

            var btnFile = UIHelper.CreateAutoBtn("导入文件", Color.FromArgb(240, 240, 240), UIHelper.TextDark); btnFile.Click += (s, e) => BrowseFile(); actionFlow.Controls.Add(btnFile);
            var btnDir = UIHelper.CreateAutoBtn("导入整个文件夹", Color.FromArgb(240, 240, 240), UIHelper.TextDark); btnDir.Click += (s, e) => BrowseFolder(); actionFlow.Controls.Add(btnDir);
            var btnScanDef = UIHelper.CreateAutoBtn("扫描默认铃声库 (AppData/ring)", Color.FromArgb(240, 240, 240), UIHelper.TextDark); btnScanDef.Click += (s, e) => DoScan(Program.RingDir); actionFlow.Controls.Add(btnScanDef);
            var btnScanCust = UIHelper.CreateAutoBtn("扫描自定义目录...", Color.FromArgb(240, 240, 240), UIHelper.TextDark); btnScanCust.Click += (s, e) => { using var fbd = new FolderBrowserDialog { Description = "请选择要批量提取音频的文件夹" }; if (fbd.ShowDialog() == DialogResult.OK) DoScan(fbd.SelectedPath); }; actionFlow.Controls.Add(btnScanCust);
            var btnOpenRingDir = UIHelper.CreateAutoBtn("在资源管理器中打开默认库", Color.FromArgb(240, 240, 240), UIHelper.TextDark); btnOpenRingDir.Click += (s, e) => { Process.Start(new ProcessStartInfo(Program.RingDir) { UseShellExecute = true }); }; actionFlow.Controls.Add(btnOpenRingDir);

            tlpTop.Controls.Add(actionFlow, 0, 0);
            var gbForm = new GroupBox { Text = " 选中下方的资源后，在此处实时修改内容", Dock = DockStyle.Fill, Font = UIHelper.MainFont, Padding = new Padding(10) }; var tlpForm = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 6, RowCount = 1, Padding = new Padding(5) };
            tlpForm.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); tlpForm.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40F)); tlpForm.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); tlpForm.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60F)); tlpForm.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); tlpForm.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));

            tlpForm.Controls.Add(new Label { Text = "显示名称:", AutoSize = true, Anchor = AnchorStyles.Right, Margin = new Padding(0, 8, 5, 0) }, 0, 0); txtName = new TextBox { Dock = DockStyle.Fill, Margin = new Padding(0, 5, 15, 0) }; tlpForm.Controls.Add(txtName, 1, 0); txtName.TextChanged += RealTimeUpdate;
            tlpForm.Controls.Add(new Label { Text = "物理路径 / URL:", AutoSize = true, Anchor = AnchorStyles.Right, Margin = new Padding(0, 8, 5, 0) }, 2, 0); txtPath = new TextBox { Dock = DockStyle.Fill, Margin = new Padding(0, 5, 15, 0) }; tlpForm.Controls.Add(txtPath, 3, 0); txtPath.TextChanged += RealTimeUpdate;
            tlpForm.Controls.Add(new Label { Text = "类型:", AutoSize = true, Anchor = AnchorStyles.Right, Margin = new Padding(0, 8, 5, 0) }, 4, 0); cmbType = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(0, 5, 0, 0) }; cmbType.Items.AddRange(new string[] { "file", "folder", "hls", "web" }); cmbType.SelectedIndex = 0; cmbType.SelectedIndexChanged += RealTimeUpdate; tlpForm.Controls.Add(cmbType, 5, 0);

            gbForm.Controls.Add(tlpForm); tlpTop.Controls.Add(gbForm, 0, 1); pnlTop.Controls.Add(tlpTop);
            var gbList = new GroupBox { Text = " 系统音乐库列表 (自动连号，使用底部按钮控制顺序)", Dock = DockStyle.Fill, Font = UIHelper.MainFont }; lvMusic = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = true, MultiSelect = false, Margin = new Padding(10), HideSelection = false };
            lvMusic.Columns.Add("ID (自动)", 90); lvMusic.Columns.Add("显示名称", 280); lvMusic.Columns.Add("物理路径 / 网络源 URL", 550); lvMusic.Columns.Add("解析类型", 120);
            lvMusic.SelectedIndexChanged += (s, e) => { if (lvMusic.SelectedItems.Count > 0) { isUpdatingUI = true; var item = lvMusic.SelectedItems[0]; txtName.Text = item.SubItems[1].Text; txtPath.Text = item.SubItems[2].Text; cmbType.Text = item.SubItems[3].Text; isUpdatingUI = false; } else { isUpdatingUI = true; txtName.Text = ""; txtPath.Text = ""; cmbType.SelectedIndex = 0; isUpdatingUI = false; } };
            gbList.Controls.Add(lvMusic); pnlCenter.Controls.Add(gbList);

            var tlpBottom = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 }; tlpBottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F)); tlpBottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var flpLeft = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = new Padding(0), Anchor = AnchorStyles.Left | AnchorStyles.None };
            var btnTop = UIHelper.CreateAutoBtn("置于页首", Color.FromArgb(240, 240, 240), UIHelper.TextDark); btnTop.Click += (s, e) => MoveItemToExtreme(true); flpLeft.Controls.Add(btnTop); var btnUp = UIHelper.CreateAutoBtn("上移", Color.FromArgb(240, 240, 240), UIHelper.TextDark); btnUp.Click += (s, e) => MoveItem(-1); flpLeft.Controls.Add(btnUp); var btnDown = UIHelper.CreateAutoBtn("下移", Color.FromArgb(240, 240, 240), UIHelper.TextDark); btnDown.Click += (s, e) => MoveItem(1); flpLeft.Controls.Add(btnDown); var btnBottom = UIHelper.CreateAutoBtn("置于页尾", Color.FromArgb(240, 240, 240), UIHelper.TextDark); btnBottom.Click += (s, e) => MoveItemToExtreme(false); flpLeft.Controls.Add(btnBottom);
            var btnDel = UIHelper.CreateAutoBtn("删除选中", UIHelper.Danger, Color.White); btnDel.Margin = new Padding(25, 4, 4, 4); btnDel.Click += (s, e) => DeleteSelected(); flpLeft.Controls.Add(btnDel); var btnClearAll = UIHelper.CreateAutoBtn("清空列表", Color.FromArgb(240, 240, 240), UIHelper.TextGray); btnClearAll.Click += (s, e) => { if (MessageBox.Show("确定清空全部资源？", "确认", MessageBoxButtons.YesNo) == DialogResult.Yes) { musicList.Clear(); UpdateList(); } }; flpLeft.Controls.Add(btnClearAll);

            tlpBottom.Controls.Add(flpLeft, 0, 0);
            var btnSave = UIHelper.CreateAutoBtn("保存配置并应用到网页端", UIHelper.Primary, Color.White); btnSave.Padding = new Padding(20, 8, 20, 8); btnSave.Anchor = AnchorStyles.Right | AnchorStyles.None; btnSave.Click += (s, e) => SaveData(); tlpBottom.Controls.Add(btnSave, 1, 0);
            pnlBottom.Controls.Add(tlpBottom);
        }

        private void RealTimeUpdate(object sender, EventArgs e) { if (isUpdatingUI || lvMusic.SelectedItems.Count == 0) return; var item = lvMusic.SelectedItems[0]; item.SubItems[1].Text = txtName.Text.Trim(); item.SubItems[2].Text = txtPath.Text.Trim(); item.SubItems[3].Text = cmbType.Text; var obj = (JsonObject)item.Tag; obj["name"] = txtName.Text.Trim(); obj["path"] = txtPath.Text.Trim(); obj["type"] = cmbType.Text; }
        private void MoveItem(int direction) { if (lvMusic.SelectedItems.Count == 0) return; int idx = lvMusic.SelectedIndices[0]; if (idx + direction < 0 || idx + direction >= musicList.Count) return; var item = musicList[idx]; musicList.RemoveAt(idx); musicList.Insert(idx + direction, item); UpdateList(); lvMusic.Items[idx + direction].Selected = true; lvMusic.Focus(); }
        private void MoveItemToExtreme(bool toTop) { if (lvMusic.SelectedItems.Count == 0) return; int idx = lvMusic.SelectedIndices[0]; if (toTop && idx == 0) return; if (!toTop && idx == musicList.Count - 1) return; var item = musicList[idx]; musicList.RemoveAt(idx); if (toTop) musicList.Insert(0, item); else musicList.Add(item); UpdateList(); int newIdx = toTop ? 0 : musicList.Count - 1; lvMusic.Items[newIdx].Selected = true; lvMusic.Focus(); }
        private string MakeRelPath(string filepath) { string bDir = Program.AppDataDir.TrimEnd('\\', '/') + Path.DirectorySeparatorChar; string absPath = Path.GetFullPath(filepath); if (absPath.StartsWith(bDir, StringComparison.OrdinalIgnoreCase)) return absPath.Substring(bDir.Length).Replace('\\', '/'); return filepath.Replace('\\', '/'); }
        private void LoadData() { musicList.Clear(); var all = Program.ReadJsonl("music.jsonl"); foreach (var item in all) { if (item["_type"]?.ToString() != "settings" && item["id"] != null) musicList.Add(item.AsObject()); } UpdateList(); }
        private void SaveData() { try { var arr = new JsonArray(); for (int i = 0; i < musicList.Count; i++) { var item = musicList[i].DeepClone().AsObject(); item["id"] = (i + 1).ToString(); arr.Add(item); } Program.WriteJsonl("music.jsonl", arr); MessageBox.Show("配置保存成功！网页端数据已实时更新。", "成功", MessageBoxButtons.OK, MessageBoxIcon.Information); } catch (Exception ex) { MessageBox.Show("保存失败: " + ex.Message); } }
        private void UpdateList() { lvMusic.Items.Clear(); for (int i = 0; i < musicList.Count; i++) { var m = musicList[i]; string currentAutoId = (i + 1).ToString(); m["id"] = currentAutoId; var item = new ListViewItem(new[] { currentAutoId, m["name"]?.ToString(), m["path"]?.ToString(), m["type"]?.ToString() }) { Tag = m }; lvMusic.Items.Add(item); } if (lvMusic.Items.Count > 0 && lvMusic.SelectedItems.Count == 0) { lvMusic.Items[lvMusic.Items.Count - 1].Selected = true; lvMusic.Items[lvMusic.Items.Count - 1].EnsureVisible(); } }
        private void BrowseFile() { using var ofd = new OpenFileDialog { Filter = "音频/列表|*.mp3;*.wav;*.flac;*.m3u8;*.m3u|所有文件|*.*" }; if (ofd.ShowDialog() == DialogResult.OK) { string p = MakeRelPath(ofd.FileName); string n = Path.GetFileNameWithoutExtension(ofd.FileName); var obj = new JsonObject { ["name"] = n, ["path"] = p, ["type"] = "file" }; musicList.Add(obj); UpdateList(); lvMusic.Items[lvMusic.Items.Count - 1].Selected = true; } }
        private void BrowseFolder() { using var fbd = new FolderBrowserDialog(); if (fbd.ShowDialog() == DialogResult.OK) { string p = MakeRelPath(fbd.SelectedPath); string n = Path.GetFileName(fbd.SelectedPath) + " (目录)"; var obj = new JsonObject { ["name"] = n, ["path"] = p, ["type"] = "folder" }; musicList.Add(obj); UpdateList(); lvMusic.Items[lvMusic.Items.Count - 1].Selected = true; } }
        private void DeleteSelected() { if (lvMusic.SelectedItems.Count == 0) return; var selIds = lvMusic.SelectedItems.Cast<ListViewItem>().Select(i => ((JsonObject)i.Tag)["id"]?.ToString()).ToHashSet(); musicList.RemoveAll(m => selIds.Contains(m["id"]?.ToString())); UpdateList(); }
        private void DoScan(string targetDir)
        {
            if (!Directory.Exists(targetDir)) return; var exts = new HashSet<string> { ".mp3", ".wav", ".flac", ".m4a", ".aac" }; int added = 0, updated = 0;
            foreach (var fp in Directory.GetFileSystemEntries(targetDir).OrderBy(f => f))
            {
                string name = Path.GetFileName(fp); if (name.StartsWith(".")) continue; string storePath = MakeRelPath(fp); string entryName, entryType;
                if (Directory.Exists(fp)) { entryName = name + " (目录)"; entryType = "folder"; } else if (File.Exists(fp) && exts.Contains(Path.GetExtension(fp).ToLower())) { entryName = Path.GetFileNameWithoutExtension(fp); entryType = "file"; } else continue;
                var existing = musicList.FirstOrDefault(m => m["name"]?.ToString() == entryName);
                if (existing != null) { existing["path"] = storePath; existing["type"] = entryType; updated++; } else { musicList.Add(new JsonObject { ["name"] = entryName, ["path"] = storePath, ["type"] = entryType }); added++; }
            }
            UpdateList(); MessageBox.Show($"扫描完毕！\n\n- 新增资源: {added} 个\n- 更新覆盖: {updated} 个", "扫描完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }
}
