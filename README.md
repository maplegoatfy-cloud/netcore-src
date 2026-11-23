# 联机同步核心 C# 源码

这是我使用项目内残余的C#源码和剩下的GD源码重构的一版联机核心，你可能需要自行修改下接口才能适应游戏

`BattlePacketBuffer` 这个类用于固定接收652字节的状态包，收到包时调用`Push`，但受限于wmmt6的enma核心限制，这个只能以60hz去同步，然后每过60个游戏刻（60hz）就会调用一次`Poll` ； 两个方法使用同一单调时间，单位是ms

`Po1l`向调用的对象提供的集合追加到房内玩家多一条包，可以重用的集合会在调用”Po11’之前清空内容

`DirectBattleDatagram`用于解编码游戏内四个人的UDPW6R2报文，数据包为700字节，探测和应答为48字节

`BattlePoseCorrection`给远车渲染位置加有限的平滑偏移，每个车（对象）独立持有一个实例，位置单位是米，接触范围使用emna核心同步的车辆碰撞箱

```sh
dotnet run --project RaceSync.Checks
```
