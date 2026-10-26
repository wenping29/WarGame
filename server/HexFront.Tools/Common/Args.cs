namespace HexFront.Tools.Common;

/// <summary>
/// 极简命令行解析器，镜像 Python argparse 的常见用法：
///   --key value       单值
///   --key v1 v2 v3    多值（贪婪读到下一个 '-' 开头的 token 为止）
///   --flag            布尔（无值）
/// 也支持单横线 -key。位置参数另存。
/// </summary>
public sealed class Args
{
    private readonly Dictionary<string, List<string>> _opts =
        new(StringComparer.Ordinal);
    private readonly List<string> _pos = new();

    public Args(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a.StartsWith("--"))
            {
                Collect(a.Substring(2), args, ref i);
            }
            else if (a.StartsWith("-") && a.Length > 1)
            {
                Collect(a.Substring(1), args, ref i);
            }
            else
            {
                _pos.Add(a);
            }
        }
    }

    private void Collect(string key, string[] args, ref int i)
    {
        var vals = new List<string>();
        while (i + 1 < args.Length && !args[i + 1].StartsWith("-"))
        {
            vals.Add(args[++i]);
        }
        _opts[key] = vals;
    }

    public bool Has(string k) => _opts.ContainsKey(k);

    /// <summary>第一个值，或 null。</summary>
    public string? Get(string k) =>
        _opts.TryGetValue(k, out var v) && v.Count > 0 ? v[0] : null;

    /// <summary>全部值（无则空列表）。</summary>
    public IReadOnlyList<string> GetList(string k) =>
        _opts.TryGetValue(k, out var v) ? v : Array.Empty<string>();

    public string Get(string k, string d) => Get(k) ?? d;

    public int Int(string k, int d)
    {
        var v = Get(k);
        return v == null ? d : int.Parse(v, System.Globalization.CultureInfo.InvariantCulture);
    }

    public double Dbl(string k, double d)
    {
        var v = Get(k);
        return v == null ? d : double.Parse(v, System.Globalization.CultureInfo.InvariantCulture);
    }

    public bool Bool(string k) => Has(k);

    public IReadOnlyList<string> Positional => _pos;
}
