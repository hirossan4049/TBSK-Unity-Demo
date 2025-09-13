using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using jp.nyatla.tbaskmodem;
using jp.nyatla.kokolink.utils.math;

/// <summary>
/// 統合版TBSKレシーバー
/// Unity Microphone APIを直接使用し、非ブロッキングで効率的にTBSK信号をデコード
/// </summary>
public class UnifiedTBSKReceiver : MonoBehaviour
{
    [Header("Recording Settings")]
    [SerializeField] private bool startRecordingOnStart = false;
    [SerializeField] private KeyCode recordKey = KeyCode.R;
    [SerializeField] private int sampleRate = 8000;
    [SerializeField] private string deviceName = null; // null = default microphone
    
    [Header("Controls")]
    [SerializeField] private bool enableKeyToggle = false; // キーボードRでトグル（デフォルト無効）
    
    [Header("Performance Settings")]
    [SerializeField] private int ringBufferSeconds = 2; // リングバッファの長さ（秒）
    [SerializeField] private int processChunkSize = 800; // 一度に処理するサンプル数（0.1秒分）
    [SerializeField] private float decodeThresholdSeconds = 0.5f; // デコード開始閾値（秒）
    [SerializeField] private bool useAsyncDecode = true; // 非同期デコードを使用

    [Header("Decode Triggering")]
    [SerializeField] private bool decodeOnSilence = true; // サイレンス検出でデコード
    [SerializeField] private float silenceRmsThreshold = 0.2f; // サイレンス判定RMS
    [SerializeField] private float silenceHoldTime = 0.4f; // 連続サイレンス時間
    [SerializeField] private float minBufferSeconds = 0.8f; // デコードに必要な最小バッファ秒
    
    [Header("Debug")]
    [SerializeField] private bool showDebugInfo = false;
    [SerializeField] private float currentRMS = 0f;
    
    private AudioClip micClip;
    private TbskDemodulator demodulator;
    private List<double> audioBuffer;
    private readonly object bufferLock = new object();
    
    private bool isRecording = false;
    private bool isDecoding = false;
    private int lastReadPosition = 0;
    private float[] tempBuffer;
    private float silenceTimer = 0f;
    
    // RMS計算用
    private Rms rmsCalculator;
    
    // パフォーマンス監視
    private int samplesProcessed = 0;
    private float lastProcessTime = 0f;

    // 直近の復調メッセージ（UI等から参照可能）
    [Header("Output")]
    [SerializeField] private string lastDecodedMessage = string.Empty;
    public string LastDecodedMessage => lastDecodedMessage;
    public event System.Action<string> MessageDecoded; // 外部が購読してUI更新等に使用
    
    void Start()
    {
        InitializeComponents();
        LogAvailableDevices();
        
        if (startRecordingOnStart)
        {
            StartRecording();
        }
    }
    
    private void InitializeComponents()
    {
        try
        {
            // TBSKデモジュレーター初期化
            var tone = TbskTone.CreateXPskSin(10, 10).Mul(0.5);
            demodulator = new TbskDemodulator(tone);
            
            // バッファ初期化
            audioBuffer = new List<double>();
            tempBuffer = new float[processChunkSize];
            
            // RMS計算機初期化
            rmsCalculator = new Rms(Math.Max(sampleRate / 100, 10));
            
            if (enableKeyToggle)
            {
                Debug.Log($"Unified TBSK Receiver initialized. Press {recordKey} to toggle recording.");
            }
            else
            {
                Debug.Log("Unified TBSK Receiver initialized.");
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"Failed to initialize: {ex.Message}");
        }
    }
    
    private void LogAvailableDevices()
    {
        var devices = Microphone.devices;
        Debug.Log($"Found {devices.Length} microphone(s):");
        for (int i = 0; i < devices.Length; i++)
        {
            Debug.Log($"  [{i}]: {devices[i]}");
        }
        
        if (string.IsNullOrEmpty(deviceName) && devices.Length > 0)
        {
            deviceName = devices[0];
            Debug.Log($"Using default device: {deviceName}");
        }
    }
    
    void Update()
    {
        // 録音トグル
        if (enableKeyToggle && Input.GetKeyDown(recordKey))
        {
            if (isRecording)
                StopRecording();
            else
                StartRecording();
        }
        
        // 録音中の処理
        if (isRecording && micClip != null)
        {
            ProcessMicrophoneData();
        }
        
        // デバッグ情報更新
        if (showDebugInfo)
        {
            currentRMS = (float)(rmsCalculator?.GetLastRms() ?? 0.0);
        }

        // サイレンス検出でデコードをトリガー
        if (decodeOnSilence && isRecording)
        {
            // 現在のRMS（0に近いほど静か）
            var rms = (float)(rmsCalculator?.GetLastRms() ?? 0.0);
            if (rms < silenceRmsThreshold)
            {
                silenceTimer += Time.deltaTime;
            }
            else
            {
                silenceTimer = 0f; // 音が来ているのでリセット
            }

            // 十分静かで、ある程度バッファが溜まっていればデコード
            int bufSize = BufferSize;
            if (!isDecoding && silenceTimer >= silenceHoldTime && bufSize >= (int)(sampleRate * minBufferSeconds))
            {
                if (useAsyncDecode)
                {
                    StartAsyncDecode();
                }
                else
                {
                    TryDecodeSync();
                }
                silenceTimer = 0f; // 次のメッセージに備える
            }
        }
    }
    
