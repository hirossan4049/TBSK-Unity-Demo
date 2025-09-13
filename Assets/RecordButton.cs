using UnityEngine;
using UnityEngine.UI;

public class RecordButton : MonoBehaviour
{
    private UnifiedTBSKReceiver receiver;  // TBSKReceiver → UnifiedTBSKReceiver に変更
    private Button button;
    private Text buttonText;
    private float lastToggleTime = -1f;
    [SerializeField] private float toggleDebounceSeconds = 0.2f;
    
    [Header("UI Settings")]
    [SerializeField] private string recordingText = "Stop Recording";
    [SerializeField] private string idleText = "Start Recording";
    [SerializeField] private Color recordingColor = Color.red;
    [SerializeField] private Color idleColor = Color.white;
    
    void Start()
    {
        // UnifiedTBSKReceiverを探す
        receiver = FindObjectOfType<UnifiedTBSKReceiver>();
        if (receiver == null)
        {
            // シーンに存在しない場合は自動で作成
            var go = new GameObject("TBSKReceiver");
            receiver = go.AddComponent<UnifiedTBSKReceiver>();
            // 自動作成時は、ボタンでのみ制御する
            receiver.DisableAutoStart();
            receiver.SetEnableKeyToggle(false);
            receiver.SetStopOnPause(false);
            receiver.SetStopOnFocusLoss(false);
            Debug.Log("UnifiedTBSKReceiver was missing; created a new one at runtime.");
        }
        else
        {
            // 既存レシーバーでもキーボード操作は無効化
            receiver.DisableAutoStart();
            receiver.SetEnableKeyToggle(false);
            receiver.SetStopOnPause(false);
            receiver.SetStopOnFocusLoss(false);
            // 初期状態は必ず停止状態に統一
            if (receiver.IsRecording)
            {
                receiver.StopRecording();
                Debug.Log("UnifiedTBSKReceiver was recording at start; forced to Stop for button control.");
            }
        }
        
        // UIコンポーネント取得
        button = GetComponent<Button>();
        buttonText = GetComponentInChildren<Text>();
        
        if (button == null)
        {
            Debug.LogError("Button component not found!");
            enabled = false;
            return;
        }
        
        // ボタンクリックイベント設定
        button.onClick.AddListener(OnClick);
        
        // 初期状態を設定
        UpdateButtonUI();
    }
    
    void Update()
    {
        // 録音状態が変わったらUIを更新
        UpdateButtonUI();
    }
    
    private void OnClick()
    {
        // 連打や二重発火の抑制
        if (lastToggleTime > 0 && Time.unscaledTime - lastToggleTime < toggleDebounceSeconds)
        {
            return;
        }
        lastToggleTime = Time.unscaledTime;

        if (receiver.IsRecording)
        {
            receiver.StopRecording();
        }
        else
        {
            receiver.StartRecording();
        }
        
        UpdateButtonUI();
    }
    
    private void UpdateButtonUI()
    {
        if (buttonText != null)
        {
            buttonText.text = receiver.IsRecording ? recordingText : idleText;
        }
        
        // ボタンの色を変更（Imageコンポーネントがある場合）
        var image = button.GetComponent<Image>();
        if (image != null)
        {
            image.color = receiver.IsRecording ? recordingColor : idleColor;
        }
        
        // デバッグ情報表示（オプション）
        if (receiver.IsRecording && receiver.IsDecoding)
        {
            // デコード中の表示など
            if (buttonText != null && receiver.IsDecoding)
            {
                buttonText.text = recordingText + " (Decoding...)";
            }
        }
    }
    
    void OnDestroy()
    {
        if (button != null)
        {
            button.onClick.RemoveListener(OnClick);
        }
    }
}
