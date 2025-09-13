// Assets/KokolinkUnityAudio.cs
// Unity 2021+ / .NET 4.x
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using UnityEngine;
using jp.nyatla.kokolink.types;
using jp.nyatla.kokolink.utils.wavefile;
using jp.nyatla.kokolink.utils.math;
using jp.nyatla.kokolink.utils;

namespace jp.nyatla.kokolink.io.audioif
{
    /// <summary>
    /// Unity上でのAudio再生用実装。NAudioPlayerのUnity版。
    /// - Raw PCM(byte[]) を float[-1,1] に変換し、AudioClipに格納して再生
    /// - 非MonoBehaviourだが、内部でDontDestroyOnLoadのGameObject/AudioSourceを生成
    /// </summary>
    public class UnityAudioPlayer : IAudioPlayer, IDisposable
    {
        private class PlayerHost : MonoBehaviour { }

        private GameObject _go;
        private AudioSource _source;
        private AudioClip _clip;

        private bool _disposed;

        public UnityAudioPlayer(PcmData src, int device_no = -1)
            : this(src.Data, src.SampleBits, (int)src.Framerate, 1, device_no) { }

        public UnityAudioPlayer(IEnumerable<byte> data, int samplebits, int framerate, int channels, int device_no = -1)
        {
            if (channels != 1 && channels != 2)
            {
                throw new NotImplementedException("channelsは1または2のみ対応");
            }
            if (samplebits != 8 && samplebits != 16)
            {
                throw new NotImplementedException("samplebitsは8または16のみ対応");
            }

            // Host生成
            _go = new GameObject("KokolinkUnityAudioPlayer");
            UnityEngine.Object.DontDestroyOnLoad(_go);
            _go.AddComponent<PlayerHost>();
            _source = _go.AddComponent<AudioSource>();
            _source.playOnAwake = false;

            // PCM -> float[]
            var bytes = data.ToArray();
            int bytesPerSample = samplebits / 8;
            int totalSamples = bytes.Length / bytesPerSample / channels;

            var floatBuf = new float[totalSamples * channels];
            if (samplebits == 8)
            {
                // Unsigned 8bit PCM (0..255) => float(-1..1)
                int idx = 0;
                for (int i = 0; i < bytes.Length; i++)
                {
                    floatBuf[idx++] = (bytes[i] - 128) / 128f;
                }
            }
            else
            {
                // 16bit little-endian => float(-1..1)
                int idx = 0;
                for (int i = 0; i < bytes.Length; i += 2)
                {
                    short s = (short)(bytes[i] | (bytes[i + 1] << 8));
                    floatBuf[idx++] = Mathf.Clamp(s / 32768f, -1f, 1f);
                }
            }

            // クリップ作成・データ設定
            // name, lengthSamples, channels, frequency, stream=false
            _clip = AudioClip.Create("Kokolink_PCM", totalSamples, channels, framerate, false);
            _clip.SetData(floatBuf, 0);

            _source.clip = _clip;
        }

        public void Play()
        {
            EnsureNotDisposed();
            if (_source.isPlaying) return;
            _source.Play();
        }

        public void Stop()
        {
            if (_disposed) return;
            if (!_source.isPlaying) return;
            _source.Stop();
        }

        public void Wait()
        {
            EnsureNotDisposed();
            // 注意：メインスレッドでのブロッキングは非推奨。必要なら別スレッドで呼ぶか、コルーチンを推奨。
            while (_source != null && _source.isPlaying)
            {
                Thread.Sleep(50);
            }
        }

        public void Close() => Dispose();

        public void Dispose()
        {
            if (_disposed) return;
            try
            {
                Stop();
                if (_clip != null)
                {
                    UnityEngine.Object.Destroy(_clip);
                    _clip = null;
                }
                if (_go != null)
                {
                    UnityEngine.Object.Destroy(_go);
                    _go = null;
                }
            }
            finally
            {
                _disposed = true;
            }
        }

