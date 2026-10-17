using System;

namespace HexFront.Core.Services;

/// <summary>
/// 确定性随机源：mulberry32。状态是 uint 且可序列化，所以存档续跑不会破坏一致性。
/// 全引擎只用这一个实例（GameState.Rng），任何 new Random() 都会让同种子对拍失效。
/// </summary>
public sealed class SeededRng
{
    private uint _state;

    public SeededRng(uint seed) => _state = seed;

    public uint State
    {
        get => _state;
        set => _state = value;
    }

    public uint NextUInt32()
    {
        unchecked
        {
            _state += 0x6D2B79F5;
            var t = _state;
            t = (t ^ (t >> 15)) * (t | 1);
            t ^= t + ((t ^ (t >> 7)) * (t | 61));
            return t ^ (t >> 14);
        }
    }

    /// <summary>[0,1) 均匀分布：除以 2^32，与 Dart 版逐位一致。</summary>
    public double NextDouble() => NextUInt32() / 4294967296.0;

    /// <summary>[lo, hi] 闭区间整数，对齐 Dart nextInt。</summary>
    public int NextInt(int lo, int hi)
    {
        if (hi < lo) return lo;
        var span = (ulong)(hi - lo + 1);
        return lo + (int)(NextUInt32() % span);
    }

    /// <summary>概率判定：p 取 0..1。</summary>
    public bool Chance(double p) => NextDouble() < p;

    /// <summary>抖动系数 1±amp，下限 0.05 保证不出现 0 导致的除零。</summary>
    public double Jitter(double amp)
    {
        var v = 1.0 + (NextDouble() * 2 - 1) * amp;
        if (v < 0.05) return 0.05;
        return v > 3.0 ? 3.0 : v;
    }

    /// <summary>派生独立子流（用于不影响主局对拍序列的旁路噪声）。</summary>
    public SeededRng Fork() => new SeededRng(NextUInt32());

    public SeededRng Clone() => new SeededRng(_state);
}
