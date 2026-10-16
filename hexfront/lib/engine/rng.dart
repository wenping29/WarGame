// 确定性随机源：mulberry32。状态可序列化 → 存档续跑不破坏一致性。
class SeededRNG {
  int _state;
  SeededRNG(int seed) : _state = seed & 0xFFFFFFFF;

  int get state => _state;
  set state(int v) => _state = v & 0xFFFFFFFF;

  int nextUint32() {
    _state = (_state + 0x6D2B79F5) & 0xFFFFFFFF;
    var t = _state;
    t = ((t ^ (t >> 15)) * (t | 1)) & 0xFFFFFFFF;
    t ^= (t + (((t ^ (t >> 7)) * (t | 61)) & 0xFFFFFFFF)) & 0xFFFFFFFF;
    return (t ^ (t >> 14)) & 0xFFFFFFFF;
  }

  double nextDouble() => nextUint32() / 4294967296.0;

  /// [lo, hi] 闭区间整数
  int nextInt(int lo, int hi) {
    if (hi < lo) return lo;
    final span = hi - lo + 1;
    return lo + (nextUint32() % span);
  }

  /// 判定：p 为 0..1 概率
  bool chance(double p) => nextDouble() < p;

  /// 抖动系数：1 ± amp（用于随机噪声，避免 0 导致除零）
  double jitter(double amp) => clampd(1.0 + (nextDouble() * 2 - 1) * amp, 0.05, 3.0);

  SeededRNG fork() => SeededRNG(nextUint32());

  static double clampd(double v, double lo, double hi) => v < lo ? lo : (v > hi ? hi : v);
}
