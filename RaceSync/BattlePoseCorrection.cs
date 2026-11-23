using System.Numerics;

namespace RaceSync;

/// 给远车的渲染位置叠一个有限偏移，只影响画面不影响物理
public sealed class BattlePoseCorrection
{
    // 14 米内完全不修正，碰撞判定继续用原生姿态
    public const float ExactRadiusMeters = 14.0f;
    // 32 米外修正量给满，中间按 smoothstep 过渡
    public const float FullBlendRadiusMeters = 32.0f;
    // 视觉偏移上限 1.15 米，再大就能看出错位
    public const float MaxErrorMeters = 1.15f;
    // 单次位移超过 24 米视为传送，清空历史
    public const float TeleportMeters = 24.0f;
    // 每帧把偏移衰减到 72%，跳变很快就会收回
    private const float Decay = 0.72f;

    private Vector3 _rawPosition;
    // 每 tick 的匀速度估计，用来区分匀速与跳变
    private Vector3 _velocityPerTick;
    // 当前累积的渲染偏移
    private Vector3 _offset;
    private bool _initialized;

    public void Clear()
    {
        _initialized = false;
        _velocityPerTick = Vector3.Zero;
        _offset = Vector3.Zero;
    }

    /// 返回要叠加到原生姿态上的渲染偏移，每帧调用一次
    public Vector3 OffsetFor(Vector3 nativePosition, Vector3 localNativePosition)
    {
        Vector3 change = nativePosition - _rawPosition;
        _rawPosition = nativePosition;
        // 首次采样或发生传送时只记录位置，不产生偏移
        if (!_initialized || change.LengthSquared() > TeleportMeters * TeleportMeters)
        {
            Clear();
            _initialized = true;
            return Vector3.Zero;
        }

        // 越远修正越强，14 到 32 米之间平滑过渡
        float distance = Vector3.Distance(nativePosition, localNativePosition);
        float blend = Math.Clamp((distance - ExactRadiusMeters) / (FullBlendRadiusMeters - ExactRadiusMeters), 0.0f, 1.0f);
        blend *= blend * (3.0f - 2.0f * blend);

        // 低通估计匀速位移，残差才是需要抹掉的跳变
        Vector3 excess = _velocityPerTick - change;
        _velocityPerTick += (change - _velocityPerTick) * 0.32f;
        if (blend <= 0.0f)
        {
            // 接触范围内清零，直接跟原生姿态
            _offset = Vector3.Zero;
            return Vector3.Zero;
        }

        // 小于 0.4 米的残差忽略，静止时不抖
        if (excess.LengthSquared() > 0.16f)
            _offset += LimitLength(excess, MaxErrorMeters) * 0.8f;
        // 每帧衰减一次并把长度压在上限内
        _offset = LimitLength(_offset * Decay, MaxErrorMeters);
        return _offset * blend;
    }

    // 超长时按比例缩到上限，方向不变
    private static Vector3 LimitLength(Vector3 vector, float maximum)
    {
        float lengthSquared = vector.LengthSquared();
        return lengthSquared > maximum * maximum ? vector * (maximum / MathF.Sqrt(lengthSquared)) : vector;
    }
}
