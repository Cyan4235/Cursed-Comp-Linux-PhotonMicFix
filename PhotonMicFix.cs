using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Diagnostics;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime.Injection;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace PhotonMicFix;

[BepInPlugin("com.linuxfix.photonmicfix", "Photon Mic Fix for Linux", "2.2.1")]
public class PhotonMicFixPlugin : BasePlugin
{
    internal static new ManualLogSource Log;
    internal static PhotonMicFixPlugin Instance;
    internal static ConfigEntry<string> SourceName;

    // Bridge writer
    private static Process _parecProcess;
    private static Thread _bridgeThread;
    private static volatile bool _stopBridge;
    private static string _bridgeWritePath;
    private static string _rawFilePath;

    // Bridge constants (must match RecorderPatcher)
    private const int B_HEADER = 8;
    private const int B_RATE = 48000;
    private const int B_CHUNK_MS = 20;
    private const int B_CHUNKS = 25;
    private const int B_BPS = 4; // float32
    private const int B_SPK = B_RATE * B_CHUNK_MS / 1000;  // 960 samples/chunk
    private const int B_BPC = B_SPK * B_BPS;                // 3840 bytes/chunk

    public override void Load()
    {
        Log = base.Log;
        Instance = this;

        SourceName = Config.Bind("Bridge", "SourceName", "",
            "PulseAudio/PipeWire source name. Leave empty for default mic. Run 'pactl list short sources' to find yours.");

        Log.LogInfo("Photon Mic Fix v2.2.1 — AudioClip bridge (persistent)");

        LaunchBridge();

        ClassInjector.RegisterTypeInIl2Cpp<RecorderPatcher>();
        SpawnPatcher();
    }