        private void EnsureNotDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(UnityAudioPlayer));
        }
    }

    /// <summary>
    /// Unity上でのマイク入力取得クラス。NAudioInputIteratorのUnity版。
    /// - Microphone.Startで録音リングバッファを作成
    /// - バックグラウンドスレッドで新規サンプルを取得し、BlockingCollection<double>へ供給
    /// - GetRms()で直近のRMSを取得
    /// </summary>
    public class UnityMicInputIterator : IAudioInputIterator, IDisposable
    {
        private abstract class SampleQ
        {
            private readonly Rms _rms;
            protected readonly BlockingCollection<double> _q;

            protected SampleQ(int sample_rate)
            {
                _q = new BlockingCollection<double>(sample_rate);
                _rms = new Rms(Math.Max(sample_rate / 100, 10));
            }

            public double Take()
            {
                if (!_q.TryTake(out var ret, 3000))
                    throw new PyStopIteration();
                return ret;
            }

            public abstract void Puts(float[] f32, int count, int channelStride);

            protected void Put(double v)
            {
                while (!_q.TryAdd(v, 0))
                {
                    _q.TryTake(out _, 0);
                }
                lock (_rms)
                {
                    _rms.Update(v);
                }
            }

            public double Rms { get { lock (_rms) { return _rms.GetLastRms(); } } }
        }

        private class SampleQ16AsFloat : SampleQ
        {
            public SampleQ16AsFloat(int sample_rate) : base(sample_rate) { }
            public override void Puts(float[] f32, int count, int channelStride)
            {
                // UnityのGetDataはfloat(-1..1)を返す。doubleへ昇格して格納。
                // channelStride=1(モノラル)想定。ステレオの場合はLのみ使用に簡略化（必要ならミックスへ変更）。
                for (int i = 0; i < count; i += channelStride)
                {
                    base.Put(f32[i]);
                }
            }
        }

        private class ReaderHost : MonoBehaviour { }

        private GameObject _go;
        private AudioClip _clip;
        private string _deviceName;
        private int _sampleRate;
        private int _channels;
        private volatile bool _running;
        private Thread _readerThread;

        private SampleQ _q;
        private bool _playNowFlag;

        // 読み出しポインタ（AudioClip内サンプルインデックス）
        private int _readPos;
        
        // AudioClipのサンプル数をキャッシュ（メインスレッドでのみ取得）
        private int _totalSamples;

        public static IList<(int id, string name)> GetDevices()
        {
            var list = new List<(int, string)>();
            var devs = Microphone.devices;
            // UnityのMicrophoneはIDは持たないので、便宜上indexを返す
            for (int i = 0; i < devs.Length; i++)
            {
                list.Add((i, devs[i]));
            }
            // 互換目的で-1=デフォルト扱い
            list.Insert(0, (-1, devs.Length > 0 ? devs[0] : "Default"));
            return list;
        }

        /// <param name="framerate">サンプリングレート</param>
        /// <param name="bits_par_sample">8/16 指定は保持のみ（Unityは内部float）。RMSやAPI整合のため引数は受ける。</param>
        /// <param name="device_no">-1はデフォルト。0以降はMicrophone.devicesのインデックス。</param>
        public UnityMicInputIterator(int framerate = 8000, int bits_par_sample = 16, int device_no = -1)
        {
            _sampleRate = framerate;
            _channels = 1; // モノラル固定（必要なら拡張）
            _deviceName = ResolveDeviceName(device_no);

            // Host生成
            _go = new GameObject("KokolinkUnityMicInput");
            UnityEngine.Object.DontDestroyOnLoad(_go);
            _go.AddComponent<ReaderHost>();

            // Queue（RMSウィンドウはRmsに依存）
            _q = new SampleQ16AsFloat(framerate);
        }

        public void Start()
        {
            if (_running) throw new InvalidOperationException("Already started.");

            // lengthSecはリングバッファ長（1～2秒程度推奨）
            int lengthSec = Math.Max(1, _sampleRate >= 48000 ? 2 : 1);

            _clip = Microphone.Start(_deviceName, true, lengthSec, _sampleRate);
            if (_clip == null) throw new InvalidOperationException("Microphone.Start failed.");

            // 開始まで待機
            while (Microphone.GetPosition(_deviceName) <= 0) { }

            // メインスレッドでサンプル数を取得してキャッシュ
            _totalSamples = _clip.samples;
            
            _readPos = 0;
            _running = true;
            _playNowFlag = true;

            _readerThread = new Thread(ReadLoop) { IsBackground = true, Name = "UnityMicReader" };
            _readerThread.Start();
        }

        public void Stop()
        {
            if (!_running) return;
            _running = false;
            _playNowFlag = false;

            try { _readerThread?.Join(500); } catch { /* ignore */ }

            if (_clip != null)
            {
                Microphone.End(_deviceName);
                UnityEngine.Object.Destroy(_clip);
                _clip = null;
            }
        }

        public double Next()
        {
            if (!_playNowFlag) throw new InvalidOperationException();
            return _q.Take();
        }

        public double GetRms() => _q.Rms;

        public void Close() => Dispose();

        public void Dispose()
        {
            Stop();
            if (_go != null)
            {
                UnityEngine.Object.Destroy(_go);
                _go = null;
            }
        }

        private void ReadLoop()
        {
            var buf = new float[_sampleRate * _channels]; // 1秒分最大
            // キャッシュされたサンプル数を使用（メインスレッドで取得済み）
            int totalSamples = _totalSamples;

            while (_running && _clip != null)
            {
                try
                {
                    int writePos = Microphone.GetPosition(_deviceName);
                    if (writePos < 0) { Thread.Sleep(5); continue; } // デバイス未準備

                    int available = (writePos >= _readPos)
                        ? (writePos - _readPos)
                        : (totalSamples - _readPos + writePos); // ラップアラウンド

                    // 小さく区切って読む（バースト抑制）
                    int chunk = Math.Min(available, Math.Min(buf.Length, _sampleRate / 10)); // 最大100msぶん
                    if (chunk > 0)
                    {
                        // ラップを意識して2回に分けて取得する場合あり
                        int first = Math.Min(chunk, totalSamples - _readPos);
                        _clip.GetData(buf, _readPos);
                        _q.Puts(buf, first * _channels, _channels);

                        if (chunk > first)
                        {
                            _clip.GetData(buf, 0);
                            _q.Puts(buf, (chunk - first) * _channels, _channels);
                        }

                        _readPos = (_readPos + chunk) % totalSamples;
                    }
                }
                catch
                {
                    // デバイス変化など一過性の失敗はスキップ
                }
                Thread.Sleep(5);
            }
        }

        private static string ResolveDeviceName(int device_no)
        {
            var devs = Microphone.devices;
            if (devs == null || devs.Length == 0)
                return null; // OSデフォルト
            if (device_no < 0 || device_no >= devs.Length)
                return devs[0];
            return devs[device_no];
        }
    }
}
