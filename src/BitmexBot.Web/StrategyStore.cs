using System.Text.Json;
using System.Text.Json.Serialization;
using BitmexBot.Core;
using Microsoft.Extensions.Options;

namespace BitmexBot.Web;

/// <summary>Strategies saved as one JSON file in the data folder.</summary>
public sealed class StrategyStore
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    readonly string _path;
    readonly Lock _gate = new();
    List<StrategyDefinition> _items;

    public StrategyStore(IOptions<AppOptions> app)
    {
        Directory.CreateDirectory(app.Value.DataDir);
        _path = Path.Combine(app.Value.DataDir, "strategies.json");
        _items = File.Exists(_path)
            ? JsonSerializer.Deserialize<List<StrategyDefinition>>(File.ReadAllText(_path), Json) ?? []
            : [Example];
    }

    public event Action<StrategyDefinition?, string>? Changed;   // (definition or null when deleted, id)

    public IReadOnlyList<StrategyDefinition> All { get { lock (_gate) return _items.ToList(); } }

    public StrategyDefinition Upsert(StrategyDefinition def)
    {
        _ = new RuleStrategy(def);   // throws ArgumentException on unknown indicators/operators before anything is saved
        Timeframes.Size(def.Timeframe);
        lock (_gate)
        {
            _items.RemoveAll(s => s.Id == def.Id);
            _items.Add(def);
            Save();
        }
        Changed?.Invoke(def, def.Id);
        return def;
    }

    public bool Delete(string id)
    {
        bool removed;
        lock (_gate) { removed = _items.RemoveAll(s => s.Id == id) > 0; if (removed) Save(); }
        if (removed) Changed?.Invoke(null, id);
        return removed;
    }

    void Save()
    {
        string tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_items, Json));
        File.Move(tmp, _path, overwrite: true);   // write-then-rename so a crash never leaves a half-written file
    }

    /// <summary>The old bot's EMA/MA cross, alert-only, so the app has something to show on first start.</summary>
    static StrategyDefinition Example => new()
    {
        Id = "oldbot01",
        Name = "Old bot EMA/MA cross (fixed)",
        Timeframe = "15m",
        Long = new("all", [new(new Operand { Ind = "ema", Period = 70 }, "crossesAbove", new Operand { Ind = "sma", Period = 80 })]),
        Short = new("all", [new(new Operand { Ind = "ema", Period = 100 }, "crossesBelow", new Operand { Ind = "sma", Period = 80 })]),
    };
}
