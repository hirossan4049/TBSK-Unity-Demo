using UnityEngine;

public class button : MonoBehaviour
{
    [SerializeField] private TBSKDemo tbskDemo;

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
        
        if (tbskDemo != null)
        {
            tbskDemo.PlayAudio();
        }
        else
        {
            Debug.LogError("Failed to create or find TBSKDemo!");
        }
    }
}
