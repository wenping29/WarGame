using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using HexFront.Core.Geometry;

namespace HexFront.Core.Services;

/// <summary>
/// 指令的线上/落盘形状：{op:"move", unit:"R1", path:[[q,r],…], target, zone:[q,r], mission}。
/// 与设计文档 WS 协议一致，也让 turns.jsonl 能被人和工具直接读。
/// </summary>
public static class CommandWire
{
    public static string OpName(CommandOp op) => op switch
    {
        CommandOp.Move => "move",
        CommandOp.Attack => "attack",
        CommandOp.Fire => "fire",
        CommandOp.Dig => "dig",
        CommandOp.Rest => "rest",
        CommandOp.AirMission => "air_mission",
        CommandOp.Halt => "halt",
        _ => "unknown",
    };

    public static CommandOp ParseOp(string? name) => name switch
    {
        "move" => CommandOp.Move,
        "attack" => CommandOp.Attack,
        "fire" => CommandOp.Fire,
        "dig" => CommandOp.Dig,
        "rest" => CommandOp.Rest,
        "air_mission" => CommandOp.AirMission,
        "halt" => CommandOp.Halt,
        // 认不出的动作要留在指令流里由校验器回 unknown_op，直接吞掉玩家就只会看到"什么都没发生"
        _ => CommandOp.Unknown,
    };

    /// <summary>宽松解析：unit/unitId、path 既收 [[q,r]] 也收 {q,r}；非对象载荷返回 null。</summary>
    public static Command? FromJson(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        var opStr = Str(el, "op");
        var op = ParseOp(opStr);

        var unit = Str(el, "unit") ?? Str(el, "unitId") ?? Str(el, "unit_id") ?? "";

        var path = new List<Hex>();
        if (el.TryGetProperty("path", out var pathEl) && pathEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in pathEl.EnumerateArray())
                if (ReadHex(p) is { } h) path.Add(h);
        }

        Hex? zone = null;
        if (el.TryGetProperty("zone", out var zoneEl) &&
            zoneEl.ValueKind is JsonValueKind.Array or JsonValueKind.Object)
            zone = ReadHex(zoneEl);

        return new Command
        {
            Op = op,
            OpRaw = opStr ?? "",
            UnitId = unit,
            Path = path,
            Target = Str(el, "target"),
            Zone = zone,
            Mission = Str(el, "mission") ?? "none",
        };
    }

    /// <summary>回显动作名：未知动作原样回显玩家写的那个词，拒令提示才对得上玩家实际提交的东西。</summary>
    public static string OpEcho(Command c) =>
        c.Op == CommandOp.Unknown ? (string.IsNullOrEmpty(c.OpRaw) ? "unknown" : c.OpRaw!) : OpName(c.Op);

    /// <summary>WS 状态包里的指令回显形状（与落盘同构，用字典便于塞进 object 载荷）。</summary>
    public static Dictionary<string, object?> ToMap(Command c) => new()
    {
        ["op"] = OpEcho(c),
        ["unit"] = c.UnitId,
        ["path"] = c.Path.Count > 0 ? c.Path.Select(h => new int[] { h.Q, h.R }).ToList() : null,
        ["target"] = c.Target,
        ["zone"] = c.Zone is Hex z ? new int[] { z.Q, z.R } : null,
        ["mission"] = c.Mission,
    };

    private static string? Str(JsonElement el, string name) =>
        el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static Hex? ReadHex(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.Array && el.GetArrayLength() >= 2 &&
            el[0].TryGetInt32(out var q) && el[1].TryGetInt32(out var r))
            return new Hex(q, r);
        if (el.ValueKind == JsonValueKind.Object &&
            el.TryGetProperty("q", out var qEl) && el.TryGetProperty("r", out var rEl) &&
            qEl.TryGetInt32(out var qv) && rEl.TryGetInt32(out var rv))
            return new Hex(qv, rv);
        return null;
    }

    /// <summary>让 Command 按上面的线上形状读写，而不是 STJ 默认的枚举序号 + 对象格。</summary>
    public sealed class WireConverter : JsonConverter<Command>
    {
        public override Command? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var doc = JsonDocument.ParseValue(ref reader);
            return FromJson(doc.RootElement);
        }

        public override void Write(Utf8JsonWriter writer, Command value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteString("op", OpEcho(value));
            writer.WriteString("unit", value.UnitId);
            if (value.Path.Count > 0)
            {
                writer.WriteStartArray("path");
                foreach (var h in value.Path) WriteHex(writer, h);
                writer.WriteEndArray();
            }
            if (value.Target != null) writer.WriteString("target", value.Target);
            if (value.Zone is Hex zone) WriteHex(writer, zone);
            writer.WriteString("mission", value.Mission);
            writer.WriteEndObject();
        }

        private static void WriteHex(Utf8JsonWriter writer, Hex h)
        {
            writer.WriteStartArray();
            writer.WriteNumberValue(h.Q);
            writer.WriteNumberValue(h.R);
            writer.WriteEndArray();
        }
    }
}