    private void LaunchBridge()
    {
        // Create bridge file in /dev/shm (via Wine Z: drive mapping)
        _bridgeWritePath = Path.Combine("Z:\\dev\\shm", "cursed_companions_mic_bridge.raw");
        try
        {
            byte[] init = new byte[B_HEADER + B_BPC * B_CHUNKS];
            // Header: write_pos=0, sample_rate=48000
            BitConverter.GetBytes((uint)0).CopyTo(init, 0);
            BitConverter.GetBytes((uint)B_RATE).CopyTo(init, 4);
            using var fs = new FileStream(_bridgeWritePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
            fs.Write(init, 0, init.Length);
            Log.LogInfo($"Bridge file created: {_bridgeWritePath}");
        }
        catch (Exception ex)
        {
            Log.LogError($"Failed to create bridge file: {ex.Message}");
            return;
        }

        // Build parec command — no device flag = use default source
        string source = SourceName.Value?.Trim() ?? "";

        // Launch parec via bash, writing raw audio to a temp file instead of stdout redirect
        // (stdout redirect is broken under Wine/pressure-vessel)
        string rawPath = "Z:\\dev\\shm\\cursed_companions_parec_raw";
        string rawNative = "/dev/shm/cursed_companions_parec_raw";

        // Override PULSE_SERVER inside the bash command to escape pressure-vessel container
        string xdgRuntime = "/run/user/1000";
        string pulseServer = $"unix:{xdgRuntime}/pulse/native";

        // Write a launcher script to /dev/shm — avoids all quoting hell
        string launcherNative = "/dev/shm/cursed_companions_parec_launch.sh";
        string launcherWine = "Z:\\dev\\shm\\cursed_companions_parec_launch.sh";
        string deviceArg = string.IsNullOrEmpty(source) ? "" : $"--device='{source}'";

        string scriptContent =
            $"#!/bin/bash\n" +
            $"export PULSE_SERVER={pulseServer}\n" +
            $"# Host library paths for pressure-vessel container\n" +
            $"export LD_LIBRARY_PATH=/run/host/usr/lib:/run/host/usr/lib/x86_64-linux-gnu:/run/host/usr/lib64:/run/host/usr/lib/x86_64-linux-gnu/pulseaudio:/run/host/usr/lib/pulseaudio:/run/host/usr/lib64/pulseaudio:${{LD_LIBRARY_PATH:-}}\n" +
            $"# Try to find parec: native, host mount, or flatpak escape\n" +
            $"for p in /usr/bin/parec /run/host/usr/bin/parec; do\n" +
            $"  if [ -x \"$p\" ]; then\n" +
            $"    exec \"$p\" --raw --format=float32le --channels=1 --rate={B_RATE} {deviceArg} >> {rawNative} 2>{rawNative}.log\n" +
            $"  fi\n" +
            $"done\n" +
            $"if command -v flatpak-spawn >/dev/null 2>&1; then\n" +
            $"  exec flatpak-spawn --host parec --raw --format=float32le --channels=1 --rate={B_RATE} {deviceArg} >> {rawNative} 2>{rawNative}.log\n" +
            $"fi\n" +
            $"echo 'parec not found (tried /usr/bin, /run/host, flatpak-spawn)' > {rawNative}.log\n";

        try
        {
            File.WriteAllText(launcherWine, scriptContent);
        }
        catch (Exception ex)
        {
            Log.LogError($"Failed to write launcher script: {ex.Message}");
            return;
        }

        string bashCmd = $"chmod +x {launcherNative} && exec {launcherNative}";

        Log.LogInfo($"Launching bridge via script: {launcherNative}");

        // Pre-create the raw output file so C# thread can open it immediately
        try
        {
            using var fs = new FileStream(rawPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        }
        catch { }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "/bin/bash",
                Arguments = $"-c \"{bashCmd}\"",
                WorkingDirectory = "/tmp",
                UseShellExecute = false,
                RedirectStandardOutput = false,
                RedirectStandardError = false,
                CreateNoWindow = true
            };

            _parecProcess = Process.Start(psi);
            if (_parecProcess == null)
            {
                Log.LogError("Failed to start parec");
                return;
            }

            // Start writer thread that reads from the raw file
            _stopBridge = false;
            _rawFilePath = rawPath;
            _bridgeThread = new Thread(BridgeWriterLoop)
            {
                IsBackground = true,
                Name = "MicBridgeWriter"
            };
            _bridgeThread.Start();

            string srcDisplay = string.IsNullOrEmpty(source) ? "(default)" : source;
            Log.LogInfo($"Bridge started, source: {srcDisplay}");
        }
        catch (Exception ex)
        {
            Log.LogError($"Failed to launch parec: {ex.Message}");
        }
    }

