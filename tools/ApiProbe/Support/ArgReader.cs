using System.Globalization;

namespace HotspotMeter.ApiProbe.Support;

/// <summary>
/// 极简命令行参数读取。原型故意不引入任何参数解析库：
/// 依赖越少，验证结论越不会受第三方行为干扰。
/// </summary>
internal sealed class ArgReader
{
    private readonly string[] _args;

    public ArgReader(string[] args) => _args = args;

    public string Command => _args.Length > 0 ? _args[0].ToLowerInvariant() : string.Empty;

    public bool Has(string name) =>
        _args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

    public string? Get(string name)
    {
        for (var i = 0; i < _args.Length - 1; i++)
        {
            if (string.Equals(_args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return _args[i + 1];
            }
        }

        return null;
    }

    public int GetInt(string name, int fallback)
    {
        var raw = Get(name);
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;
    }

    public long GetLong(string name, long fallback)
    {
        var raw = Get(name);
        return long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;
    }

    public Guid? GetGuid(string name)
    {
        var raw = Get(name);
        return Guid.TryParse(raw, out var value) ? value : null;
    }
}