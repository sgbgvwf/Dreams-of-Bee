/// <summary>
/// 门钥匙身份接口(门的扩展口):读卡器(CardReader)用它对进门触发区的物体做身份判定,
/// 不再硬编码"只有 Card 组件才算刷卡" —— 未来任何"能开门的物件"(钥匙卡 / 徽章 / 其他钥匙物)
/// 只要实现本接口即可被读卡器识别,卡片只是它的第一个实现。
///
/// 身份模型(见 CardReader):
///   - KeyId:与读卡器可选字段 requiredItemId 配对 —— 读卡器指定了 Id 就只认这把钥匙。
///
/// 没有"归属关卡"成员:关卡物件随穿门卸载销毁,钥匙天然带不出关;刷卡去哪由钥匙上的
/// 目的地数据决定(Card 携带,见 Card.cs),不依赖关卡序号。
/// </summary>
public interface IDoorKey
{
    /// <summary>钥匙身份 Id(空串 = 不参与身份校验)。</summary>
    string KeyId { get; }
}