    private static void BridgeWriterLoop()
    {
        try
        {
            // Wait a moment for parec to start writing
            Thread.Sleep(500);

            byte[] chunk = new byte[B_BPC];
            byte[] posBytes = new byte[4];
            uint writePos = 0;

            using var rawFs = new FileStream(_rawFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var bridgeFs = new FileStream(_bridgeWritePath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);

            PhotonMicFixPlugin.Log?.LogInfo("[Bridge] Writer thread started, reading from raw file");

            while (!_stopBridge)
            {
                // Read exactly one chunk from the raw parec output file
                int bytesRead = 0;
                int retries = 0;
                while (bytesRead < B_BPC && !_stopBridge)
                {
                    int n = rawFs.Read(chunk, bytesRead, B_BPC - bytesRead);
                    if (n <= 0)
                    {
                        // No data yet — parec may not have written yet, or it's between chunks
                        retries++;
                        if (retries > 500) // ~5 seconds of no data
                        {
                            // Read parec's stderr log for diagnostics
                            try
                            {
                                string errLogWine = _rawFilePath + ".log";
                                if (File.Exists(errLogWine))
                                {
                                    string errText = File.ReadAllText(errLogWine).Trim();
                                    if (!string.IsNullOrEmpty(errText))
                                        PhotonMicFixPlugin.Log?.LogError($"[Bridge] parec stderr: {errText}");
                                }
                            }
                            catch { }
                            PhotonMicFixPlugin.Log?.LogError("[Bridge] No data from parec after 5s, stopping");
                            return;
                        }
                        Thread.Sleep(10);
                        continue;
                    }
                    bytesRead += n;
                    retries = 0;
                }
                if (_stopBridge) break;

                // Write chunk to ring buffer slot
                int slot = (int)(writePos % B_CHUNKS);
                int offset = B_HEADER + slot * B_BPC;
                bridgeFs.Seek(offset, SeekOrigin.Begin);
                bridgeFs.Write(chunk, 0, B_BPC);

                // Update write position (after data, so reader never reads partial)
                writePos++;
                BitConverter.GetBytes(writePos).CopyTo(posBytes, 0);
                bridgeFs.Seek(0, SeekOrigin.Begin);
                bridgeFs.Write(posBytes, 0, 4);
                bridgeFs.Flush();
            }
        }
        catch (Exception ex)
        {
            PhotonMicFixPlugin.Log?.LogError($"[Bridge] Writer thread error: {ex.Message}");
        }
    }

    internal static void KillBridge()
    {
        _stopBridge = true;
        try
        {
            if (_parecProcess != null && !_parecProcess.HasExited)
            {
                _parecProcess.Kill();
                Log.LogInfo("Bridge process stopped");
            }
        }
        catch { }

        // Clean up bridge files
        try
        {
            if (_bridgeWritePath != null && File.Exists(_bridgeWritePath))
                File.Delete(_bridgeWritePath);
            if (_rawFilePath != null && File.Exists(_rawFilePath))
                File.Delete(_rawFilePath);
            // Also clean up launcher script and log
            string launcherWine = "Z:\\dev\\shm\\cursed_companions_parec_launch.sh";
            string logWine = _rawFilePath + ".log";
            if (File.Exists(launcherWine)) File.Delete(launcherWine);
            if (File.Exists(logWine)) File.Delete(logWine);
        }
        catch { }
    }

    internal static void SpawnPatcher()
    {
        // Check if one already exists
        var existing = UnityEngine.Object.FindObjectOfType<RecorderPatcher>();
        if (existing != null) return;

        var go = new GameObject("PhotonMicFix_Persistent");
        GameObject.DontDestroyOnLoad(go);
        go.hideFlags = HideFlags.HideAndDontSave;
        go.AddComponent<RecorderPatcher>();
        Log.LogInfo("Patcher spawned");
    }
}

public class RecorderPatcher : MonoBehaviour
{
    private const int HEADER_SIZE = 8;
    private const int CHUNK_DURATION_MS = 20;
    private const int BUFFER_CHUNKS = 25;
    private const int BYTES_PER_SAMPLE = 4;
    private const string SHM_BRIDGE_FILENAME = "cursed_companions_mic_bridge.raw";
    private const int CLIP_SECONDS = 1;
    private const int LEAD_SAMPLES = 960;

    private enum Phase { WaitForRecorder, WaitForGame, Inject, Running, Failed }
    private Phase _phase = Phase.WaitForRecorder;

    private Photon.Voice.Unity.Recorder _recorder = null;
    private float _timer = 0f;
    private int _scanCount = 0;
    private int _gameWaitCount = 0;

    // Bridge
    private string _bridgeFilePath;
    private int _sampleRate = 48000;
    private int _samplesPerChunk, _bytesPerChunk, _totalBridgeSize;
    private byte[] _fileBuffer;
    private uint _lastReadPos = 0;
    private bool _bridgeConnected = false;

    // AudioClip
    private AudioClip _clip = null;
    private int _clipTotalSamples;
    private int _writeOffset = 0;
    private float _startTime = 0f;
    private Il2CppStructArray<float> _il2cppWriteBuf = null;
    private float[] _managedReadBuf;
    private MethodInfo _setDataMethod = null;

