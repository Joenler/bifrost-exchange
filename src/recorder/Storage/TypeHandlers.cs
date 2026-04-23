using System.Data;
using System.Globalization;
using Dapper;

namespace Bifrost.Recorder.Storage;

/// <summary>
/// Dapper decimal handler: invariant-culture string round-trip. Keeps decimal
/// precision deterministic across hosts (de-DE vs en-US) — Arena lesson.
/// </summary>
public sealed class DecimalTypeHandler : SqlMapper.TypeHandler<decimal>
{
    public override void SetValue(IDbDataParameter parameter, decimal value)
        => parameter.Value = value.ToString(CultureInfo.InvariantCulture);

    public override decimal Parse(object value)
        => decimal.Parse((string)value, CultureInfo.InvariantCulture);
}

/// <summary>
/// Dapper bool handler: SQLite has no native boolean, so persist as 1/0 integers
/// and parse defensively on read.
/// </summary>
public sealed class BoolTypeHandler : SqlMapper.TypeHandler<bool>
{
    public override void SetValue(IDbDataParameter parameter, bool value)
        => parameter.Value = value ? 1L : 0L;

    public override bool Parse(object value)
        => Convert.ToBoolean(value);
}
