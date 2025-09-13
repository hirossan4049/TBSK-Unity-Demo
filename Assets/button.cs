using UnityEngine;
using UnityEngine.UI;

public class button : MonoBehaviour
{
    [SerializeField] private TBSKDemo tbskDemo;
    [Header("Transmit UI")]
    [SerializeField] private InputField messageInput; // 入力テキストをここから取得
    [SerializeField] private bool regenerateBeforePlay = true; // 送信前に都度生成

    void Start()
    {
        // TBSKDemoを自動検索（インスペクタで設定されていない場合）
        if (tbskDemo == null)
        {
            tbskDemo = FindObjectOfType<TBSKDemo>();
        }
    }

    void Update()
    {

    }

    public void OnClick()
    {
        Debug.Log("Button Clicked");
        
        // TBSKDemoを再検索
        if (tbskDemo == null)
        {
            tbskDemo = FindObjectOfType<TBSKDemo>();
        }
        
        // 見つからない場合は新しく作成
        if (tbskDemo == null)
        {
            Debug.Log("TBSKDemo not found. Creating new TBSKDemo object.");
            GameObject tbskObj = new GameObject("TBSKDemoObject");
            tbskDemo = tbskObj.AddComponent<TBSKDemo>();
        }
        
        if (tbskDemo == null)
        {
            Debug.LogError("Failed to create or find TBSKDemo!");
            return;
        }

        // 入力ボックスがあれば、そのテキストを送信メッセージに反映
        if (messageInput != null)
        {
            var msg = messageInput.text ?? string.Empty;
            tbskDemo.SetMessageAndGenerate(msg);
        }
        else if (regenerateBeforePlay)
        {
            // 明示的な入力が無い場合でも最新メッセージで再生成（任意）
            tbskDemo.SetMessageAndGenerate(tbskDemo.GetCurrentMessage());
        }

        tbskDemo.PlayAudio();
    }
}