    // Guard
    private float _reEnableTimer = 0f;
    private int _reEnableCount = 0;

    // Monitor
    private float _monitorTimer = 0f;
    private int _monitorCount = 0;
    private int _totalChunksWritten = 0;
    private float _peakLevel = 0f;
    private int _zeroReadCount = 0;

    void Awake() => PhotonMicFixPlugin.Log.LogInfo("RecorderPatcher ready (v2.2.1)");

    void Update()
    {
        _timer += Time.deltaTime;

        switch (_phase)
        {
            case Phase.WaitForRecorder:
                if (_timer < 2f) return;
                _timer = 0f;
                _scanCount++;
                TryFindRecorder();
                break;

            case Phase.WaitForGame:
                if (_timer < 1f) return;
                _timer = 0f;
                _gameWaitCount++;
                WaitForGameSetup();
                break;

            case Phase.Inject:
                DoInject();
                break;

            case Phase.Running:
                RunningUpdate();
                break;

            case Phase.Failed:
                // Retry after a while
                if (_timer > 10f)
                {
                    _timer = 0f;
                    _phase = Phase.WaitForRecorder;
                    _scanCount = 0;
                    PhotonMicFixPlugin.Log.LogInfo("Retrying after failure...");
                }
                break;
        }

        _monitorTimer += Time.deltaTime;
        if (_monitorTimer >= 5f && _phase == Phase.Running)
        {
            _monitorTimer = 0f;
            Monitor();
        }
    }

    void OnApplicationQuit()
    {
        PhotonMicFixPlugin.KillBridge();
    }

    // Respawn if destroyed
    void OnDestroy()
    {
        PhotonMicFixPlugin.Log.LogWarning("Patcher destroyed! Respawning...");
        // Schedule respawn on next frame
        try { PhotonMicFixPlugin.SpawnPatcher(); }
        catch { }
    }

    // ===== PHASE 1 =====

    private void TryFindRecorder()
    {
        try
        {
            var rec = UnityEngine.Object.FindObjectOfType<Photon.Voice.Unity.Recorder>();
            if (rec == null)
            {
                if (_scanCount <= 5) PhotonMicFixPlugin.Log.LogInfo($"Scan #{_scanCount}: No Recorder...");
                return;
            }
            _recorder = rec;
            PhotonMicFixPlugin.Log.LogWarning("Recorder found!");
            if (!InitBridge()) { _phase = Phase.Failed; return; }
            _phase = Phase.WaitForGame;
            _gameWaitCount = 0;
        }
        catch (Exception ex) { PhotonMicFixPlugin.Log.LogError($"TryFindRecorder: {ex.Message}"); }
    }

    // ===== PHASE 2 =====

    private void WaitForGameSetup()
    {
        try
        {
            // Check recorder still exists
            if (_recorder == null)
            {
                PhotonMicFixPlugin.Log.LogWarning("Recorder gone during wait — restarting");
                _phase = Phase.WaitForRecorder;
                _scanCount = 0;
                return;
            }

            bool tx = _recorder.TransmitEnabled;
            bool rec = _recorder.RecordingEnabled;

            if (_gameWaitCount <= 10 || _gameWaitCount % 10 == 0)
                PhotonMicFixPlugin.Log.LogInfo(
                    $"WaitForGame #{_gameWaitCount}: Tx={tx} Rec={rec} Src={_recorder.SourceType}");

            if (rec || tx || _gameWaitCount > 30)
            {
                if (_gameWaitCount > 30)
                    PhotonMicFixPlugin.Log.LogWarning("Timeout — injecting anyway");
                else
                    PhotonMicFixPlugin.Log.LogWarning($"Game ready! Tx={tx} Rec={rec} — injecting");
                _phase = Phase.Inject;
            }
        }
        catch (Exception ex) { PhotonMicFixPlugin.Log.LogError($"WaitForGame: {ex.Message}"); }
    }

