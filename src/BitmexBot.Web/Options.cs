namespace BitmexBot.Web;

public sealed class ExchangeOptions
{
    /// <summary>Which exchange adapter to run. "paper" is the built-in simulator; real exchanges plug in as IExchange adapters.</summary>
    public string Name { get; set; } = "paper";
    /// <summary>Use the exchange's testnet unless explicitly set to false. Mainnet trades real money.</summary>
    public bool Testnet { get; set; } = true;
    public string ApiKey { get; set; } = "";
    public string ApiSecret { get; set; } = "";
    public double MakerFee { get; set; } = 0.0002;
    public double TakerFee { get; set; } = 0.00075;
    public double PaperStartPrice { get; set; } = 60000;
    public double PaperStartBalanceBtc { get; set; } = 1;
}

public sealed class RiskOptions
{
    /// <summary>Largest absolute position (and single order) in contracts.</summary>
    public int MaxContracts { get; set; } = 1000;
    public int MaxOrdersPerMinute { get; set; } = 20;
    /// <summary>Auto trading switches off when equity falls this many percent below the day's starting equity (UTC). 0 disables.</summary>
    public double DailyLossLimitPct { get; set; } = 5;
}

public sealed class AppOptions
{
    /// <summary>When set, every /api and /hub request must send it as a bearer token (or ?access_token= for the websocket).</summary>
    public string AccessToken { get; set; } = "";
    public string DataDir { get; set; } = "data";
}