    private void ProcessMicrophoneData()
    {
        // 現在の書き込み位置を取得
        int writePos = Microphone.GetPosition(deviceName);
        if (writePos < 0) return; // まだ録音開始していない
        
        int totalSamples = micClip.samples;
        
        // 利用可能なサンプル数を計算
        int availableSamples;
        if (writePos >= lastReadPosition)
        {
            availableSamples = writePos - lastReadPosition;
        }
        else
        {
            // ラップアラウンド
            availableSamples = (totalSamples - lastReadPosition) + writePos;
        }
        
        // 処理するサンプル数を制限
        int samplesToProcess = Math.Min(availableSamples, processChunkSize);
        
        if (samplesToProcess > 0)
        {
            // 一時バッファのサイズ調整
            if (tempBuffer.Length < samplesToProcess)
            {
                tempBuffer = new float[samplesToProcess];
            }
            
            // データ取得
            micClip.GetData(tempBuffer, lastReadPosition);
            
            // double配列に変換してバッファに追加
            lock (bufferLock)
            {
                for (int i = 0; i < samplesToProcess; i++)
                {
                    double sample = tempBuffer[i];
                    audioBuffer.Add(sample);
                    
                    // RMS更新
                    if (rmsCalculator != null)
                    {
                        rmsCalculator.Update(sample);
                    }
                }
                
                samplesProcessed += samplesToProcess;
            }
            
            // 読み取り位置を更新
            lastReadPosition = (lastReadPosition + samplesToProcess) % totalSamples;
            
            // 旧しきい値ベースのトリガー（サイレンス検出を使わない場合のみ）
            if (!decodeOnSilence)
            {
                int bufferSize;
                lock (bufferLock)
                {
                    bufferSize = audioBuffer.Count;
                }

                if (bufferSize >= (int)(sampleRate * decodeThresholdSeconds) && !isDecoding)
                {
                    if (useAsyncDecode)
                    {
                        StartAsyncDecode();
                    }
                    else
                    {
                        TryDecodeSync();
                    }
                }
            }
        }
        
        // パフォーマンス監視
        if (showDebugInfo && Time.time - lastProcessTime > 1.0f)
        {
            Debug.Log($"[TBSK] Processed {samplesProcessed} samples/sec, Buffer: {audioBuffer.Count}, RMS: {currentRMS:F3}");
            samplesProcessed = 0;
            lastProcessTime = Time.time;
        }
    }
    
    private async void StartAsyncDecode()
    {
        if (isDecoding) return;
        
        isDecoding = true;
        
        // バッファのコピーを作成
        List<double> bufferCopy;
        lock (bufferLock)
        {
            bufferCopy = new List<double>(audioBuffer);
            audioBuffer.Clear();
        }
        
        try
        {
            // バックグラウンドでデコード
            var result = await Task.Run(() => DecodeBuffer(bufferCopy));
            
            if (!string.IsNullOrEmpty(result))
            {
                // メインスレッドで結果を表示
                Debug.Log($"[DECODED] {result}");
                OnMessageDecoded(result);
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"Async decode error: {ex.Message}");
        }
        finally
        {
            isDecoding = false;
        }
    }
    
    private void TryDecodeSync()
    {
        if (isDecoding) return;
        
        isDecoding = true;
        
        List<double> bufferCopy;
        lock (bufferLock)
        {
            bufferCopy = new List<double>(audioBuffer);
            audioBuffer.Clear();
        }
        
        try
        {
            string result = DecodeBuffer(bufferCopy);
            if (!string.IsNullOrEmpty(result))
            {
                Debug.Log($"[DECODED] {result}");
                OnMessageDecoded(result);
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"Decode error: {ex.Message}");
        }
        finally
        {
            isDecoding = false;
        }
    }
    