    // ===== PHASE 3 =====

    private void DoInject()
    {
        try
        {
            _clipTotalSamples = _sampleRate * CLIP_SECONDS;
            _clip = AudioClip.Create("BridgeMic", _clipTotalSamples, 1, _sampleRate, false);
            if (_clip == null) { _phase = Phase.Failed; return; }

            // Pre-fill silence
            int fillSize = 4800;
            var silence = new Il2CppStructArray<float>(fillSize);
            for (int pos = 0; pos < _clipTotalSamples; pos += fillSize)
            {
                int count = Math.Min(fillSize, _clipTotalSamples - pos);
                Il2CppStructArray<float> buf = (count == fillSize) ? silence : new Il2CppStructArray<float>(count);
                ClipSetData(buf, pos);
            }

            _il2cppWriteBuf = new Il2CppStructArray<float>(_samplesPerChunk);
            _managedReadBuf = new float[_samplesPerChunk * BUFFER_CHUNKS];
            _writeOffset = LEAD_SAMPLES;

            // RE-SYNC bridge position to current
            byte[] hdr = new byte[HEADER_SIZE];
            using (var fs = new FileStream(_bridgeFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                fs.Read(hdr, 0, HEADER_SIZE);
            _lastReadPos = BitConverter.ToUInt32(hdr, 0);
            PhotonMicFixPlugin.Log.LogInfo($"Bridge re-synced to pos={_lastReadPos}");

            ApplyRecorderSettings();

            _startTime = Time.time;
            _totalChunksWritten = 0;
            _reEnableCount = 0;
            _zeroReadCount = 0;
            _phase = Phase.Running;

            PhotonMicFixPlugin.Log.LogWarning(
                $"RUNNING! Tx={_recorder.TransmitEnabled} Rec={_recorder.RecordingEnabled} Src={_recorder.SourceType}");
        }
        catch (Exception ex)
        {
            PhotonMicFixPlugin.Log.LogError($"DoInject: {ex}");
            _phase = Phase.Failed;
        }
    }

    private void ApplyRecorderSettings()
    {
        _recorder.RecordingEnabled = false;
        _recorder.GetType().GetProperty("samplingRate").SetValue(_recorder, 48000);
        _recorder.SourceType = Photon.Voice.Unity.Recorder.InputSourceType.AudioClip;
        _recorder.AudioClip = _clip;
        _recorder.LoopAudioClip = true;
        _recorder.DebugEchoMode = false;
        _recorder.VoiceDetection = false;
        _recorder.UseMicrophoneTypeFallback = false;
        _recorder.TransmitEnabled = true;
        _recorder.RecordingEnabled = true;
        _recorder.RestartRecording();
    }

    // ===== PHASE 4 =====

    private void RunningUpdate()
    {
        if (_clip == null || !_bridgeConnected) return;

        // Check recorder still valid
        try
        {
            if (_recorder == null)
            {
                PhotonMicFixPlugin.Log.LogWarning("Recorder gone — restarting");
                _phase = Phase.WaitForRecorder;
                _scanCount = 0;
                _clip = null;
                return;
            }
        }
        catch
        {
            _phase = Phase.WaitForRecorder;
            _scanCount = 0;
            _clip = null;
            return;
        }

        // Guard: re-enable if game turned us off
        _reEnableTimer += Time.deltaTime;
        if (_reEnableTimer >= 0.5f)
        {
            _reEnableTimer = 0f;
            try
            {
                bool needFix = !_recorder.TransmitEnabled || !_recorder.RecordingEnabled ||
                    _recorder.SourceType != Photon.Voice.Unity.Recorder.InputSourceType.AudioClip;

                if (needFix)
                {
                    _reEnableCount++;
                    if (_reEnableCount <= 20 || _reEnableCount % 20 == 0)
                        PhotonMicFixPlugin.Log.LogWarning(
                            $"Re-enable #{_reEnableCount}: Tx={_recorder.TransmitEnabled} " +
                            $"Rec={_recorder.RecordingEnabled} Src={_recorder.SourceType}");
                    ApplyRecorderSettings();
                    _startTime = Time.time;
                    _writeOffset = LEAD_SAMPLES;

                    // Re-sync bridge
                    byte[] hdr = new byte[HEADER_SIZE];
                    using (var fs = new FileStream(_bridgeFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        fs.Read(hdr, 0, HEADER_SIZE);
                    _lastReadPos = BitConverter.ToUInt32(hdr, 0);
                }
            }
            catch { }
        }

        PumpBridgeToClip();
    }

    private void PumpBridgeToClip()
    {
        try
        {
            int samplesRead = ReadBridgeChunks(_managedReadBuf);
            if (samplesRead <= 0)
            {
                _zeroReadCount++;
                return;
            }

            int srcOffset = 0;
            while (srcOffset < samplesRead)
            {
                int count = Math.Min(_samplesPerChunk, samplesRead - srcOffset);

                Il2CppStructArray<float> buf;
                if (count == _samplesPerChunk)
                    buf = _il2cppWriteBuf;
                else
                    buf = new Il2CppStructArray<float>(count);

                for (int i = 0; i < count; i++)
                {
                    float s = _managedReadBuf[srcOffset + i];
                    // Clamp to prevent clipping
                    if (s > 1.0f) s = 1.0f;
                    else if (s < -1.0f) s = -1.0f;
                    buf[i] = s;
                    float abs = s < 0 ? -s : s;
                    if (abs > _peakLevel) _peakLevel = abs;
                }

                int spaceToEnd = _clipTotalSamples - _writeOffset;
                if (count <= spaceToEnd)
                {
                    ClipSetData(buf, _writeOffset);
                    _writeOffset += count;
                }
                else
                {
                    if (spaceToEnd > 0)
                    {
                        var p1 = new Il2CppStructArray<float>(spaceToEnd);
                        for (int i = 0; i < spaceToEnd; i++) p1[i] = buf[i];
                        ClipSetData(p1, _writeOffset);
                    }
                    int rem = count - spaceToEnd;
                    if (rem > 0)
                    {
                        var p2 = new Il2CppStructArray<float>(rem);
                        for (int i = 0; i < rem; i++) p2[i] = buf[spaceToEnd + i];
                        ClipSetData(p2, 0);
                    }
                    _writeOffset = count - spaceToEnd;
                }

                if (_writeOffset >= _clipTotalSamples)
                    _writeOffset %= _clipTotalSamples;

                srcOffset += count;
                _totalChunksWritten++;
            }
        }
        catch (Exception ex) { PhotonMicFixPlugin.Log.LogError($"Pump: {ex.Message}"); }
    }

    // ===== SETDATA =====

    private void ClipSetData(Il2CppStructArray<float> data, int offset)
    {
        if (_setDataMethod == null)
        {
            _setDataMethod = typeof(AudioClip).GetMethod("SetData",
                new[] { typeof(Il2CppStructArray<float>), typeof(int) });
            if (_setDataMethod == null)
            {
                foreach (var m in typeof(AudioClip).GetMethods())
                {
                    if (m.Name == "SetData" && m.GetParameters().Length == 2 &&
                        m.GetParameters()[1].ParameterType == typeof(int))
                    {
                        _setDataMethod = m;
                        break;
                    }
                }
            }
            PhotonMicFixPlugin.Log.LogInfo($"SetData resolved: {_setDataMethod?.Name ?? "FAILED"}");
        }
        _setDataMethod?.Invoke(_clip, new object[] { data, offset });
    }

    // ===== BRIDGE =====

    private string FindBridgeFile()
    {
        string shmPath = Path.Combine("Z:\\dev\\shm", SHM_BRIDGE_FILENAME);
        if (File.Exists(shmPath)) return shmPath;
        var gameDir = Path.GetDirectoryName(BepInEx.Paths.BepInExRootPath);
        string gamePath = Path.Combine(gameDir, "mic_bridge.raw");
        if (File.Exists(gamePath)) return gamePath;
        return null;
    }

    private bool InitBridge()
    {
        _bridgeFilePath = FindBridgeFile();
        if (_bridgeFilePath == null)
        {
            PhotonMicFixPlugin.Log.LogError("Bridge not found! Is parec running?");
            return false;
        }
        byte[] header = new byte[HEADER_SIZE];
        using (var fs = new FileStream(_bridgeFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            fs.Read(header, 0, HEADER_SIZE);
        _sampleRate = (int)BitConverter.ToUInt32(header, 4);
        if (_sampleRate <= 0 || _sampleRate > 96000) return false;
        _samplesPerChunk = _sampleRate * CHUNK_DURATION_MS / 1000;
        _bytesPerChunk = _samplesPerChunk * BYTES_PER_SAMPLE;
        _totalBridgeSize = HEADER_SIZE + _bytesPerChunk * BUFFER_CHUNKS;
        _fileBuffer = new byte[_totalBridgeSize];
        _lastReadPos = BitConverter.ToUInt32(header, 0);
        _bridgeConnected = true;
        PhotonMicFixPlugin.Log.LogInfo($"Bridge: rate={_sampleRate}, chunk={_samplesPerChunk}smp");
        return true;
    }

    private int ReadBridgeChunks(float[] destBuffer)
    {
        if (!_bridgeConnected) return 0;
        try
        {
            using var fs = new FileStream(_bridgeFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            int toRead = Math.Min(_totalBridgeSize, (int)fs.Length);
            if (toRead < HEADER_SIZE) return 0;
            fs.Read(_fileBuffer, 0, toRead);
            uint writePos = BitConverter.ToUInt32(_fileBuffer, 0);
            if (writePos == _lastReadPos) return 0;
            int samplesRead = 0;
            uint rp = _lastReadPos;
            int maxChunks = BUFFER_CHUNKS;
            while (rp != writePos && maxChunks-- > 0)
            {
                int chunkIndex = (int)(rp % BUFFER_CHUNKS);
                int byteOffset = HEADER_SIZE + chunkIndex * _bytesPerChunk;
                for (int i = 0; i < _samplesPerChunk && (samplesRead + i) < destBuffer.Length; i++)
                    destBuffer[samplesRead + i] = BitConverter.ToSingle(_fileBuffer, byteOffset + i * BYTES_PER_SAMPLE);
                samplesRead += _samplesPerChunk;
                rp++;
            }
            _lastReadPos = writePos;
            return samplesRead;
        }
        catch { return 0; }
    }

    // ===== MONITOR =====

    private void Monitor()
    {
        _monitorCount++;
        try
        {
            if (_recorder == null) return;
            float elapsed = Time.time - _startTime;
            long estRead = _clipTotalSamples > 0 ? ((long)(elapsed * _sampleRate)) % _clipTotalSamples : 0;
            long lead = _writeOffset - estRead;
            if (lead < 0) lead += _clipTotalSamples;
            float leadMs = _sampleRate > 0 ? (lead * 1000f) / _sampleRate : 0;

            PhotonMicFixPlugin.Log.LogInfo(
                $"[Mon#{_monitorCount}] W={_writeOffset} Lead={leadMs:F0}ms " +
                $"Chunks={_totalChunksWritten} Peak={_peakLevel:F3} Zero={_zeroReadCount} " +
                $"Tx={_recorder.TransmitEnabled} Rec={_recorder.RecordingEnabled} " +
                $"Src={_recorder.SourceType} ReEn={_reEnableCount}");
            _peakLevel = 0f;
            _zeroReadCount = 0;
        }
        catch { }
    }
}
