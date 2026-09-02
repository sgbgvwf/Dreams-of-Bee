using UnityEngine;

/// <summary>3D 一次性音效:播放指定时长后自动销毁自己(PlaySfxAt 创建)。</summary>
public class OneShotAudio : MonoBehaviour
{
    private float endTime;

    public void Init(float duration)
    {
        endTime = Time.time + duration;
    }

    private void Update()
    {
        if (Time.time >= endTime) Destroy(gameObject);
    }
}
