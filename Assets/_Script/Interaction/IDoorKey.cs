/// <summary>
/// 门钥匙身份接口(门的扩展口):读卡器(CardReader)用它对进门触发区的物体做归属判定,
/// 不再硬编码"只有 Card 组件才算刷卡" —— 未来任何"能开门的物件"(钥匙卡 / 徽章 / 其他钥匙物)
/// 只要实现本接口即可被读卡器识别,卡片只是它的第一个实现。
///
/// 归属模型(见 CardReader):
///   - LevelIndex:钥匙所属关卡与门的 LevelIndex 都 ≥ 0 时要求相等(卡不能带出本关乱刷);
///   - KeyId:与读卡器可选字段 requiredItemId 配对 —— 同一关两扇门需要两把不同钥匙分流时,
///     给钥匙配 Interactable.itemId、给对应读卡器填 requiredItemId 即可,其余内容零感知。
/// </summary>
public interface IDoorKey
{
    /// <summary>钥匙归属关卡(-1 = 不校验)。</summary>
    int LevelIndex { get; }

    /// <summary>钥匙身份 Id(空串 = 不参与身份校验)。</summary>
    string KeyId { get; }
}