    private string DecodeBuffer(List<double> buffer)
    {
        try
        {
            var bitsEnumerable = demodulator.DemodulateAsBit(buffer);
            
            if (bitsEnumerable != null)
            {
                var bits = new List<int>();
                foreach (var bit in bitsEnumerable)
                {
                    bits.Add(bit);
                }
                
                if (bits.Count > 0)
                {
                    return BitsToString(bits);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"Demodulation failed: {ex.Message}");
        }
        
        return null;
    }
    
    private string BitsToString(List<int> bits)
    {
        // 8ビット境界に調整
        if (bits.Count % 8 != 0)
        {
            int padding = 8 - (bits.Count % 8);
            for (int i = 0; i < padding; i++)
            {
                bits.Add(0);
            }
        }
        
        var bytes = new List<byte>();
        for (int i = 0; i < bits.Count; i += 8)
        {
            byte b = 0;
            for (int j = 0; j < 8 && i + j < bits.Count; j++)
            {
                b |= (byte)((bits[i + j] & 1) << (7 - j));
            }
            bytes.Add(b);
        }
        
        try
        {
            return Encoding.UTF8.GetString(bytes.ToArray()).TrimEnd('\0');
        }
        catch
        {
            // UTF-8デコード失敗時は16進表示
            var sb = new StringBuilder();
            foreach (byte b in bytes)
            {
                sb.Append($"{b:X2} ");
            }
            return $"[HEX] {sb.ToString().Trim()}";
        }
    }
    
    public void StartRecording()
    {
        if (isRecording) return;
        
        try
        {
            // マイク録音開始
            micClip = Microphone.Start(deviceName, true, ringBufferSeconds, sampleRate);
            
            if (micClip == null)
            {
                Debug.LogError("Failed to start microphone");
                return;
            }
            
            // 録音開始を待つ
            while (Microphone.GetPosition(deviceName) <= 0) { }
            
            isRecording = true;
            isDecoding = false;
            lastReadPosition = 0;
            samplesProcessed = 0;
            
            lock (bufferLock)
            {
                audioBuffer.Clear();
            }
            
            Debug.Log($"Started recording from {deviceName} at {sampleRate}Hz");
        }
        catch (Exception ex)
        {
            Debug.LogError($"Failed to start recording: {ex.Message}");
        }
    }
    
    public void StopRecording()
    {
        if (!isRecording) return;
        
        try
        {
            isRecording = false;
            
            // マイク停止
            if (!string.IsNullOrEmpty(deviceName))
            {
                Microphone.End(deviceName);
            }
            
            // クリップ破棄
            if (micClip != null)
            {
                Destroy(micClip);
                micClip = null;
            }
            
            // 残りのバッファをデコード
            lock (bufferLock)
            {
                if (audioBuffer.Count > 0 && !isDecoding)
                {
                    TryDecodeSync();
                }
            }
            
            Debug.Log("Stopped recording");
        }
        catch (Exception ex)
        {
            Debug.LogError($"Error stopping recording: {ex.Message}");
        }
    }
    
    /// <summary>
    /// メッセージがデコードされた時のイベント
    /// </summary>
    protected virtual void OnMessageDecoded(string message)
    {
        // メインスレッドコンテキストで呼ばれる想定（StartAsyncDecodeのawait継続/同期デコード）
        lastDecodedMessage = message ?? string.Empty;
        try { MessageDecoded?.Invoke(lastDecodedMessage); } catch { /* listener side errors ignored */ }
    }
    
    // プロパティ
    public bool IsRecording => isRecording;
    public bool IsDecoding => isDecoding;
    public float CurrentRMS => currentRMS;
    public int BufferSize => audioBuffer?.Count ?? 0;
    
    void OnDestroy()
    {
        StopRecording();
    }
    
    [Header("Lifecycle")]
    [SerializeField] private bool stopOnPause = false; // 一時停止時に停止
    [SerializeField] private bool stopOnFocusLoss = false; // フォーカス喪失時に停止

    void OnApplicationPause(bool pauseStatus)
    {
        if (pauseStatus && stopOnPause)
        {
            StopRecording();
        }
    }
    
    void OnApplicationFocus(bool hasFocus)
    {
        if (!hasFocus && isRecording && stopOnFocusLoss)
        {
            StopRecording();
        }
    }

    // 外部制御用の公開メソッド
    public void DisableAutoStart() { startRecordingOnStart = false; }
    public void SetEnableKeyToggle(bool enable) { enableKeyToggle = enable; }
    public void SetStopOnPause(bool enable) { stopOnPause = enable; }
    public void SetStopOnFocusLoss(bool enable) { stopOnFocusLoss = enable; }
}

/// <summary>
/// RMS（Root Mean Square）計算用クラス
/// </summary>
public class Rms
{
    private readonly double[] _window;
    private readonly int _windowSize;
    private int _index;
    private double _sum;
    private bool _filled;
    
    public Rms(int windowSize)
    {
        _windowSize = windowSize;
        _window = new double[windowSize];
        _index = 0;
        _sum = 0;
        _filled = false;
    }
    
    public void Update(double value)
    {
        double squared = value * value;
        
        if (_filled)
        {
            _sum -= _window[_index];
        }
        
        _window[_index] = squared;
        _sum += squared;
        
        _index = (_index + 1) % _windowSize;
        if (_index == 0)
        {
            _filled = true;
        }
    }
    
    public double GetLastRms()
    {
        int count = _filled ? _windowSize : _index;
        if (count == 0) return 0;
        
        return Math.Sqrt(_sum / count);
    }
}
