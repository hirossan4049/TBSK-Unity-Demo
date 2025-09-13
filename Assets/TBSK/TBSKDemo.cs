using System.Collections.Generic;
using System.IO;
using UnityEngine;
using jp.nyatla.tbaskmodem;
using jp.nyatla.kokolink.utils.wavefile;
using jp.nyatla.kokolink.io.audioif;

public class TBSKDemo : MonoBehaviour
{
    [SerializeField] private bool playAudioOnStart = false; // ボタンで再生するのでデフォルトはfalse
    [SerializeField] private KeyCode playKey = KeyCode.Space;
    [SerializeField] private string messageToSend = "12345432;12345432;12345432;12345432;12345432;12345432"; // 送信する文字列
    
    private PcmData generatedPcm;
    private UnityAudioPlayer audioPlayer;

    void Start()
    {
        GenerateAudio();
        
        if (playAudioOnStart)
        {
            PlayAudio();
        }
    }

    void Update()
    {
        if (Input.GetKeyDown(playKey))
        {
            PlayAudio();
        }
    }

    void GenerateAudio()
    {
        var tone = TbskTone.CreateXPskSin(10, 10).Mul(0.5);
        var carrier = 8000;
        var mod = new TbskModulator(tone);

        // 文字列をビットデータに変換
        var payload = StringToBits(messageToSend);

        var src_pcm = new List<double>(mod.ModulateAsBit(payload));
        generatedPcm = new PcmData(src_pcm, 16, carrier);

        // WAVファイルも保存
        var filePath = System.IO.Path.Combine(Application.persistentDataPath, "tbsk_message.wav");
        using (var stream = File.Open(filePath, FileMode.Create, FileAccess.Write))
        {
            PcmData.Dump(generatedPcm, stream);
        }

        Debug.Log($"Message to send: '{messageToSend}'");
        Debug.Log($"Converted to {payload.Count} bits");
        Debug.Log($"WAV file saved to: {filePath}");
        Debug.Log($"Press {playKey} to play audio or use PlayAudio() method");
    }

    // 文字列をビットの配列に変換する
    private List<int> StringToBits(string text)
    {
        var bits = new List<int>();
        
        // 各文字をUTF-8バイトに変換
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        
        foreach (byte b in bytes)
        {
            // 各バイトを8ビットに展開（MSBファースト）
            for (int i = 7; i >= 0; i--)
            {
                bits.Add((b >> i) & 1);
            }
        }
        
        return bits;
    }

    public void PlayAudio()
    {
        // オーディオがまだ生成されていない場合は生成する
        if (generatedPcm == null)
        {
            Debug.Log("Audio not generated yet. Generating now...");
            GenerateAudio();
        }

        if (generatedPcm == null)
        {
            Debug.LogError("Failed to generate audio");
            return;
        }

        // 既存のプレイヤーを停止・破棄
        if (audioPlayer != null)
        {
            audioPlayer.Stop();
            audioPlayer.Dispose();
        }

        // 新しいプレイヤーを作成して再生
        audioPlayer = new UnityAudioPlayer(generatedPcm);
        audioPlayer.Play();

        Debug.Log("Playing TBSK modulated audio");
    }

    public void StopAudio()
    {
        if (audioPlayer != null)
        {
            audioPlayer.Stop();
        }
    }

    // 文字列を設定して新しいオーディオを生成
    public void SetMessageAndGenerate(string newMessage)
    {
        messageToSend = newMessage;
        GenerateAudio();
        Debug.Log($"New message set: '{newMessage}'");
    }

    // 現在の送信メッセージを取得
    public string GetCurrentMessage()
    {
        return messageToSend;
    }

    void OnDestroy()
    {
        if (audioPlayer != null)
        {
            audioPlayer.Dispose();
        }
    }
}
