# Dreams of Bee - 音效素材放置说明

把音频文件放到本文件夹(Assets/Resources/SFX/),文件名与下方清单一致
(匹配不带扩展名的名字;支持 .wav / .ogg / .mp3)。
游戏启动时优先加载这里的文件;缺失的文件临时用程序生成的占位音代替,
并在 Console 输出一次缺失清单警告(缺失名单 = 你还需要提供的素材)。

导入建议:使用 Unity 默认 / 3D 导入预设,不要选 "2D" 导入预设 ——
2D 预设会把空间混合强制为 0,3D 空间音效将不再随距离衰减。

标注"循环"的音效请做成无缝循环(首尾相接),长度建议 >= 1 秒。

必需文件(26 个):
TakeOff.wav        起飞
Landing.wav        落地
FallStart.wav      开始下落
CrawlStep.wav      爬行脚步
WingBuzz.wav       翅膀振翅(循环)
FallWind.wav       下落风声(循环)
PickUp.wav         拾取
Drop.wav           放下
AimOn.wav          瞄准高亮获得
AimOff.wav         瞄准高亮丢失
SwitchClick.wav    开关拨动
DoorOpen.wav       开门
DoorClose.wav      关门
DoorSlideLoop.wav  滑门马达(循环)
DoorClunk.wav      门到位碰撞
DoorDeny.wav       上锁拒绝
DoorLock.wav       门上锁
DoorUnlock.wav     门解锁
CardSuccess.wav    刷卡成功
CardDeny.wav       刷卡被拒
TransitionWhoosh.wav 过渡开始
LevelConfirm.wav   关卡切换确认
UnloadFade.wav     关卡卸载淡出
RoomAmbient.wav    房间环境音(循环)
FlickerBuzz.wav    灯闪烁嗡鸣(循环)
FlickerCrackle.wav 灯断电噼啪
