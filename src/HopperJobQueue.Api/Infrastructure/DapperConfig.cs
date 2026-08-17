using System.Data;
using Dapper;

namespace HopperJobQueue.Api.Infrastructure;

public static class DapperConfig
{
    private static bool _configured;

    public static void Configure()
    {
        if (_configured)
        {
            return;
        }

        _configured = true;
        DefaultTypeMap.MatchNamesWithUnderscores = true;
        SqlMapper.AddTypeHandler(new DateTimeOffsetHandler());
        SqlMapper.AddTypeHandler(new NullableDateTimeOffsetHandler());
    }

    /// <summary>
    /// Npgsql reads a <c>timestamptz</c> as <see cref="DateTime"/> (Kind=Utc); this handler
    /// guarantees the mapping to the <see cref="DateTimeOffset"/> the project mandates.
    /// </summary>
    private sealed class DateTimeOffsetHandler : SqlMapper.TypeHandler<DateTimeOffset>
    {
        public override DateTimeOffset Parse(object value) => value switch
        {
            DateTimeOffset dto => dto,
            DateTime dt => new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)),
            _ => throw new DataException($"Cannot map {value.GetType()} to DateTimeOffset"),
        };

        public override void SetValue(IDbDataParameter parameter, DateTimeOffset value)
        {
            parameter.Value = value.UtcDateTime;
        }
    }

    private sealed class NullableDateTimeOffsetHandler : SqlMapper.TypeHandler<DateTimeOffset?>
    {
        public override DateTimeOffset? Parse(object? value) => value switch
        {
            null or DBNull => null,
            DateTimeOffset dto => dto,
            DateTime dt => new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)),
            _ => throw new DataException($"Cannot map {value.GetType()} to DateTimeOffset?"),
        };

        public override void SetValue(IDbDataParameter parameter, DateTimeOffset? value)
        {
            parameter.Value = value.HasValue ? value.Value.UtcDateTime : DBNull.Value;
        }
    }
}
